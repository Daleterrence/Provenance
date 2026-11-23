using Provenance.Common.Hook;

namespace Provenance.Common.Social;

public sealed record PolSendResult(string Token, string State, long Time = 0, long Seq = 0, int FromSlot = -1,
                                   bool ToOther = false);

public sealed record PolQueuedSend(string Token, string State);

public sealed record PolReturnedSend(long Time, long Seq, int FromSlot);

public sealed record GameWrites(bool Send, bool Status, bool Edits)
{
    public static readonly GameWrites None = new(false, false, false);
}

// FfxiService is the Content ID check ("active" or "none"), Notice the account notice status (221..252 = LM-01..LM-32)
public sealed record PolSocialSnapshot(PolStatus MyStatus, string MyHandle, int MyProfilePictureId,
                                       IReadOnlyList<PolFriend> Friends, IReadOnlyList<PolMessage> Messages,
                                       string FfxiService = "active", int Notice = 0, string Presence = "")
{
    public HookJob SendJob { get; init; } = HookJob.None;
    public bool FriendsLoaded { get; init; } = true;
    // "live" while the game runs, "off" once the sampler's stopped for good, "" from the hold
    public string Sampler { get; init; } = "";
    // which writes the game side takes, null from the hold or a hook that predates them
    public GameWrites? GameWrites { get; init; }
    public int Beat { get; init; }
    public bool GameFriends { get; init; }
    public bool Lean { get; init; }
    public string Mail { get; init; } = "";
    public bool StatusBusy { get; init; }
    public int NoticeDays { get; init; }
    public bool BadPassword { get; init; }
    public IReadOnlyList<PolSendResult> SendResults { get; init; } = [];
    public IEnumerable<PolSendResult> AllSends => SendResults.Append(new PolSendResult(SendJob.Token, SendJob.State));
    public IReadOnlyList<PolQueuedSend> SendQueue { get; init; } = [];
    public IReadOnlyList<PolReturnedSend> Returned { get; init; } = [];
    // lines typed in the game that went whilst it ran, from the sampler
    public IReadOnlyList<PolSentLine> GameSent { get; init; } = [];

    public string? SendStateFor(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (SendJob.Token == token) return SendJob.State;
        return SendResults.LastOrDefault(r => r.Token == token)?.State;
    }

    public HookJob EditJob { get; init; } = HookJob.None;
    public string MyComment { get; init; } = "";
    public string MyKey { get; init; } = "";
    public IReadOnlyList<PolBlocked> Blocked { get; init; } = [];
    public HookJob PictureJob { get; init; } = HookJob.None;
    public HookJob SearchJob { get; init; } = HookJob.None;
    public SearchResult Search { get; init; } = SearchResult.None;
    public HookJob ProfileJob { get; init; } = HookJob.None;
    public PolProfile? Profile { get; init; }
    public IReadOnlyList<PolGroup> GroupList { get; init; } = [];
    public HookJob GroupJob { get; init; } = HookJob.None;
    public HookJob MeJob { get; init; } = HookJob.None;
    public HookJob CharacterJob { get; init; } = HookJob.None;
    public PolCharacterLookup? Character { get; init; }
    public HookJob FfxiProfileJob { get; init; } = HookJob.None;
    public PolFfxiProfile? FfxiProfile { get; init; }
    public bool ProfileServerBusy => SearchJob.State == "searching" || ProfileJob.State == "loading" || CharacterJob.State == "loading" ||
                                     FfxiProfileJob.State == "loading" || MeJob.State is "loading" or "saving" ||
                                     PictureJob.State == "saving" ||
                                     Chat is { ZonesJob.State: "searching" } or { RoomsJob.State: "searching" } ||
                                     (HandleJob.State == "working" && HandleOp == "switch");

    public PolStoredLines? Stored { get; init; }
    public IReadOnlyList<PolHandle> Handles { get; init; } = [];
    public IReadOnlyList<PolContentId> ContentIds { get; init; } = [];
    public HookJob HandleJob { get; init; } = HookJob.None;
    public string HandleOp { get; init; } = "";
    public bool HandleUnconfirmed { get; init; }
    public ChatRoomsSnapshot? Chat { get; init; }
    public GroupChatSnapshot? GroupChat { get; init; }

    public static readonly PolSocialSnapshot Empty = new(PolStatus.AppearOffline, "", -1, [], []);
}
