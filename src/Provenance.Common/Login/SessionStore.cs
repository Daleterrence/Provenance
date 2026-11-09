using System.Text.Json;
using System.Text.Json.Serialization;
using Provenance.Common.Game;
using Provenance.Common.IO;
using Provenance.Common.Security;

namespace Provenance.Common.Login;

internal static class SessionStore
{
    // Pid 0 is a claim, start times are UTC ticks. The property names are the file's format, older copies read it too
    internal sealed record SessionFile(int Pid, long StartTicks, string? ProfileId, int OwnerPid = 0, long OwnerStartTicks = 0,
                                       bool Played = false)
    {
        [JsonIgnore] public ProcessStamp Game => new(Pid, StartTicks);
        [JsonIgnore] public ProcessStamp Owner => new(OwnerPid, OwnerStartTicks);
    }

    // admin-only, so nothing running as the user can plant a record that makes the app end a process. Only a good
    // answer's kept, a failed check gets tried again next time
    private static readonly object SessionDirGate = new();
    private static string? _sessionDir;
    internal static string SessionDir
    {
        get { lock (SessionDirGate) return _sessionDir ??= ProtectedFolders.SubDir("sessions"); }
    }

    internal static SessionFile? TryReadRecord(string path)
    {
        try { return JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // write then move, another copy may read it at any moment
    internal static void WriteRecord(string path, ProcessStamp game, string? profileId, bool played = false)
    {
        var me = ProcessStamp.Of(Environment.ProcessId) ?? new ProcessStamp(Environment.ProcessId, 0);
        AtomicFile.Write(path, JsonSerializer.Serialize(
            new SessionFile(game.Pid, game.StartUtcTicks, profileId, me.Pid, me.StartUtcTicks, played)));
    }

    internal static List<string> SessionFilePaths()
    {
        var files = new List<string>();
        try { if (Directory.Exists(SessionDir)) files.AddRange(Directory.GetFiles(SessionDir, "*.json")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return files;
    }
}
