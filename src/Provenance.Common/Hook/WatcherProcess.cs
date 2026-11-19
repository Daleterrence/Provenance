using System.ComponentModel;
using System.Diagnostics;
using Provenance.Common.Security;

namespace Provenance.Common.Hook;

public sealed record WatcherRun(int ExitCode, string Out, string Err);

public static class WatcherProcess
{
    public static string HookDllPath => Path.Combine(HookFiles.NativeDir, HookFiles.HookDll);

    public static ProcessStartInfo StartInfo(string watcherExe) => new(watcherExe)
    {
        WorkingDirectory = HookFiles.NativeDir, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
    };

    // both pipes, a full stderr pipe would block the watcher forever
    public static WatcherRun? Wait(Process watcher, TimeSpan limit)
    {
        var outTask = watcher.StandardOutput.ReadToEndAsync();
        var errTask = watcher.StandardError.ReadToEndAsync();
        if (!watcher.WaitForExit(limit)) return null;
        return new(watcher.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    public static bool RefusedToStart(Win32Exception e) => e.NativeErrorCode == CodeIntegrity.PolicyViolation;

    public static bool Refused(DateTime sinceUtc) => CodeIntegrity.Refused(sinceUtc, HookFiles.WatcherExe, HookFiles.HookDll);
}
