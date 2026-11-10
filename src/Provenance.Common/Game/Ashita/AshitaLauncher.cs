using System.Diagnostics;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Hook;
using Provenance.Common.IO;
using Provenance.Common.Login;
using Provenance.Common.Security;
using Provenance.Common.Settings;
using Serilog;

namespace Provenance.Common.Game.Ashita;

public static class AshitaLauncher
{
    private const string PolExeName = "pol.exe";

    public const string AccountBootFileProblem =
        "The Ashita boot configuration chosen for this account has to be a file in Ashita's config\\boot folder. Choose it again in Settings, under Multi-account.";

    // Ashita-cli starts pol.exe itself, then the watcher's --attach loads the hook into it. Credentials only travel as
    // Credential Manager target names in the environment, never on a command line
    internal static int? StartViaAshita(ISettingsService settings, Watcher watcher, ProcessStartInfo launchPsi,
                                        string polExe, string launchId, string? profile, IProgress<LaunchProgress>? progress,
                                        CancellationToken ct, Action<ProcessStamp>? hookStarted = null, Action? polStarted = null,
                                        LaunchLimits? limits = null)
    {
        var (outcome, started, text) = watcher.Retrying(
            () => Attempt(settings, watcher, launchPsi, polExe, launchId, profile, progress, ct,
                          hookStarted, polStarted, limits ?? LaunchLimits.Default),
            progress, ct, checkLog: false);
        switch (outcome)
        {
            case Watcher.LaunchOutcome.Started:
                return started.Pid;
            case Watcher.LaunchOutcome.Cancelled:
                progress?.Report(LaunchProgress.Cancelled(started.Closed));
                return null;
            default:
                progress?.Report(LaunchProgress.Failed(text));
                return null;
        }
    }

