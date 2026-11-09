using System.Text.Json;
using Provenance.Common.IO;

namespace Provenance.Common.Otp;

// what's kept next to each tool: the version it said it was when it went in, and when an update was last looked for
internal sealed record ToolNote(string Version, DateTime Checked)
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);

    internal static bool IsDue(ToolNote? state)
    {
        if (state is null) return true;
        var age = DateTime.UtcNow - state.Checked;
        return age < TimeSpan.Zero || age >= CheckEvery;   // a clock that went back counts as due
    }

    private static string StatePath(string dir, string tool) => Path.Combine(dir, tool + ".json");

    internal static ToolNote? Read(string dir, string tool)
    {
        try { return JsonSerializer.Deserialize<ToolNote>(File.ReadAllText(StatePath(dir, tool))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // what the file said when it went in, so the own copy isn't asked before every run
    internal static System.Version? NotedVersion(string dir, string tool) => System.Version.TryParse(Read(dir, tool)?.Version, out var v) ? v : null;

    internal static void Write(string dir, string tool, ToolNote state)
    {
        var path = StatePath(dir, tool);
        AtomicFile.Write(path, JsonSerializer.Serialize(state));
    }
}
