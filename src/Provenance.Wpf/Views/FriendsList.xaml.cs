using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Provenance.Wpf.ViewModels;
using RequestAnswer = Provenance.Common.Social.SocialSnapshot.RequestAnswer;

namespace Provenance.Wpf.Views;

public partial class FriendsList : UserControl
{
    public FriendsList() => InitializeComponent();

    public event Action<FriendItem>? FriendActivated;

    public event Action<FriendItem>? FriendSelected;

    private SocialViewModel? Vm => DataContext as SocialViewModel;
    private Window? Owner => Window.GetWindow(this);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.IsKeyboardFocusWithin && List.SelectedItem is FriendItem f) FriendSelected?.Invoke(f);
    }

    // the row under the pointer, not whatever was selected before
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(List, e.OriginalSource as DependencyObject);
        if (row is ListBoxItem { DataContext: FriendItem f }) Activate(f);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;   // never on to the window's default button (Play)
        if (List.SelectedItem is FriendItem f) Activate(f);
    }

    // a request or invitation gets the viewer's answer window (Ignore lives on the right-click menu)
    private void Activate(FriendItem f)
    {
        if (!f.IsRequest && !f.IsGroupInvite)
        {
            FriendActivated?.Invoke(f);
            return;
        }
        if (Vm is not { CanEdit: true } vm) return;
        if (f.IsGroupInvite)
        {
            if (MessageDialog.Confirm(Owner, "Group invitation from " + f.Name, f.Comment, "ANSWER",
                                      "Accept: join the group", "Decline") is { } answer)
                vm.AnswerGroupInvite(f, answer == 0);
            return;
        }
        if (MessageDialog.Confirm(Owner, "Friend request from " + f.Name,
                $"{f.Name} would like to be your friend on PlayOnline.", "ANSWER",
                "Accept: add " + f.Name + " to your friend list", "Decline", destructive: true) is { } pick)
            vm.AnswerRequest(f, pick == 0 ? RequestAnswer.Accept : RequestAnswer.Decline);
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(List, e.OriginalSource as DependencyObject);
        if (row is not ListBoxItem { DataContext: FriendItem f } || Vm is null)
        {
            e.Handled = true;
            return;
        }
        FillMenu(List.ContextMenu ??= new ContextMenu(), f);
    }

    // the friends window's conversation header shows this menu too, so everything's reachable from a conversation
    public void FillMenu(ContextMenu menu, FriendItem f)
    {
        if (Vm is not { } vm) return;
        bool edit = vm.CanEdit && f.IsFriend && f.Key.Length > 0;
        menu.Items.Clear();
        if (f.IsGroupInvite)
        {
            menu.Items.Add(Item("Accept group invitation", vm.CanEdit, () => vm.AnswerGroupInvite(f, true)));
            menu.Items.Add(Item("Decline group invitation", vm.CanEdit, () => vm.AnswerGroupInvite(f, false)));
            return;
        }
        if (f.IsRequest)
        {
            menu.Items.Add(Item("Accept friend request", vm.CanEdit && !f.IsMe,
                                () => vm.AnswerRequest(f, RequestAnswer.Accept)));
            menu.Items.Add(Item("Decline friend request...", vm.CanEdit, Confirmed(Owner, "Decline friend request",
                $"Decline the friend request from {f.Name}?", "DECLINE",
                () => vm.AnswerRequest(f, RequestAnswer.Decline))));
            menu.Items.Add(new Separator());
            // the viewer's request window only ignores this one handle
            menu.Items.Add(Item("Ignore...", vm.CanEdit && !f.IsMe, Confirmed(Owner, "Ignore " + f.Name,
                $"Add {f.Name} to your ignore list? PlayOnline will decline the request, and this handle's messages won't reach you any more. You can stop ignoring them later from your ignore list.",
                "IGNORE", () => vm.AnswerRequest(f, RequestAnswer.Ignore))));
            return;
        }
        if (!f.IsFriend && !f.Pending)
        {
            menu.Items.Add(Item($"Add {f.Name} as a friend...", vm.CanEdit,
                () => new AddFriendDialog(vm, f.Name, f.Key) { Owner = Owner }.ShowDialog()));
            return;
        }
        menu.Items.Add(Item("Send message", f.CanMessage, () =>
        {
            vm.SelectedFriend = f;
            FriendActivated?.Invoke(f);
        }));
        menu.Items.Add(Item("View profile", f.IsFriend && !f.Pending, () => ProfileWindow.ShowFor(vm, f, Owner)));
        // the viewer's Invite into Group: only groups I'm master or sub-master of, and never myself
        var inviteTo = vm.Groups.Where(g => g.CanInvite && !g.Members.Any(m => m.Key.Length > 0 && m.Key == f.Key))
                                .ToList();
        if (inviteTo.Count > 0 && f.IsFriend && !f.Pending && !f.IsMe)
        {
            var sub = new MenuItem { Header = "Invite into group", IsEnabled = vm.CanEdit };
            foreach (var g in inviteTo) sub.Items.Add(Item(g.Name, vm.CanEdit, () => vm.InviteToGroup(g, f)));
            menu.Items.Add(sub);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Rename...", edit && !f.Pending, () => Rename(vm, f)));
        var removeBody = f.Pending
            ? $"Delete your friend request to {f.Name}? This takes it off your own friend list."
            : $"Remove {f.Name} from your PlayOnline friend list? Your saved messages with {f.Name} stay in Provenance.";
        menu.Items.Add(Item(f.Pending ? "Delete friend request..." : "Remove from friends...", edit,
            Confirmed(Owner, f.Pending ? "Delete friend request" : "Remove friend", removeBody,
                      f.Pending ? "DELETE" : "REMOVE", () => vm.RemoveFriend(f))));
        menu.Items.Add(Item("Ignore...", edit && !f.Pending && !f.IsMe, () => Ignore(vm, f)));
    }

    internal static MenuItem Item(string header, bool enabled, Action click)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        mi.Click += (_, _) => click();
        return mi;
    }

    // asks first (Enter and Esc on cancel), then does it
    internal static Action Confirmed(Window? owner, string title, string body, string ok, Action then) => () =>
    {
        if (MessageDialog.Confirm(owner, title, body, ok, destructive: true) is not null) then();
    };

    private void OnAddFriend(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) new AddFriendDialog(vm) { Owner = Owner }.ShowDialog();
    }

    private void Rename(SocialViewModel vm, FriendItem f)
    {
        if (Owner is not { } w) return;
        var name = InputDialog.Prompt(w, "Rename friend",
            $"Choose a new name for {f.Name} on your friend list (up to 15 English letters, numbers or symbols). " +
            "PlayOnline saves it with your friend list.", f.Name, maxLength: 15);
        // the same name has nothing to save, the viewer skips it too
        if (name is not null && name.Trim() != f.Name) vm.RenameFriend(f, name);
    }

    private void Ignore(SocialViewModel vm, FriendItem f)
    {
        var pick = MessageDialog.Confirm(Owner, "Ignore " + f.Name,
            $"PlayOnline will take {f.Name} off your friend list, and their messages won't reach you any more. " +
            "You can stop ignoring them later, but that won't put them back on your friend list.", "IGNORE",
            "Ignore this handle only", "Ignore every handle of this PlayOnline ID", destructive: true);
        if (pick is not null) vm.IgnoreFriend(f, pick == 1);
    }
}
