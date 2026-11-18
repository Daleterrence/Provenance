using Provenance.Common.Hook;
using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels.Social;

internal sealed class SocialSession
{
    public SocialSession()
    {
        Sends = new MailSendQueue();
        Sync = new ChatSync(Sends);
    }

    public ChatStore? Chat { get; set; }
    // the requests this account answered, so one that comes round at a later login is finished unseen
    public AnsweredRequests? Remembered { get; set; }
    public PolSocialSnapshot Last { get; set; } = PolSocialSnapshot.Empty;
    public bool FriendsDirty { get; set; } = true;
    public PolStatus? RequestedStatus { get; set; }
    public DateTime RequestedAt { get; set; }
    public string? PopError { get; set; }
    public DateTime PopErrorUntil { get; set; }
    public MailSendQueue Sends { get; }
    public ChatSync Sync { get; }
    public HashSet<int> AckPending { get; } = new();
    public HashSet<int> Answered { get; } = new();
    // answers on their way, which stop counting as waiting till one fails
    public HashSet<int> Answering { get; } = new();
    public HashSet<int> ShownRequests { get; } = new();
    public HashSet<string> OnlineFriends { get; set; } = new();
    public DateTime? PresenceQuietUntil { get; set; }
    // unsent text per conversation, shared so it follows a conversation between the pane and a pop-out
    public Dictionary<string, string> Drafts { get; } = new();
    // friends held in UNREAD while their conversation's open, by id, with where they were
    public Dictionary<string, (long Time, long Seq)> Held { get; } = new();
    public HookJobWatch Edit { get; } = new();
    public HookJobWatch Picture { get; } = new();
    // my own characters' profiles as Edit profile read them, by the hook's key
    public Dictionary<string, PolFfxiProfile> OwnCharacterProfiles { get; } = new(StringComparer.Ordinal);
}
