namespace Provenance.Common.Social;

// Time and Seq are the sent message's header ones, 0 when nothing went. FromSlot is the handle slot a line that went
// came from (-1 for none) and ToOther whether it went to another POL ID
public sealed record PolSendResult(string Token, string State, long Time = 0, long Seq = 0, int FromSlot = -1,
                                   bool ToOther = false);

public sealed record PolQueuedSend(string Token, string State);

// the token the app gave the hook for a job and the state the hook has it in. Both are empty when the hook has none
public sealed record HookJob(string Token, string State)
{
    public static readonly HookJob None = new("", "");
}

// Time and Seq are the header ones of the line that came back
public sealed record PolReturnedSend(long Time, long Seq, int FromSlot);

// pol_hook.<pid>.social.json, rewritten every couple of secs. FfxiService is the Content ID check ("active" or
// "none"), Notice the account notice status (221..252 = LM-01..LM-32)
public sealed record PolSocialSnapshot(PolStatus MyStatus, string MyHandle, int MyProfilePictureId,
                                       IReadOnlyList<PolFriend> Friends, IReadOnlyList<PolMessage> Messages,
                                       string FfxiService = "active", int Notice = 0, string Presence = "")
{
    public HookJob SendJob { get; init; } = HookJob.None;
    public bool FriendsLoaded { get; init; } = true;
    public string Mail { get; init; } = "";   // "pending", "ok" or "failed"
    public bool StatusBusy { get; init; }
    // LM-02's day count (svc 0xd24), 0 when the hook didn't read one
    public int NoticeDays { get; init; }
    // viewer message 0x7007
    public bool BadPassword { get; init; }
    // chat lines, answers and group invites share the send slots, so a result can get bumped between snapshots
    public IReadOnlyList<PolSendResult> SendResults { get; init; } = [];
    // the results plus the one the send slot holds right now
    public IEnumerable<PolSendResult> AllSends => SendResults.Append(new PolSendResult(SendJob.Token, SendJob.State));
    // the lines the hook has, "sending" once its PUT's started, "queued" before
    public IReadOnlyList<PolQueuedSend> SendQueue { get; init; } = [];
    // chat lines from my own POL ID the hook got back, which never reached anyone
    public IReadOnlyList<PolReturnedSend> Returned { get; init; } = [];

    public string? SendStateFor(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (SendJob.Token == token) return SendJob.State;
        return SendResults.LastOrDefault(r => r.Token == token)?.State;
    }

    // job states run "saving"/"searching"/"loading" -> "saved"/"done"/"ready", or "failed:..."
    public HookJob EditJob { get; init; } = HookJob.None;
    public string MyComment { get; init; } = "";
    public string MyKey { get; init; } = "";
    public IReadOnlyList<PolBlocked> Blocked { get; init; } = [];
    public HookJob PictureJob { get; init; } = HookJob.None;
    public HookJob SearchJob { get; init; } = HookJob.None;
    public IReadOnlyList<PolSearchHit> SearchResults { get; init; } = [];
    public FriendSearchKind SearchKind { get; init; }
    // how many POL found, which can be more than came back
    public int SearchTotal { get; init; }
    public bool SearchTooMany { get; init; }
    public HookJob ProfileJob { get; init; } = HookJob.None;
    public PolProfile? Profile { get; init; }
    public IReadOnlyList<PolGroup> GroupList { get; init; } = [];
    public HookJob GroupJob { get; init; } = HookJob.None;
    public HookJob MeJob { get; init; } = HookJob.None;
    // one of my FFXI characters on the profile editor. Key is the character's
    public HookJob XiJob { get; init; } = HookJob.None;
    public string XiKey { get; init; } = "";
    public PrivacyLevel XiLevel { get; init; }
    public string XiName { get; init; } = "";
    public string XiWorldName { get; init; } = "";
    public HookJob CharacterJob { get; init; } = HookJob.None;
    public PolCharacterLookup? Character { get; init; }
    public HookJob FfxiProfileJob { get; init; } = HookJob.None;
    public PolFfxiProfile? FfxiProfile { get; init; }
    // the hook runs one profile server job at a time, so while one's going any other just waits its turn
    public bool ProfileServerBusy => SearchJob.State == "searching" || ProfileJob.State == "loading" || CharacterJob.State == "loading" ||
                                     FfxiProfileJob.State == "loading" || MeJob.State is "loading" or "saving" || XiJob.State == "loading" ||
                                     PictureJob.State == "saving" ||
                                     Chat is { ZonesState: "searching" } or { RoomsState: "searching" } ||
                                     (HandleJob.State == "working" && HandleOp == "switch");
    // the game's message store, read once at login (pol_hook.<pid>.store.json), null when there's none
    public PolStoredLines? Stored { get; init; }
    public IReadOnlyList<PolHandle> Handles { get; init; } = [];
    public IReadOnlyList<PolContentId> ContentIds { get; init; } = [];
    public HookJob HandleJob { get; init; } = HookJob.None;
    public string HandleOp { get; init; } = "";
    // null till chat rooms get used this session
    public ChatRoomsSnapshot? Chat { get; init; }
    public GroupChatSnapshot? GroupChat { get; init; }

    public static readonly PolSocialSnapshot Empty = new(PolStatus.AppearOffline, "", -1, [], []);
}
