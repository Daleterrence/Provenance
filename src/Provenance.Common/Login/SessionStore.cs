using System.Text.Json;
using System.Text.Json.Serialization;
using Provenance.Common.Game;
using Provenance.Common.Hook;
using Provenance.Common.Security;

namespace Provenance.Common.Login;

internal static class SessionStore
{
    // Pid 0 is a claim, start times are UTC ticks
    internal sealed record SessionFile(int Pid, long StartTicks, string? ProfileId, int OwnerPid = 0, long OwnerStartTicks = 0,
                                       bool Played = false)
    {
        [JsonIgnore] public ProcessStamp Game => new(Pid, StartTicks);
        [JsonIgnore] public ProcessStamp Owner => new(OwnerPid, OwnerStartTicks);
    }

    // admin-only, so nothing running as the user can plant a record that makes the app end a process
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

    internal static void WriteRecord(string path, ProcessStamp game, string? profileId, bool played = false)
    {
        var me = ProcessStamp.Of(Environment.ProcessId) ?? new ProcessStamp(Environment.ProcessId, 0);
        HookFiles.Transient(path, JsonSerializer.Serialize(
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
