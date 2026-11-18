using CommunityToolkit.Mvvm.ComponentModel;

namespace Provenance.Wpf.ViewModels.Social;

public sealed class PoppedConversation : ObservableObject
{
    private FriendItem _friend;

    internal PoppedConversation(SocialViewModel social, FriendItem friend)
    {
        _friend = friend;
        Messages = new SocialMessages(social, () => Friend);
    }

    public SocialMessages Messages { get; }
    public FriendItem Friend => _friend;

    // capitals like every other title, but a handle's spelt the way its owner spells it
    public string Title => $"MESSAGES - {_friend.Name}";

    // a presence update gives the same person a new row
    internal void Update(FriendItem friend)
    {
        if (ReferenceEquals(friend, _friend)) return;
        bool renamed = friend.Name != _friend.Name;
        _friend = friend;
        OnPropertyChanged(nameof(Friend));
        if (renamed) OnPropertyChanged(nameof(Title));
        Messages.SelectionChanged(friend);
    }
}
