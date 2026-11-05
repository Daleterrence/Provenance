using System.Diagnostics;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Hook;
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

    // a plain .ini name in config\boot, never a path that'd get read or written somewhere else as admin
    public static bool IsBootFileName(string boot) =>
        boot == Path.GetFileName(boot) && boot.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);

    // nothing POL_ or PROVENANCE_ from the user's environment, every POL_ value of this sign-in, plus the launch marker
    // and id. Never PROVENANCE_HOOK, the watcher's --attach loads the hook
    internal static void ApplyContract(IDictionary<string, string?> target, IDictionary<string, string?> launchEnv, string launchId)
    {
        LaunchEnvironment.Reset(target);
        foreach (var kv in launchEnv)
            if (kv.Key.StartsWith("POL_", StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                target[kv.Key] = kv.Value;
        target["PROVENANCE_LAUNCH"] = "1";
        target["PROVENANCE_LAUNCH_ID"] = launchId;
    }

    // Ashita-cli starts pol.exe itself, then the watcher's --attach loads the hook into it. Credentials only travel as
    // Credential Manager target names in the environment, never on a command line
    internal static int? StartViaAshita(ISettingsService settings, ProcessStartInfo launchPsi,
                                        string polExe, string launchId, string? profile,
                                        IProgress<LaunchProgress>? progress, CancellationToken ct,
                                        Action<int, DateTime>? hookStarted = null, Action? polStarted = null,
                                        LaunchLimits? limits = null)
    {
        const string name = "Ashita";
        var limit = limits ?? LaunchLimits.Default;
        int? Fail(string text)
        {
            progress?.Report(new LaunchProgress("Failed", text));
            return null;
        }
        var s = settings.Current;
        string? dir = AshitaInstall.Detect(s.AshitaPath);
        if (dir is null) return Fail("Couldn't find Ashita. Set its folder in Settings, under Windower/Ashita.");

        bool fromAccount = profile is { Length: > 0 };
        var boot = fromAccount ? profile
                 : string.IsNullOrWhiteSpace(s.AshitaBootConfig) ? AshitaInstall.LastUsedBootConfig(dir) : s.AshitaBootConfig;
        if (boot is { Length: > 0 } && !IsBootFileName(boot))
            return Fail(fromAccount ? AccountBootFileProblem
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
        ApplyContract(psi.Environment, launchPsi.Environment, launchId);

        var pidFile = HookFiles.PidFile(launchId);
        try { File.Delete(pidFile); } catch { }
        Process? hookProc = null;
        DateTime hookStart;
        progress?.Report(new LaunchProgress("Starting", $"Starting {name}...", LaunchSteps.StartPol));
        Log.Information("Launching through {Platform} at {Dir}", name, dir);
        try
        {
            hookProc = Process.Start(psi) ?? throw new InvalidOperationException($"{name} did not start");
            hookStart = hookProc.StartTime;
            hookStarted?.Invoke(hookProc.Id, hookStart);
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
        var hookStartUtc = ProcessTree.StartTime(hookPid) ?? hookStart.ToUniversalTime();
        using var tree = new LaunchTree(hookPid, hookStartUtc);

        // Till a pol.exe turns up there's only the long limit, a cold start can keep Ashita-cli busy for a while. Then
        // the hook gets limit.Report to say it's in
        var startedAt = DateTime.UtcNow;
        DateTime? reportBy = null, hookGoneAt = null;
        bool saidSlow = false;
        int? attached = null;
        // not the viewer's (Ashita's own bootloader, say), or its path unreadable for 2 secs
        var skipped = new HashSet<int>();
        var tries = new Dictionary<int, int>();
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            if (reportBy is { } by ? now >= by : now - startedAt >= limit.Start) break;
            if (ReadPid(pidFile) is { } pid)
            {
                try { File.Delete(pidFile); } catch { }
                progress?.Report(new LaunchProgress("Starting", $"{name} has started PlayOnline.", LaunchSteps.StartPol));
                return pid;
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
                    // gives up after 2 secs, so it can't hide a later pol.exe
                    tries[newPol.Pid] = tries.GetValueOrDefault(newPol.Pid) + 1;
                    if (tries[newPol.Pid] >= 20) skipped.Add(newPol.Pid);
                }
                else
                {
                    skipped.Add(newPol.Pid);
                    attached = newPol.Pid;
                    if (AttachHook(newPol.Pid) is { } attachError)
                    {
                        EndLaunch(hookProc, tree);
                        return Fail(attachError);
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
                progress?.Report(new LaunchProgress("Starting", $"{name} is still starting. Press Cancel to stop it.", LaunchSteps.StartPol));
            }
            Thread.Sleep(attached is null ? 100 : 250);
        }
        if (ct.IsCancellationRequested)
        {
            bool closed = EndLaunch(hookProc, tree);
            try { File.Delete(pidFile); } catch { }
            progress?.Report(new LaunchProgress("Stopped", closed ? "The login was cancelled, and PlayOnline has been closed."
                                                                  : "The login was cancelled."));
            return null;
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
            foreach (var (pid, _) in tree.Alive())
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    bool isViewer = PolProcess.IsViewerPol(p);
                    var exe = p.ProcessName;
                    p.Kill();
                    p.WaitForExit(3000);
                    viewer |= isViewer;
                    again = true;
                    Log.Information("Ended {Exe} (pid {Pid}): the login through the third-party hook was stopped", exe, pid);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return viewer;
    }

    private static bool SamePath(string? a, string b) =>
        a is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string? AttachHook(int pid)
    {
        var started = DateTime.UtcNow;
        var psi = new ProcessStartInfo(Path.Combine(HookFiles.NativeDir, HookFiles.WatcherExe))
        {
            WorkingDirectory = HookFiles.NativeDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--attach");
        psi.ArgumentList.Add(pid.ToString());
        psi.ArgumentList.Add(Path.Combine(HookFiles.NativeDir, "pol_hook.dll"));
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Provenance Watcher did not start");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } return "Provenance couldn't start logging in to PlayOnline in time. Try again."; }
            var stdout = outTask.GetAwaiter().GetResult();
            var stderr = errTask.GetAwaiter().GetResult().Trim();
            if (p.ExitCode == 0 && stdout.Contains("attached to pol.exe", StringComparison.Ordinal)) return null;
            Log.Error("Provenance Watcher --attach {Pid} failed: {Error}", pid, stderr);
            return CodeIntegrity.Refused(started, HookFiles.WatcherExe, "pol_hook.dll") ? CodeIntegrity.RefusedText
                 : NotPickedUp;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == CodeIntegrity.PolicyViolation)
        {
            Log.Warning(ex, "Windows refused to start Provenance Watcher.exe");
            return CodeIntegrity.RefusedText;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Provenance Watcher --attach {Pid} did not run", pid);
            return NotPickedUp;
        }
    }

    private const string NotPickedUp = "Provenance couldn't start logging in to PlayOnline. Try again.";

    // null while it's missing or mid-write (the hook writes it with read sharing only)
    private static int? ReadPid(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return int.TryParse(sr.ReadToEnd().Trim(), out var pid) ? pid : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
