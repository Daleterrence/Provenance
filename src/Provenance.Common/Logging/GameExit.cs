using Provenance.Common.Hook;

namespace Provenance.Common.Logging;

public static class GameExit
{
    public static string Reading(int? exitCode)
    {
        if (exitCode is not { } code) return "unknown";
        uint raw = unchecked((uint)code);
        string? what = raw switch
        {
            0 => "closed normally",
            1 => "ended from outside, as Task Manager does, or a general error",
            0xFFFFFFFF => "ended by force, as Provenance does",
            0xC0000005 => "crashed: access violation",
            0xC0000409 => "crashed: fail fast",
            0xC0000374 => "crashed: heap corruption",
            0xC00000FD => "crashed: stack overflow",
            0xC000001D => "crashed: illegal instruction",
            0xC0000142 => "crashed: a DLL failed to start",
            0xC0000135 => "crashed: a DLL is missing",
            _ => Enum.IsDefined((HookExit)code) ? $"the hook ended it: {(HookExit)code}" : null,
        };
        return what is null ? $"0x{raw:X8}" : $"0x{raw:X8} ({what})";
    }

    public static void Log(int pid, int? exitCode, TimeSpan? ran) =>
        Serilog.Log.Information("FINAL FANTASY XI ended: pol.exe {Pid}{Ran:l}, exit code {Code:l}",
                                pid, ran is { } t ? " ran for " + Span(t) : "", Reading(exitCode));

    private static string Span(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m {t.Seconds:00}s"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds:00}s"
        : $"{t.Seconds}s";
}
