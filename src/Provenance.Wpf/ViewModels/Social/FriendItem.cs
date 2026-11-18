using System.Windows.Media;
using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels.Social;

// a friend, or someone who wrote but isn't on the list (Row -1). PlayingShort is the FFXI line without the world, for
// when that doesn't fit
public sealed record FriendItem(string Name, string Playing, string Comment, string StatusLabel,
                                bool Online, int Row, ImageSource? Avatar, int Unread, FriendSection Section)
{
    public PolStatus Status { get; init; }
    public bool HasStatus => StatusLabel.Length > 0;
    public string SectionTitle => Section.Title();
    public string PlayingShort { get; init; } = "";
    public string Key { get; init; } = "";   // the hook's hashed POL ID + handle, empty if it couldn't make one
    public bool Pending { get; init; }
    public int RequestMid { get; init; }
    public bool IsGroupInvite { get; init; }
    public bool IsMe { get; init; }           // one of my own handles on my list, it can only be removed
    public bool SenderIgnored { get; init; } // a request's sender is on my ignore list, for any handle or this one
    public bool IsRequest => RequestMid > 0 && !IsGroupInvite;
    public bool HasUnread => Unread > 0;
    public bool InGame { get; init; }
    public bool IsFriend => Row >= 0;
    public bool CanMessage => Row >= 0 && !Pending && !IsMe;

    public bool Is(FriendItem other) =>
        Key.Length > 0 && other.Key.Length > 0 ? Key == other.Key
                                               : string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
}
