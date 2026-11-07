using System.ComponentModel;
using System.Diagnostics;
using Provenance.Common.Security;

namespace Provenance.Common.Hook;

public sealed record WatcherRun(int ExitCode, string Out, string Err);

// Provenance Watcher starts or attaches to pol.exe and puts pol_hook.dll in it. Each launch path builds the same
// process, waits for it the same way and asks the same questions when Windows refuses it
public static class WatcherProcess
{
    public static string HookDllPath => Path.Combine(HookFiles.NativeDir, HookFiles.HookDll);

    public static ProcessStartInfo StartInfo(string watcherExe) => new(watcherExe)
    {
        WorkingDirectory = HookFiles.NativeDir, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
    };

    // both pipes, a full stderr pipe would block the watcher forever. Null when it's still going at the limit
    public static WatcherRun? Wait(Process watcher, TimeSpan limit)
    {
        var outTask = watcher.StandardOutput.ReadToEndAsync();
        var errTask = watcher.StandardError.ReadToEndAsync();
        if (!watcher.WaitForExit(limit)) return null;
        return new(watcher.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    // Windows' Smart App Control stopped it before it ran
    public static bool RefusedToStart(Win32Exception e) => e.NativeErrorCode == CodeIntegrity.PolicyViolation;

    // or stopped it, or the hook, after it started
    public static bool Refused(DateTime sinceUtc) => CodeIntegrity.Refused(sinceUtc, HookFiles.WatcherExe, HookFiles.HookDll);
}
