using System.Windows;
using Provenance.Common.Social;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Shared;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

// the windows and questions the friend and group menus open, over whichever window the menu belongs to
internal sealed class SocialMenuDialogs(SocialViewModel vm, Window? owner, Action<FriendItem>? openConversation = null)
    : ISocialMenuHost
{
    public bool Confirm(MenuConfirm question) => vm.Dialogs.Confirm(question);

    public void AddFriend(FriendItem f) => new AddFriendDialog(vm, f.Name, f.Key) { Owner = owner }.ShowDialog();

    public void ViewProfile(FriendItem f) => vm.Windows.ShowProfile(vm, f);

    public void OpenConversation(FriendItem f) => openConversation?.Invoke(f);

    public string? AskRename(FriendItem f) => vm.Dialogs.Ask("Rename friend",
        $"Choose a new name for {f.Name} on your friend list (up to 15 English letters, numbers or symbols). " +
        "PlayOnline saves it with your friend list.", f.Name, maxLength: PolLimits.HandleName);

    public int? AskIgnore(FriendItem f) => vm.Dialogs.Choose("Ignore " + f.Name,
        $"PlayOnline will take {f.Name} off your friend list, and their messages won't reach you any more. " +
        "You can stop ignoring them later, but that won't put them back on your friend list.", "IGNORE",
        "Ignore this handle only", "Ignore every handle of this PlayOnline ID", ConfirmStyle.Destructive);

    public string? AskGroupComment(SocialViewModel.GroupItem g, string mine) => vm.Dialogs.Ask(
        "Group comment", $"Your comment in \"{g.Name}\" (up to 49 characters).", mine, maxLength: PolLimits.Comment, allowEmpty: true);
}