    // ends everything it started if it doesn't get there, so a retry starts from nothing
    private static (Watcher.LaunchOutcome Outcome, (int Pid, bool Closed) Value, string Failure) Attempt(
        ISettingsService settings, Watcher watcher, ProcessStartInfo launchPsi, string polExe, string launchId, string? profile,
        IProgress<LaunchProgress>? progress, CancellationToken ct, Action<ProcessStamp>? hookStarted, Action? polStarted, LaunchLimits limit)
    {
        const string name = "Ashita";
        (Watcher.LaunchOutcome, (int, bool), string) Fail(string text) => (Watcher.LaunchOutcome.Failed, default, text);
        var s = settings.Current;
        var target = PlayTarget.For(s, profile, ThirdPartyHook.Ashita);
        string? dir = target.Dir;
        if (dir is null) return Fail("Couldn't find Ashita. Set its folder in Settings, under Windower/Ashita.");

        string? boot = target.Profile.Length > 0 ? target.Profile : null;
        if (boot is not null && !AshitaBootFiles.IsBootFileName(boot))
            return Fail(target.FromAccount ? AccountBootFileProblem
                : "The Ashita boot configuration has to be a file in Ashita's config\\boot folder. Choose it again in Settings, under Windower/Ashita.");
        string bootFile;
        try { bootFile = Path.GetFileName(AshitaInstall.WriteBoot(dir, boot, polExe)); }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not write the Ashita boot config");
            return Fail(ErrorText.Of(ex, "Provenance couldn't make its copy of your Ashita boot configuration."));
        }
        // no shell, so it inherits the environment below
        var psi = new ProcessStartInfo(Path.Combine(dir, "Ashita-cli.exe")) { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(bootFile);
        HookEnvironment.ApplyContract(psi.Environment, launchPsi.Environment, launchId);

        var pidFile = HookFiles.PidFile(launchId);
        FileDelete.Try(pidFile);
        Process? hookProc = null;
        ProcessStamp hook;
        progress?.Report(new LaunchProgress("Starting", $"Starting {name}..."));
        Log.Information("Launching through {Platform} at {Dir}", name, dir);
        try
        {
            hookProc = Process.Start(psi) ?? throw new InvalidOperationException($"{name} did not start");
            hook = ProcessStamp.Of(hookProc.Id) ?? throw new InvalidOperationException($"{name}'s start time couldn't be read");
            hookStarted?.Invoke(hook);
        }
        catch (Exception ex)
        {
            hookProc?.Dispose();
            Log.Error(ex, "{Platform} did not start", name);
            return Fail($"Couldn't start {name}. Check its folder in Settings, under Windower/Ashita.");
        }
        // held open till the end: Windows won't reuse a pid while a handle to it is open, so a pol.exe under this pid
        // really is its child
        using var hookHandle = hookProc;
        int hookPid = hookProc.Id;
        using var tree = new LaunchTree(hookPid, hook.StartUtc);

        // Till a pol.exe turns up there's only the long limit, a cold start can keep Ashita-cli busy for a while. Then
        // the hook gets limit.Report to say it's in
        var startedAt = DateTime.UtcNow;
        DateTime? reportBy = null, hookGoneAt = null;
        bool saidSlow = false;
        int? attached = null;
        // not the viewer's (Ashita's own bootloader, say), or its path unreadable for 2s
        var skipped = new HashSet<int>();
        var tries = new Dictionary<int, int>();
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            if (reportBy is { } by ? now >= by : now - startedAt >= limit.Start) break;
            if (ReadPid(pidFile) is { } pid)
            {
                FileDelete.Try(pidFile);
                progress?.Report(new LaunchProgress("Starting", $"{name} has started PlayOnline."));
                return (Watcher.LaunchOutcome.Started, (pid, false), "");
            }
            bool hookGone = hookProc.HasExited;   // read first, any pol.exe it made is in the snapshot by then
            tree.Update();
            var pols = tree.Alive(PolExeName);
            // one whose start can't be read counts as turned up too, so it can't keep the wait going
            if (reportBy is null && (pols.Count > 0 || tree.Unreadable(PolExeName)))
            {
                reportBy = now + limit.Report;
                polStarted?.Invoke();
            }
            if (attached is null && pols.FirstOrDefault(m => !skipped.Contains(m.Pid)) is { Pid: > 0 } newPol)
            {
                var image = ProcessTree.ImagePath(newPol.Pid);
                if (image is not null && !SamePath(image, polExe))
                    skipped.Add(newPol.Pid);
                else if (image is null)
                {
                    // gives up after 2s, so it can't hide a later pol.exe
                    tries[newPol.Pid] = tries.GetValueOrDefault(newPol.Pid) + 1;
                    if (tries[newPol.Pid] >= 20) skipped.Add(newPol.Pid);
                }
                else
                {
                    skipped.Add(newPol.Pid);
                    attached = newPol.Pid;
                    var attach = watcher.Attach(newPol.Pid);
                    if (attach.Outcome != Watcher.LaunchOutcome.Started)
                    {
                        EndLaunch(hookProc, tree);
                        FileDelete.Try(pidFile);
                        return (attach.Outcome, default, attach.Failure);
                    }
                    Log.Information("Loaded the Provenance hook into {Platform}'s pol.exe (pid {Pid})", name, newPol.Pid);
                }
            }
            else if (reportBy is null && hookGone)
            {
                // it can close before its pol.exe shows up (Ashita's autoclose, a launcher that hands over), so a few
                // more secs, and as long as anything it started is still running
                if (tree.AnyAlive) hookGoneAt = null;
                else if ((hookGoneAt ??= now) + limit.HookGone <= now) break;
            }
            if (reportBy is null && !saidSlow && now - startedAt >= limit.SlowNote)
            {
                saidSlow = true;
                progress?.Report(new LaunchProgress("Starting", $"{name} is still starting. Press Cancel to stop it."));
            }
            Thread.Sleep(attached is null ? 100 : 250);
        }
        if (ct.IsCancellationRequested)
        {
            bool closed = EndLaunch(hookProc, tree);
            FileDelete.Try(pidFile);
            return (Watcher.LaunchOutcome.Cancelled, (0, closed), "");
        }
        // never reported, so nothing it started stays (a hooked one would be half signed in)
        EndLaunch(hookProc, tree);
        return Fail($"{name} started, but Provenance couldn't log in through it. Check {name} starts the game without Provenance, then try again.");
    }

    // how long a third-party launch gets: to start a pol.exe, to report once one's up, to start one after closing
    // itself, and when the slow-start line shows
    internal sealed record LaunchLimits(TimeSpan Start, TimeSpan Report, TimeSpan HookGone, TimeSpan SlowNote)
    {
        public static readonly LaunchLimits Default =
            new(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
    }

    // Ashita first so it can't start another, then everything left in its tree. A pol.exe without the hook carries on
    // into the viewer's own menu, the one thing nobody's meant to see
    internal static bool EndLaunch(Process hook, LaunchTree tree)
    {
        try
        {
            if (!hook.HasExited)
            {
                hook.Kill();
                hook.WaitForExit(3000);
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Could not end the third-party hook (pid {Pid})", hook.Id); }
        bool viewer = false;
        // again till a look finds nothing, a pol.exe can start one of its own (Ashita's bootloader, say) meanwhile
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        for (bool again = true; again && DateTime.UtcNow < until;)
        {
            again = false;
            tree.Update();
            // the tree holds every member open, so each pid's still the process it checked
            foreach (var (pid, start) in tree.Alive())
            {
                var stamp = new ProcessStamp(pid, start.Ticks);
                bool isViewer = stamp.IsViewerPol();
                var exe = Path.GetFileNameWithoutExtension(ProcessTree.ImagePath(pid)) ?? "a process";
                if (stamp.EndIfStill(afterMs: 3000) != ProcessStamp.Ending.Ended) continue;
                viewer |= isViewer;
                again = true;
                Log.Information("Ended {Exe} (pid {Pid}): the login through the third-party hook was stopped", exe, pid);
            }
        }
        return viewer;
    }

    private static bool SamePath(string? a, string b) =>
        a is not null && PathInside.Same(a, b);

    // null while it's missing or mid-write (the hook writes it with read sharing only)
    private static int? ReadPid(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return int.TryParse(SharedFile.ReadAllText(path).Trim(), out var pid) ? pid : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
