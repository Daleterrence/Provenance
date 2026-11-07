using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Social;

// the windows and questions a friend or group menu needs, which the view model can't open itself
public interface ISocialMenuHost
{
    bool Confirm(MenuConfirm question);
    void AddFriend(FriendItem f);
    void ViewProfile(FriendItem f);
    void OpenConversation(FriendItem f);
    string? AskRename(FriendItem f);
    int? AskIgnore(FriendItem f);
    string? AskGroupComment(SocialViewModel.GroupItem g, string mine);
}
