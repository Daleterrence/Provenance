using System.Text.Json;
using System.Text.Json.Serialization;
using Provenance.Common.Game;
using Provenance.Common.IO;
using Provenance.Common.Security;
using Serilog;

namespace Provenance.Common.Login;

public sealed class SessionRecords(Func<ProcessStamp, bool> ours, Func<ProcessStamp, bool> endOurs)
{
    private const string SessionClaimLock = "session-claim";

    // Pid 0 is a claim, start times are UTC ticks. The property names are the file's format, older copies read it too
    private sealed record SessionFile(int Pid, long StartTicks, string? ProfileId, int OwnerPid = 0, long OwnerStartTicks = 0,
                                      bool Played = false)
    {
        [JsonIgnore] public ProcessStamp Game => new(Pid, StartTicks);
        [JsonIgnore] public ProcessStamp Owner => new(OwnerPid, OwnerStartTicks);
    }

    // admin-only, so nothing running as the user can plant a record that makes the app end a process. Only a good
    // answer's kept, a failed check gets tried again next time
    private static readonly object SessionDirGate = new();
    private static string? _sessionDir;
    private static string SessionDir
    {
        get { lock (SessionDirGate) return _sessionDir ??= ProtectedFolders.SubDir("sessions"); }
    }

    private string? _sessionFile;
    private string? _claimFile;

    internal string? Profile { get; private set; }

    // a record whose pol.exe and copy have both gone, so nothing saw that pol.exe end
    internal static bool PolEndedUnseen() => SessionFilePaths().Any(path =>
        TryReadRecord(path) is { Pid: > 0 } f && !f.Game.IsRunning() && !f.Owner.MightBeRunning());

    private static SessionFile? TryReadRecord(string path)
    {
        try { return JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // write then move, another copy may read it at any moment
    private static void WriteRecord(string path, ProcessStamp game, string? profileId, bool played = false)
    {
        var me = ProcessStamp.Of(Environment.ProcessId) ?? new ProcessStamp(Environment.ProcessId, 0);
        AtomicFile.Write(path, JsonSerializer.Serialize(
            new SessionFile(game.Pid, game.StartUtcTicks, profileId, me.Pid, me.StartUtcTicks, played)));
    }

    private static List<string> SessionFilePaths()
    {
        var files = new List<string>();
        try { if (Directory.Exists(SessionDir)) files.AddRange(Directory.GetFiles(SessionDir, "*.json")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return files;
    }

    internal void Save(ProcessStamp stamp, string? profileId, bool played = false)
    {
        try
        {
            Profile = profileId;
            Directory.CreateDirectory(SessionDir);
            var path = Path.Combine(SessionDir, $"{stamp.Pid}.json");
            WriteRecord(path, stamp, profileId, played);
            _sessionFile = path;
            ReleaseClaim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Could not save the session record, another copy may not see this login");
        }
    }

    public bool TryClaimAccount(string profileId, out string? problem)
    {
        problem = null;
        try
        {
            using var claim = AdminLock.TryAcquire(SessionClaimLock, TimeSpan.FromSeconds(5));
            if (claim is null) { problem = "Another copy of Provenance is busy logging in. Try again in a moment."; return false; }
            if (HeldByAnotherCopy(profileId))
            {
                // multi-account mode lets go of its games, so a game's record might have no live copy
                problem = GameRunningFor(profileId)
                    ? "FINAL FANTASY XI is already running on this account. Log in again once you've closed that game."
                    : "Another copy of Provenance is already logged in to this account, or logging in. Use that copy, or log out there first.";
                return false;
            }
            ReleaseClaim();
            var path = Path.Combine(SessionDir, $"claim-{Environment.ProcessId}.json");
            WriteRecord(path, default, profileId);
            _claimFile = path;
            return true;
        }
        catch (Exception ex)
        {
            // can't check means not safe, a sign-in here could take another copy's session over
            Log.Warning(ex, "Could not claim account {Account}", profileId);
            problem = "Provenance couldn't check whether another copy of Provenance is using this account. Try again in a moment.";
            return false;
        }
    }

    public void ReleaseClaim()
    {
        if (_claimFile is { } f) FileDelete.Try(f);
        _claimFile = null;
    }

    internal void Clear()
    {
        if (_sessionFile is { } f) FileDelete.Try(f);
        _sessionFile = null;
    }

    internal void Forget()
    {
        _sessionFile = null;
        Profile = null;
    }

    // a copy that ended before Play can leave its pol.exe logged in (or still logging itself out), so it's ended here
    // and the next login starts fresh
    public void EndOrphans()
    {
        foreach (var path in SessionFilePaths())
        {
            // one that won't read may be another copy's mid-replace, so it's never deleted
            if (TryReadRecord(path) is not { } s) continue;
            bool ownerAlive = s.Owner.MightBeRunning();
            if (ownerAlive && s.OwnerPid != Environment.ProcessId) continue;
            if (!ours(s.Game))
            {
                FileDelete.Try(path);
                continue;
            }
            if (s.Played || ownerAlive) continue;
            EndOrphan(s.Game);
            FileDelete.Try(path);
        }
    }

    public static bool HeldByAnotherCopy(string profileId)
    {
        foreach (var path in SessionFilePaths())
        {
            if (TryReadRecord(path) is not { } f || f.ProfileId != profileId) continue;
            if (f.Pid != 0 && !f.Game.IsRunning()) continue;
            if (f.Played) return true;
            if (f.OwnerPid != Environment.ProcessId && f.Owner.MightBeRunning()) return true;
        }
        return false;
    }

    public static bool GameRunningFor(string profileId) => SessionFilePaths().Any(path =>
        TryReadRecord(path) is { Played: true } f && f.ProfileId == profileId && f.Game.IsRunning());

    private void EndOrphan(ProcessStamp pol)
    {
        if (endOurs(pol))
            Log.Warning("Ended pol.exe {Pid}: its copy of Provenance ended before the game started", pol.Pid);
    }
}
