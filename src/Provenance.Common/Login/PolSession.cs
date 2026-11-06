using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Provenance.Common.Credentials;
using Provenance.Common.Game;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;
using Provenance.Common.Hook;
using Provenance.Common.IO;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Common.Progress;
using Provenance.Common.Security;
using Provenance.Common.Settings;
using Provenance.Common.Social;
using Provenance.Common.Win32;
using Serilog;

namespace Provenance.Common.Login;

internal delegate IDisposable? HoldViewerFiles(string viewerDir, out string? problem);

public sealed class PolSession : IDisposable
{
    public const string DetailsAgainText = "Provenance needs this account's login details again. Press Edit account to add them.";

    // POL's password box stops at 16 and polcore has no room for more, a longer one gets refused before pol.exe sees it
    public const int MaxPolPasswordLength = 16;
    public const string PolPasswordTooLongText =
        "Your saved PlayOnline password is longer than 16 characters, which PlayOnline doesn't allow. Press Edit account and enter it again.";
    public const string PlayUnreadableText = "Provenance couldn't start the game. Close Provenance and open it again.";
    public const string PlayRefusedText = "PlayOnline didn't let the game start. Log in again and try once more.";
    public const string PlaySessionClosedText = "Your PlayOnline session closed before the game could start. Log in again and try once more.";
    public const string PrivateFolderText = "Provenance couldn't use its own folder. Restart Provenance and try again.";
    public const string UnsafeStartText = "Provenance couldn't start PlayOnline safely. Restart Provenance and try again.";
    public const string RefusedText = "PlayOnline didn't accept your login details. Check your PlayOnline ID, passwords and one-time password.";

    public const string GameStartFailedText = "FINAL FANTASY XI couldn't start. Try again, and if it keeps happening, check the game's files with Repair.";
    public const string NoPolHandleText = "This PlayOnline account doesn't have a handle, and none was created.";

    public const string UnfinishedText = "The PlayOnline login didn't finish. Try again.";

    internal static (int Code, string Text) EndedBeforeLogIn(int? exitCode) => (HookExit?)exitCode switch
    {
        HookExit.DetailsUnreadable => (LoginCodes.DetailsUnreadable, DetailsAgainText),
        HookExit.NoHandle => (LoginCodes.NoPolHandle, NoPolHandleText),
        HookExit.BadIpcFolder => (LoginCodes.LauncherFailed, PrivateFolderText),
        HookExit.UnsafeStart => (LoginCodes.LauncherFailed, UnsafeStartText),
        HookExit.GameStartFailed => (LoginCodes.LauncherFailed, GameStartFailedText),
        HookExit.Refused => (LoginCodes.AuthFailed, RefusedText),
        HookExit.TurnedAway => (LoginCodes.AccountBlocked, "PlayOnline turned this account away. Log in with the PlayOnline Viewer to see why."),
        HookExit.ListLoadFailed => (LoginCodes.Unfinished, LoadFailedText("")),
        _ => (LoginCodes.Unfinished, "PlayOnline closed before the login finished."),
    };

    internal static string SessionClosedText(int? exitCode) => (HookExit?)exitCode switch
    {
        HookExit.BadIpcFolder => PrivateFolderText,
        HookExit.UnsafeStart => UnsafeStartText,
        HookExit.GameStartFailed => GameStartFailedText,
        _ => "Your PlayOnline session has closed. Log in again to see your friends and messages.",
    };

    private const string LaunchKeyPrefix = "LAUNCH:";

