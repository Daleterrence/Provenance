using System.Windows;
using Provenance.Common.Social;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.ViewModels.Shared;
using Provenance.Wpf.ViewModels.Social;
using Provenance.Wpf.Views.Dialogs;

namespace Provenance.Wpf.Views.Social;

// the windows and questions the friend and group menus open, over whichever window the menu belongs to
internal sealed class SocialMenuDialogs(SocialViewModel vm, Window? owner, Action<FriendItem>? openConversation = null)
    : ISocialMenuHost
{
    public bool Confirm(MenuConfirm question) => Menus.Confirm(owner)(question);

    public void AddFriend(FriendItem f) => new AddFriendDialog(vm, f.Name, f.Key) { Owner = owner }.ShowDialog();

    public void ViewProfile(FriendItem f) => ProfileWindow.ShowFor(vm, f, owner);

    public void OpenConversation(FriendItem f) => openConversation?.Invoke(f);

    public string? AskRename(FriendItem f) => owner is not { } w ? null : InputDialog.Prompt(w, "Rename friend",
        $"Choose a new name for {f.Name} on your friend list (up to 15 English letters, numbers or symbols). " +
        "PlayOnline saves it with your friend list.", f.Name, maxLength: PolLimits.HandleName);

    public int? AskIgnore(FriendItem f) => MessageDialog.Confirm(owner, "Ignore " + f.Name,
        $"PlayOnline will take {f.Name} off your friend list, and their messages won't reach you any more. " +
        "You can stop ignoring them later, but that won't put them back on your friend list.", "IGNORE",
        "Ignore this handle only", "Ignore every handle of this PlayOnline ID", destructive: true);

    public string? AskGroupComment(SocialViewModel.GroupItem g, string mine) => InputDialog.Prompt(owner!, "Group comment",
        $"Your comment in \"{g.Name}\" (up to 49 characters).", mine, maxLength: PolLimits.Comment, allowEmpty: true);
}
