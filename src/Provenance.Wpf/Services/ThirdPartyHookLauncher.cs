using System.Diagnostics;
using System.IO;
using Provenance.Common.Game;
using Provenance.Common.Social;
using Serilog;

namespace Provenance.Wpf.Services;

public static class ThirdPartyHookLauncher
{
    private const string PolExeName = "pol.exe";

    internal const string AccountBootFileProblem =
        "The Ashita boot configuration chosen for this account has to be a file in Ashita's config\\boot folder. Choose it again in Settings, under Multi-account.";

    // a plain .ini name in config\boot, never a path that'd get read or written somewhere else as admin
    internal static bool IsBootFileName(string boot) =>
        boot == Path.GetFileName(boot) && boot.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);

    // nothing POL_ or PROVENANCE_ from the user's environment, every POL_ value of this sign-in, plus the launch marker
    // and id. Never PROVENANCE_HOOK, pol_launch --attach loads the hook
    internal static void ApplyContract(IDictionary<string, string?> target, IDictionary<string, string?> launchEnv, string launchId)
    {
        LaunchEnvironment.Reset(target);
        foreach (var kv in launchEnv)
            if (kv.Key.StartsWith("POL_", StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                target[kv.Key] = kv.Value;
        target["PROVENANCE_LAUNCH"] = "1";
        target["PROVENANCE_LAUNCH_ID"] = launchId;
    }

    // Windower/Ashita start pol.exe themselves, then pol_launch --attach loads the hook into it. Credentials only
    // travel as Credential Manager target names in the environment, never on a command line
    internal static int? StartViaThirdPartyHook(ISettingsService settings, string platform, ProcessStartInfo launchPsi,
                                       string polExe, string launchId, string? profile,
                                       IProgress<LaunchProgress>? progress, CancellationToken ct,
                                       Action<int, DateTime>? hookStarted = null)
    {
        int? Fail(string text)
        {
            progress?.Report(new LaunchProgress("Failed", text));
            return null;
        }
        var s = settings.Current;
        bool ashita = platform == ThirdPartyHookService.Ashita;
        string? dir = ashita ? ThirdPartyHookService.DetectAshita(s.AshitaPath) : ThirdPartyHookService.DetectWindower(s.WindowerPath);
        var name = ashita ? "Ashita" : "Windower";
        if (dir is null) return Fail($"Couldn't find {name}. Set its folder in Settings, under Windower/Ashita.");

        // no shell, so it inherits the environment below
        var psi = new ProcessStartInfo { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = ashita };
        if (ashita)
        {
            bool fromAccount = profile is { Length: > 0 };
            var boot = fromAccount ? profile
                     : string.IsNullOrWhiteSpace(s.AshitaBootConfig) ? ThirdPartyHookService.LastUsedAshitaBootConfig(dir) : s.AshitaBootConfig;
            if (boot is { Length: > 0 } && !IsBootFileName(boot))
                return Fail(fromAccount ? AccountBootFileProblem
                    : "The Ashita boot configuration has to be a file in Ashita's config\\boot folder. Choose it again in Settings, under Windower/Ashita.");
            string bootFile;
            try { bootFile = Path.GetFileName(ThirdPartyHookService.WriteAshitaBoot(dir, boot, polExe)); }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not write the Ashita boot config");
                return Fail("Provenance couldn't make its copy of your Ashita boot configuration.");
            }
            psi.FileName = Path.Combine(dir, "Ashita-cli.exe");
            psi.ArgumentList.Add(bootFile);
        }
        else
        {
            string copy;
            try { copy = ThirdPartyHookService.PrepareWindowerProfile(dir, profile ?? s.WindowerProfile); }
            catch (Exception ex)
            {
                // Windower's fast login would take the sign-in over, so no copy, no launch
                Log.Error(ex, "Could not write Provenance's Windower profile");
                return Fail("Provenance couldn't make its copy of your Windower profile.");
            }
            psi.FileName = Path.Combine(dir, "Windower.exe");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(copy);
        }
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

        var until = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        (int Pid, DateTime Start)? attached = null;
        // not the viewer's, older than Windower/Ashita (another account's, say), or unreadable for 2 secs
        var skipped = new HashSet<int>();
        var tries = new Dictionary<int, int>();
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            if (ReadPid(pidFile) is { } pid)
            {
                try { File.Delete(pidFile); } catch { }
                progress?.Report(new LaunchProgress("Starting", $"{name} has started PlayOnline.", LaunchSteps.StartPol));
                return pid;
            }
            if (attached is null && ProcessTree.FindDescendant(ProcessTree.Snapshot(), hookPid, PolExeName, skipped) is { } newPol)
            {
                var image = ProcessTree.ImagePath(newPol);
                var polStart = StartTime(newPol);
                if ((image is not null && !SamePath(image, polExe)) || polStart < hookStart)
                    skipped.Add(newPol);   // Ashita's own bootloader\pol.exe, say
                else if (image is null || polStart is null)
                {
                    // gives up after 2 secs, so it can't hide a later pol.exe
                    tries[newPol] = tries.GetValueOrDefault(newPol) + 1;
                    if (tries[newPol] >= 20) skipped.Add(newPol);
                }
                else
                {
                    skipped.Add(newPol);
                    attached = (newPol, polStart.Value);
                    if (AttachHook(newPol) is { } attachError)
                    {
                        KillIfPol(newPol, polStart);
                        return Fail(attachError);
                    }
                    Log.Information("Loaded the Provenance hook into {Platform}'s pol.exe (pid {Pid})", name, newPol);
                }
            }
            Thread.Sleep(attached is null ? 100 : 250);
        }
        if (ct.IsCancellationRequested)
        {
            // a pol.exe may already be signing in. It gets a few secs to report, then it's ended so it can't hold the
            // account or the single-instance gate
            var grace = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            int? late = null;
            while (DateTime.UtcNow < grace && late is null)
            {
                if (ReadPid(pidFile) is { } lp) late = lp;
                else Thread.Sleep(250);
            }
            try { File.Delete(pidFile); } catch { }
            bool closed = late is { } kp ? KillIfPol(kp) : attached is { } a && KillIfPol(a.Pid, a.Start);
            progress?.Report(new LaunchProgress("Stopped", closed
                ? $"The login was cancelled, and PlayOnline has been closed. {name} might still be open."
                : $"The login was cancelled before {name} started PlayOnline. {name} might still be open."));
            return null;
        }
        // never reported, and a pol.exe the hook got loaded into mustn't stay half signed in
        if (attached is { } stuck) KillIfPol(stuck.Pid, stuck.Start);
        return Fail($"{name} started, but Provenance couldn't log in through it. Check {name} starts PlayOnline from your PlayOnline Viewer folder.");
    }

    private static DateTime? StartTime(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime; }
        catch { return null; }
    }

    private static bool SamePath(string? a, string b) =>
        a is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string? AttachHook(int pid)
    {
        var started = DateTime.UtcNow;
        var psi = new ProcessStartInfo(Path.Combine(ThirdPartyHookService.NativeDir, "pol_launch.exe"))
        {
            WorkingDirectory = ThirdPartyHookService.NativeDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--attach");
        psi.ArgumentList.Add(pid.ToString());
        psi.ArgumentList.Add(Path.Combine(ThirdPartyHookService.NativeDir, "pol_hook.dll"));
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("pol_launch did not start");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } return "Provenance couldn't start logging in to PlayOnline in time. Try again."; }
            var stdout = outTask.GetAwaiter().GetResult();
            var stderr = errTask.GetAwaiter().GetResult().Trim();
            if (p.ExitCode == 0 && stdout.Contains("attached to pol.exe", StringComparison.Ordinal)) return null;
            Log.Error("pol_launch --attach {Pid} failed: {Error}", pid, stderr);
            return CodeIntegrity.Refused(started, "pol_launch.exe", "pol_hook.dll") ? CodeIntegrity.RefusedText
                 : NotPickedUp;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == CodeIntegrity.PolicyViolation)
        {
            Log.Warning(ex, "Windows refused to start pol_launch.exe");
            return CodeIntegrity.RefusedText;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "pol_launch --attach {Pid} did not run", pid);
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

    // only a real viewer pol.exe, and with started only that exact process, so a reused pid never gets killed
    private static bool KillIfPol(int pid, DateTime? started = null)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!PolProcess.IsViewerPol(p)) return false;
            if (started is { } st && p.StartTime != st) return false;
            p.Kill();
            p.WaitForExit(3000);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not stop pid {Pid}", pid);
            return false;
        }
    }
}
