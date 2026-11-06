using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Hook;
using Provenance.Common.Login;
using Provenance.Common.PolFormat;
using Provenance.Common.Security;
using Provenance.Common.Settings;
using Provenance.Common.Win32;
using Serilog;

namespace Provenance.Common.Game.Windower;

// what Windower.exe does before starting pol.exe, then the watcher's --windower puts in Hook.dll and pol_hook. The
// mapping and event have fixed names, so launches take turns till the watcher's done
public sealed class WindowerLauncher : IDisposable
{
    internal const string LockName = @"Local\ProvenanceWindowerLaunch";
    public static readonly TimeSpan WatcherLimit = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan UpdateLimit = TimeSpan.FromMinutes(2);
    // a whole turn can be the update and then the watcher
    public static readonly TimeSpan TurnWait = UpdateLimit + WatcherLimit + TimeSpan.FromSeconds(30);

    public const string UpdateNoticeText = "Windower couldn't be fully updated this time, so the game starts with the files it already has.";

    // what Windower.exe writes when there's no init.txt, byte for byte
    internal static readonly string DefaultInit = string.Concat(new[]
    {
        "keyboard_allowmmf 1", "", "console_position 15 50", "console_color 255 255 0 0", "console_displayactivity 1",
        "console_fadedelay 3500", "console_screendir screenshots", "", "bind f12 showfps", "bind sysrq screenshot jpg hide",
        "bind ^v paste",
    }.Select(l => l + "\r\n"));

    private readonly WindowerLaunchSetup _setup;
    private Mutex? _turn;
    private bool _holdsTurn;
    private SafeMemoryMappedFileHandle? _mapping;
    private SafeWaitHandle? _ready;
    private string? _launchId;

    private WindowerLauncher(WindowerLaunchSetup setup) => _setup = setup;

    public string HookDll => Path.Combine(_setup.WindowerDir, "Hook.dll");
    public WindowerProfile Profile { get; private set; } = new();
    public WindowerHookBlock? Block { get; private set; }
    public WindowerLaunchLists? Lists { get; private set; }
    public WindowerUpdateResult? Update { get; private set; }
    public string? UpdateNotice { get; private set; }
    public IReadOnlyList<string> RegistryWritten { get; private set; } = [];
    public bool HandoverOpen => _mapping is not null && _ready is not null;
    public WindowerGameValues GameValues => new(Profile.Clone(), Region, _setup.RegistryRoot);

    private WindowerRegion Region => _setup.Region switch
    {
        PolRegion.Japan => WindowerRegion.JP,
        PolRegion.Europe => WindowerRegion.EU,
        _ => WindowerRegion.US,
    };

    public static WindowerLaunchSetup SetupFor(ISettingsService settings, string polExe, string? accountProfile,
                                              IReadOnlyDictionary<string, WindowerAddonList>? lists, WindowerAddonList? shared = null)
    {
        var s = settings.Current;
        var dir = WindowerInstall.Detect(s.WindowerPath)
                  ?? throw Shown.Mark(new DirectoryNotFoundException("Couldn't find Windower. Set its folder in Settings, under Windower/Ashita."));
        var region = ThirdPartyHook.RegionOfPol(polExe) ?? settings.DetectedRegion ?? PolRegion.UnitedStates;
        return new WindowerLaunchSetup
        {
            WindowerDir = dir,
            ProfileName = WindowerSettingsFile.OldCopySource(accountProfile ?? s.WindowerProfile),
            ProfileFromAccount = accountProfile is not null,
            PolExe = polExe,
            Region = region,
            FfxiDir = PolPaths.TryGetInstallFolder(region, PolPaths.FfxiProduct),
            Lists = lists,
            SharedExtras = shared,
        };
    }

    // every step up to running the watcher. A throw has a message for the player when it's Shown
    public static WindowerLauncher Prepare(WindowerLaunchSetup setup, IProgress<LaunchProgress>? progress, CancellationToken ct)
    {
        var launch = new WindowerLauncher(setup);
        try
        {
            launch.Steps(progress, ct);
            return launch;
        }
        catch
        {
            launch.Dispose();
            throw;
        }
    }