    // the room password's one-shot entry, and the full target the hook gets as POL_CRED_CHAT
    internal static string ChatKey(string launchId) => $"{LaunchKeyPrefix}{launchId}:CHAT";
    internal static string ChatTarget(string launchId) => $"{WindowsCredentialStore.DefaultPrefix}:{ChatKey(launchId)}";
    private const string SessionClaimLock = "session-claim";
    private const string PrestartLock = "prestart";
    private static readonly TimeSpan[] RefusedRetryWaits = [TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60)];
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(10);

    private enum LaunchOutcome { Started, Failed, Refused, Cancelled }

    internal sealed record Prestarted(int Pid, ProcessStamp? Stamp);

    // Pid 0 is a claim, start times are UTC ticks. The property names are the file's format, older copies read it too
    private sealed record SessionFile(int Pid, long StartTicks, string? ProfileId, int OwnerPid = 0, long OwnerStartTicks = 0,
                                      bool Played = false)
    {
        [JsonIgnore] public ProcessStamp Game => new(Pid, StartTicks);
        [JsonIgnore] public ProcessStamp Owner => new(OwnerPid, OwnerStartTicks);
    }

    // admin-only, so nothing running as the user can plant a record that makes the app end a process. Only a good
    // answer's kept, a failed check gets tried again next time
    private static readonly object SessionDirGate = new();
    private static string? _sessionDir;
    private static string SessionDir
    {
        get { lock (SessionDirGate) return _sessionDir ??= ProtectedFolders.SubDir("sessions"); }
    }

    internal static long _dnsWarmedAt;

    private readonly ISettingsService _settings;
    private readonly WindowsCredentialStore _store;
    private readonly WrappedStoreCache<WindowsCredentialStore> _wrapped;
    private readonly Action? _dropKey;
    private readonly System.Threading.Timer _sweepTimer;
    private readonly HashSet<string> _usedLaunchIds = [];
    private readonly object _gate = new();
    private readonly object _hookGate = new();
    private volatile bool _disposed;
    private int _sweepRuns;
    internal string? _launchId;
    private HookLog? _hookLog;
    internal bool _played;   // the held pol.exe is the game now, never kill it
    private ProcessStamp? _polStamp;
    internal Process? _polProc;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    internal Task<Prestarted?>? _prestart;
    private int _prestartHolds;   // under _gate
    private string? _sessionFile;
    private string? _sessionProfile;
    private string? _claimFile;
    private ProcessStamp? _hook;
    private WindowerGameValues? _windowerPlay;

    public PolSession(ISettingsService settings)
        : this(settings, new WindowsCredentialStore(), () => new WindowsCredentialStore(wrapKey: CredentialKey.Open()),
               SweepInterval, CredentialKey.Reload) { }

    internal PolSession(ISettingsService settings, WindowsCredentialStore store,
                               Func<WindowsCredentialStore> openWrapped, TimeSpan sweepEvery,
                               Action? dropKey = null)
    {
        _settings = settings;
        _store = store;
        _wrapped = new WrappedStoreCache<WindowsCredentialStore>(openWrapped);
        _dropKey = dropKey;
        FindLaunchFiles = () => LaunchFiles(Region);
        GameIsInstalled = () => GameInstalled(Region);
        UpdateViewer = (dir, progress, freeFiles, ct) => PolUpdates.UpdateViewer(dir, Region, progress, freeFiles, ct);
        WindowerSetup = (polExe, profile, lists, shared) => WindowerLauncher.SetupFor(_settings, polExe, profile, lists, shared);
        // no recorded start time means no proof, so it's never mine (the user may run their own pol.exe on a reused pid)
        PrestartAlive = pre => pre.Stamp is { } stamp && Ours(stamp);
        DeleteLeftovers();
        _sweepTimer = new System.Threading.Timer(_ => SweepLeftovers(), null, sweepEvery, sweepEvery);
    }

    public int? Pid { get; internal set; }

    // everything a pol.exe start (or ending an earlier copy's) reaches outside this class, so they can be checked
    // offline with stand-ins
    internal Func<(string? ViewerDir, string? PolExe, string LauncherExe)> FindLaunchFiles { get; init; }
    internal Func<bool> GameIsInstalled { get; init; }
    internal Func<bool> ViewerPolRunning { get; init; } = PolUpdates.ViewerRunning;
    internal Func<string, IProgress<PatchProgress>?, Action, CancellationToken, PatchResult> UpdateViewer { get; init; }
    internal Func<ProcessStartInfo, Process?> StartProcess { get; init; } = Process.Start;
    internal Func<bool> AnyPolRunningNow { get; init; } = AnyPolRunning;
    internal Func<bool?> WindowsScreenSaver { get; init; } = ReadScreenSaver;
    internal Func<string, string?, IReadOnlyDictionary<string, WindowerAddonList>?, WindowerAddonList?, WindowerLaunchSetup> WindowerSetup { get; init; }
    internal HoldViewerFiles HoldViewer { get; init; } = ViewerBuild.Hold;
    internal Func<Prestarted, bool> PrestartAlive { get; init; }
    internal Func<ProcessStamp, bool> IsViewerPol { get; init; } = stamp => stamp.IsViewerPol();
    internal Action ScreenSaverBack { get; init; } = PutScreenSaverBack;
    // quiet this long and the login's stuck. polcore fails a list load or a login step at 100 s by itself, apart from
    // state 23, which it sits in for 400 s
    internal TimeSpan StallLimit { get; init; } = TimeSpan.FromSeconds(260);
    // the hook only looks for the stop file every 150 ms, then gives writes under way 5 s before it logs out
    internal static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(6);

    public event Action<PolSocialSnapshot>? SnapshotUpdated;

    public event Action<string>? SessionEnded;

    public int? LastRefusalReason { get; private set; }

    public ThirdPartyHook Platform => _settings.Current.ThirdPartyHookPlatform;

    internal int SweepRuns => Volatile.Read(ref _sweepRuns);

    private WindowsCredentialStore Wrapped => _wrapped.Get();

    // a login or pre-start only gets this far on a supported region
    private PolRegion Region => _settings.DetectedRegion ?? PolRegion.UnitedStates;

    // the key's opened before anything starts, so one that can't be read gets its own text
    internal string? KeyForLogIn()
    {
        try
        {
            _ = Wrapped;
            return null;
        }
        catch (CredentialKeyUnavailableException) { return CredentialKey.UnavailableText; }
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

    public async Task<int> ConnectAsync(PolAccount pol, SquareEnixAccount squareEnix, string otp, string? rememberedProfileId,
                                        IProgress<LaunchProgress>? progress, Func<string?>? askHandleName,
                                        CancellationToken ct = default, SessionOptions? options = null)
    {
        // no gate here, StopHold can hold it for seconds and this is the UI thread
        if (Volatile.Read(ref _played))
            throw Shown.Mark(new InvalidOperationException("Provenance is still finishing your last game. Close Provenance and open it again, then log in."));
        LastRefusalReason = null;
        var launch = ForLaunch(options);
        int rc = LoginCodes.LauncherFailed;
        try
        {
            rc = await Task.Run(() => ConnectCore(pol, squareEnix, otp, rememberedProfileId, progress, askHandleName, launch, ct), ct);
            return rc;
        }
        finally { if (rc != 0) ReleaseClaim(); }
    }

    // on the UI thread, where Settings changes the live addon lists while the login carries on in the background
    internal SessionOptions ForLaunch(SessionOptions? options) =>
        (options ?? new SessionOptions(false, null)) with
        {
            WindowerLists = WindowerLauncher.Snapshot(_settings.Current.WindowerAddonLists),
            WindowerShared = _settings.Current.WindowerSharedExtras?.Copy(),
        };

    private int ConnectCore(PolAccount pol, SquareEnixAccount squareEnix, string otp, string? rememberedProfileId,
                            IProgress<LaunchProgress>? progress, Func<string?>? askHandleName, SessionOptions options,
                            CancellationToken ct)
    {
        int Fail(string text, int code = LoginCodes.LauncherFailed)
        {
            progress?.Report(new LaunchProgress("Failed", text));
            return code;
        }
        _hookLog = null;
        _played = false;
        if (pol.Password.Length > MaxPolPasswordLength) return Fail(PolPasswordTooLongText, LoginCodes.DetailsUnreadable);
        var region = _settings.DetectedRegion;
        if (!PolPaths.Supported(region))
            return Fail(region is null
                ? "PlayOnline isn't installed." : $"Provenance doesn't support the {PolPaths.VersionName(region.Value)} version of PlayOnline yet.");
        var (viewerDir, polExe, launcherExe) = FindLaunchFiles();
        if (polExe is null)
            return Fail("Some of the files needed to start PlayOnline are missing. Check FINAL FANTASY XI is installed, and if it is, reinstall Provenance.");
        if (KeyForLogIn() is { } keyProblem) return Fail(keyProblem);
        // the viewer checks for its own update on every connect and always says yes
        try
        {
            UpdateViewer(viewerDir!, new InlineProgress<PatchProgress>(
                p => progress?.Report(LaunchProgress.Updating("Starting", LaunchSteps.StartPol, p, 0.02, LaunchProgress.LogInUpdateEnd))),
                FreeViewerFiles, ct);
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new LaunchProgress("Stopped", "The login was cancelled."));
            return LoginCodes.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PlayOnline Viewer update failed");
            return Fail(ErrorText.Of(ex, "The PlayOnline Viewer couldn't be updated. Try logging in again in a moment."));
        }
        // after the viewer's update and before the hold, which stops any write
        LargeAddressAwareBeforeStart(viewerDir!, "before the login");
        NoteScreenSaverNow();
        // the hook patches fixed offsets in pol.exe and polcore.dll, so both stay locked until the sign-in ends
        using var viewerHold = HoldViewer(viewerDir!, out var buildProblem);
        if (buildProblem != null)
        {
            Log.Warning("Viewer build not supported: {Problem}", buildProblem);
            return Fail(buildProblem);
        }
        _launchId = NewLaunchId();
        var keys = new List<string>();
        try
        {
            return ConnectHeld(pol, squareEnix, otp, rememberedProfileId, progress, askHandleName, polExe, launcherExe, keys,
                               options, ct);
        }
        finally
        {
            CleanupOneShots(keys);   // on every way out, so passwords never linger in Credential Manager
        }
    }

    private int ConnectHeld(PolAccount pol, SquareEnixAccount squareEnix, string otp, string? rememberedProfileId,
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
            return LoginCodes.Cancelled;
        }
        int Fail(int code, string detail)
        {
            if (code == LoginCodes.DetailsUnreadable) DropKey();
            progress?.Report(new LaunchProgress("Failed", detail));
            return code;
        }

        string polTarget, squareEnixTarget;
        var oneShot = new List<string> { "OTP" };
        var profile = string.IsNullOrWhiteSpace(rememberedProfileId) ? null : rememberedProfileId;
        if (profile != null && _store.ReadUserName($"{CredentialTargets.Pol}:{profile}") == pol.PolId &&
            _store.ReadUserName($"{CredentialTargets.Sea}:{profile}") == squareEnix.Username)
        {
            polTarget = Target($"{CredentialTargets.Pol}:{profile}");
            squareEnixTarget = Target($"{CredentialTargets.Sea}:{profile}");
        }
        else
        {
            polTarget = OneShot("POL", pol.PolId, pol.Password);
            squareEnixTarget = OneShot("SEA", squareEnix.Username, squareEnix.Password);
            oneShot.InsertRange(0, ["POL", "SEA"]);
        }
        var otpTarget = string.IsNullOrWhiteSpace(otp) ? null : OneShot("OTP", "otp", otp.Trim());

        var psi = LauncherStart(launcherExe, polExe);
        var env = psi.Environment;
        SetBaseEnvironment(env);
        // only the viewer's menu values, busy goes online
        var status = _settings.Current.PlayOnlineStatus;
        env[HookContract.Env.Status] = status is 0 or 1 or 4 ? status.ToString() : "0";
        env[HookContract.Env.CredPol] = polTarget;
        env[HookContract.Env.CredSea] = squareEnixTarget;
        env[HookContract.Env.CredOneshot] = string.Join(",", oneShot);
        env[HookContract.Env.CredHandle] = Target(Key("HANDLE"));
        env[HookContract.Env.CredPlay] = Target(Key("PLAY"));
        env[HookContract.Env.CredChat] = ChatTarget(_launchId!);
        keys.AddRange([Key("HANDLE"), Key("PLAY")]);
        if (otpTarget != null) env[HookContract.Env.CredOtp] = otpTarget;
        if (options.Lean) env[HookContract.Env.Lean] = "1";
        if (HomeSlots.Valid(options.HomeSlot))
            env[HookContract.Env.Home] = options.HomeSlot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SetHandleEnvironment(env, options.HandleKey);

        if (ct.IsCancellationRequested) return Stopped("The login was cancelled.");
        progress?.Report(new LaunchProgress("Starting", "Starting PlayOnline...", LaunchSteps.StartPol, 0.02));
        int pid;
        var platform = Platform;
        Volatile.Write(ref _windowerPlay, null);
        if (platform == ThirdPartyHook.Ashita)
        {
            var hookPid = AshitaLauncher.StartViaAshita(
                _settings, psi, polExe, _launchId!, options.ThirdPartyHookProfile, progress, ct,
                hookStarted: TrackThirdPartyHook);
            if (hookPid is null) return ct.IsCancellationRequested ? LoginCodes.Cancelled : LoginCodes.LauncherFailed;
            pid = hookPid.Value;
        }
        else if (platform == ThirdPartyHook.Windower)
        {
            if (StartThroughWindower(psi, polExe, options.ThirdPartyHookProfile, _launchId!, progress, ct, options.WindowerLists, options.WindowerShared) is not { } started)
                return ct.IsCancellationRequested ? LoginCodes.Cancelled : LoginCodes.LauncherFailed;
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
            if (StartWithWatcher(psi, progress, ct, WatcherLimit, PolDidNotStart) is not { } started)
                return ct.IsCancellationRequested ? LoginCodes.Cancelled : LoginCodes.LauncherFailed;
            pid = started;
        }
        lock (_gate)
        {
            Pid = pid;
            // kept so the exit code still says why if it dies mid sign-in
            _polProc?.Dispose();
            try { _polProc = Process.GetProcessById(pid); _ = _polProc.Handle; _polStamp = ProcessStamp.Of(pid); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { _polProc = null; _polStamp = null; }
        }
        // saved now, if this copy dies mid sign-in the next start ends this pol.exe
        SaveSession(rememberedProfileId);
        Log.Information("Hold session: pol.exe pid {Pid}", pid);
        progress?.Report(new LaunchProgress("Starting", "Starting PlayOnline...", LaunchSteps.StartPol, 0.04));
        // an old process with this pid left these. Not the go file, a pre-started pol.exe reads its sign-in from it
        HookFiles.ClearFor(pid);
        DropChatPassword();
        HookFiles.Delete(pid, HookContract.Files.Stop);
        HookFiles.Delete(pid, HookContract.Files.Ended);

        var logPath = HookFiles.PathFor(pid, HookContract.Files.Log);
        var hookLog = _hookLog = HookLog.OfThisRun(logPath);
        var progressOf = new HookProgress();
        var lastActivity = DateTime.UtcNow;
        int? viewerLanguage = null;
        int? Ending(HookEvent? e)
        {
            switch (e)
            {
                case ViewerStarted { Language: { } lang }:
                    // the language the hook gave polcore, the same one app.dll builds the jail page's address from
                    viewerLanguage = lang;
                    return null;
                case DetailsUnreadable:
                    return Fail(LoginCodes.DetailsUnreadable, DetailsAgainText);
                case AuthRefused refused:
                    LastRefusalReason = refused.Reason;
                    return Fail(LoginCodes.AuthFailed, LastRefusalReason is { } r
                        ? PolAccountNotices.LogInRefused(r, Region)
                        : RefusedText);
                case AccountBlocked blocked:
                    return Fail(LoginCodes.AccountBlocked, PolAccountNotices.Blocked(blocked.Notice, Region, viewerLanguage));
                case ListLoadFailed failed:
                    return Fail(LoginCodes.Unfinished, LoadFailedText(failed.List));
                case NoHandle:
                    return Fail(LoginCodes.NoPolHandle, NoPolHandleText);
                case HandleRefused:
                    return Fail(LoginCodes.HandleRejected, "PlayOnline didn't accept that handle name. Log in again to try a different one.");
                case NoSession:
                    return Fail(LoginCodes.Unfinished, UnfinishedText);
                default:
                    return null;
            }
        }
        while (!ct.IsCancellationRequested)
        {
            foreach (var line in hookLog.ReadNewLines())
            {
                lastActivity = DateTime.UtcNow;
                var e = HookLog.Parse(line);
                if (progressOf.Step(e) is { } step) progress?.Report(step);
                if (Ending(e) is { } result) return result;
                if (e is not HandleWanted) continue;
                progress?.Report(new LaunchProgress("PlayOnline handle", "This account needs a PlayOnline handle."));
                var name = askHandleName?.Invoke();
                Wrapped.Save(Key("HANDLE"), "handle", string.IsNullOrWhiteSpace(name) ? "" : name!.Trim(), sessionOnly: true);
                lastActivity = DateTime.UtcNow;
            }
            if (ct.IsCancellationRequested) break;   // a cancel that lands with the first snapshot still wins
            if (SocialSnapshotFiles.TryRead(pid) is { } snap)
            {
                // the hook writes the store file before its first snapshot, so it's there now even if Play comes next
                SnapshotUpdated?.Invoke(snap with { Stored = SocialSnapshotFiles.TryReadStore(pid) });
                StartWatcher(pid);
                SaveSession(rememberedProfileId);
                progress?.Report(new LaunchProgress("Connected", "You're logged in.", LaunchSteps.StartGame, 1.0));
                return 0;
            }
            if (!ProcessStamp.PidRunning(pid))
            {
                // it can log its refusal and exit between two reads
                foreach (var line in hookLog.ReadRemainingLines())
                    if (Ending(HookLog.Parse(line)) is { } result) return result;
                int? exit = null;
                try { if (_polProc is { HasExited: true } ended) exit = ended.ExitCode; }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
                PutScreenSaverBack();
                LogEarlyExit(pid, exit, logPath);
                var (endedCode, endedText) = EndedBeforeLogIn(exit);
                return Fail(endedCode, endedText);
            }
            if (DateTime.UtcNow - lastActivity > StallLimit)
                return Fail(LoginCodes.Stalled, "PlayOnline stopped responding during the login. Try again in a moment.");
            Thread.Sleep(250);
        }
        return Stopped("The login was cancelled, and PlayOnline has been closed.");
    }

    // null once progress has said why
    internal int? StartThroughWindower(ProcessStartInfo psi, string polExe, string? profile, string launchId,
                                       IProgress<LaunchProgress>? progress, CancellationToken ct,
                                       IReadOnlyDictionary<string, WindowerAddonList>? lists, WindowerAddonList? shared = null)
    {
        WindowerLauncher windower;
        try { windower = WindowerLauncher.Prepare(WindowerSetup(polExe, profile, lists, shared), progress, ct); }
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
        // held till the watcher is done, Hook.dll copies the mapping out before it signals
        using (windower)
        {
            windower.ApplyTo(psi, launchId);
            progress?.Report(new LaunchProgress("Starting", "Starting PlayOnline with Windower...", LaunchSteps.StartPol, 0.02));
            var pid = StartWithWatcher(psi, progress, ct, WindowerLauncher.WatcherLimit, WindowerDidNotStart);
            if (pid is not null) Volatile.Write(ref _windowerPlay, windower.GameValues);
            return pid;
        }
    }

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
            var crashes = HookLog.EventsOfThisRun(hookLog).OfType<Crashed>();
            foreach (var crash in crashes.TakeLast(3)) Log.Warning("pol_hook: {Line}", crash.Line.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string LoadFailedText(string list) =>
        $"PlayOnline didn't send {list switch { "status" => "your status", "friends" => "your friend list", "handles" => "your handles", "characters" => "your Content ID list", "groups" => "your friend groups", _ => "everything it needs at login" }}, so you've been logged out. Try again in a moment.";

    private static (string? ViewerDir, string? PolExe, string LauncherExe) LaunchFiles(PolRegion region)
    {
        var viewerDir = PolPaths.TryGetInstallFolder(region, PolPaths.ViewerProduct);
        var polExe = viewerDir is null ? null : Path.Combine(viewerDir, "pol.exe");
        var launcherExe = Path.Combine(HookFiles.NativeDir, HookFiles.WatcherExe);
        bool found = polExe != null && File.Exists(polExe) && File.Exists(launcherExe)
                     && File.Exists(Path.Combine(HookFiles.NativeDir, "pol_hook.dll"));
        return (viewerDir, found ? polExe : null, launcherExe);
    }

    private void LargeAddressAwareBeforeStart(string viewerDir, string when) =>
        LargeAddressAwareSwitch.Apply(_settings, new LargeAddressAwareSwitch.Target(viewerDir, ViewerPolRunning, () => null, () => null), when);

    // Provenance can sit in the tray for days, so the screen saver's read again before every pol.exe start
    private void NoteScreenSaverNow() => NoteScreenSaver(_settings, AnyPolRunningNow, WindowsScreenSaver);

    internal static ProcessStartInfo LauncherStart(string launcherExe, string polExe)
    {
        var psi = new ProcessStartInfo(launcherExe)
        {
            WorkingDirectory = HookFiles.NativeDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add(polExe);
        psi.ArgumentList.Add(Path.Combine(HookFiles.NativeDir, "pol_hook.dll"));
        return psi;
    }

    // with POL_OWNER the hook logs out once this copy's gone, unless the game's started
    internal static void SetBaseEnvironment(IDictionary<string, string?> env)
    {
        LaunchEnvironment.Reset(env);
        env[HookContract.Env.IpcDir] = HookFiles.RequiredDir;
        env[HookContract.Env.CredKeyfile] = CredentialKey.FilePath;
        env[HookContract.Env.Hold] = "1";
        env[HookContract.Env.Owner] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ScreenSaverAtStart is { } saver) env[HookContract.Env.Screensaver] = saver;
    }

    // pol.exe switches the screen saver off while it runs, so it's read before each start and the last pol.exe out puts
    // it back
    internal static string? ScreenSaverAtStart { get; set; }

    public static void NoteScreenSaver(ISettingsService settings) =>
        NoteScreenSaver(settings, AnyPolRunning, ReadScreenSaver, PolEndedUnseen, SetScreenSaver);

    private static bool AnyPolRunning()
    {
        var pols = Process.GetProcessesByName("pol");
        foreach (var p in pols) p.Dispose();
        return pols.Length > 0;
    }

    // Windows only has the real answer with no pol.exe about, so that's kept for starts while one runs. A settings.json
    // that didn't load is left for the user's next save
    internal static void NoteScreenSaver(ISettingsService settings, Func<bool> polRunning, Func<bool?> readWindows,
                                         Func<bool>? endedUnseen = null, Action<bool>? set = null)
    {
        bool? on;
        if (polRunning())
        {
            on = settings.Current.ScreenSaverWasOn ?? readWindows();
        }
        else
        {
            on = readWindows();
            // pol.exe only ever switches it off, and one that crashed with nothing watching never put it back
            if (on == false && settings.Current.ScreenSaverWasOn == true && set is not null && endedUnseen?.Invoke() == true)
            {
                Log.Information("A pol.exe ended while no copy of Provenance was watching, so the screen saver's been put back on");
                set(true);
                on = true;
            }
            if (on is { } fresh)
            {
                if (settings.LoadProblem is null) settings.SaveChange(s => s.ScreenSaverWasOn = fresh);
                else settings.Current.ScreenSaverWasOn = fresh;
            }
        }
        ScreenSaverAtStart = on is { } v ? (v ? "1" : "0") : null;
        SavedScreenSaver = settings.Current.ScreenSaverWasOn;
        if (on is null) Log.Warning("Could not read the screen saver setting, the hook reads it itself");
    }

    internal static bool? SavedScreenSaver { get; set; }

    // a pol.exe that crashed or got killed never put it back itself, and the next start's read has to be the real one
    internal static void PutScreenSaverBack() => PutScreenSaverBack(SavedScreenSaver, AnyPolRunning, SetScreenSaver);

    internal static bool PutScreenSaverBack(bool? wasOn, Func<bool> polRunning, Action<bool> set)
    {
        if (wasOn is not { } on || polRunning()) return false;
        set(on);
        return true;
    }

    private static bool? ReadScreenSaver() =>
        User32.SystemParametersInfoW(SpiGetScreenSaveActive, 0, out int on, 0) ? on != 0 : null;

    private static void SetScreenSaver(bool on) => User32.SystemParametersInfoW(SpiSetScreenSaveActive, on ? 1u : 0u, IntPtr.Zero, 0);

    // a record whose pol.exe and copy have both gone, so nothing saw that pol.exe end
    internal static bool PolEndedUnseen() => SessionFilePaths().Any(path =>
        TryReadRecord(path) is { Pid: > 0 } f && !f.Game.IsRunning() && !f.Owner.MightBeRunning());

    private const uint SpiGetScreenSaveActive = 0x10;
    private const uint SpiSetScreenSaveActive = 0x11;

    private void StartWatcher(int pid)
    {
        StopWatcher();
        _debounce = new Timer(_ => ReadSnapshot(), null, Timeout.Infinite, Timeout.Infinite);
        var w = new FileSystemWatcher(HookFiles.RequiredDir, Path.GetFileName(SocialSnapshotFiles.PathForPid(pid)))
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
        && Platform is not (ThirdPartyHook.Ashita or ThirdPartyHook.Windower)
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
            catch (System.Net.Sockets.SocketException) { }
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
            NoteScreenSaverNow();
            var psi = LauncherStart(launcherExe, polExe);
            SetPrestartEnvironment(psi.Environment);
            using var launcher = StartProcess(psi);
            if (launcher is null || RunLauncher(launcher, WatcherLimit) is not { } run) return null;
            if (run.ExitCode != 0 || HookLog.StartedPid(run.Out) is not { } pid) return null;
            // an older pol.exe with this pid left these, and its stop file would end this one before it logs in
            foreach (var name in new[] { HookContract.Files.Stop, HookContract.Files.Ended, HookContract.Files.Go }) HookFiles.Delete(pid, name);
            var stamp = ProcessStamp.Of(pid);
            // so another copy that's about to change the viewer's files can end it while it's only waiting
            if (stamp is { } at)
                try { File.WriteAllText(HookFiles.PathFor(pid, HookContract.Files.Prestart), at.StartUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warning(e, "Could not note the pre-started pol.exe for other copies"); }
            Log.Information("Pre-started pol.exe (pid {Pid}), waiting for LOG IN", pid);
            return new Prestarted(pid, stamp);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Pre-start failed; LOG IN starts pol.exe itself");
            return null;
        }
    }

    private static bool GameInstalled(PolRegion region) =>
        PolPaths.TryGetInstallFolder(region, PolPaths.FfxiProduct) is { } ffxi && File.Exists(Path.Combine(ffxi, "FFXiMain.dll"));

    // POL_IPC_DIR, POL_CRED_KEYFILE and POL_OWNER have to go in here, the go file never carries them
    internal static void SetPrestartEnvironment(IDictionary<string, string?> env)
    {
        SetBaseEnvironment(env);
        env[HookContract.Env.Prestart] = "1";
    }

    // the hook's key for a Handle ID is 11 hex digits, anything else from a hand-edited settings.json stays out
    internal static void SetHandleEnvironment(IDictionary<string, string?> env, string? key)
    {
        if (key is { Length: 11 } && key.All(char.IsAsciiHexDigit)) env[HookContract.Env.Handle] = key;
        else env.Remove(HookContract.Env.Handle);
    }

    internal static IEnumerable<string> GoFileEntries(IDictionary<string, string?> env)
    {
        foreach (var k in HookContract.Env.GoFile)
            if (env.TryGetValue(k, out var v) && v != null) yield return $"{k}={v}";
    }

    // nothing's sent till the go file's written, so a cancel before then leaves it waiting, hidden, for the next login.
    // Only a live one goes back, a dead one would block a fresh Prestart
    internal int? TakePrestarted(ProcessStartInfo psi, CancellationToken ct)
    {
        if (TakePrestart(ct) is not { } pre) return null;
        int pid = pre.Pid;
        try
        {
            if (!PrestartAlive(pre)) return null;
            if (ct.IsCancellationRequested) { GiveBack(Task.FromResult<Prestarted?>(pre)); return null; }
            var log = HookFiles.PathFor(pid, HookContract.Files.Log);
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < until && !PrestartIsReady(log) && !ct.IsCancellationRequested) Thread.Sleep(100);
            if (ct.IsCancellationRequested) { GiveBack(Task.FromResult<Prestarted?>(pre)); return null; }
            if (!PrestartIsReady(log)) { EndPrestarted(pre); return null; }
            // under the lock another copy ends waiting pre-starts with, so it's either been stopped or it's mine to use
            using (var handOver = AdminLock.TryAcquire(PrestartLock, TimeSpan.FromSeconds(5)))
            {
                if (handOver is null || File.Exists(HookFiles.PathFor(pid, HookContract.Files.Stop))) { EndPrestarted(pre); return null; }
                File.Delete(HookFiles.PathFor(pid, HookContract.Files.Prestart));
                var go = HookFiles.PathFor(pid, HookContract.Files.Go);
                AtomicFile.Write(go, string.Join("\n", GoFileEntries(psi.Environment)));
            }
            Log.Information("Using the pre-started pol.exe (pid {Pid})", pid);
            return pid;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { EndPrestarted(pre); return null; }
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

    private void EndWhenDone(Task<Prestarted?> task) =>
        task.ContinueWith(t => { if (t.Result is { } late) EndPrestarted(late); }, TaskContinuationOptions.OnlyOnRanToCompletion);

    private void EndPrestarted(Prestarted pre)
    {
        try { HookFiles.RequestStop(pre.Pid); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        HookFiles.Delete(pre.Pid, HookContract.Files.Prestart);
        if (pre.Stamp is { } stamp) EndOurs(stamp, graceMs: 3000);
    }

    public void StopPrestart()
    {
        try { if (TakePrestart() is { } pre) EndPrestarted(pre); }
        catch (AggregateException) { }
    }

    // a repair, an install or a Large Address Aware write changes viewer files, so nothing pre-starts pol.exe until
    // every hold's let go (a sign-out's own Prestart included). They nest
    public void HoldPrestarts()
    {
        lock (_gate) _prestartHolds++;
        FreeViewerFiles();
    }

    private void FreeViewerFiles()
    {
        StopPrestart();
        EndWaitingPrestarts();
    }

    // other copies' waiting pre-starts hold the viewer's files too (their next login just starts another). One already
    // being handed to a login has lost its record by the time this has the lock
    internal void EndWaitingPrestarts()
    {
        var ending = new List<ProcessStamp>();
        try
        {
            using var handOver = AdminLock.TryAcquire(PrestartLock, TimeSpan.FromSeconds(5));
            if (handOver is null)
            {
                Log.Warning("Other copies' pre-started pol.exe were left running: the hand-over lock stayed busy");
                return;
            }
            foreach (var record in Directory.GetFiles(HookFiles.RequiredDir, "pol_hook.*.prestart"))
            {
                try
                {
                    var parts = Path.GetFileName(record).Split('.');
                    if (parts.Length != 3 || !int.TryParse(parts[1], out var pid)
                        || !long.TryParse(File.ReadAllText(record).Trim(), out var ticks)) continue;
                    var stamp = new ProcessStamp(pid, ticks);
                    if (Ours(stamp))
                    {
                        HookFiles.RequestStop(pid);
                        ending.Add(stamp);
                    }
                    File.Delete(record);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(e, "Could not end the pre-started pol.exe of {Record}", Path.GetFileName(record));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warning(e, "Could not end other copies' pre-started pol.exe");
        }
        foreach (var stamp in ending)
        {
            Log.Information("Ending another copy's pre-started pol.exe (pid {Pid}): the viewer's files are about to change", stamp.Pid);
            EndOurs(stamp, graceMs: 3000);
        }
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

    private static bool PrestartIsReady(string logPath)
    {
        try { return HookLog.PrestartReadyInThisRun(logPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static readonly TimeSpan WatcherLimit = TimeSpan.FromSeconds(60);

    private static (int ExitCode, string Out, string Err)? RunLauncher(Process launcher, TimeSpan limit)
    {
        // both pipes, a full stderr pipe would block the watcher forever
        var outTask = launcher.StandardOutput.ReadToEndAsync();
        var errTask = launcher.StandardError.ReadToEndAsync();
        if (!launcher.WaitForExit(limit)) { EndStuckLauncher(launcher, limit); return null; }
        return (launcher.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    // Smart App Control refuses a new unsigned build till Windows' cloud check knows it, so that gets retried. Null once
    // progress has said why
    private int? StartWithWatcher(ProcessStartInfo psi, IProgress<LaunchProgress>? progress, CancellationToken ct, TimeSpan limit, string failText)
    {
        for (int attempt = 1; ; attempt++)
        {
            var attemptStart = DateTime.UtcNow;
            var (outcome, launchedPid, failure) = RunWatcher(psi, ct, limit, failText);
            if (outcome == LaunchOutcome.Started) return launchedPid;
            if (outcome == LaunchOutcome.Cancelled)
            {
                progress?.Report(new LaunchProgress("Stopped", "The login was cancelled."));
                return null;
            }
            bool refused = outcome == LaunchOutcome.Refused || CodeIntegrity.Refused(attemptStart, HookFiles.WatcherExe, "pol_hook.dll");
            if (refused && attempt <= RefusedRetryWaits.Length)
            {
                var wait = RefusedRetryWaits[attempt - 1];
                Log.Warning("Windows Code Integrity refused Provenance Watcher.exe or pol_hook.dll (attempt {Attempt}); trying again in {Wait}", attempt, wait);
                progress?.Report(new LaunchProgress("Starting", $"Windows is checking Provenance's files. Trying again in {wait.TotalSeconds:0} seconds...", LaunchSteps.StartPol, 0.02));
                if (ct.WaitHandle.WaitOne(wait))
                {
                    progress?.Report(new LaunchProgress("Stopped", "The login was cancelled."));
                    return null;
                }
                continue;
            }
            if (refused) Log.Error("Windows Code Integrity refused Provenance Watcher.exe or pol_hook.dll {Attempts} times", attempt);
            progress?.Report(new LaunchProgress("Failed", refused ? CodeIntegrity.RefusedText : failure));
            return null;
        }
    }

    // A cancel waits for the watcher (its steps time out on their own) then ends its pol.exe. Killing it half way would
    // leave a suspended pol.exe behind.
    private (LaunchOutcome Outcome, int Pid, string Failure) RunWatcher(ProcessStartInfo psi, CancellationToken ct, TimeSpan limit, string failText)
    {
        try
        {
            using var launcher = StartProcess(psi) ?? throw new InvalidOperationException("Provenance Watcher did not start");
            var launcherStamp = ProcessStamp.Of(launcher.Id);
            if (RunLauncher(launcher, limit) is not { } run)
                return ct.IsCancellationRequested
                    ? (LaunchOutcome.Cancelled, 0, "")
                    : (LaunchOutcome.Failed, 0, failText);
            var startedPid = HookLog.StartedPid(run.Out);
            if (ct.IsCancellationRequested)
            {
                if (startedPid is { } stray && launcherStamp is { } started) EndNewPol(stray, started);
                return (LaunchOutcome.Cancelled, 0, "");
            }
            var stderr = run.Err.Trim();
            if (run.ExitCode != 0 || startedPid is not { } pid)
            {
                Log.Error("Provenance Watcher exited {Code}: {Error}", run.ExitCode, stderr.Length > 0 ? stderr : "no message");
                return (LaunchOutcome.Failed, 0, failText);
            }
            return (LaunchOutcome.Started, pid, "");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == CodeIntegrity.PolicyViolation)
        {
            Log.Warning(ex, "Windows refused to start Provenance Watcher.exe");
            return (LaunchOutcome.Refused, 0, "");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Hold launch failed");
            return (LaunchOutcome.Failed, 0, failText);
        }
    }

    private const string PolDidNotStart = "PlayOnline didn't start. Try again in a moment.";
    private const string WindowerDidNotStart = "PlayOnline didn't start with Windower. Try again in a moment.";

    // a pol.exe the watcher started since the launcher did
    private static void EndNewPol(int pid, ProcessStamp launcher)
    {
        bool NewPol(ProcessStamp pol) => pol.StartUtcTicks >= launcher.StartUtcTicks
            && string.Equals(Path.GetFileName(ProcessTree.ImagePath(pol.Pid)), "pol.exe", StringComparison.OrdinalIgnoreCase);
        if (ProcessStamp.Of(pid) is { } found && End(found, 0, 2000, NewPol))
            Log.Information("Ended pol.exe (pid {Pid}): the sign-in was cancelled", pid);
    }

    private static void EndStuckLauncher(Process launcher, TimeSpan limit)
    {
        int id = launcher.Id;
        var stamp = ProcessStamp.Of(id);
        try { launcher.Kill(); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
        if (stamp is { } started)
            foreach (var child in ProcessTree.ChildrenOf(id)) EndNewPol(child, started);
        Log.Warning("Provenance Watcher (pid {Pid}) did not exit in {Limit} and was ended", id, limit);
    }

    // still that pol.exe, and a viewer's
    private bool Ours(ProcessStamp stamp) => stamp.IsRunning() && IsViewerPol(stamp);

    private bool EndOurs(ProcessStamp stamp, int graceMs = 0, int afterMs = 0) => End(stamp, graceMs, afterMs, IsViewerPol);

    private static bool End(ProcessStamp stamp, int graceMs, int afterMs, Func<ProcessStamp, bool> alsoIf)
    {
        var result = stamp.EndIfStill(graceMs, afterMs, alsoIf);
        if (result != ProcessStamp.Ending.NotThat) PutScreenSaverBack();
        return result == ProcessStamp.Ending.Ended;
    }

    private static SessionFile? TryReadRecord(string path)
    {
        try { return JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // write then move, another copy may read it at any moment
    private static void WriteRecord(string path, ProcessStamp game, string? profileId, bool played = false)
    {
        var me = ProcessStamp.Of(Environment.ProcessId) ?? new ProcessStamp(Environment.ProcessId, 0);
        AtomicFile.Write(path, JsonSerializer.Serialize(
            new SessionFile(game.Pid, game.StartUtcTicks, profileId, me.Pid, me.StartUtcTicks, played)));
    }

    private static List<string> SessionFilePaths()
    {
        var files = new List<string>();
        try { if (Directory.Exists(SessionDir)) files.AddRange(Directory.GetFiles(SessionDir, "*.json")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return files;
    }

    private void SaveSession(string? profileId, bool played = false)
    {
        try
        {
            if (Pid is not { } pid || _polStamp is not { } stamp) return;
            _sessionProfile = profileId;
            Directory.CreateDirectory(SessionDir);
            var path = Path.Combine(SessionDir, $"{pid}.json");
            WriteRecord(path, stamp, profileId, played);
            _sessionFile = path;
            ReleaseClaim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Could not save the session record, another copy may not see this login");
        }
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
            WriteRecord(path, default, profileId);
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
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // a copy that ended before Play can leave its pol.exe logged in (or still logging itself out), so it's ended here
    // and the next login starts fresh
    public void EndOrphans()
    {
        foreach (var path in SessionFilePaths())
        {
            // one that won't read may be another copy's mid-replace, so it's never deleted
            if (TryReadRecord(path) is not { } s) continue;
            bool ownerAlive = s.Owner.MightBeRunning();
            if (ownerAlive && s.OwnerPid != Environment.ProcessId) continue;
            if (!Ours(s.Game))
            {
                DeleteQuietly(path);
                continue;
            }
            if (s.Played || ownerAlive) continue;
            EndOrphan(s.Game);
            DeleteQuietly(path);
        }
    }

    public static bool HeldByAnotherCopy(string profileId)
    {
        foreach (var path in SessionFilePaths())
        {
            if (TryReadRecord(path) is not { } f || f.ProfileId != profileId) continue;
            if (f.Pid != 0 && !f.Game.IsRunning()) continue;
            if (f.Played) return true;
            if (f.OwnerPid != Environment.ProcessId && f.Owner.MightBeRunning()) return true;
        }
        return false;
    }

    public static bool GameRunningFor(string profileId) => SessionFilePaths().Any(path =>
        TryReadRecord(path) is { Played: true } f && f.ProfileId == profileId && f.Game.IsRunning());

    private void EndOrphan(ProcessStamp pol)
    {
        if (EndOurs(pol, afterMs: 3000))
            Log.Warning("Ended pol.exe {Pid}: its copy of Provenance ended before the game started", pol.Pid);
    }

    private void ReadSnapshot()
    {
        if (Pid is not { } pid) return;
        // the store file only turns up once, at login, and goes once the app has it
        if (SocialSnapshotFiles.TryRead(pid) is { } snap)
            SnapshotUpdated?.Invoke(snap with { Stored = SocialSnapshotFiles.TryReadStore(pid) });
    }

    public void Poll()
    {
        if (Pid is not { } pid) return;
        if (_played || ProcessStamp.PidRunning(pid))
        {
            if (_watcher == null) ThreadPool.QueueUserWorkItem(_ => ReadSnapshot());
            return;
        }
        if (!Monitor.TryEnter(_gate)) return;   // StopHold has it, so skip this tick rather than block the UI thread
        int? exit = null;
        try
        {
            if (Pid != pid) return;
            try { if (_polProc is { HasExited: true } gone && gone.Id == pid) exit = gone.ExitCode; }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
            Log.Warning("Held PlayOnline session (pid {Pid}) ended on its own (exit code {Exit})", pid, exit);
            PutScreenSaverBack();
            HookFiles.ClearFor(pid);
            DropChatPassword();
            Pid = null;
            _polStamp = null;
        }
        finally { Monitor.Exit(_gate); }
        StopWatcher();
        ClearSession();
        // taken now so one that a new sign-in starts meanwhile is left alone
        var hook = TakeThirdPartyHook();
        Task.Run(() => EndThirdPartyHook(hook));
        SessionEnded?.Invoke(LostSessionText(pid) ?? SessionClosedText(exit));
    }

    // the hook's reason when it lost the session itself, read once
    private string? LostSessionText(int pid)
    {
        string? why = null;
        var ended = HookFiles.PathFor(pid, HookContract.Files.Ended);
        try
        {
            if (File.Exists(ended) && int.TryParse(File.ReadAllText(ended).Trim(), out var reason))
                why = PolAccountNotices.SessionLost(reason, Region);
            File.Delete(ended);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return why;
    }

    // the room password goes to the hook as a one-shot entry like the account details, the hook deletes it once read.
    // User is the enter's token, so it only ever works for that one
    public bool SaveChatPassword(string token, string password)
    {
        if (_launchId is not { } id) return false;
        try
        {
            Wrapped.Save(ChatKey(id), token, password, sessionOnly: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not hand the room password to the hook");
            return false;
        }
    }

    public void DropChatPassword()
    {
        if (_launchId is { } id)
            try { _store.Delete(ChatKey(id)); } catch (Win32Exception) { }
    }

    public void SignalPlay()
    {
        if (_launchId is null) return;
        // skip the log so far, only the answer to this Play counts and not an unreadable entry planted earlier
        if (Pid is { } pid)
            try { LogOf(pid).ReadNewLines(); } catch (Exception ex) { Log.Warning(ex, "Could not read the hook log before Play"); }
        PutWindowerValuesBack();
        // saved first, so a throw leaves the session held
        Wrapped.Save($"{LaunchKeyPrefix}{_launchId}:PLAY", "play", "go", sessionOnly: true);
        _played = true;
        StopWatcher();
        // the record stays as the game's, so no copy signs this account in on top of it
        SaveSession(_sessionProfile, played: true);
    }

    private void TrackThirdPartyHook(ProcessStamp hook)
    {
        lock (_hookGate) _hook = hook;
    }

    private ProcessStamp? TakeThirdPartyHook()
    {
        lock (_hookGate)
        {
            var taken = _hook;
            _hook = null;
            return taken;
        }
    }

    // Ashita-cli closes by itself once the game's up, so it only gets a couple of secs before it's ended
    private static void EndThirdPartyHook(ProcessStamp? hook)
    {
        if (hook is not { } stamp) return;
        var name = Path.GetFileNameWithoutExtension(ProcessTree.ImagePath(stamp.Pid)) ?? "the third-party hook";
        if (stamp.EndIfStill(graceMs: 2000) == ProcessStamp.Ending.Ended)
            Log.Information("Ended {Name} (pid {Pid}): the game was not started", name, stamp.Pid);
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
                var stamp = _polStamp;
                try { HookFiles.RequestStop(pid); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                if (stamp is { } game && EndOurs(game, (int)StopGrace.TotalMilliseconds, afterMs: 2000))
                    Log.Warning("pol.exe {Pid} hadn't logged out {Seconds} s after its stop file, so it was ended", pid,
                                (int)StopGrace.TotalSeconds);
                HookFiles.ClearFor(pid);
                DropChatPassword();
                Log.Information("Stopped held PlayOnline session (pid {Pid})", pid);
                Pid = null;
                _polStamp = null;
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
            _polStamp = null;
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
            _polStamp = null;
            _launchId = null;
            _played = false;
            _sessionFile = null;
            _sessionProfile = null;
            // nothing watches the game from here, and one that crashes leaves the screen saver off
            if (_polProc is { } game)
                game.WaitForExitAsync().ContinueWith(_ => { game.Dispose(); ScreenSaverBack(); }, TaskScheduler.Default);
            _polProc = null;
            lock (_hookGate) _hook = null;
        }
    }

    internal void AdoptHeldForTest(int pid, DateTime started)
    {
        lock (_gate)
        {
            Pid = pid;
            _polStamp = new ProcessStamp(pid, started.ToUniversalTime().Ticks);
        }
    }

    internal string AdoptPlayedForTest(int pid, DateTime started, string profileId, (int Pid, DateTime Start)? hook = null)
    {
        var launchId = "test" + Guid.NewGuid().ToString("N")[..8];
        lock (_gate)
        {
            Pid = pid;
            _polStamp = new ProcessStamp(pid, started.ToUniversalTime().Ticks);
            _launchId = launchId;
            _played = true;
        }
        if (hook is { } h) TrackThirdPartyHook(new ProcessStamp(h.Pid, h.Start.ToUniversalTime().Ticks));
        SaveSession(profileId, played: true);
        return launchId;
    }

    public Task<string?> WaitForPlayAnswerAsync(TimeSpan timeout) => Task.Run(() =>
    {
        if (Pid is not { } pid)
        {
            EndUnstartedPlay();
            return PlaySessionClosedText;
        }
        var hookLog = LogOf(pid);
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            bool alive = ProcessStamp.PidRunning(pid);
            // it can log its answer and end between two reads, so read what's left once it's gone
            foreach (var line in alive ? hookLog.ReadNewLines() : hookLog.ReadRemainingLines())
            {
                switch (HookLog.Parse(line))
                {
                    case PlayStarted:
                        return null;
                    case PlayUnreadable:
                        Log.Error("The hook could not read the Play entry (pol.exe {Pid}): the game did not start; ending the session", pid);
                        DropKey();
                        EndUnstartedPlay();
                        return PlayUnreadableText;
                    case PlayRefused:
                        Log.Warning("The hook refused Play (pol.exe {Pid}): {Line}; the game did not start, ending the session", pid, line.Trim());
                        EndUnstartedPlay();
                        return PlayRefusedText;
                }
            }
            // nothing polls while Play runs, so a session the hook lost meanwhile only shows here
            if (!alive)
            {
                Log.Warning("pol.exe {Pid} ended before the game started, the PlayOnline session was lost", pid);
                var why = LostSessionText(pid) ?? PlaySessionClosedText;
                PutScreenSaverBack();
                EndUnstartedPlay();
                // no pol.exe left to stop, and a later one with this pid would read it
                HookFiles.Delete(pid, HookContract.Files.Stop);
                return why;
            }
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

    // the game's exit code, when there's still a way to read it
    public async Task<int?> WaitForExitAsync()
    {
        if (Pid is not { } pid) return null;
        try
        {
            using var p = Process.GetProcessById(pid);
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        // already gone, the handle kept from its start can still say how it ended. No gate, this can be the UI thread
        var kept = _polProc;
        try { return kept is { HasExited: true } && kept.Id == pid ? kept.ExitCode : null; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    // what to tell the player once the game's pol.exe has ended, null for a normal end
    public static string? GameEndedText(int? exitCode) => exitCode == (int)HookExit.GameStartFailed ? GameStartFailedText : null;

    private void CleanupOneShots(List<string> keys)
    {
        foreach (var k in keys)
        {
            try { _store.Delete(k); } catch (Win32Exception) { }
        }
        keys.Clear();
    }

    private HookLog LogOf(int pid)
    {
        var path = HookFiles.PathFor(pid, HookContract.Files.Log);
        return _hookLog is { } log && log.Path == path ? log : _hookLog = new HookLog(path);
    }
}
