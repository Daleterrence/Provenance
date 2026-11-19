using System.Diagnostics.Eventing.Reader;
using Serilog;

namespace Provenance.Common.Security;

// Smart App Control blocks a new unsigned build until Windows' cloud check knows it, so a fresh one can fail and then work
public static class CodeIntegrity
{
    private const string LogName = "Microsoft-Windows-CodeIntegrity/Operational";

    // ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION
    public const int PolicyViolation = 4551;

    // a refusal can last over an hour (seen one do exactly that, it's not fun), so no promise that waiting fixes it
    public const string RefusedText =
        "Windows Smart App Control blocked one of Provenance's files. Smart App Control blocks programs it doesn't recognise, and Provenance isn't digitally signed yet. Try again in a few minutes. If it's still blocked, you can't log in with this version of Provenance on this PC.";

    public static bool Refused(DateTime sinceUtc, params string[] fileNames)
    {
        try
        {
            var since = sinceUtc.ToUniversalTime().AddSeconds(-2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var query = new EventLogQuery(LogName, PathType.LogName,
                $"*[System[(EventID=3033 or EventID=3077) and TimeCreated[@SystemTime>='{since}']]]");
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    foreach (var p in record.Properties)
                        if (p.Value is string s && fileNames.Any(f => s.EndsWith("\\" + f, StringComparison.OrdinalIgnoreCase)))
                            return true;
                }
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "The Code Integrity log could not be read");
        }
        return false;
    }
}
