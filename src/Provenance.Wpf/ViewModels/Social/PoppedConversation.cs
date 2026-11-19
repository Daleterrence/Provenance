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

    public string Title => $"MESSAGES - {_friend.Name}";

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
