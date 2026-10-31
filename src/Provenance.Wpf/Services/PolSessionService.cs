using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Provenance.Common;
using Provenance.Common.Credentials;
using Provenance.Common.Game;
using Provenance.Common.Game.Windower;
using Provenance.Common.Locks;
using Provenance.Common.Multi;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Common.Social;
using Serilog;

namespace Provenance.Wpf.Services;

// Lean is POL_LEAN, a multi-account sign-in with no social side. HomeSlot is POL_HOME, the account's pub\homeNN
public sealed record SessionOptions(bool Lean, string? ThirdPartyHookProfile, int? KeyGeneration = null,
                                    int HomeSlot = 0);

public sealed class PolSessionService : IDisposable
{
    public const int ErrorAuthFailed = SignInCodes.AuthFailed;
    public const int ErrorNoPolHandle = SignInCodes.NoPolHandle;
    public const int ErrorLauncherFailed = SignInCodes.LauncherFailed;
    public const int ErrorStalled = SignInCodes.Stalled;
    public const int ErrorHandleRejected = SignInCodes.HandleRejected;
    public const int ErrorCancelled = SignInCodes.Cancelled;
    public const int ErrorAccountBlocked = SignInCodes.AccountBlocked;
    public const int ErrorDetailsUnreadable = SignInCodes.DetailsUnreadable;

    public const string DetailsAgainText = "Provenance needs this account's login details again. Press Edit account to add them.";

    // POL's password box stops at 16 and polcore has no room for more, a longer one gets refused before pol.exe sees it
    public const int MaxPolPasswordLength = 16;
    public const string PolPasswordTooLongText =
        "Your saved PlayOnline password is longer than 16 characters, which PlayOnline doesn't allow. Press Edit account and enter it again.";
    public const string PlayUnreadableText = "Provenance couldn't start the game. Close Provenance and open it again.";
    public const string PlayRefusedText = "PlayOnline didn't let the game start. Log in again and try once more.";
    public const string PrivateFolderText = "Provenance couldn't use its own folder. Restart Provenance as an administrator.";
    public const string UnsafeStartText = "Provenance couldn't start PlayOnline safely. Restart Provenance and try again.";
    public const string RefusedText = "PlayOnline didn't accept your login details. Check your PlayOnline ID, passwords and one-time password.";

    // pol_hook.c exits 11 on unreadable saved entries, 12 on a bad POL_IPC_DIR, 13 when its mutexes can't get secret
    // names (never the stock ones, anything can create those first), 14 when pol.exe's region isn't POL_REGION and 15
    // when FFXI's GameStart fails
    internal const int UnsafeStartExitCode = 13;
    internal const int RegionExitCode = 14;
    internal const int GameStartExitCode = 15;
    public const string RegionChangedText = "PlayOnline started as a different version than the one Provenance checked. Restart Provenance and try again.";
    public const string GameStartFailedText = "FINAL FANTASY XI couldn't start. Try again, and if it keeps happening, check the game's files with Repair.";

    internal static bool DetailsUnreadable(string? line = null, int? exitCode = null) =>
        (line?.Contains("phase_c: SAVED DETAILS UNREADABLE", StringComparison.Ordinal) ?? false) || exitCode == 11;

    internal enum PlayAnswer { None, Started, Unreadable, Refused }

    internal static PlayAnswer PlayAnswerOf(string? line) =>
        line is null ? PlayAnswer.None
        : line.Contains("hold: Play signalled", StringComparison.Ordinal) ? PlayAnswer.Started
        : line.Contains("hold: Play entry unreadable", StringComparison.Ordinal) ? PlayAnswer.Unreadable
        : line.Contains("hold: Play refused:", StringComparison.Ordinal) ? PlayAnswer.Refused
        : PlayAnswer.None;

    // 3 refused, 7 turned away, 10 a list didn't load
    internal static (int Code, string Text) EndedBeforeSignIn(int? exitCode) => exitCode switch
    {
        _ when DetailsUnreadable(exitCode: exitCode) => (ErrorDetailsUnreadable, DetailsAgainText),
        12 => (ErrorLauncherFailed, PrivateFolderText),
        UnsafeStartExitCode => (ErrorLauncherFailed, UnsafeStartText),
        RegionExitCode => (ErrorLauncherFailed, RegionChangedText),
        GameStartExitCode => (ErrorLauncherFailed, GameStartFailedText),
        3 => (ErrorAuthFailed, RefusedText),
        7 => (ErrorAccountBlocked, "PlayOnline turned this account away. Log in with the PlayOnline Viewer to see why."),
        10 => (ErrorStalled, LoadFailedText("")),
        _ => (ErrorStalled, "PlayOnline closed before the login finished."),
    };

    internal static string SessionClosedText(int? exitCode) => exitCode switch
    {
        12 => PrivateFolderText,
        UnsafeStartExitCode => UnsafeStartText,
        RegionExitCode => RegionChangedText,
        GameStartExitCode => GameStartFailedText,
        _ => "Your PlayOnline session has closed. Log in again to see your friends and messages.",
    };

    // same rule as ipc_dir in pol_hook.c, checked up front so exit 12 gets a proper message
    internal static bool IpcDirUsable(string? dir) =>
        !string.IsNullOrEmpty(dir) && dir.Length < 260 - 64 && Directory.Exists(dir);

