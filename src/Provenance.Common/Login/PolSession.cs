using System.ComponentModel;
using System.Diagnostics;
using Provenance.Common.Credentials;
using Provenance.Common.Game;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;
using Provenance.Common.Hook;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Common.Progress;
using Provenance.Common.Settings;
using Provenance.Common.Social;
using Serilog;

namespace Provenance.Common.Login;

internal delegate IDisposable? HoldViewerFiles(string viewerDir, CancellationToken ct, out string? problem);

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
    private const string ClosedBeforeLogInText = "PlayOnline closed before the login finished.";

    internal static (int Code, string Text) EndedBeforeLogIn(int? exitCode) => (HookExit?)exitCode switch
    {
        HookExit.AddressesNotFound => (LoginCodes.Unfinished, ViewerBuild.UnsupportedText),
        HookExit.DetailsUnreadable => (LoginCodes.DetailsUnreadable, DetailsAgainText),
        HookExit.NoHandle => (LoginCodes.NoPolHandle, NoPolHandleText),
        HookExit.BadIpcFolder => (LoginCodes.LauncherFailed, PrivateFolderText),
        HookExit.UnsafeStart => (LoginCodes.LauncherFailed, UnsafeStartText),
        HookExit.GameStartFailed => (LoginCodes.LauncherFailed, GameStartFailedText),
        HookExit.Refused => (LoginCodes.AuthFailed, RefusedText),
        HookExit.TurnedAway => (LoginCodes.AccountBlocked, "PlayOnline turned this account away. Log in with the PlayOnline Viewer to see why."),
        HookExit.ListLoadFailed => (LoginCodes.Unfinished, LoadFailedText("")),
        _ => (LoginCodes.Unfinished, ClosedBeforeLogInText),
    };

    internal static string SessionClosedText(int? exitCode) => (HookExit?)exitCode switch
    {
        HookExit.BadIpcFolder => PrivateFolderText,
        HookExit.UnsafeStart => UnsafeStartText,
        HookExit.GameStartFailed => GameStartFailedText,
        _ => "Your PlayOnline session has closed. Log in again to see your friends and messages.",
    };

    private readonly ISettingsService _settings;
    private readonly LaunchCredentials _credentials;
    private readonly Watcher _starter;
    private readonly object _gate = new();
    private readonly object _hookGate = new();
    private HookLog? _hookLog;
    private bool _played;   // the held pol.exe is the game now, never kill it
    private ProcessStamp? _polStamp;
    private Process? _polProc;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private ProcessStamp? _hook;
    private WindowerGameValues? _windowerPlay;

    public PolSession(ISettingsService settings) : this(settings, new LaunchCredentials()) { }

    internal PolSession(ISettingsService settings, LaunchCredentials credentials, Watcher? watcher = null, Prestarter.Seams? prestart = null)
    {
        _settings = settings;
        _credentials = credentials;
        _starter = watcher ?? new Watcher(settings);
        Prestarter = new Prestarter(settings, _starter, () => Pid is not null, _gate, prestart);
        UpdateViewer = (dir, progress, freeFiles, ct) => PolUpdates.UpdateViewer(dir, Region, progress, freeFiles, ct);
        WindowerSetup = (polExe, profile, lists, shared) => WindowerLauncher.SetupFor(_settings, polExe, profile, lists, shared);
        Records = new SessionRecords(_starter.Ours, stamp => _starter.EndOurs(stamp, afterMs: 3000));
    }

    public int? Pid { get; internal set; }

    public SessionRecords Records { get; }

    public LaunchCredentials Credentials => _credentials;

    public Prestarter Prestarter { get; }

    internal Func<string, IProgress<PatchProgress>?, Action, CancellationToken, PatchResult> UpdateViewer { get; init; }
    internal Func<string, string?, IReadOnlyDictionary<string, WindowerAddonList>?, WindowerAddonList?, WindowerLaunchSetup> WindowerSetup { get; init; }
    internal HoldViewerFiles HoldViewer { get; init; } = SignedBuilds.Hold;
    internal Action<string, CancellationToken> FindBuild { get; init; } = SignedBuilds.FetchIfUnknown;
    internal Action ScreenSaverBack { get; init; } = ScreenSaver.PutBack;
    // quiet this long and the login's stuck. polcore fails a list load or a login step at 100 s by itself, apart from
    // state 23, which it sits in for 400 s
    internal TimeSpan StallLimit { get; init; } = TimeSpan.FromSeconds(260);
    // the hook only looks for the stop file every 150 ms, then gives writes under way 5 s before it logs out
    internal static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(6);

    public event Action<PolSocialSnapshot>? SnapshotUpdated;

    public event Action<string>? SessionEnded;

    public int? LastRefusalReason { get; private set; }

    public ThirdPartyHook Platform => _settings.Current.ThirdPartyHookPlatform;

    // a login or pre-start only gets this far on a supported region
    private PolRegion Region => _settings.DetectedRegion ?? PolRegion.UnitedStates;

    public async Task<int> ConnectAsync(LoginRequest request, CancellationToken ct = default)
    {
        // no gate here, StopHold can hold it for seconds and this is the UI thread
        if (Volatile.Read(ref _played))
            throw Shown.Mark(new InvalidOperationException("Provenance is still finishing your last game. Close Provenance and open it again, then log in."));
        LastRefusalReason = null;
        var launch = request with { Options = ForLaunch(request.Options) };
        int rc = LoginCodes.LauncherFailed;
        try
        {
            rc = await Task.Run(() => ConnectCore(launch, ct), ct);
            return rc;
        }
        finally { if (rc != 0) Records.ReleaseClaim(); }
    }

    // on the UI thread, where Settings changes the live addon lists while the login carries on in the background
    internal SessionOptions ForLaunch(SessionOptions? options) =>
        (options ?? new SessionOptions(false, null)) with
        {
            WindowerLists = WindowerLauncher.Snapshot(_settings.Current.WindowerAddonLists),
            WindowerShared = _settings.Current.WindowerSharedExtras?.Copy(),
        };

    private int ConnectCore(LoginRequest request, CancellationToken ct)
    {
        var progress = request.Progress;
        int Fail(string text, int code = LoginCodes.LauncherFailed)
        {
            progress?.Report(LaunchProgress.Failed(text));
            return code;
        }
        _hookLog = null;
        _played = false;
        if (request.Pol.Password.Length > MaxPolPasswordLength) return Fail(PolPasswordTooLongText, LoginCodes.DetailsUnreadable);
        var region = _settings.DetectedRegion;
        if (!PolPaths.Supported(region))
            return Fail(region is null
                ? "PlayOnline isn't installed." : $"Provenance doesn't support the {PolPaths.VersionName(region.Value)} version of PlayOnline yet.");
        var (viewerDir, polExe, launcherExe) = _starter.FindFiles();
        if (polExe is null)
            return Fail("Some of the files needed to start PlayOnline are missing. Check FINAL FANTASY XI is installed, and if it is, reinstall Provenance.");
        if (_credentials.KeyForLogIn() is { } keyProblem) return Fail(keyProblem);
        // the viewer checks for its own update on every connect and always says yes
        try
        {
            UpdateViewer(viewerDir!, new InlineProgress<PatchProgress>(
                p => progress?.Report(LaunchProgress.Updating("Starting", p, 0.02, LaunchProgress.LogInUpdateEnd))),
                Prestarter.FreeViewerFiles, ct);
        }
        catch (OperationCanceledException)
        {
            progress?.Report(LaunchProgress.Cancelled());
            return LoginCodes.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PlayOnline Viewer update failed");
            return Fail(ErrorText.Of(ex, "The PlayOnline Viewer couldn't be updated. Try logging in again in a moment."));
        }
        // after the viewer's update and before the hold, which stops any write. A build only the mirror's list knows has to be on it first
        FindBuild(viewerDir!, ct);
        if (ct.IsCancellationRequested)
        {
            progress?.Report(LaunchProgress.Cancelled());
            return LoginCodes.Cancelled;
        }
        _starter.BeforeStart(viewerDir!, "before the login");
        // the hook patches fixed offsets in pol.exe and polcore.dll, so both stay locked until the sign-in ends
        using var viewerHold = HoldViewer(viewerDir!, ct, out var buildProblem);
        if (buildProblem != null)
        {
            Log.Warning("Viewer build not supported: {Problem}", buildProblem);
            return Fail(buildProblem);
        }
        lock (_gate) _credentials.NewLaunchId();
        var keys = new List<string>();
        try
        {
            return ConnectHeld(request, polExe, launcherExe, keys, ct);
        }
        finally
        {
            _credentials.CleanupOneShots(keys);   // on every way out, so passwords never linger in Credential Manager
        }
    }

    private int ConnectHeld(LoginRequest request, string polExe, string launcherExe, List<string> keys, CancellationToken ct)
    {
        var progress = request.Progress;
        var psi = BuildEnvironment(request, polExe, launcherExe, keys);
        if (ct.IsCancellationRequested) return Stopped(progress);
        progress?.Report(new LaunchProgress("Starting", "Starting PlayOnline...", 0.02));
        Volatile.Write(ref _windowerPlay, null);
        if (StartPol(request, psi, polExe, ct) is not { } pid)
            return ct.IsCancellationRequested ? LoginCodes.Cancelled : LoginCodes.LauncherFailed;
        AdoptPid(pid, request.ProfileId);
        progress?.Report(new LaunchProgress("Starting", "Starting PlayOnline...", 0.04));
        // an old process with this pid left these. Not the go file, a pre-started pol.exe reads its sign-in from it
        HookFiles.ClearFor(pid);
        _credentials.DropChatPassword();
        HookFiles.Delete(pid, HookContract.Files.Stop);
        HookFiles.Delete(pid, HookContract.Files.Ended);
        return WaitForLogin(request, pid, ct);
    }

    private static int Stopped(IProgress<LaunchProgress>? progress, bool polClosed = false)
    {
        progress?.Report(LaunchProgress.Cancelled(polClosed));
        return LoginCodes.Cancelled;
    }

    private ProcessStartInfo BuildEnvironment(LoginRequest request, string polExe, string launcherExe, List<string> keys)
    {
        string polTarget, squareEnixTarget;
        var oneShot = new List<string> { "OTP" };
        var profile = string.IsNullOrWhiteSpace(request.ProfileId) ? null : request.ProfileId;
        if (profile != null && _credentials.SavedUserName($"{CredentialTargets.Pol}:{profile}") == request.Pol.PolId &&
            _credentials.SavedUserName($"{CredentialTargets.Sea}:{profile}") == request.SquareEnix.Username)
        {
            polTarget = LaunchCredentials.Target($"{CredentialTargets.Pol}:{profile}");
            squareEnixTarget = LaunchCredentials.Target($"{CredentialTargets.Sea}:{profile}");
        }
        else
        {
            polTarget = _credentials.SaveOneShot("POL", request.Pol.PolId, request.Pol.Password, keys);
            squareEnixTarget = _credentials.SaveOneShot("SEA", request.SquareEnix.Username, request.SquareEnix.Password, keys);
            oneShot.InsertRange(0, ["POL", "SEA"]);
        }
        var otpTarget = string.IsNullOrWhiteSpace(request.Otp) ? null : _credentials.SaveOneShot("OTP", "otp", request.Otp.Trim(), keys);

        var psi = Watcher.LauncherStart(launcherExe, polExe);
        keys.AddRange([_credentials.Key("HANDLE"), _credentials.Key("PLAY")]);
        HookEnvironment.SetLogin(psi.Environment,
            new HookEnvironment.Targets(polTarget, squareEnixTarget, oneShot, otpTarget, _credentials.TargetOf("HANDLE"),
                                        _credentials.TargetOf("PLAY"), LaunchCredentials.ChatTarget(_credentials.LaunchId!)),
            _settings.Current.PlayOnlineStatus, request.Options);
        return psi;
    }

    // null once progress has said why
    private int? StartPol(LoginRequest request, ProcessStartInfo psi, string polExe, CancellationToken ct)
    {
        var progress = request.Progress;
        var options = request.Options;
        var platform = Platform;
        if (platform == ThirdPartyHook.Ashita)
        {
            return AshitaLauncher.StartViaAshita(_settings, _starter, psi, polExe, _credentials.LaunchId!, options.ThirdPartyHookProfile,
                                                 progress, ct, hookStarted: TrackThirdPartyHook);
        }
        if (platform == ThirdPartyHook.Windower)
            return StartThroughWindower(psi, polExe, _credentials.LaunchId!, request, ct);
        // never for a lean session, the go file can't carry POL_LEAN
        if (!options.Lean && Prestarter.TakePrestarted(psi, ct) is { } pre) return pre;
        // a cancel while the pre-start was being taken leaves it waiting, so nothing starts in its place
        if (ct.IsCancellationRequested)
        {
            progress?.Report(LaunchProgress.Cancelled());
            return null;
        }
        return _starter.Start(psi, progress, ct, Watcher.Limit, Watcher.PolDidNotStart);
    }

    internal void AdoptPid(int pid, string? profileId)
    {
        lock (_gate)
        {
            Pid = pid;
            // kept so the exit code still says why if it dies mid sign-in
            _polProc?.Dispose();
            try { _polProc = Process.GetProcessById(pid); _ = _polProc.Handle; _polStamp = ProcessStamp.Of(pid); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { _polProc = null; _polStamp = null; }
        }
        // saved now, if this copy dies mid sign-in the next start ends this pol.exe
        SaveSession(profileId);
        Log.Information("Hold session: pol.exe pid {Pid}", pid);
    }

    private int WaitForLogin(LoginRequest request, int pid, CancellationToken ct)
    {
        var progress = request.Progress;
        int Fail(int code, string detail)
        {
            if (code == LoginCodes.DetailsUnreadable) _credentials.DropKey();
            progress?.Report(LaunchProgress.Failed(detail));
            return code;
        }
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
                var name = request.AskHandleName?.Invoke();
                _credentials.SaveHandle(name);
                lastActivity = DateTime.UtcNow;
            }
            if (ct.IsCancellationRequested) break;   // a cancel that lands with the first snapshot still wins
            if (SocialSnapshotFiles.TryRead(pid) is { } snap)
            {
                // the hook writes the store file before its first snapshot, so it's there now even if Play comes next
                SnapshotUpdated?.Invoke(snap with { Stored = SocialSnapshotFiles.TryReadStore(pid) });
                StartWatcher(pid);
                SaveSession(request.ProfileId);
                progress?.Report(new LaunchProgress("Connected", "You're logged in.", 1.0));
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
                ScreenSaver.PutBack();
                LogEarlyExit(pid, exit, logPath);
                var (endedCode, endedText) = EndedBeforeLogIn(exit);
                return Fail(endedCode, endedText);
            }
            if (DateTime.UtcNow - lastActivity > StallLimit)
                return Fail(LoginCodes.Stalled, "PlayOnline stopped responding during the login. Try again in a moment.");
            Thread.Sleep(250);
        }
        return Stopped(progress, polClosed: true);
    }

    // null once progress has said why
    internal int? StartThroughWindower(ProcessStartInfo psi, string polExe, string launchId, LoginRequest request, CancellationToken ct)
    {
        var progress = request.Progress;
        var options = request.Options;
        WindowerLauncher windower;
        try { windower = WindowerLauncher.Prepare(WindowerSetup(polExe, options.ThirdPartyHookProfile, options.WindowerLists, options.WindowerShared), progress, ct); }
        catch (OperationCanceledException)
        {
            progress?.Report(LaunchProgress.Cancelled());
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Windower wasn't ready to start the game");
            progress?.Report(LaunchProgress.Failed(ErrorText.Of(ex, "Provenance couldn't get Windower ready to start the game. Try again in a moment.")));
            return null;
        }
        // held till the watcher is done, Hook.dll copies the mapping out before it signals
        using (windower)
        {
            Watcher.SetArguments(psi, windower.Arguments(launchId));
            HookEnvironment.SetWindower(psi.Environment, launchId);
            progress?.Report(new LaunchProgress("Starting", "Starting PlayOnline with Windower...", 0.02));
            var pid = _starter.Start(psi, progress, ct, WindowerLauncher.WatcherLimit, Watcher.WindowerDidNotStart);
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

    private void SaveSession(string? profileId, bool played = false)
    {
        if (Pid is not { } || _polStamp is not { } stamp) return;
        Records.Save(stamp, profileId, played);
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
            ScreenSaver.PutBack();
            HookFiles.ClearFor(pid);
            _credentials.DropChatPassword();
            Pid = null;
            _polStamp = null;
        }
        finally { Monitor.Exit(_gate); }
        StopWatcher();
        Records.Clear();
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

    public void SignalPlay()
    {
        if (_credentials.LaunchId is null) return;
        // skip the log so far, only the answer to this Play counts and not an unreadable entry planted earlier
        if (Pid is { } pid)
            try { LogOf(pid).ReadNewLines(); } catch (Exception ex) { Log.Warning(ex, "Could not read the hook log before Play"); }
        PutWindowerValuesBack();
        // saved first, so a throw leaves the session held
        _credentials.SavePlay();
        _played = true;
        StopWatcher();
        // the record stays as the game's, so no copy signs this account in on top of it
        SaveSession(Records.Profile, played: true);
    }

    internal void TrackThirdPartyHook(ProcessStamp hook)
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
            Records.Clear();
            if (Pid is { } pid)
            {
                var stamp = _polStamp;
                try { HookFiles.RequestStop(pid); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                if (stamp is { } game && _starter.EndOurs(game, (int)StopGrace.TotalMilliseconds, afterMs: 2000))
                    Log.Warning("pol.exe {Pid} hadn't logged out {Seconds} s after its stop file, so it was ended", pid,
                                (int)StopGrace.TotalSeconds);
                HookFiles.ClearFor(pid);
                _credentials.DropChatPassword();
                Log.Information("Stopped held PlayOnline session (pid {Pid})", pid);
                Pid = null;
                _polStamp = null;
            }
            EndThirdPartyHook(TakeThirdPartyHook());
        }
    }

    public void Dispose()
    {
        Prestarter.Close();
        _credentials.StopSweeps();
        try
        {
            try { Prestarter.StopPrestart(); }
            finally { StopHold(); }
        }
        finally { _credentials.DeleteLaunches(); }
    }

    public void GameExited()
    {
        ScreenSaver.PutBack();
        lock (_gate)
        {
            if (!_played) return;
            Pid = null;
            _polStamp = null;
            _played = false;
        }
        Records.Clear();
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
            _credentials.ForgetLaunch();
            _played = false;
            Records.Forget();
            // nothing watches the game from here, and one that crashes leaves the screen saver off
            if (_polProc is { } game)
                game.WaitForExitAsync().ContinueWith(_ => { game.Dispose(); ScreenSaverBack(); }, TaskScheduler.Default);
            _polProc = null;
            lock (_hookGate) _hook = null;
        }
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
                        _credentials.DropKey();
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
                ScreenSaver.PutBack();
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
        _credentials.DeletePlay();
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

    private HookLog LogOf(int pid)
    {
        var path = HookFiles.PathFor(pid, HookContract.Files.Log);
        return _hookLog is { } log && log.Path == path ? log : _hookLog = new HookLog(path);
    }
}
