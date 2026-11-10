namespace Provenance.Common.Hook;

public sealed record HookJob(string Token, string State)
{
    public static readonly HookJob None = new("", "");

    public string? StateFor(string? token) => !string.IsNullOrEmpty(token) && Token == token ? State : null;

    public static bool IsFailed(string? state) => state?.StartsWith("failed", StringComparison.Ordinal) == true;
}

// one job the app's waiting on. It has no clock of its own, every call takes the time
public sealed class HookJobWatch
{
    private DateTime _since;
    private TimeSpan _limit;

    public string? Token { get; private set; }
    public bool Running => Token is not null;

    public string Begin(TimeSpan limit, DateTime now) => Begin(HookFiles.NewToken(), limit, now);

    public string Begin(string token, TimeSpan limit, DateTime now)
    {
        Token = token;
        _limit = limit;
        _since = now;
        return token;
    }

    public void Forget() => Token = null;

    public void Restart(DateTime now) => _since = now;

    // profile server jobs go one at a time, so one that hasn't started yet shouldn't be timed
    public void WaitingTurn(string hookToken, bool hookBusy, DateTime now)
    {
        if (Running && hookToken != Token && hookBusy) _since = now;
    }

    public TimeSpan Elapsed(DateTime now) => now - _since;

    public bool Overdue(DateTime now) => Elapsed(now) > _limit;

    public bool Reached(DateTime now) => Elapsed(now) >= _limit;

    // the job's last word (ok or "failed:..."), "timeout" once it's overdue, or null while it's still going
    public string? Settle(string? state, string ok, DateTime now)
    {
        if (Token is null) return null;
        var end = state == ok || HookJob.IsFailed(state) ? state : Overdue(now) ? "timeout" : null;
        if (end is not null) Token = null;
        return end;
    }
}

// the only place the app's waits are written. Each is built from the limit it sits past
public static class HookLimits
{
    // polcore fails any job 100s after its slot started
    public static readonly TimeSpan PolcoreJob = TimeSpan.FromSeconds(100);
    // JOB_TIMEOUT_MS
    public static readonly TimeSpan HookSlotWait = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan HookRename = 2 * HookSlotWait;
    // CR_WAIT_MS
    public static readonly TimeSpan HookChatAnswer = TimeSpan.FromSeconds(360);
    // GC_GIVE_UP_MS and GC_SEND_MS
    public static readonly TimeSpan HookGroupChat = TimeSpan.FromSeconds(30);
    // hold_db_free
    public static readonly TimeSpan ProfileServerGap = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan FriendEdit = PolcoreJob + TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StatusRefused = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Status = PolcoreJob + TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Picture = 2 * PolcoreJob + TimeSpan.FromSeconds(50);
    public static readonly TimeSpan Answer = 2 * PolcoreJob + TimeSpan.FromSeconds(50);
    public static readonly TimeSpan Search = 2 * PolcoreJob + TimeSpan.FromSeconds(50);
    public static readonly TimeSpan FriendRequest = 2 * PolcoreJob + TimeSpan.FromSeconds(50);
    public static readonly TimeSpan Profile = PolcoreJob + TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FfxiProfile = PolcoreJob + TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Group = PolcoreJob + TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MyProfile = HookSlotWait + PolcoreJob + TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MyProfileRename = HookRename + TimeSpan.FromSeconds(20);
    public static readonly TimeSpan HandleUntaken = PolcoreJob + TimeSpan.FromSeconds(10);
    public static readonly TimeSpan HandleWorking = PolcoreJob + TimeSpan.FromSeconds(50);
    public static readonly TimeSpan CharacterLookup = PolcoreJob + TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ChatLine = HookChatAnswer + TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ChatMaster = HookChatAnswer + TimeSpan.FromSeconds(10);
    public static readonly TimeSpan GroupChatLine = HookGroupChat + TimeSpan.FromSeconds(5);
    public static readonly TimeSpan GroupChatJoin = HookGroupChat + TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MailInFlight = TimeSpan.FromMinutes(3);
}