    internal static Dictionary<string, WindowerAddonList>? Snapshot(IReadOnlyDictionary<string, WindowerAddonList>? lists) =>
        lists?.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Copy(), StringComparer.Ordinal);

    private void Steps(IProgress<LaunchProgress>? progress, CancellationToken ct)
    {
        var dir = _setup.WindowerDir;
        if (FolderPins.Linked(dir)) throw Shown.Mark(new IOException(WindowerInstall.LinkedText));
        TakeTurn(progress, ct);
        var file = LoadSettings(dir);
        Profile = FindProfile(file);
        UpdateWindower(file.Branch, progress, ct);
        if (!File.Exists(HookDll))
            throw Shown.Mark(new FileNotFoundException("Windower's Hook.dll is missing and couldn't be downloaded. Check your internet connection, then try again."));
        if (!File.Exists(Path.Combine(dir, "plugins", "LuaCore.dll")))
            throw Shown.Mark(new FileNotFoundException("Windower's LuaCore plugin is missing and couldn't be downloaded. Check your internet connection, then try again."));
        ApplyRegistry();
        MakeFolders(dir);
        Build(file);
        OpenHandover();
    }

    private void TakeTurn(IProgress<LaunchProgress>? progress, CancellationToken ct)
    {
        _turn = new Mutex(false, LockName);
        var until = DateTime.UtcNow + TurnWait;
        bool said = false;
        while (true)
        {
            try { if (_turn.WaitOne(250)) break; }
            catch (AbandonedMutexException) { break; }   // a copy that died part way, the turn's mine now
            ct.ThrowIfCancellationRequested();
            if (!said)
            {
                said = true;
                progress?.Report(new LaunchProgress("Starting", "Waiting for another game to finish starting through Windower...", LaunchSteps.StartPol));
            }
            if (DateTime.UtcNow >= until)
                throw Shown.Mark(new TimeoutException("Another game is still starting through Windower. Try again once it's up."));
        }
        _holdsTurn = true;
    }

    private static WindowerSettingsFile LoadSettings(string dir)
    {
        WindowerSettingsFile file;
        try { file = WindowerSettingsFile.Load(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Could not read Windower's settings.xml");
            throw Shown.Mark(new IOException("Provenance couldn't read Windower's settings.xml. Try again in a moment."));
        }
        if (file.LoadError is { } why)
        {
            Log.Warning("Windower's settings.xml couldn't be read: {Why}", why);
            throw Shown.Mark(new InvalidDataException(file.Unreadable("start a new one in Settings, under Windower/Ashita.")));
        }
        foreach (var w in file.Warnings) Log.Warning("Windower settings: {Warning}", w);
        return file;
    }

    private WindowerProfile FindProfile(WindowerSettingsFile file)
    {
        var name = _setup.ProfileName.Trim();
        var p = file.Find(name) ?? (name.Length == 0 ? file.Unnamed ?? new WindowerProfile() : null);
        if (p is not null) return p;
        throw Shown.Mark(new InvalidOperationException(_setup.ProfileFromAccount
            ? $"The Windower profile {name} chosen for this account isn't in Windower's settings any more. Choose another one in Settings, under Multi-account."
            : $"The Windower profile {name} isn't in Windower's settings any more. Choose another one in Settings, under Windower/Ashita."));
    }

    private void UpdateWindower(string branch, IProgress<LaunchProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new LaunchProgress("Starting", "Checking for Windower updates...", LaunchSteps.StartPol));
        var updater = (_setup.NewUpdater ?? ((d, b) => new WindowerUpdater(d, b)))(_setup.WindowerDir, branch);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_setup.UpdateLimit);
        bool failed = false;
        try { Update = updater.UpdateAsync(limit.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warning("Windower's update took longer than {Limit}, the launch carries on without it", _setup.UpdateLimit);
            failed = true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Windower's update failed");
            failed = true;
        }
        ct.ThrowIfCancellationRequested();
        if (Update is { } r)
        {
            foreach (var e in r.Errors) Log.Warning("Windower update: {Error}", e);
            if (r.Skipped) Log.Information("Windower update: {Reason}", r.SkipReason);
            Log.Information("Windower update ({Branch}): Hook.dll {Before} -> {After}, {Plugins} plugin(s), {Addons} addon(s)",
                            r.Branch, r.HookBefore, r.HookAfter, r.PluginsUpdated.Count, r.AddonsUpdated.Count);
        }
        if (!failed && Update is { Errors.Count: 0, LockTimedOut: false }) return;
        UpdateNotice = UpdateNoticeText;
        progress?.Report(new LaunchProgress("Notice", UpdateNotice));
    }

    private void ApplyRegistry()
    {
        var region = Region;
        try { RegistryWritten = GameValues.Apply(); }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException)
        {
            Log.Error(e, "Could not write Windower's FFXI settings");
            throw Shown.Mark(new InvalidOperationException("Provenance couldn't set the FINAL FANTASY XI settings for this Windower profile."));
        }
        if (RegistryWritten.Count == 0) Log.Warning("No FFXI settings key for {Region}, the profile's game settings weren't applied", region);
    }

    // the same folders Windower.exe makes, but never through a link, this runs as admin in a folder users can write to
    private static void MakeFolders(string dir)
    {
        try
        {
            using var pins = FolderPins.Hold(dir, dir) ?? throw Shown.Mark(new IOException(WindowerInstall.LinkedText));
            bool scripts = true;
            foreach (var sub in new[] { "screenshots", "videos", "scripts" })
                if (!pins.Pin(Path.Combine(dir, sub)))
                {
                    Log.Warning("Windower's {Folder} folder is a link, so Provenance left it alone", sub);
                    scripts &= sub != "scripts";
                }
            var init = Path.Combine(dir, "scripts", "init.txt");
            if (!scripts || File.Exists(init)) return;
            using var fs = new FileStream(init, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.Write(Encoding.ASCII.GetBytes(DefaultInit));
        }
        catch (Exception e) when (!Shown.Is(e) && e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Could not make Windower's folders or init.txt");
        }
    }

    private void Build(WindowerSettingsFile file)
    {
        var dir = _setup.WindowerDir;
        var ffxi = _setup.FfxiDir
                   ?? throw Shown.Mark(new DirectoryNotFoundException("Provenance couldn't find the FINAL FANTASY XI folder, so it can't start the game through Windower."));
        Lists = WindowerAddonList.ForLaunch(dir, Profile, WindowerAddonList.Effective(file, _setup.Lists, Profile.Name, _setup.SharedExtras));
        var sent = Profile.Clone();
        // fast login swaps in Windower's own POL proxy and the other two patch pol.exe, all of which fight the hook.
        // settings.xml keeps the user's own values for Windower.exe
        sent.IncreaseAccountLimit = false;
        sent.EnableFastLogin = false;
        sent.PreventBackgroundThrottling = false;
        Block = WindowerHookBlock.Build(new WindowerHookInput
        {
            Profile = sent,
            Region = Region,
            PlayOnlinePath = Path.GetDirectoryName(Path.GetFullPath(_setup.PolExe))!,
            FfxiPath = ffxi,
            WindowerPath = dir,
            Branch = file.Branch,
            LauncherVersion = WindowerHookBlock.LauncherVersion(dir),
            HookVersion = WindowerHookBlock.HookVersion(dir),
            Width = Profile.Width,
            Height = Profile.Height,
            RequiredPlugins = Lists.RequiredPlugins,
            Plugins = Lists.Plugins,
            Addons = Lists.Addons,
        });
        foreach (var w in Block.Warnings) Log.Warning("Windower hook block: {Warning}", w);
        foreach (var s in Lists.Skipped) Log.Information("Windower: not loading {What}", s);
    }

    private const string BusyText = "Windower is starting another game at the moment. Try again once it's up.";

    // pol.exe trusts every path in the mapping, so one made first by anything else is never used. Hook.dll may keep
    // its own event though, and an admin's is fine
    private void OpenHandover()
    {
        using var sa = HandoverSecurity.Create();
        var mapping = Kernel32.CreateFileMappingW(new IntPtr(-1), sa.Attributes, PageReadWrite, 0, (uint)WindowerHookBlock.Size, WindowerHookBlock.MappingName);
        int error = Marshal.GetLastPInvokeError();
        if (mapping.IsInvalid || error == ErrorAlreadyExists)
        {
            mapping.Dispose();
            Log.Warning("Windower's settings mapping couldn't be made new (error {Error})", error);
            throw Shown.Mark(new IOException(BusyText));
        }
        _mapping = mapping;
        var view = Kernel32.MapViewOfFile(mapping, FileMapWrite, 0, 0, (UIntPtr)WindowerHookBlock.Size);
        if (view == IntPtr.Zero) throw new IOException($"Windower's settings mapping couldn't be written (error {Marshal.GetLastPInvokeError()}).");
        try { Marshal.Copy(Block!.Bytes, 0, view, Block.Bytes.Length); }
        finally { Kernel32.UnmapViewOfFile(view); }

        var ready = Kernel32.CreateEventW(sa.Attributes, false, false, WindowerHookBlock.EventName);
        error = Marshal.GetLastPInvokeError();
        if (ready.IsInvalid || error == ErrorAlreadyExists && !OwnedByAdmins(ready))
        {
            ready.Dispose();
            Log.Warning("Windower's injection event is already there and isn't an admin's, or couldn't be opened (error {Error})", error);
            throw Shown.Mark(new IOException(BusyText));
        }
        _ready = ready;
    }

    private static bool OwnedByAdmins(SafeWaitHandle handle)
    {
        if (Advapi32.GetSecurityInfo(handle, KernelObject, OwnerInformation, out var owner, out _, out _, out _, out var sd) != 0) return false;
        try
        {
            var sid = new SecurityIdentifier(owner);
            return sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid);
        }
        finally { Kernel32.LocalFree(sd); }
    }

    public void ApplyTo(ProcessStartInfo psi, string launchId)
    {
        psi.ArgumentList.Clear();
        psi.ArgumentList.Add("--windower");
        psi.ArgumentList.Add(HookDll);
        psi.ArgumentList.Add(_setup.PolExe);
        psi.ArgumentList.Add(Path.Combine(HookFiles.NativeDir, "pol_hook.dll"));
        // pol_hook leaves pol.exe's mutex names to Hook.dll when it sees this
        psi.Environment["PROVENANCE_LAUNCH_ID"] = _launchId = launchId;
    }

    // on the thread that took the turn, a mutex can't be let go from another
    public void Dispose()
    {
        _ready?.Dispose();
        _ready = null;
        _mapping?.Dispose();
        _mapping = null;
        if (_launchId is not null)
            try { File.Delete(HookFiles.PidFile(_launchId)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (_holdsTurn)
        {
            _holdsTurn = false;
            try { _turn!.ReleaseMutex(); }
            catch (ApplicationException e) { Log.Warning(e, "The Windower launch turn was let go on another thread"); }
        }
        _turn?.Dispose();
        _turn = null;
    }

    // Elevated, only Administrators and SYSTEM, and nothing below high can open either. Not elevated (a dev run) the
    // user too, or the launch couldn't open its own
    private sealed class HandoverSecurity : IDisposable
    {
        private IntPtr _descriptor;
        public SecurityAttributes Attributes;

        public static HandoverSecurity Create()
        {
            using var me = WindowsIdentity.GetCurrent();
            bool elevated = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
            var sddl = elevated ? "O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)S:(ML;;NWNRNX;;;HI)" : $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{me.User})";
            if (!Advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var sd, IntPtr.Zero))
                throw new InvalidOperationException($"Windower's handover security couldn't be made (error {Marshal.GetLastPInvokeError()}).");
            return new HandoverSecurity
            {
                _descriptor = sd,
                Attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = sd },
            };
        }

        public void Dispose()
        {
            if (_descriptor != IntPtr.Zero) Kernel32.LocalFree(_descriptor);
            _descriptor = IntPtr.Zero;
        }
    }

    private const int ErrorAlreadyExists = 183, KernelObject = 6, OwnerInformation = 1;
    private const uint PageReadWrite = 0x04, FileMapWrite = 0x0002;
}
