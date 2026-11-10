using System.ComponentModel;
using System.Diagnostics;
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
    private readonly ISettingsService _settings;
    private readonly Watcher _watcher;
    private readonly OurPol _pol;
    private readonly SnapshotWatcher _snapshots;
    private readonly object _gate = new();
    private HookLog? _hookLog;
    private bool _played;   // the held pol.exe is the game now, never kill it
    private ProcessStamp? _polStamp;
    private Process? _polProc;

    public PolSession(ISettingsService settings) : this(settings, new LaunchCredentials()) { }

    internal PolSession(ISettingsService settings, LaunchCredentials credentials, Watcher? watcher = null, Prestarter.Seams? prestart = null,
                        WindowerSetup? windowerSetup = null, OurPol? pol = null)
    {
        _settings = settings;
        Credentials = credentials;
        _watcher = watcher ?? new Watcher(settings);
        _pol = pol ?? new OurPol();
        Prestarter = new Prestarter(settings, _watcher, _pol, () => Pid is not null, _gate, prestart);
        UpdateViewer = (dir, progress, freeFiles, ct) => PolUpdates.UpdateViewer(dir, _settings.RegionOrDefault, progress, freeFiles, ct);
        Windower = new WindowerStart(settings, _watcher, windowerSetup);
        _snapshots = new SnapshotWatcher(() => Pid, snap => SnapshotUpdated?.Invoke(snap));
        Records = new SessionRecords(_pol);
    }

    private int? _pid;

    public int? Pid
    {
        get => _pid;
        internal set
        {
            if (_pid == value) return;
            _pid = value;
            PidChanged?.Invoke();
        }
    }

    // raised on whichever thread changed it, often under the session's lock, so a handler just hands the work on
    public event Action? PidChanged;

    public SessionRecords Records { get; }

    public LaunchCredentials Credentials { get; }

    public Prestarter Prestarter { get; }

    internal Func<string, IProgress<PatchProgress>?, Action, CancellationToken, PatchResult> UpdateViewer { get; init; }
    internal HoldViewerFiles HoldViewer { get; init; } = SignedBuilds.Hold;
    internal Action<string, CancellationToken> FindBuild { get; init; } = SignedBuilds.FetchIfUnknown;
    internal Action ScreenSaverBack { get; init; } = ScreenSaver.PutBack;
    // quiet this long and the login's stuck. polcore fails a list load or a login step at 100 s by itself, apart from
    // state 23, which it sits in for 400 s
    internal TimeSpan StallLimit { get; init; } = TimeSpan.FromSeconds(260);
    internal WindowerStart Windower { get; }
    internal ThirdPartyHookTracker HookTracker { get; } = new();
    // the hook only looks for the stop file every 150 ms, then gives writes under way 5 s before it logs out
    internal static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(6);

    public event Action<PolSocialSnapshot>? SnapshotUpdated;

    public event Action<string>? SessionEnded;

    public int? LastRefusalReason { get; private set; }

    public ThirdPartyHook Platform => _settings.Current.ThirdPartyHookPlatform;

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
        if (request.Pol.Password.Length > HookExitText.MaxPolPasswordLength) return Fail(HookExitText.PolPasswordTooLongText, LoginCodes.DetailsUnreadable);
        var region = _settings.DetectedRegion;
        if (!PolPaths.Supported(region))
            return Fail(region is null
                ? "PlayOnline isn't installed." : $"Provenance doesn't support the {PolPaths.VersionName(region.Value)} version of PlayOnline yet.");
        var (viewerDir, polExe, launcherExe) = _watcher.FindFiles();
        if (polExe is null)
            return Fail("Some of the files needed to start PlayOnline are missing. Check FINAL FANTASY XI is installed, and if it is, reinstall Provenance.");
        if (Credentials.KeyForLogIn() is { } keyProblem) return Fail(keyProblem);
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
        _watcher.BeforeStart(viewerDir!, "before the login");
        // the hook patches fixed offsets in pol.exe and polcore.dll, so both stay locked until the sign-in ends
        using var viewerHold = HoldViewer(viewerDir!, ct, out var buildProblem);
        if (buildProblem != null)
        {
            Log.Warning("Viewer build not supported: {Problem}", buildProblem);
            return Fail(buildProblem);
        }
        // under the gate so a StopHold still running has dropped its chat entry before the id changes
        string launchId;
        lock (_gate) launchId = Credentials.NewLaunchId();
        var keys = new List<string>();
        try
        {
            return ConnectHeld(request, polExe, launcherExe, launchId, keys, ct);
        }
        finally
        {
            Credentials.CleanupOneShots(keys);   // on every way out, so passwords never linger in Credential Manager
        }
    }

    private int ConnectHeld(LoginRequest request, string polExe, string launcherExe, string launchId, List<string> keys, CancellationToken ct)
    {
        var progress = request.Progress;
        var psi = BuildEnvironment(request, polExe, launcherExe, keys);
        if (ct.IsCancellationRequested) return Stopped(progress);
        progress?.Report(new LaunchProgress("Starting", "Starting PlayOnline...", 0.02));
        Windower.Forget();
        if (StartPol(request, psi, polExe, launchId, ct) is not { } pid)
            return ct.IsCancellationRequested ? LoginCodes.Cancelled : LoginCodes.LauncherFailed;
        AdoptPid(pid, request.ProfileId);
        progress?.Report(new LaunchProgress("Starting", "Starting PlayOnline...", 0.04));
        // an old process with this pid left these. Not the go file, a pre-started pol.exe reads its sign-in from it
        HookFiles.ClearFor(pid);
        Credentials.DropChatPassword();
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
        var targets = Credentials.TargetsFor(request, keys);
        var psi = Watcher.LauncherStart(launcherExe, polExe);
        HookEnvironment.SetLogin(psi.Environment, targets, _settings.Current.PlayOnlineStatus, request.Options);
        return psi;
    }

    // null once progress has said why
    private int? StartPol(LoginRequest request, ProcessStartInfo psi, string polExe, string launchId, CancellationToken ct)
    {
        var progress = request.Progress;
        var options = request.Options;
        var platform = Platform;
        if (platform == ThirdPartyHook.Ashita)
        {
            return AshitaLauncher.StartViaAshita(_settings, _watcher, psi, polExe, launchId, options.ThirdPartyHookProfile,
                                                 progress, ct, hookStarted: HookTracker.Track);
        }
        if (platform == ThirdPartyHook.Windower)
            return Windower.Start(psi, polExe, launchId, options, progress, ct);
        // never for a lean session, the go file can't carry POL_LEAN
        if (!options.Lean && Prestarter.TakePrestarted(psi, ct) is { } pre) return pre;
        // a cancel while the pre-start was being taken leaves it waiting, so nothing starts in its place
        if (ct.IsCancellationRequested)
        {
            progress?.Report(LaunchProgress.Cancelled());
            return null;
        }
        return _watcher.Start(psi, progress, ct, Watcher.Limit, Watcher.PolDidNotStart);
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
            if (code == LoginCodes.DetailsUnreadable) Credentials.DropKey();
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
            // the language the hook gave polcore, the same one app.dll builds the jail page's address from
            if (e is ViewerStarted { Language: { } lang }) viewerLanguage = lang;
            if (e is AuthRefused refused) LastRefusalReason = refused.Reason;
            return HookExitText.LoginEnd(e, _settings.RegionOrDefault, viewerLanguage) is var (code, text) ? Fail(code, text) : null;
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
                Credentials.SaveHandle(name);
                lastActivity = DateTime.UtcNow;
            }
            if (ct.IsCancellationRequested) break;   // a cancel that lands with the first snapshot still wins
            if (SocialSnapshotFiles.TryRead(pid) is { } snap)
            {
                // the hook writes the store file before its first snapshot, so it's there now even if Play comes next
                SnapshotUpdated?.Invoke(snap with { Stored = SocialSnapshotFiles.TryReadStore(pid) });
                _snapshots.Start(pid);
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
                var (endedCode, endedText) = HookExitText.EndedBeforeLogIn(exit);
                return Fail(endedCode, endedText);
            }
            if (DateTime.UtcNow - lastActivity > StallLimit)
                return Fail(LoginCodes.Stalled, "PlayOnline stopped responding during the login. Try again in a moment.");
            Thread.Sleep(250);
        }
        return Stopped(progress, polClosed: true);
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

    private void SaveSession(string? profileId)
    {
        if (Pid is not { } || _polStamp is not { } stamp) return;
        Records.Save(stamp, profileId);
    }

    public void Poll()
    {
        if (Pid is not { } pid) return;
        if (_played || ProcessStamp.PidRunning(pid))
        {
            if (!_snapshots.Watching) ThreadPool.QueueUserWorkItem(_ => _snapshots.Read());
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
            Credentials.DropChatPassword();
            Pid = null;
            _polStamp = null;
        }
        finally { Monitor.Exit(_gate); }
        _snapshots.Stop();
        Records.Clear();
        // taken now so one that a new sign-in starts meanwhile is left alone
        var hook = HookTracker.Take();
        Task.Run(() => ThirdPartyHookTracker.End(hook));
        SessionEnded?.Invoke(LostSessionText(pid) ?? HookExitText.SessionClosedText(exit));
    }

    // the hook's reason when it lost the session itself, read once
    private string? LostSessionText(int pid)
    {
        string? why = null;
        var ended = HookFiles.PathFor(pid, HookContract.Files.Ended);
        try
        {
            if (File.Exists(ended) && int.TryParse(File.ReadAllText(ended).Trim(), out var reason))
                why = PolAccountNotices.SessionLost(reason, _settings.RegionOrDefault);
            File.Delete(ended);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return why;
    }

    public void SignalPlay()
    {
        if (Credentials.LaunchId is null) return;
        // skip the log so far, only the answer to this Play counts and not an unreadable entry planted earlier
        if (Pid is { } pid)
            try { LogOf(pid).ReadNewLines(); } catch (Exception ex) { Log.Warning(ex, "Could not read the hook log before Play"); }
        Windower.PutValuesBack();
        // saved first, so a throw leaves the session held
        Credentials.SavePlay();
        _played = true;
        _snapshots.Stop();
        if (Pid is not null) Records.MarkPlayed();
    }

    public void StopHold()
    {
        _snapshots.Stop();
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
                if (stamp is { } game && _pol.End(game, (int)StopGrace.TotalMilliseconds, afterMs: 2000))
                    Log.Warning("pol.exe {Pid} hadn't logged out {Seconds} s after its stop file, so it was ended", pid,
                                (int)StopGrace.TotalSeconds);
                HookFiles.ClearFor(pid);
                Credentials.DropChatPassword();
                Log.Information("Stopped held PlayOnline session (pid {Pid})", pid);
                Pid = null;
                _polStamp = null;
            }
            HookTracker.EndNow();
        }
    }

    public void Dispose()
    {
        Prestarter.Close();
        Credentials.StopSweeps();
        try
        {
            try { Prestarter.StopPrestart(); }
            finally { StopHold(); }
        }
        finally { Credentials.DeleteLaunches(); }
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
        HookTracker.Forget();
    }

    // multi-account mode: forget the played session, the game and its record stay as they are
    public void ReleasePlayedSession()
    {
        lock (_gate)
        {
            if (!_played) return;
            _snapshots.Stop();
            Log.Information("Let go of the played session (pid {Pid}); the game keeps running", Pid);
            Pid = null;
            _polStamp = null;
            Credentials.ForgetLaunch();
            _played = false;
            Records.Forget();
            // nothing watches the game from here, and one that crashes leaves the screen saver off
            if (_polProc is { } game)
                game.WaitForExitAsync().ContinueWith(_ => { game.Dispose(); ScreenSaverBack(); }, TaskScheduler.Default);
            _polProc = null;
            HookTracker.Forget();
        }
    }

    public Task<string?> WaitForPlayAnswerAsync(TimeSpan timeout) => Task.Run(() =>
    {
        if (Pid is not { } pid)
        {
            EndUnstartedPlay();
            return HookExitText.PlaySessionClosedText;
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
                        Credentials.DropKey();
                        EndUnstartedPlay();
                        return HookExitText.PlayUnreadableText;
                    case PlayRefused:
                        Log.Warning("The hook refused Play (pol.exe {Pid}): {Line}; the game did not start, ending the session", pid, line.Trim());
                        EndUnstartedPlay();
                        return HookExitText.PlayRefusedText;
                }
            }
            // nothing polls while Play runs, so a session the hook lost meanwhile only shows here
            if (!alive)
            {
                Log.Warning("pol.exe {Pid} ended before the game started, the PlayOnline session was lost", pid);
                var why = LostSessionText(pid) ?? HookExitText.PlaySessionClosedText;
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
                return HookExitText.PlayUnreadableText;
            }
            Thread.Sleep(150);
        }
    });

    private void EndUnstartedPlay()
    {
        // the Play entry goes first, a hook that wakes late checks Play before the stop file and would boot the game
        Credentials.DeletePlay();
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

    private HookLog LogOf(int pid)
    {
        var path = HookFiles.PathFor(pid, HookContract.Files.Log);
        return _hookLog is { } log && log.Path == path ? log : _hookLog = new HookLog(path);
    }
}
