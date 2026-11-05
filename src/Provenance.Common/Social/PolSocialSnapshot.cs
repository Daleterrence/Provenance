namespace Provenance.Common.Social;

// pol_hook.<pid>.social.json, rewritten every couple of secs. FfxiService is the Content ID check ("active" or
// "none"), Notice the account notice status (221..252 = LM-01..LM-32)
public sealed record PolSocialSnapshot(PolStatus MyStatus, string MyHandle, int MyProfilePictureId,
                                       IReadOnlyList<PolFriend> Friends, IReadOnlyList<PolMessage> Messages,
                                       string FfxiService = "active", int Notice = 0,
                                       string SendToken = "", string SendState = "", string Presence = "")
{
    public bool FriendsLoaded { get; init; } = true;
    public string Mail { get; init; } = "";   // "pending", "ok" or "failed"
    public bool StatusBusy { get; init; }
    // LM-02's day count (svc 0xd24), 0 when the hook didn't read one
    public int NoticeDays { get; init; }
    // viewer message 0x7007
    public bool BadPassword { get; init; }
    // chat lines, answers and group invites share the send slots, so a result can get bumped between snapshots. Time and
    // Seq are the sent message's header ones, 0 when nothing went
    public IReadOnlyList<(string Token, string State, long Time, long Seq)> SendResults { get; init; } = [];
    // for the lines that went: the handle slot they went from and whether they went to another POL ID
    public IReadOnlyList<(string Token, int FromSlot, bool ToOther)> SentFrom { get; init; } = [];
    // the lines the hook has, "sending" once its PUT's started, "queued" before
    public IReadOnlyList<(string Token, string State)> SendQueue { get; init; } = [];
    // the header time and seq of chat lines from my own POL ID the hook got back, which never reached anyone
    public IReadOnlyList<(long Time, long Seq, int FromSlot)> Returned { get; init; } = [];

    public string? SendStateFor(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (SendToken == token) return SendState;
        for (int i = SendResults.Count - 1; i >= 0; i--)
            if (SendResults[i].Token == token) return SendResults[i].State;
        return null;
    }

    // job states run "saving"/"searching"/"loading" -> "saved"/"done"/"ready", or "failed:..."
    public string EditToken { get; init; } = "";
    public string EditState { get; init; } = "";
    public string MyComment { get; init; } = "";
    public string MyKey { get; init; } = "";
    public IReadOnlyList<PolBlocked> Blocked { get; init; } = [];
    public string ProfilePictureToken { get; init; } = "";
    public string ProfilePictureState { get; init; } = "";
    public string SearchToken { get; init; } = "";
    public string SearchState { get; init; } = "";
    public IReadOnlyList<PolSearchHit> SearchResults { get; init; } = [];
    public FriendSearchKind SearchKind { get; init; }
    // how many POL found, which can be more than came back
    public int SearchTotal { get; init; }
    public bool SearchTooMany { get; init; }
    public string ProfileToken { get; init; } = "";
    public string ProfileState { get; init; } = "";
    public PolProfile? Profile { get; init; }
    public IReadOnlyList<PolGroup> GroupList { get; init; } = [];
    public string GroupToken { get; init; } = "";
    public string GroupState { get; init; } = "";
    public string MeToken { get; init; } = "";
    public string MeState { get; init; } = "";
    // one of my FFXI characters on the profile editor. Key is the character's, Level 0 till one's read or saved
    public string XiToken { get; init; } = "";
    public string XiState { get; init; } = "";
    public string XiKey { get; init; } = "";
    public int XiLevel { get; init; }
    public string XiName { get; init; } = "";
    public string XiWorldName { get; init; } = "";
    public string CharacterToken { get; init; } = "";
    public string CharacterState { get; init; } = "";
    public PolCharacterLookup? Character { get; init; }
    public string FfxiProfileToken { get; init; } = "";
    public string FfxiProfileState { get; init; } = "";
    public PolFfxiProfile? FfxiProfile { get; init; }
    // the hook runs one profile server job at a time, so while one's going any other just waits its turn
    public bool ProfileServerBusy => SearchState == "searching" || ProfileState == "loading" || CharacterState == "loading" ||
                                     FfxiProfileState == "loading" || MeState is "loading" or "saving" || XiState == "loading" ||
                                     ProfilePictureState == "saving" ||
                                     Chat is { ZonesState: "searching" } or { RoomsState: "searching" } ||
                                     (HandleState == "working" && HandleOp == "switch");
    // the game's message store, read once at login (pol_hook.<pid>.store.json), null when there's none
    public PolStoredLines? Stored { get; init; }
    public IReadOnlyList<PolHandle> Handles { get; init; } = [];
    public IReadOnlyList<PolContentId> ContentIds { get; init; } = [];
    public string HandleToken { get; init; } = "";
    public string HandleState { get; init; } = "";
    public string HandleOp { get; init; } = "";
    // null till chat rooms get used this session
    public ChatRoomsSnapshot? Chat { get; init; }
    public GroupChatSnapshot? GroupChat { get; init; }

    public static readonly PolSocialSnapshot Empty = new(PolStatus.AppearOffline, "", -1, [], []);
}
