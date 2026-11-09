using System.ComponentModel;
using System.Diagnostics;
using Provenance.Common.Game;
using Provenance.Common.Hook;
using Provenance.Common.Install;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Common.Security;
using Provenance.Common.Settings;
using Serilog;

namespace Provenance.Common.Login;

internal sealed class Watcher
{
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan[] RefusedRetryWaits = [TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60)];

    private enum LaunchOutcome { Started, Failed, Refused, Cancelled }

    private readonly ISettingsService _settings;

    public Watcher(ISettingsService settings)
    {
        _settings = settings;
        FindFiles = () => LaunchFiles(Region);
    }

    // everything a pol.exe start (or ending an earlier copy's) reaches outside this class, so they can be checked
    // offline with stand-ins
    internal Func<(string? ViewerDir, string? PolExe, string LauncherExe)> FindFiles { get; init; }
    internal Func<ProcessStartInfo, Process?> StartProcess { get; init; } = Process.Start;
    internal Func<bool> ViewerPolRunning { get; init; } = PolUpdates.ViewerRunning;
    internal Func<bool> AnyPolRunningNow { get; init; } = ScreenSaver.AnyPolRunning;
    internal Func<bool?> WindowsScreenSaver { get; init; } = ScreenSaver.Read;
    internal Func<ProcessStamp, bool> IsViewerPol { get; init; } = stamp => stamp.IsViewerPol();

    // a login or pre-start only gets this far on a supported region
    private PolRegion Region => _settings.DetectedRegion ?? PolRegion.UnitedStates;

    private static (string? ViewerDir, string? PolExe, string LauncherExe) LaunchFiles(PolRegion region)
    {
        var viewerDir = PolPaths.TryGetInstallFolder(region, GameProduct.Viewer);
        var polExe = viewerDir is null ? null : Path.Combine(viewerDir, GameProduct.Viewer.KeyFile);
        var launcherExe = Path.Combine(HookFiles.NativeDir, HookFiles.WatcherExe);
        bool found = polExe != null && File.Exists(polExe) && File.Exists(launcherExe)
                     && File.Exists(WatcherProcess.HookDllPath);
        return (viewerDir, found ? polExe : null, launcherExe);
    }

    internal static ProcessStartInfo LauncherStart(string launcherExe, string polExe) =>
        StartInfo(launcherExe, [polExe, WatcherProcess.HookDllPath]);

    internal static ProcessStartInfo StartInfo(string launcherExe, IEnumerable<string> arguments)
    {
        var psi = WatcherProcess.StartInfo(launcherExe);
        SetArguments(psi, arguments);
        return psi;
    }

    internal static void SetArguments(ProcessStartInfo psi, IEnumerable<string> arguments)
    {
        psi.ArgumentList.Clear();
        foreach (var a in arguments) psi.ArgumentList.Add(a);
    }

    // Provenance can sit in the tray for days, so the screen saver's read again before every pol.exe start
    internal void BeforeStart(string viewerDir, string when)
    {
        LargeAddressAwareSwitch.Apply(_settings, new LargeAddressAwareSwitch.Target(viewerDir, ViewerPolRunning, () => null, () => null), when);
        ScreenSaver.Note(_settings, AnyPolRunningNow, WindowsScreenSaver);
    }

    internal static WatcherRun? RunLauncher(Process launcher, TimeSpan limit)
    {
        var run = WatcherProcess.Wait(launcher, limit);
        if (run is null) EndStuckLauncher(launcher, limit);
        return run;
    }

    // Smart App Control refuses a new unsigned build till Windows' cloud check knows it, so that gets retried. Null once
    // progress has said why
    internal int? Start(ProcessStartInfo psi, IProgress<LaunchProgress>? progress, CancellationToken ct, TimeSpan limit, string failText)
    {
        for (int attempt = 1; ; attempt++)
        {
            var attemptStart = DateTime.UtcNow;
            var (outcome, launchedPid, failure) = RunOnce(psi, ct, limit, failText);
            if (outcome == LaunchOutcome.Started) return launchedPid;
            if (outcome == LaunchOutcome.Cancelled)
            {
                progress?.Report(LaunchProgress.Cancelled());
                return null;
            }
            bool refused = outcome == LaunchOutcome.Refused || WatcherProcess.Refused(attemptStart);
            if (refused && attempt <= RefusedRetryWaits.Length)
            {
                var wait = RefusedRetryWaits[attempt - 1];
                Log.Warning("Windows Code Integrity refused Provenance Watcher.exe or pol_hook.dll (attempt {Attempt}); trying again in {Wait}", attempt, wait);
                progress?.Report(new LaunchProgress("Starting", $"Windows is checking Provenance's files. Trying again in {wait.TotalSeconds:0} seconds...", 0.02));
                if (ct.WaitHandle.WaitOne(wait))
                {
                    progress?.Report(LaunchProgress.Cancelled());
                    return null;
                }
                continue;
            }
            if (refused) Log.Error("Windows Code Integrity refused Provenance Watcher.exe or pol_hook.dll {Attempts} times", attempt);
            progress?.Report(LaunchProgress.Failed(refused ? CodeIntegrity.RefusedText : failure));
            return null;
        }
    }

    // A cancel waits for the watcher (its steps time out on their own) then ends its pol.exe. Killing it half way would
    // leave a suspended pol.exe behind.
    private (LaunchOutcome Outcome, int Pid, string Failure) RunOnce(ProcessStartInfo psi, CancellationToken ct, TimeSpan limit, string failText)
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
        catch (Win32Exception ex) when (WatcherProcess.RefusedToStart(ex))
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

    internal const string PolDidNotStart = "PlayOnline didn't start. Try again in a moment.";
    internal const string WindowerDidNotStart = "PlayOnline didn't start with Windower. Try again in a moment.";

    // a pol.exe the watcher started since the launcher did
    private static void EndNewPol(int pid, ProcessStamp launcher)
    {
        bool NewPol(ProcessStamp pol) => pol.StartUtcTicks >= launcher.StartUtcTicks
            && string.Equals(Path.GetFileName(ProcessTree.ImagePath(pol.Pid)), GameProduct.Viewer.KeyFile, StringComparison.OrdinalIgnoreCase);
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
    internal bool Ours(ProcessStamp stamp) => stamp.IsRunning() && IsViewerPol(stamp);

    internal bool EndOurs(ProcessStamp stamp, int graceMs = 0, int afterMs = 0) => End(stamp, graceMs, afterMs, IsViewerPol);

    private static bool End(ProcessStamp stamp, int graceMs, int afterMs, Func<ProcessStamp, bool> alsoIf)
    {
        var result = stamp.EndIfStill(graceMs, afterMs, alsoIf);
        if (result != ProcessStamp.Ending.NotThat) ScreenSaver.PutBack();
        return result == ProcessStamp.Ending.Ended;
    }
}