    private const string LaunchKeyPrefix = "LAUNCH:";
    private const string SessionClaimLock = "session-claim";
    private static readonly Regex PidLine = new(@"pol\.exe running \(pid (\d+)\)", RegexOptions.Compiled);
    private static readonly TimeSpan[] RefusedRetryWaits = [TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60)];
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(10);

    // a new request file goes here too (I'll forget)
    private static readonly string[] RequestFiles =
        ["send", "status", "mail", "ack", "comment", "pic", "friend", "answer", "search", "profile", "group",
         "myprofile", "saveprofile", "rename"];

    // Has to match prestart_wait's allow-list in pol_hook.c, the hook drops any other key
    private static readonly string[] GoFileKeys =
        ["POL_CRED_POL", "POL_CRED_SEA", "POL_CRED_ONESHOT", "POL_CRED_OTP", "POL_CRED_HANDLE", "POL_CRED_PLAY", "POL_STATUS",
         "POL_HOME"];

    private enum LaunchOutcome { Started, Failed, Refused, Cancelled }

    internal sealed record Prestarted(int Pid, DateTime? Started);

    // Pid 0 is a claim, start times are UTC ticks. The Windower ones are what its Play needs if another copy picks it up
    internal sealed record SessionFile(int Pid, long StartTicks, string LaunchId, string? ProfileId,
                                       int OwnerPid = 0, long OwnerStartTicks = 0, bool Played = false, bool Lean = false,
                                       string? WindowerDir = null, string? WindowerProfile = null, int WindowerRegion = -1);

    // admin-only, so nothing running as the user can plant a record that makes the app attach to or end a process
    private static readonly Lazy<string> SessionDirLazy = new(() => SecureFiles.SubDir("sessions"));
    private static string SessionDir => SessionDirLazy.Value;

    private static long _dnsWarmedAt;

    private readonly ISettingsService _settings;
    private readonly WindowsCredentialStore _store;
    private readonly WrappedStoreCache<WindowsCredentialStore> _wrapped;
    private readonly Func<bool>? _checkKey;
    private readonly Action? _dropKey;
    private readonly System.Threading.Timer _sweepTimer;
    private readonly HashSet<string> _usedLaunchIds = [];
    private readonly object _gate = new();
    private readonly object _hookGate = new();
    private readonly StringBuilder _pending = new();
    private volatile bool _disposed;
    private int _sweepRuns;
    private string? _launchId;
    private long _logOffset;
    private bool _played;   // the held pol.exe is the game now, never kill it
    private DateTime? _pidStarted;
    private Process? _polProc;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private Task<Prestarted?>? _prestart;
    private int _prestartHolds;   // under _gate
    private string? _sessionFile;
    private string? _sessionProfile;
    private bool _sessionLean;
    private string? _claimFile;
    private int? _hookPid;
    private DateTime _hookStart;
    private WindowerGameValues? _windowerPlay;

    public PolSessionService(ISettingsService settings)
        : this(settings, new WindowsCredentialStore(), () => new WindowsCredentialStore(wrapKey: CredentialKey.Open()),
               SweepInterval, CredentialKey.CheckFile, CredentialKey.Reload) { }

    internal PolSessionService(ISettingsService settings, WindowsCredentialStore store,
                               Func<WindowsCredentialStore> openWrapped, TimeSpan sweepEvery,
                               Func<bool>? checkKey = null, Action? dropKey = null)
    {
        _settings = settings;
        _store = store;
        _wrapped = new WrappedStoreCache<WindowsCredentialStore>(openWrapped);
        _checkKey = checkKey;
        _dropKey = dropKey;
        FindLaunchFiles = () => LaunchFiles(Region);
        GameIsInstalled = () => GameInstalled(Region);
        UpdateViewer = (dir, progress, freeFiles, ct) => PolUpdates.UpdateViewer(dir, Region, progress, freeFiles, ct);
        WindowerSetup = (polExe, profile) => WindowerLaunch.SetupFor(_settings, polExe, profile);
        DeleteLeftovers();
        _sweepTimer = new System.Threading.Timer(_ => SweepLeftovers(), null, sweepEvery, sweepEvery);
    }

    public int? Pid { get; private set; }

    // everything a pol.exe start reaches outside this class, so the starts can be checked offline with stand-ins
    internal Func<(string? ViewerDir, string? PolExe, string LauncherExe)> FindLaunchFiles { get; init; }
    internal Func<bool> GameIsInstalled { get; init; }
    internal Func<bool> ViewerPolRunning { get; init; } = PolUpdates.ViewerRunning;
    internal Func<string, IProgress<PatchProgress>?, Action, CancellationToken, PatchResult> UpdateViewer { get; init; }
    internal Func<ProcessStartInfo, Process?> StartProcess { get; init; } = Process.Start;
    internal Func<string, string?, WindowerLaunchSetup> WindowerSetup { get; init; }
    internal RegistryKey? WindowerRegistryRoot { get; init; }
    internal Func<Prestarted, bool> PrestartAlive { get; init; } = pre =>
    {
        try { using var p = Process.GetProcessById(pre.Pid); return !p.HasExited && IsSameProcess(p, pre.Started); }
        catch (ArgumentException) { return false; }
    };

    public event Action<PolSocialSnapshot>? SnapshotUpdated;

    public event Action<string>? SessionEnded;

    public int? LastRefusalReason { get; private set; }

    internal string Platform =>
        (_settings.Current.ThirdPartyHookPlatform ?? ThirdPartyHookService.None).Trim().ToLowerInvariant();

    internal int SweepRuns => Volatile.Read(ref _sweepRuns);

    private WindowsCredentialStore Wrapped => _wrapped.Get();

    // a login or pre-start only gets this far on a supported region
    private PolRegion Region => _settings.DetectedRegion ?? PolRegion.UnitedStates;

    // a changed key stops the sign-in, the hook would read the entries with the new one and exit 11
    internal (int Code, string Text)? KeyForSignIn(int? detailsGeneration = null)
    {
        try
        {
            _ = Wrapped;
            bool changed = _checkKey?.Invoke() == true;
            if (!changed && (detailsGeneration is null || detailsGeneration == CredentialKey.Generation)) return null;
            Log.Warning("The key file changed before this sign-in: the account's details are asked for again");
            return (ErrorDetailsUnreadable, DetailsAgainText);
        }
        catch (CredentialKeyUnavailableException) { return (ErrorLauncherFailed, CredentialKey.UnavailableText); }
    }

    internal void DropKey()
    {
        try { _dropKey?.Invoke(); }
        catch (Exception ex) { Log.Warning(ex, "Could not read the key file again after the hook could not read an entry"); }
    }

    public Task OpenKeyInBackground() => Task.Run(() =>
    {
        try { _ = Wrapped; }
        catch (CredentialKeyUnavailableException) { }
    });

    private void SweepLeftovers()
    {
        if (_disposed) return;
        Interlocked.Increment(ref _sweepRuns);
        DeleteLeftovers();
    }

    private void DeleteLeftovers()
    {
        try
        {
            int swept = _store.DeleteByPrefix(LaunchKeyPrefix, LeftoverAge);
            if (swept > 0) Log.Warning("Removed {Count} leftover one-shot sign-in credential(s)", swept);
        }
        catch (Exception ex) { Log.Warning(ex, "Sweep of leftover sign-in credentials failed"); }
    }

    internal string NewLaunchId()
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        lock (_gate) _usedLaunchIds.Add(id);
        return id;
    }

    public async Task<int> ConnectAsync(PolAccount pol, SeaAccount sea, string otp, string? rememberedProfileId,
                                        IProgress<LaunchProgress>? progress, Func<string?>? askHandleName,
                                        CancellationToken ct = default, SessionOptions? options = null)
    {
        // no gate here, StopHold can hold it for seconds and this is the UI thread
        if (Volatile.Read(ref _played))
            throw Shown.Mark(new InvalidOperationException("Provenance is still finishing your last game. Close Provenance and open it again, then log in."));
        LastRefusalReason = null;
        int rc = ErrorLauncherFailed;
        try
        {
            rc = await Task.Run(() => ConnectCore(pol, sea, otp, rememberedProfileId, progress, askHandleName,
                                                  options ?? new SessionOptions(false, null), ct), ct);
            return rc;
        }
        finally { if (rc != 0) ReleaseClaim(); }
    }

    private int ConnectCore(PolAccount pol, SeaAccount sea, string otp, string? rememberedProfileId,
                            IProgress<LaunchProgress>? progress, Func<string?>? askHandleName, SessionOptions options,
                            CancellationToken ct)
    {
        int Fail(string text, int code = ErrorLauncherFailed)
        {
            progress?.Report(new LaunchProgress("Failed", text));
            return code;
        }
        _logOffset = 0;
        _pending.Clear();
        _played = false;
        _sessionLean = options.Lean;
        if (pol.Password.Length > MaxPolPasswordLength) return Fail(PolPasswordTooLongText, ErrorDetailsUnreadable);
        var region = _settings.DetectedRegion;
        if (!PolPaths.Supported(region))
            return Fail(region is null
                ? "PlayOnline isn't installed." : $"Provenance doesn't support the {PolPaths.VersionName(region.Value)} version of PlayOnline yet.");
        var (viewerDir, polExe, launcherExe) = FindLaunchFiles();
        if (polExe is null)
            return Fail("Some of the files needed to start PlayOnline are missing. Check FINAL FANTASY XI is installed, and if it is, reinstall Provenance.");
        if (!IpcDirUsable(HookFiles.Dir))
        {
            Log.Error("The folder of the hook's files cannot be used: {Dir}", HookFiles.Dir);
            return Fail(PrivateFolderText);
        }
        if (KeyForSignIn(options.KeyGeneration) is { } keyProblem) return Fail(keyProblem.Text, keyProblem.Code);
        // the viewer checks for its own update on every connect and always says yes
        try
        {
            UpdateViewer(viewerDir!, new SyncProgress<PatchProgress>(
                p => progress?.Report(LaunchProgress.Updating("Starting", LaunchSteps.StartPol, p, 0.02, LaunchProgress.SignInUpdateEnd))),
                StopPrestart, ct);
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new LaunchProgress("Stopped", "The login was cancelled."));
            return ErrorCancelled;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PlayOnline Viewer update failed");
            return Fail(ErrorText.Of(ex, "The PlayOnline Viewer couldn't be updated. Try logging in again in a moment."));
        }
        // after the viewer's update and before the hold, which stops any write
        LargeAddressAwareBeforeStart(viewerDir!, "before the login");
        // the hook patches fixed offsets in pol.exe and polcore.dll, so both stay locked until the sign-in ends
        using var viewerHold = ViewerBuild.Hold(viewerDir!, out var buildProblem);
        if (buildProblem != null)
        {
            Log.Warning("Viewer build not supported: {Problem}", buildProblem);
            return Fail(buildProblem);
        }
        _launchId = NewLaunchId();
        var keys = new List<string>();
        try
        {
            return ConnectHeld(pol, sea, otp, rememberedProfileId, progress, askHandleName, polExe, launcherExe, keys,
                               options, ct);
        }
        finally
        {
            CleanupOneShots(keys);   // on every way out, so passwords never linger in Credential Manager
        }
    }

    private int ConnectHeld(PolAccount pol, SeaAccount sea, string otp, string? rememberedProfileId,
                            IProgress<LaunchProgress>? progress, Func<string?>? askHandleName, string polExe,
                            string launcherExe, List<string> keys, SessionOptions options, CancellationToken ct)
    {
        string Key(string kind) => $"{LaunchKeyPrefix}{_launchId}:{kind}";
        string Target(string key) => $"{WindowsCredentialStore.DefaultPrefix}:{key}";
        string OneShot(string kind, string user, string secret)
        {
            Wrapped.Save(Key(kind), user, secret, sessionOnly: true);
            keys.Add(Key(kind));
            return Target(Key(kind));
        }
        int Stopped(string text)
        {
            progress?.Report(new LaunchProgress("Stopped", text));
            return ErrorCancelled;
        }
        int Fail(int code, string detail)
        {
            if (code == ErrorDetailsUnreadable) DropKey();
            progress?.Report(new LaunchProgress("Failed", detail));
            return code;
        }

        string polTarget, seaTarget;
        var oneShot = new List<string> { "OTP" };
        var profile = string.IsNullOrWhiteSpace(rememberedProfileId) ? null : rememberedProfileId;
        if (profile != null && _store.ReadUserName($"{CredentialKeys.Pol}:{profile}") == pol.PolId &&
            _store.ReadUserName($"{CredentialKeys.Sea}:{profile}") == sea.Username)
        {
            polTarget = Target($"{CredentialKeys.Pol}:{profile}");
            seaTarget = Target($"{CredentialKeys.Sea}:{profile}");
        }
        else
        {
            polTarget = OneShot("POL", pol.PolId, pol.Password);
            seaTarget = OneShot("SEA", sea.Username, sea.Password);
            oneShot.InsertRange(0, ["POL", "SEA"]);
        }
        var otpTarget = string.IsNullOrWhiteSpace(otp) ? null : OneShot("OTP", "otp", otp.Trim());

        var psi = LauncherStart(launcherExe, polExe);
        var env = psi.Environment;
        SetBaseEnvironment(env, PolEnv.Region(Path.GetDirectoryName(polExe)!));
        // only the viewer's menu values, busy goes online
        var status = _settings.Current.PlayOnlineStatus;
        env["POL_STATUS"] = status is 0 or 1 or 4 ? status.ToString() : "0";
        env["POL_CRED_POL"] = polTarget;
        env["POL_CRED_SEA"] = seaTarget;
        env["POL_CRED_ONESHOT"] = string.Join(",", oneShot);
        env["POL_CRED_HANDLE"] = Target(Key("HANDLE"));
        env["POL_CRED_PLAY"] = Target(Key("PLAY"));
        keys.AddRange([Key("HANDLE"), Key("PLAY")]);
        if (otpTarget != null) env["POL_CRED_OTP"] = otpTarget;
        if (options.Lean) env["POL_LEAN"] = "1";
        if (HomeSlots.Valid(options.HomeSlot))
            env["POL_HOME"] = options.HomeSlot.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (ct.IsCancellationRequested) return Stopped("The login was cancelled.");
        progress?.Report(new LaunchProgress("Starting", "Starting PlayOnline...", LaunchSteps.StartPol, 0.02));
        int pid;
        var platform = Platform;
        Volatile.Write(ref _windowerPlay, null);
        if (platform == ThirdPartyHookService.Ashita)
        {
            var hookPid = ThirdPartyHookLauncher.StartViaAshita(
                _settings, psi, polExe, _launchId!, options.ThirdPartyHookProfile, progress, ct,
                hookStarted: TrackThirdPartyHook);
            if (hookPid is null) return ct.IsCancellationRequested ? ErrorCancelled : ErrorLauncherFailed;
            pid = hookPid.Value;
        }
        else if (platform == ThirdPartyHookService.Windower)
        {
            if (StartThroughWindower(psi, polExe, options.ThirdPartyHookProfile, _launchId!, progress, ct) is not { } started)
                return ct.IsCancellationRequested ? ErrorCancelled : ErrorLauncherFailed;
            pid = started;
        }
        // never for a lean session, the go file can't carry POL_LEAN
        else if (!options.Lean && TakePrestarted(psi, ct) is { } pre)
        {
            pid = pre;
        }
        else
        {
            // a cancel while the pre-start was being taken leaves it waiting, so nothing starts in its place
            if (ct.IsCancellationRequested) return Stopped("The login was cancelled.");
            if (StartWithPolLaunch(psi, progress, ct, PolLaunchLimit, PolDidNotStart) is not { } started)
                return ct.IsCancellationRequested ? ErrorCancelled : ErrorLauncherFailed;
            pid = started;
        }
        lock (_gate)
        {
            Pid = pid;
            // kept so the exit code still says why if it dies mid sign-in
            _polProc?.Dispose();
            try { _polProc = Process.GetProcessById(pid); _ = _polProc.Handle; _pidStarted = _polProc.StartTime; }
            catch { _polProc = null; _pidStarted = null; }
        }
        // saved now, if this copy dies mid sign-in the next start ends this pol.exe
        SaveSession(rememberedProfileId);
        Log.Information("Hold session: pol.exe pid {Pid}", pid);
        progress?.Report(new LaunchProgress("Starting", "Starting PlayOnline...", LaunchSteps.StartPol, 0.04));
        // an old process with this pid left these. Not the go file, a pre-started pol.exe reads its sign-in from it
        DeleteQuietly(SocialSnapshot.PathForPid(pid));
        DeleteQuietly(SocialSnapshot.StorePathForPid(pid));
        DeleteQuietly(HookFiles.PathFor(pid, "stop"));
        foreach (var ext in RequestFiles)
        {
            DeleteQuietly(HookFiles.PathFor(pid, ext));
            DeleteQuietly(HookFiles.PathFor(pid, ext) + ".taking");
        }

        var logPath = HookFiles.PathFor(pid, "log");
        var translator = new HookLogTranslator();
        var lastActivity = DateTime.UtcNow;
        int? viewerLanguage = null;
        int? Ending(string line)
        {
            // the language the hook gave polcore, the same one app.dll builds the jail page's address from
            if (Regex.Match(line, @"viewer_init: lang=(\d+)") is { Success: true } lm && int.TryParse(lm.Groups[1].Value, out var lang))
                viewerLanguage = lang;
            if (DetailsUnreadable(line)) return Fail(ErrorDetailsUnreadable, DetailsAgainText);
            if (line.Contains("phase_c: AUTH FAILED"))
            {
                var rm = Regex.Match(line, @"reason=(-?\d+)");
                LastRefusalReason = rm.Success && int.TryParse(rm.Groups[1].Value, out var reason) ? reason : null;
                return Fail(ErrorAuthFailed, LastRefusalReason is { } r
                    ? PolAccountNotices.SignInRefused(r)
                    : RefusedText);
            }
            if (line.Contains("phase_c: ACCOUNT BLOCKED "))
                return Fail(ErrorAccountBlocked,
                            PolAccountNotices.Blocked(Regex.Match(line, @"ACCOUNT BLOCKED (\S+)").Groups[1].Value, Region, viewerLanguage));
            if (line.Contains("phase_c: LOAD FAILED "))
                return Fail(ErrorStalled, LoadFailedText(Regex.Match(line, @"LOAD FAILED (\w+)").Groups[1].Value));
            if (line.Contains("phase_c: NO POL HANDLE"))
                return Fail(ErrorNoPolHandle, "This PlayOnline account doesn't have a handle, and none was created.");
            if (line.Contains("handle name rejected") || line.Contains("handle claim failed")
                || line.Contains("handle save failed"))
                return Fail(ErrorHandleRejected, "PlayOnline didn't accept that handle name.");
            if (line.Contains("phase_c: FFXI SERVICE UNKNOWN") || line.Contains("hold: no signed-in session"))
                return Fail(ErrorAuthFailed, "The PlayOnline login didn't finish. Try again.");
            return null;
        }
        while (!ct.IsCancellationRequested)
        {
            foreach (var line in ReadNewLines(logPath))
            {
                lastActivity = DateTime.UtcNow;
                if (translator.Translate(line) is { } step) progress?.Report(step);
                if (Ending(line) is { } result) return result;
                if (!line.Contains("phase_c: waiting for handle name")) continue;
                progress?.Report(new LaunchProgress("PlayOnline handle", "This account needs a PlayOnline handle."));
                var name = askHandleName?.Invoke();
                Wrapped.Save(Key("HANDLE"), "handle", string.IsNullOrWhiteSpace(name) ? "" : name!.Trim(), sessionOnly: true);
                lastActivity = DateTime.UtcNow;
            }
            if (ct.IsCancellationRequested) break;   // a cancel that lands with the first snapshot still wins
            if (SocialSnapshot.TryRead(pid) is { } snap)
            {
                // the hook writes the store file before its first snapshot, so it's there now even if Play comes next
                SnapshotUpdated?.Invoke(snap with { Stored = SocialSnapshot.TryReadStore(pid) });
                StartWatcher(pid);
                SaveSession(rememberedProfileId);
                progress?.Report(new LaunchProgress("Connected", "You're logged in.", LaunchSteps.StartGame, 1.0));
                return 0;
            }
            if (!IsAlive(pid))
            {
                // it can log its refusal and exit between two reads
                foreach (var line in ReadRemainingLines(logPath))
                    if (Ending(line) is { } result) return result;
                int? exit = null;
                try { if (_polProc is { HasExited: true } ended) exit = ended.ExitCode; } catch { }
                PutScreenSaverBack();
                LogEarlyExit(pid, exit, logPath);
                var (endedCode, endedText) = EndedBeforeSignIn(exit);
                return Fail(endedCode, endedText);
            }
            if (DateTime.UtcNow - lastActivity > TimeSpan.FromSeconds(260))   // above the hook's 240 s list-load limit
                return Fail(ErrorStalled, "PlayOnline stopped responding during the login. Try again in a moment.");
            Thread.Sleep(250);
        }
        return Stopped("The login was cancelled, and PlayOnline has been closed.");
    }

    // null once progress has said why
    internal int? StartThroughWindower(ProcessStartInfo psi, string polExe, string? profile, string launchId,
                                       IProgress<LaunchProgress>? progress, CancellationToken ct)
    {
        WindowerLaunch windower;
        try { windower = WindowerLaunch.Prepare(WindowerSetup(polExe, profile), progress, ct); }
        catch (OperationCanceledException)
        {
            progress?.Report(new LaunchProgress("Stopped", "The login was cancelled."));
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Windower wasn't ready to start the game");
            progress?.Report(new LaunchProgress("Failed", ErrorText.Of(ex, "Provenance couldn't get Windower ready to start the game. Try again in a moment.")));
            return null;
        }
        // held till pol_launch is done, Hook.dll copies the mapping out before it signals
        using (windower)
        {
            windower.ApplyTo(psi, launchId);
            progress?.Report(new LaunchProgress("Starting", "Starting PlayOnline with Windower...", LaunchSteps.StartPol, 0.02));
            var pid = StartWithPolLaunch(psi, progress, ct, WindowerLaunch.PolLaunchLimit, WindowerDidNotStart);
            if (pid is not null) Volatile.Write(ref _windowerPlay, windower.GameValues);
            return pid;
        }
    }

    internal void ReattachWindower(string? windowerDir, string? profile, int region) =>
        Volatile.Write(ref _windowerPlay, windowerDir is null || profile is null || region is < 0 or > 2 ? null
            : WindowerGameValues.Find(windowerDir, profile, (WindowerRegion)region, WindowerRegistryRoot));

    // another launch through Windower may have written its own profile's values since this one's
    internal void PutWindowerValuesBack()
    {
        if (Volatile.Read(ref _windowerPlay) is not { } values) return;
        try { Log.Information("Windower's game settings for {Profile} written again before Play ({Keys})", values.Profile.Name, values.Apply().Count); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warning(ex, "Could not write Windower's game settings again before Play");
        }
    }

    // the hook's own log is in the admin-only folder, so a player's log needs the exit code and the crash lines too
    private static void LogEarlyExit(int pid, int? exit, string hookLog)
    {
        Log.Warning("pol.exe {Pid} ended before the login finished, exit code {Exit}", pid, exit is { } e ? $"0x{e:X8}" : "unknown");
        try
        {
            using var fs = new FileStream(hookLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var crashes = new List<string>();
            while (reader.ReadLine() is { } line)
                if (line.Contains("CRASH ", StringComparison.Ordinal)) crashes.Add(line.Trim());
            foreach (var line in crashes.TakeLast(3)) Log.Warning("pol_hook: {Line}", line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string LoadFailedText(string list) =>
        $"PlayOnline didn't send {list switch { "status" => "your status", "friends" => "your friend list", "handles" => "your handles", "characters" => "your service list", "groups" => "your friend groups", _ => "everything it needs at login" }}, so you've been logged out (the PlayOnline Viewer does the same). Try again in a moment.";

    private static (string? ViewerDir, string? PolExe, string LauncherExe) LaunchFiles(PolRegion region)
    {
        var viewerDir = PolPaths.TryGetInstallFolder(region, PolPaths.ViewerProduct);
        var polExe = viewerDir is null ? null : Path.Combine(viewerDir, "pol.exe");
        var launcherExe = Path.Combine(ThirdPartyHookService.NativeDir, "pol_launch.exe");
        bool found = polExe != null && File.Exists(polExe) && File.Exists(launcherExe)
                     && File.Exists(Path.Combine(ThirdPartyHookService.NativeDir, "pol_hook.dll"));
        return (viewerDir, found ? polExe : null, launcherExe);
    }

    private void LargeAddressAwareBeforeStart(string viewerDir, string when) =>
        LargeAddressAwareSwitch.Apply(_settings, new LargeAddressAwareSwitch.Target(viewerDir, ViewerPolRunning, () => null, () => null), when);

    internal static ProcessStartInfo LauncherStart(string launcherExe, string polExe)
    {
        var psi = new ProcessStartInfo(launcherExe)
        {
            WorkingDirectory = ThirdPartyHookService.NativeDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add(polExe);
        return psi;
    }

    // POL_REGION is the env.dat region the build check went by, as pol.exe's own index (0 JP, 1 US, 2 EU), which the
    // hook holds pol.exe to. An env.dat that doesn't say leaves it to pol.exe
    internal static void SetBaseEnvironment(IDictionary<string, string?> env, PolRegion? region)
    {
        LaunchEnvironment.Reset(env);
        env["POL_IPC_DIR"] = HookFiles.Dir;
        env["POL_CRED_KEYFILE"] = CredentialKey.FilePath;
        env["POL_HOLD"] = "1";
        if (region is { } r) env["POL_REGION"] = RegionIndex(r);
        if (ScreenSaverAtStart is { } saver) env["POL_SCREENSAVER"] = saver;
    }

    // pol.exe switches the screen saver off while it runs, so it's read once before any start and the last pol.exe
    // out puts it back
    internal static string? ScreenSaverAtStart { get; set; }

    internal static void NoteScreenSaver(ISettingsService settings) => NoteScreenSaver(settings, AnyPolRunning, ReadScreenSaver);

    private static bool AnyPolRunning()
    {
        var pols = Process.GetProcessesByName("pol");
        foreach (var p in pols) p.Dispose();
        return pols.Length > 0;
    }

    // Windows only has the real answer with no pol.exe about, so that one's kept for a start while one's still running.
    // A settings.json that didn't load is left for the user's own next save to replace
    internal static void NoteScreenSaver(ISettingsService settings, Func<bool> polRunning, Func<bool?> readWindows)
    {
        bool? on;
        if (polRunning())
        {
            on = settings.Current.ScreenSaverWasOn ?? readWindows();
        }
        else
        {
            on = readWindows();
            if (on is { } fresh && settings.Current.ScreenSaverWasOn != fresh)
            {
                settings.Current.ScreenSaverWasOn = fresh;
                if (settings.LoadProblem is null) settings.Save(settings.Current);
            }
        }
        ScreenSaverAtStart = on is { } v ? (v ? "1" : "0") : null;
        SavedScreenSaver = settings.Current.ScreenSaverWasOn;
        if (on is null) Log.Warning("Could not read the screen saver setting, the hook reads it itself");
    }

    internal static bool? SavedScreenSaver { get; set; }

    // a pol.exe that crashed or got killed never put it back itself, and the next start's read has to be the real one
    internal static void PutScreenSaverBack() =>
        PutScreenSaverBack(SavedScreenSaver, AnyPolRunning, on => SetSystemParameter(SpiSetScreenSaveActive, on ? 1u : 0u, IntPtr.Zero, 0));

    internal static bool PutScreenSaverBack(bool? wasOn, Func<bool> polRunning, Action<bool> set)
    {
        if (wasOn is not { } on || polRunning()) return false;
        set(on);
        return true;
    }

    private static bool? ReadScreenSaver() =>
        SystemParametersInfoW(SpiGetScreenSaveActive, 0, out int on, 0) ? on != 0 : null;

    private const uint SpiGetScreenSaveActive = 0x10;
    private const uint SpiSetScreenSaveActive = 0x11;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfoW(uint action, uint param, out int value, uint winIni);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SetSystemParameter(uint action, uint param, IntPtr value, uint winIni);

    internal static string RegionIndex(PolRegion region) => region switch
    {
        PolRegion.Japan => "0",
        PolRegion.Europe => "2",
        _ => "1",
    };

    private void StartWatcher(int pid)
    {
        StopWatcher();
        _debounce = new Timer(_ => ReadSnapshot(), null, Timeout.Infinite, Timeout.Infinite);
        var w = new FileSystemWatcher(HookFiles.Dir, Path.GetFileName(SocialSnapshot.PathForPid(pid)))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        // the timer can get disposed with an event still in flight, and a throw here takes the whole app down
        void Kick()
        {
            try { _debounce?.Change(60, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }
        w.Changed += (_, _) => Kick();
        w.Created += (_, _) => Kick();
        w.Renamed += (_, _) => Kick();
        w.EnableRaisingEvents = true;
        _watcher = w;
    }

    private void StopWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Dispose();
        _debounce = null;
    }

    // pol.exe and polcore take about 2 s to start, so one starts with the app and waits for its go file. Not with
    // Windower/Ashita, it'd open them.
    public void Prestart()
    {
        WarmDns();
        lock (_gate)
        {
            if (!MayPrestart()) return;
            _prestart = Task.Run(StartPrestarted);
        }
    }

    // under _gate
    private bool MayPrestart() =>
        _prestart == null && Pid == null && _prestartHolds == 0
        && Platform is not (ThirdPartyHookService.Ashita or ThirdPartyHookService.Windower)
        && PolPaths.Supported(_settings.DetectedRegion);

    internal bool PrestartAllowed
    {
        get { lock (_gate) return MayPrestart(); }
    }

    internal int PrestartHolds
    {
        get { lock (_gate) return _prestartHolds; }
    }

    // only the resolver sees this, nothing connects to POL
    private static void WarmDns()
    {
        long now = Environment.TickCount64;
        if (_dnsWarmedAt != 0 && now - _dnsWarmedAt < 600_000) return;
        _dnsWarmedAt = now;
        _ = Task.Run(async () =>
        {
            try { await System.Net.Dns.GetHostAddressesAsync("ci000.pol.com").ConfigureAwait(false); }
            catch { }
        });
    }

    internal Prestarted? StartPrestarted()
    {
        try
        {
            var (viewerDir, polExe, launcherExe) = FindLaunchFiles();
            // no network here, the sign-in does the version check. With no game there's no sign-in card to use it, and
            // the install would take it for POL running
            if (polExe is null || !GameIsInstalled() || ViewerBuild.Check(viewerDir!) != ViewerBuild.State.Supported) return null;
            // a queued update is the login's job, pol.exe would act on it itself (patch.ini goes to startpol.exe
            // /REPLACEPOLEXE, which starts a pol.exe that isn't mine)
            if (PolUpdater.QueuedWorkPending(viewerDir!))
            {
                Log.Information("Not pre-starting pol.exe: the viewer has update work queued, the next login finishes it first");
                return null;
            }
            LargeAddressAwareBeforeStart(viewerDir!, "before the pre-start");
            var psi = LauncherStart(launcherExe, polExe);
            SetPrestartEnvironment(psi.Environment, Environment.ProcessId, PolEnv.Region(viewerDir!));
            using var launcher = StartProcess(psi);
            if (launcher is null || RunLauncher(launcher, PolLaunchLimit) is not { } run) return null;
            var m = PidLine.Match(run.Out);
            if (run.ExitCode != 0 || !m.Success) return null;
            var pid = int.Parse(m.Groups[1].Value);
            DateTime? started = null;
            try { using var p = Process.GetProcessById(pid); started = p.StartTime; } catch { }
            Log.Information("Pre-started pol.exe (pid {Pid}), waiting for LOG IN", pid);
            return new Prestarted(pid, started);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Pre-start failed; LOG IN starts pol.exe itself");
            return null;
        }
    }

    private static bool GameInstalled(PolRegion region) =>
        PolPaths.TryGetInstallFolder(region, PolPaths.FfxiProduct) is { } ffxi && File.Exists(Path.Combine(ffxi, "FFXiMain.dll"));

    // POL_IPC_DIR and POL_CRED_KEYFILE have to go in here, the go file never carries them
    internal static void SetPrestartEnvironment(IDictionary<string, string?> env, int parentPid, PolRegion? region)
    {
        SetBaseEnvironment(env, region);
        env["POL_PRESTART"] = "1";
        env["POL_PRESTART_PARENT"] = parentPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static IEnumerable<string> GoFileEntries(IDictionary<string, string?> env)
    {
        foreach (var k in GoFileKeys)
            if (env.TryGetValue(k, out var v) && v != null) yield return $"{k}={v}";
    }

    // Nothing's sent till the go file's written, so a cancel before that leaves it waiting, hidden, for the next login.
    // Only a live one goes back, a dead one would stop the Prestart after the cancel making a fresh one
    private int? TakePrestarted(ProcessStartInfo psi, CancellationToken ct)
    {
        if (TakePrestart(ct) is not { } pre) return null;
        int pid = pre.Pid;
        try
        {
            if (!PrestartAlive(pre)) return null;
            if (ct.IsCancellationRequested) { GiveBack(Task.FromResult<Prestarted?>(pre)); return null; }
            var log = HookFiles.PathFor(pid, "log");
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < until && !LogHas(log, "prestart: ready") && !ct.IsCancellationRequested) Thread.Sleep(100);
            if (ct.IsCancellationRequested) { GiveBack(Task.FromResult<Prestarted?>(pre)); return null; }
            if (!LogHas(log, "prestart: ready")) { EndPrestarted(pre); return null; }
            var go = HookFiles.PathFor(pid, "go");
            File.WriteAllText(go + ".tmp", string.Join("\n", GoFileEntries(psi.Environment)));
            File.Move(go + ".tmp", go, overwrite: true);
            Log.Information("Using the pre-started pol.exe (pid {Pid})", pid);
            return pid;
        }
        catch { EndPrestarted(pre); return null; }
    }

    private Prestarted? TakePrestart(CancellationToken ct = default)
    {
        Task<Prestarted?>? task;
        lock (_gate) { task = _prestart; _prestart = null; }
        if (task is null) return null;
        try
        {
            if (task.Wait(8000, ct)) return task.Result;
        }
        catch (OperationCanceledException)
        {
            GiveBack(task);
            return null;
        }
        EndWhenDone(task);
        return null;
    }

    // kept for the next login, unless another one, a session or a hold has turned up meanwhile
    private void GiveBack(Task<Prestarted?> task)
    {
        lock (_gate)
        {
            if (!_disposed && MayPrestart())
            {
                _prestart = task;
                return;
            }
        }
        EndWhenDone(task);
    }

    private static void EndWhenDone(Task<Prestarted?> task) =>
        task.ContinueWith(t => { if (t.Result is { } late) EndPrestarted(late); }, TaskContinuationOptions.OnlyOnRanToCompletion);

    private static void EndPrestarted(Prestarted pre)
    {
        try { File.WriteAllText(HookFiles.PathFor(pre.Pid, "stop"), "stop"); } catch { }
        KillIfOurs(pre.Pid, p => IsSameProcess(p, pre.Started), graceMs: 3000);
    }

    public void StopPrestart()
    {
        try { if (TakePrestart() is { } pre) EndPrestarted(pre); }
        catch { }
    }

    // a repair, an install or a Large Address Aware write changes viewer files, so nothing pre-starts pol.exe until
    // every hold's let go (a sign-out's own Prestart included). They nest
    public void HoldPrestarts()
    {
        lock (_gate) _prestartHolds++;
        StopPrestart();
    }

    public void ReleasePrestarts()
    {
        if (LetGoPrestarts()) Prestart();
    }

    // true when that was the last hold
    internal bool LetGoPrestarts()
    {
        lock (_gate)
        {
            if (_prestartHolds == 0)
            {
                Log.Warning("Pre-starts were let go with none held");
                return false;
            }
            return --_prestartHolds == 0;
        }
    }

    private static bool LogHas(string path, string text)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Contains(text, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    internal static readonly TimeSpan PolLaunchLimit = TimeSpan.FromSeconds(60);

    private static (int ExitCode, string Out, string Err)? RunLauncher(Process launcher, TimeSpan limit)
    {
        // both pipes, a full stderr pipe would block pol_launch forever
        var outTask = launcher.StandardOutput.ReadToEndAsync();
        var errTask = launcher.StandardError.ReadToEndAsync();
        if (!launcher.WaitForExit(limit)) { EndStuckLauncher(launcher, limit); return null; }
        return (launcher.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    // Smart App Control refuses a new unsigned build till Windows' cloud check knows it, so that gets retried. Null once
    // progress has said why
    private int? StartWithPolLaunch(ProcessStartInfo psi, IProgress<LaunchProgress>? progress, CancellationToken ct, TimeSpan limit, string failText)
    {
        for (int attempt = 1; ; attempt++)
        {
            var attemptStart = DateTime.UtcNow;
            var (outcome, launchedPid, failure) = RunPolLaunch(psi, ct, limit, failText);
            if (outcome == LaunchOutcome.Started) return launchedPid;
            if (outcome == LaunchOutcome.Cancelled)
            {
                progress?.Report(new LaunchProgress("Stopped", "The login was cancelled."));
                return null;
            }
            bool refused = outcome == LaunchOutcome.Refused || CodeIntegrity.Refused(attemptStart, "pol_launch.exe", "pol_hook.dll");
            if (refused && attempt <= RefusedRetryWaits.Length)
            {
                var wait = RefusedRetryWaits[attempt - 1];
                Log.Warning("Windows Code Integrity refused pol_launch.exe or pol_hook.dll (attempt {Attempt}); trying again in {Wait}", attempt, wait);
                progress?.Report(new LaunchProgress("Starting", $"Windows is checking Provenance's files. Trying again in {wait.TotalSeconds:0} seconds...", LaunchSteps.StartPol, 0.02));
                if (ct.WaitHandle.WaitOne(wait))
                {
                    progress?.Report(new LaunchProgress("Stopped", "The login was cancelled."));
                    return null;
                }
                continue;
            }
            if (refused) Log.Error("Windows Code Integrity refused pol_launch.exe or pol_hook.dll {Attempts} times", attempt);
            progress?.Report(new LaunchProgress("Failed", refused ? CodeIntegrity.RefusedText : failure));
            return null;
        }
    }

    // A cancel waits for pol_launch (its steps time out on their own) then ends its pol.exe. Killing it half way would
    // leave a suspended pol.exe behind.
    private (LaunchOutcome Outcome, int Pid, string Failure) RunPolLaunch(ProcessStartInfo psi, CancellationToken ct, TimeSpan limit, string failText)
    {
        try
        {
            using var launcher = StartProcess(psi) ?? throw new InvalidOperationException("pol_launch did not start");
            var launcherStarted = launcher.StartTime;   // read now, it can throw once the process exits
            if (RunLauncher(launcher, limit) is not { } run)
                return ct.IsCancellationRequested
                    ? (LaunchOutcome.Cancelled, 0, "")
                    : (LaunchOutcome.Failed, 0, failText);
            var m = PidLine.Match(run.Out);
            if (ct.IsCancellationRequested)
            {
                if (m.Success) EndNewPol(int.Parse(m.Groups[1].Value), launcherStarted);
                return (LaunchOutcome.Cancelled, 0, "");
            }
            var stderr = run.Err.Trim();
            if (run.ExitCode != 0 || !m.Success)
            {
                Log.Error("pol_launch exited {Code}: {Error}", run.ExitCode, stderr.Length > 0 ? stderr : "no message");
                return (LaunchOutcome.Failed, 0, failText);
            }
            return (LaunchOutcome.Started, int.Parse(m.Groups[1].Value), "");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == CodeIntegrity.PolicyViolation)
        {
            Log.Warning(ex, "Windows refused to start pol_launch.exe");
            return (LaunchOutcome.Refused, 0, "");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Hold launch failed");
            return (LaunchOutcome.Failed, 0, failText);
        }
    }

    private const string PolDidNotStart = "PlayOnline didn't start. Try again in a moment.";
    private const string WindowerDidNotStart = "PlayOnline didn't start with Windower. Try again in a moment, and if it keeps happening, check for Windower updates in Settings, under Windower/Ashita.";

    private static void EndNewPol(int pid, DateTime launcherStarted)
    {
        bool NewPol(Process p) =>
            p.ProcessName.Equals("pol", StringComparison.OrdinalIgnoreCase) && p.StartTime >= launcherStarted;
        if (KillIfOurs(pid, NewPol, afterMs: 2000))
            Log.Information("Ended pol.exe (pid {Pid}): the sign-in was cancelled", pid);
    }

    private static void EndStuckLauncher(Process launcher, TimeSpan limit)
    {
        int id = launcher.Id;
        DateTime started;
        try { started = launcher.StartTime; } catch { started = DateTime.MinValue; }
        try { launcher.Kill(); } catch { }
        foreach (var child in ProcessTree.ChildrenOf(id))
            if (started != DateTime.MinValue) EndNewPol(child, started);
        Log.Warning("pol_launch (pid {Pid}) did not exit in {Limit} and was ended", id, limit);
    }

    private static bool KillIfOurs(int pid, Func<Process, bool> ours, int graceMs = 0, int afterMs = 0)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!ours(p)) return false;
            if (graceMs > 0 && p.WaitForExit(graceMs))
            {
                PutScreenSaverBack();
                return false;
            }
            p.Kill();
            p.WaitForExit(Math.Max(afterMs, 1000));
            PutScreenSaverBack();
            return true;
        }
        catch { return false; }
    }

    // No recorded start time means no proof, so it's never ours to kill (the user may run their own pol.exe on a
    // reused pid)
    private static bool IsSameProcess(Process p, DateTime? started)
    {
        if (started is not { } s) return false;
        try { return p.StartTime.ToUniversalTime() == s.ToUniversalTime() && PolProcess.IsViewerPol(p); }
        catch { return false; }
    }

    internal static SessionFile? ReadRecord(string path) => JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(path));

    // write then move, another copy may read it at any moment
    internal static void WriteRecord(string path, string tmp, int pid, long startTicks, string launchId,
                                     string? profileId, bool played = false, bool lean = false, WindowerGameValues? windower = null)
    {
        using var me = Process.GetCurrentProcess();
        File.WriteAllText(tmp, JsonSerializer.Serialize(
            new SessionFile(pid, startTicks, launchId, profileId, me.Id, PolProcess.StartUtcTicks(me), played, lean,
                            windower?.WindowerDir, windower?.Profile.Name, windower is { } w ? (int)w.Region : -1)));
        File.Move(tmp, path, overwrite: true);
    }

    private static List<string> SessionFilePaths()
    {
        var files = new List<string>();
        try { if (Directory.Exists(SessionDir)) files.AddRange(Directory.GetFiles(SessionDir, "*.json")); } catch { }
        return files;
    }

    private void SaveSession(string? profileId, bool played = false)
    {
        try
        {
            if (Pid is not { } pid || _pidStarted is not { } st || _launchId is null) return;
            _sessionProfile = profileId;
            Directory.CreateDirectory(SessionDir);
            var path = Path.Combine(SessionDir, $"{pid}.json");
            WriteRecord(path, $"{path}.{Environment.ProcessId}.tmp", pid, st.ToUniversalTime().Ticks, _launchId,
                        profileId, played, _sessionLean, Volatile.Read(ref _windowerPlay));
            _sessionFile = path;
            ReleaseClaim();
        }
        catch { }
    }

    public bool TryClaimAccount(string profileId, out string? problem)
    {
        problem = null;
        try
        {
            using var claim = AdminLock.TryAcquire(SessionClaimLock, TimeSpan.FromSeconds(5));
            if (claim is null) { problem = "Another copy of Provenance is busy logging in. Try again in a moment."; return false; }
            if (HeldByAnotherCopy(profileId))
            {
                // multi-account mode lets go of its games, so a game's record might have no live copy
                problem = GameRunningFor(profileId)
                    ? "FINAL FANTASY XI is already running on this account. Log in again once you've closed that game."
                    : "Another copy of Provenance is already logged in to this account, or logging in. Use that copy, or log out there first.";
                return false;
            }
            ReleaseClaim();
            var path = Path.Combine(SessionDir, $"claim-{Environment.ProcessId}.json");
            WriteRecord(path, path + ".tmp", 0, 0, "", profileId);
            _claimFile = path;
            return true;
        }
        catch (Exception ex)
        {
            // can't check means not safe, a sign-in here could take another copy's session over
            Log.Warning(ex, "Could not claim account {Account}", profileId);
            problem = "Provenance couldn't check whether another copy of Provenance is using this account. Try again in a moment.";
            return false;
        }
    }

    public void ReleaseClaim()
    {
        if (_claimFile is { } f) DeleteQuietly(f);
        _claimFile = null;
    }

    private void ClearSession()
    {
        if (_sessionFile is { } f) DeleteQuietly(f);
        _sessionFile = null;
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static bool IsAlive(int pid, long startUtcTicks)
    {
        if (pid <= 0) return false;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited && PolProcess.StartUtcTicks(p) == startUtcTicks; }
        catch { return false; }
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    public bool TryReattach(out string? profileId, string? wantProfile = null)
    {
        profileId = null;
        try
        {
            using var claim = AdminLock.TryAcquire(SessionClaimLock, TimeSpan.FromSeconds(3));
            // never without the lock, two copies starting together would take one session
            if (claim is null) return false;
            var files = Directory.Exists(SessionDir)
                ? new DirectoryInfo(SessionDir).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName).ToList()
                : [];
            foreach (var path in files)
            {
                SessionFile? s;
                try { s = ReadRecord(path); }
                catch { continue; }   // maybe another copy's mid-replace, never delete it
                if (s is null) continue;
                if (s.OwnerPid != Environment.ProcessId && IsAlive(s.OwnerPid, s.OwnerStartTicks)) continue;
                if (!IsAlive(s.Pid, s.StartTicks) || !IsPol(s.Pid))
                {
                    DeleteQuietly(path);
                    continue;
                }
                if (s.Played) continue;
                if (s.Lean || !File.Exists(SocialSnapshot.PathForPid(s.Pid)))
                {
                    // its copy died before the game started, so it holds the account for nobody
                    EndOrphan(s.Pid, s.StartTicks);
                    DeleteQuietly(path);
                    continue;
                }
                if (wantProfile != null && s.ProfileId != wantProfile) continue;
                DateTime started;
                using (var p = Process.GetProcessById(s.Pid)) started = p.StartTime;
                lock (_gate)
                {
                    Pid = s.Pid;
                    _pidStarted = started;
                    _launchId = s.LaunchId;
                    _played = false;
                    _sessionLean = false;
                }
                profileId = s.ProfileId;
                ReattachWindower(s.WindowerDir, s.WindowerProfile, s.WindowerRegion);
                SaveSession(s.ProfileId);
                StartWatcher(s.Pid);
                ThreadPool.QueueUserWorkItem(_ => ReadSnapshot());
                Log.Information("Re-attached to the held PlayOnline session (pid {Pid})", s.Pid);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Re-attach failed");
            return false;
        }
    }

    public static bool HeldByAnotherCopy(string profileId)
    {
        foreach (var path in SessionFilePaths())
        {
            try
            {
                if (ReadRecord(path) is not { } f || f.ProfileId != profileId) continue;
                if (f.Pid != 0 && !IsAlive(f.Pid, f.StartTicks)) continue;
                if (f.Played) return true;
                if (f.OwnerPid != Environment.ProcessId && IsAlive(f.OwnerPid, f.OwnerStartTicks)) return true;
            }
            catch { }
        }
        return false;
    }

    public static bool GameRunningFor(string profileId) => SessionFilePaths().Any(path =>
    {
        try
        {
            return ReadRecord(path) is { Played: true } f && f.ProfileId == profileId && IsAlive(f.Pid, f.StartTicks);
        }
        catch { return false; }
    });

    private static bool IsPol(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return PolProcess.IsViewerPol(p); }
        catch { return false; }
    }

    private static void EndOrphan(int pid, long startUtcTicks)
    {
        if (KillIfOurs(pid, p => PolProcess.StartUtcTicks(p) == startUtcTicks && PolProcess.IsViewerPol(p), 0, 3000))
            Log.Warning("Ended pol.exe {Pid}: its copy of Provenance ended before the game started", pid);
    }

    private void ReadSnapshot()
    {
        if (Pid is not { } pid) return;
        // the store file only turns up once, at login, and goes once the app has it
        if (SocialSnapshot.TryRead(pid) is { } snap)
            SnapshotUpdated?.Invoke(snap with { Stored = SocialSnapshot.TryReadStore(pid) });
    }

    public void Poll()
    {
        if (Pid is not { } pid) return;
        if (_played || IsAlive(pid))
        {
            if (_watcher == null) ThreadPool.QueueUserWorkItem(_ => ReadSnapshot());
            return;
        }
        if (!Monitor.TryEnter(_gate)) return;   // StopHold has it, so skip this tick rather than block the UI thread
        int? exit = null;
        try
        {
            if (Pid != pid) return;
            try { if (_polProc is { HasExited: true } gone && gone.Id == pid) exit = gone.ExitCode; } catch { }
            Log.Warning("Held PlayOnline session (pid {Pid}) ended on its own (exit code {Exit})", pid, exit);
            PutScreenSaverBack();
            DeleteQuietly(SocialSnapshot.PathForPid(pid));
            DeleteQuietly(SocialSnapshot.StorePathForPid(pid));
            Pid = null;
            _pidStarted = null;
        }
        finally { Monitor.Exit(_gate); }
        StopWatcher();
        ClearSession();
        // taken now so one that a new sign-in starts meanwhile is left alone
        var hook = TakeThirdPartyHook();
        Task.Run(() => EndThirdPartyHook(hook));
        string why = SessionClosedText(exit);
        var ended = HookFiles.PathFor(pid, "ended");
        try
        {
            if (File.Exists(ended) && int.TryParse(File.ReadAllText(ended).Trim(), out var reason))
                why = PolAccountNotices.SessionLost(reason);
            File.Delete(ended);
        }
        catch { }
        SessionEnded?.Invoke(why);
    }

    public void SignalPlay()
    {
        if (_launchId is null) return;
        // skip the log so far, only the answer to this Play counts and not an unreadable entry planted earlier
        if (Pid is { } pid)
            try { ReadNewLines(HookFiles.PathFor(pid, "log")); } catch (Exception ex) { Log.Warning(ex, "Could not read the hook log before Play"); }
        PutWindowerValuesBack();
        // saved first, so a throw leaves the session held
        Wrapped.Save($"{LaunchKeyPrefix}{_launchId}:PLAY", "play", "go", sessionOnly: true);
        _played = true;
        StopWatcher();
        // the record stays as the game's, so no copy signs this account in on top of it
        SaveSession(_sessionProfile, played: true);
    }

    private void TrackThirdPartyHook(int pid, DateTime start)
    {
        lock (_hookGate)
        {
            _hookPid = pid;
            _hookStart = start;
        }
    }

    private (int Pid, DateTime Start)? TakeThirdPartyHook()
    {
        lock (_hookGate)
        {
            (int, DateTime)? taken = _hookPid is { } pid ? (pid, _hookStart) : null;
            _hookPid = null;
            return taken;
        }
    }

    // Ashita-cli closes by itself once the game's up, so it only gets a couple of secs before it's ended
    private static void EndThirdPartyHook((int Pid, DateTime Start)? hook)
    {
        if (hook is not { } a) return;
        try
        {
            using var p = Process.GetProcessById(a.Pid);
            if (p.StartTime != a.Start || p.HasExited) return;
            if (!p.WaitForExit(2000)) { p.Kill(); Log.Information("Ended {Name} (pid {Pid}): the game was not started", p.ProcessName, a.Pid); }
        }
        catch { /* already gone */ }
    }

    public void StopHold()
    {
        StopWatcher();
        // the gate serialises this with ConnectCore's pid hand-over, so a slow stop can't clear a new session's pid
        lock (_gate)
        {
            if (_played) return;
            ClearSession();
            if (Pid is { } pid)
            {
                var started = _pidStarted;
                try { File.WriteAllText(HookFiles.PathFor(pid, "stop"), "stop"); } catch { }
                KillIfOurs(pid, p => IsSameProcess(p, started), graceMs: 4000, afterMs: 2000);
                DeleteQuietly(SocialSnapshot.PathForPid(pid));
                DeleteQuietly(SocialSnapshot.StorePathForPid(pid));
                Log.Information("Stopped held PlayOnline session (pid {Pid})", pid);
                Pid = null;
                _pidStarted = null;
            }
            EndThirdPartyHook(TakeThirdPartyHook());
        }
    }

    public void Dispose()
    {
        // no sweep touches the store once this returns
        _disposed = true;
        try
        {
            using var sweepsDone = new ManualResetEvent(false);
            if (_sweepTimer.Dispose(sweepsDone)) sweepsDone.WaitOne();
        }
        catch (Exception ex) { Log.Warning(ex, "Stopping the sweep of leftover sign-in credentials failed"); }
        try
        {
            try { StopPrestart(); }
            finally { StopHold(); }
        }
        finally
        {
            string[] ids;
            lock (_gate) { ids = _usedLaunchIds.ToArray(); _usedLaunchIds.Clear(); }
            foreach (var id in ids)
            {
                try { _store.DeleteByPrefix($"{LaunchKeyPrefix}{id}:"); }
                catch (Exception ex) { Log.Warning(ex, "Removing the one-shot sign-in credentials of a launch failed"); }
            }
        }
    }

    public void GameExited()
    {
        PutScreenSaverBack();
        lock (_gate)
        {
            if (!_played) return;
            Pid = null;
            _pidStarted = null;
            _played = false;
        }
        ClearSession();
        TakeThirdPartyHook();
    }

    // multi-account mode: forget the played session, the game and its record stay as they are
    public void ReleasePlayedSession()
    {
        lock (_gate)
        {
            if (!_played) return;
            StopWatcher();
            Log.Information("Let go of the played session (pid {Pid}); the game keeps running", Pid);
            Pid = null;
            _pidStarted = null;
            _launchId = null;
            _played = false;
            _sessionFile = null;
            _sessionProfile = null;
            _sessionLean = false;
            _polProc?.Dispose();
            _polProc = null;
            lock (_hookGate) _hookPid = null;
        }
    }

    internal string AdoptPlayedForTest(int pid, DateTime started, string profileId,
                                       (int Pid, DateTime Start)? hook = null, bool lean = false)
    {
        var launchId = "test" + Guid.NewGuid().ToString("N")[..8];
        lock (_gate)
        {
            Pid = pid;
            _pidStarted = started;
            _launchId = launchId;
            _played = true;
            _sessionLean = lean;
        }
        if (hook is { } h)
            lock (_hookGate)
            {
                _hookPid = h.Pid;
                _hookStart = h.Start;
            }
        SaveSession(profileId, played: true);
        return launchId;
    }

    public Task<string?> WaitForPlayAnswerAsync(TimeSpan timeout) => Task.Run(() =>
    {
        if (Pid is not { } pid) return null;
        var logPath = HookFiles.PathFor(pid, "log");
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            bool alive = IsAlive(pid);
            // it can log its answer and end between two reads, so read what's left once it's gone
            foreach (var line in alive ? ReadNewLines(logPath) : ReadRemainingLines(logPath))
            {
                switch (PlayAnswerOf(line))
                {
                    case PlayAnswer.Started:
                        return null;
                    case PlayAnswer.Unreadable:
                        Log.Error("The hook could not read the Play entry (pol.exe {Pid}): the game did not start; ending the session", pid);
                        DropKey();
                        EndUnstartedPlay();
                        return PlayUnreadableText;
                    case PlayAnswer.Refused:
                        Log.Warning("The hook refused Play (pol.exe {Pid}): {Line}; the game did not start, ending the session", pid, line.Trim());
                        EndUnstartedPlay();
                        return PlayRefusedText;
                }
            }
            if (!alive) return null;
            // a held pol.exe never exits by itself, so without this the wait would never end
            if (DateTime.UtcNow >= until)
            {
                Log.Error("No answer from the hook to Play within {Seconds} s (pol.exe {Pid}): the game did not start; ending the session",
                          timeout.TotalSeconds, pid);
                EndUnstartedPlay();
                return PlayUnreadableText;
            }
            Thread.Sleep(150);
        }
    });

    private void EndUnstartedPlay()
    {
        // the Play entry goes first, a hook that wakes late checks Play before the stop file and would boot the game
        if (_launchId is { } id)
        {
            try { _store.Delete($"{LaunchKeyPrefix}{id}:PLAY"); }
            catch (Exception ex) { Log.Warning(ex, "Could not remove the Play entry of the game that did not start"); }
        }
        lock (_gate) _played = false;
        StopHold();
    }

    public async Task WaitForExitAsync()
    {
        if (Pid is not { } pid) return;
        try { using var p = Process.GetProcessById(pid); await p.WaitForExitAsync(); }
        catch (ArgumentException) { }
    }

    private void CleanupOneShots(List<string> keys)
    {
        foreach (var k in keys)
        {
            try { _store.Delete(k); } catch { }
        }
        keys.Clear();
    }

    // poor man's tail -f
    private List<string> ReadNewLines(string path)
    {
        var lines = new List<string>();
        if (!File.Exists(path)) return lines;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _logOffset) _logOffset = 0;
            fs.Seek(_logOffset, SeekOrigin.Begin);
            var buf = new byte[fs.Length - _logOffset];
            int read = fs.Read(buf, 0, buf.Length);
            _logOffset += read;
            _pending.Append(Encoding.ASCII.GetString(buf, 0, read));
        }
        catch (IOException) { return lines; }
        var text = _pending.ToString();
        int last = text.LastIndexOf('\n');
        if (last < 0) return lines;
        lines.AddRange(text[..last].Split('\n').Select(l => l.TrimEnd('\r')));
        _pending.Clear();
        _pending.Append(text[(last + 1)..]);
        return lines;
    }

    private List<string> ReadRemainingLines(string path)
    {
        var lines = ReadNewLines(path);
        if (_pending.Length > 0) lines.Add(_pending.ToString().TrimEnd('\r'));
        _pending.Clear();
        return lines;
    }
}
