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
    public HashSet<int> ShownRequests { get; } = new();
    public HashSet<string> OnlineFriends { get; set; } = new();
    public DateTime? PresenceQuietUntil { get; set; }
    public HookJobWatch Edit { get; } = new();
    public HookJobWatch Picture { get; } = new();
    // my own characters' profiles as Edit profile read them, by the hook's key
    public Dictionary<string, PolFfxiProfile> OwnCharacterProfiles { get; } = new(StringComparer.Ordinal);
}
