namespace Provenance.Common.Hook;

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
    public static readonly TimeSpan GoFull = 3 * PolcoreJob + TimeSpan.FromSeconds(30);
}
