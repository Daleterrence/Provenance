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

    // credentials go in the environment as Credential Manager target names, never on a command line
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
            Log.Error(ex, "Could not write the Ashita boot configuration");
            return Fail(ErrorText.Of(ex, "Provenance couldn't make its copy of your Ashita boot configuration."));
        }
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
            try { if (hookProc is { HasExited: false }) hookProc.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            hookProc?.Dispose();
            Log.Error(ex, "{Platform} did not start", name);
            return Fail($"Couldn't start {name}. Check its folder in Settings, under Windower/Ashita.");
        }
        // held open till the end, Windows won't reuse a pid while a handle to it is open
        using var hookHandle = hookProc;
        int hookPid = hookProc.Id;
        using var tree = new LaunchTree(hookPid, hook.StartUtc);

        var startedAt = DateTime.UtcNow;
        DateTime? reportBy = null, hookGoneAt = null;
        bool saidSlow = false;
        int? attached = null;
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
        EndLaunch(hookProc, tree);
        return Fail($"{name} started, but Provenance couldn't log in through it. Check {name} starts the game without Provenance, then try again.");
    }

    internal sealed record LaunchLimits(TimeSpan Start, TimeSpan Report, TimeSpan HookGone, TimeSpan SlowNote)
    {
        public static readonly LaunchLimits Default =
            new(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
    }

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
        catch (Exception ex) { Log.Warning(ex, "Could not end Windower or Ashita (pid {Pid})", hook.Id); }
        bool viewer = false;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        for (bool again = true; again && DateTime.UtcNow < until;)
        {
            again = false;
            tree.Update();
            foreach (var (pid, start) in tree.Alive())
            {
                var stamp = new ProcessStamp(pid, start.Ticks);
                bool isViewer = stamp.IsViewerPol();
                var exe = Path.GetFileNameWithoutExtension(ProcessTree.ImagePath(pid)) ?? "a process";
                if (stamp.EndIfStill(afterMs: 3000) != ProcessStamp.Ending.Ended) continue;
                viewer |= isViewer;
                again = true;
                Log.Information("Ended {Exe} (pid {Pid}): the login through Windower or Ashita was stopped", exe, pid);
            }
        }
        return viewer;
    }

    private static bool SamePath(string? a, string b) =>
        a is not null && PathInside.Same(a, b);

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
