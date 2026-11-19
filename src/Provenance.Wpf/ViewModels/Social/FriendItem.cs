using System.Windows.Media;
using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels.Social;

public sealed record FriendItem(string Name, string Playing, string Comment, string StatusLabel,
                                bool Online, int Row, ImageSource? Avatar, int Unread, FriendSection Section)
{
    public PolStatus Status { get; init; }
    public bool HasStatus => StatusLabel.Length > 0;
    public string SectionTitle => Section.Title();
    public PartyState Party { get; init; }
    public string PartyIconFile => PartyIcons.For(Party);
    public bool HasPartyIcon => PartyIconFile.Length > 0;
    public string PartyTip => HasPartyIcon ? PartyText.Tip(Party) : "";
    public PartyIconView? PartyIcon => HasPartyIcon ? new PartyIconView(PartyIconFile, PartyTip) : null;
    public string Key { get; init; } = "";
    public bool Pending { get; init; }
    public int RequestMid { get; init; }
    public bool IsGroupInvite { get; init; }
    public bool IsMe { get; init; }
    public bool SenderIgnored { get; init; }
    public bool IsRequest => RequestMid > 0 && !IsGroupInvite;
    public bool HasUnread => Unread > 0;
    public bool InGame { get; init; }
    public (long Time, long Seq) NewestUnread { get; init; }
    public bool IsFriend => Row >= 0;
    public bool CanMessage => Row >= 0 && !Pending && !IsMe;

    public bool Is(FriendItem other) =>
        Key.Length > 0 && other.Key.Length > 0 ? Key == other.Key
                                               : string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
}
