using Provenance.Common.Game;
using Provenance.Common.IO;
using Provenance.Common.Security;
using Serilog;

namespace Provenance.Common.Login;

public sealed class SessionRecords
{
    private const string SessionClaimLock = "session-claim";

    private readonly OurPol _ourPol;

    internal SessionRecords(OurPol ourPol) => _ourPol = ourPol;

    private string? _sessionFile;
    private string? _claimFile;
    private ProcessStamp? _stamp;

    internal string? Profile { get; private set; }

    // a record whose pol.exe and copy have both gone, so nothing saw that pol.exe end
    internal static bool PolEndedUnseen() => SessionStore.SessionFilePaths().Any(path =>
        SessionStore.TryReadRecord(path) is { Pid: > 0 } f && !f.Game.IsRunning() && !f.Owner.MightBeRunning());

    internal void Save(ProcessStamp stamp, string? profileId) => Write(stamp, profileId, played: false);

    // the record stays as the game's, so no copy signs this account in on top of it
    internal void MarkPlayed()
    {
        if (_stamp is { } stamp) Write(stamp, Profile, played: true);
    }

    private void Write(ProcessStamp stamp, string? profileId, bool played)
    {
        try
        {
            _stamp = stamp;
            Profile = profileId;
            Directory.CreateDirectory(SessionStore.SessionDir);
            var path = Path.Combine(SessionStore.SessionDir, $"{stamp.Pid}.json");
            SessionStore.WriteRecord(path, stamp, profileId, played);
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
                    : "You're already logged in to this account in another copy of Provenance. If its window is closed, you can reopen it from the tray icon.";
                return false;
            }
            ReleaseClaim();
            var path = Path.Combine(SessionStore.SessionDir, $"claim-{Environment.ProcessId}.json");
            SessionStore.WriteRecord(path, default, profileId);
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
        _stamp = null;
    }

    internal void Forget()
    {
        _sessionFile = null;
        _stamp = null;
        Profile = null;
    }

    // a copy that ended before Play can leave its pol.exe logged in (or still logging itself out), so it's ended here
    // and the next login starts fresh
    public void EndOrphans()
    {
        foreach (var path in SessionStore.SessionFilePaths())
        {
            // one that won't read may be another copy's mid-replace, so it's never deleted
            if (SessionStore.TryReadRecord(path) is not { } s) continue;
            bool ownerAlive = s.Owner.MightBeRunning();
            if (ownerAlive && s.OwnerPid != Environment.ProcessId) continue;
            if (!_ourPol.Ours(s.Game))
            {
                FileDelete.Try(path);
                continue;
            }
            if (s.Played || ownerAlive) continue;
            EndOrphan(s.Game);
            FileDelete.Try(path);
        }
    }

    internal static bool HeldByAnotherCopy(string profileId)
    {
        foreach (var path in SessionStore.SessionFilePaths())
        {
            if (SessionStore.TryReadRecord(path) is not { } f || f.ProfileId != profileId) continue;
            if (f.Pid != 0 && !f.Game.IsRunning()) continue;
            if (f.Played) return true;
            if (f.OwnerPid != Environment.ProcessId && f.Owner.MightBeRunning()) return true;
        }
        return false;
    }

    public static bool GameRunningFor(string profileId) => SessionStore.SessionFilePaths().Any(path =>
        SessionStore.TryReadRecord(path) is { Played: true } f && f.ProfileId == profileId && f.Game.IsRunning());

    private void EndOrphan(ProcessStamp game)
    {
        if (_ourPol.End(game, afterMs: 3000))
            Log.Warning("Ended pol.exe {Pid}: its copy of Provenance ended before the game started", game.Pid);
    }
}
