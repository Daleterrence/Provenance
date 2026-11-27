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

    internal static bool PolEndedUnseen() => SessionStore.SessionFilePaths().Any(path =>
        SessionStore.TryReadRecord(path) is { Pid: > 0 } f && !f.Game.IsRunning() && !f.Owner.MightBeRunning());

    internal void Save(ProcessStamp stamp, string? profileId) => Write(stamp, profileId, played: false);

    internal void Park(ProcessStamp stamp) => Write(stamp, null, played: false, releaseClaim: false);

    internal void MarkPlayed()
    {
        if (_stamp is { } stamp) Write(stamp, Profile, played: true);
    }

    private void Write(ProcessStamp stamp, string? profileId, bool played, bool releaseClaim = true)
    {
        try
        {
            _stamp = stamp;
            Profile = profileId;
            Directory.CreateDirectory(SessionStore.SessionDir);
            var path = Path.Combine(SessionStore.SessionDir, $"{stamp.Pid}.json");
            SessionStore.WriteRecord(path, stamp, profileId, played);
            _sessionFile = path;
            if (releaseClaim) ReleaseClaim();
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
            if (Holder(profileId) is { } holder)
            {
                Log.Information("Account {Account} is in use: {How:l}", profileId, HeldHow(holder));
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
            // can't check means not safe, a login here could take another copy's session over
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
                if (s.Pid != 0)
                    Log.Debug("Session record of pol.exe {Pid} for account {Account} removed: that pol.exe isn't running any more", s.Pid, s.ProfileId ?? "nobody yet");
                FileDelete.Try(path);
                continue;
            }
            if (s.Played || ownerAlive) continue;
            EndOrphan(s.Game);
            FileDelete.Try(path);
        }
    }

    internal static bool HeldByAnotherCopy(string profileId) => Holder(profileId) is not null;

    private static SessionStore.SessionFile? Holder(string profileId)
    {
        foreach (var path in SessionStore.SessionFilePaths())
        {
            if (SessionStore.TryReadRecord(path) is not { } f || f.ProfileId != profileId) continue;
            if (f.Pid != 0 && !f.Game.IsRunning()) continue;
            if (f.Played) return f;
            if (f.OwnerPid != Environment.ProcessId && f.Owner.MightBeRunning()) return f;
        }
        return null;
    }

    private static string HeldHow(SessionStore.SessionFile holder) =>
        holder.Pid == 0 ? $"Provenance {holder.OwnerPid} is logging it in"
        : holder.Played ? $"pol.exe {holder.Pid} is in the game, started by Provenance {holder.OwnerPid}"
        : $"pol.exe {holder.Pid} is logged in, held by Provenance {holder.OwnerPid}";

    public static bool GameRunningFor(string profileId) => SessionStore.SessionFilePaths().Any(path =>
        SessionStore.TryReadRecord(path) is { Played: true } f && f.ProfileId == profileId && f.Game.IsRunning());

    private void EndOrphan(ProcessStamp game)
    {
        if (_ourPol.End(game, afterMs: 3000))
            Log.Warning("Ended pol.exe {Pid}: its copy of Provenance ended before the game started", game.Pid);
    }
}
