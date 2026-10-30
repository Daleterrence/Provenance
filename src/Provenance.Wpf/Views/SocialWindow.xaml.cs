using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Provenance.Common.Social;
using Provenance.Wpf.ViewModels;
using Layout = Provenance.Wpf.ViewModels.SocialViewModel.LayoutAction;

namespace Provenance.Wpf.Views;

public enum SocialPart { Both, Friends, Messages }

// split, it's two windows on one view model, neither owned by the main window so each lives its own life
public partial class SocialWindow : Window
{
    public SocialPart Part { get; }
    private bool HasMessages => Part != SocialPart.Friends;
    private bool HasFriends => Part != SocialPart.Messages;
    private SocialViewModel? Vm => DataContext as SocialViewModel;

    public SocialWindow(SocialPart part)
    {
        Part = part;
        InitializeComponent();
        WindowFrame.Attach(this);
        ApplyPart();
        // split: a click shows the Messages window alongside (focus stays in the list), a double-click brings it up
        FriendList.FriendSelected += _ => { if (Part == SocialPart.Friends) Vm?.RequestLayout(Layout.ShowMessages); };
        FriendList.FriendActivated += _ => { if (Part == SocialPart.Friends) Vm?.RequestLayout(Layout.OpenMessages); };
        Loaded += (_, _) => { if (Part == SocialPart.Messages && ShowActivated) ComposeBox.Focus(); };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SocialViewModel old) Detach(old);
            if (e.NewValue is SocialViewModel vm)
            {
                if (HasMessages) vm.Conversation.CollectionChanged += OnConversationChanged;
                vm.PropertyChanged += OnVmChanged;
                vm.MessageReceived += OnMessageLanded;
                vm.NoticeReceived += OnMessageLanded;
                vm.FriendRequestReceived += OnRequestLanded;
                vm.GroupInviteReceived += OnRequestLanded;
            }
            ShowFriendInTitle();
            ReportVisible();
        };
        IsVisibleChanged += (_, _) => ReportVisible();
        StateChanged += (_, _) => ReportVisible();
        Activated += (_, _) => _flashing = false;   // coming to the front stops it
        if (Application.Current is { } app) app.Activated += OnAppActivated;
        // the view model outlives the window, so let go or a closed window keeps scrolling in the background
        Closed += (_, _) =>
        {
            if (Vm is { } vm) Detach(vm);
            if (Application.Current is { } a) a.Activated -= OnAppActivated;
        };
        // KeyDown, not Preview, so an open menu, the status list or the comment box gets its Esc first
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    private void Detach(SocialViewModel vm)
    {
        if (HasMessages)
        {
            vm.Conversation.CollectionChanged -= OnConversationChanged;
            vm.ConversationVisible = false;
        }
        if (HasFriends) vm.FriendsListVisible = false;
        vm.PropertyChanged -= OnVmChanged;
        vm.MessageReceived -= OnMessageLanded;
        vm.NoticeReceived -= OnMessageLanded;
        vm.FriendRequestReceived -= OnRequestLanded;
        vm.GroupInviteReceived -= OnRequestLanded;
    }

    // Landing in this window counts as read or seen, so the main window stays quiet. With Provenance in the
    // background this one flashes instead, till it's brought up
    private void OnMessageLanded(string who, string _)
    {
        if (HasMessages && Vm?.SelectedFriend is { } f && string.Equals(f.Name, who, StringComparison.OrdinalIgnoreCase))
            FlashIfBehind();
    }

    private void OnRequestLanded(string who, int mid)
    {
        if (HasFriends) FlashIfBehind();
    }

    private bool _flashing;

    private void FlashIfBehind()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        if (Application.Current?.Windows.OfType<Window>().Any(w => w.IsActive) == true) return;
        TaskbarFlash.Start(this);
        _flashing = true;
    }

    // it only flashes while it's on screen, so with Provenance back in front it's done its job (the request might've
    // been answered from the main window, say)
    private void OnAppActivated(object? sender, EventArgs e)
    {
        if (!_flashing) return;
        _flashing = false;
        TaskbarFlash.Stop(this);
    }

    // a hidden or minimised window's messages stay unread until it's back, and its requests unseen
    private void ReportVisible()
    {
        if (Vm is not { } vm) return;
        bool shown = IsVisible && WindowState != WindowState.Minimized;
        if (HasMessages) vm.ConversationVisible = shown;
        if (HasFriends) vm.FriendsListVisible = shown;
    }

    private void ApplyPart()
    {
        bool friends = Part != SocialPart.Messages;
        FriendsPart.Visibility = AddFriendButton.Visibility = friends ? Visibility.Visible : Visibility.Collapsed;
        MessagesPart.Visibility = HasMessages ? Visibility.Visible : Visibility.Collapsed;
        PartSplitter.Visibility = Part == SocialPart.Both ? Visibility.Visible : Visibility.Collapsed;
        if (Part != SocialPart.Both)
        {
            FriendsColumn.MinWidth = MessagesColumn.MinWidth = 0;
            FriendsColumn.Width = friends ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            MessagesColumn.Width = HasMessages ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }
        (Title, TitleIcon.Kind, MinWidth, Width) = Part switch
        {
            SocialPart.Friends => ("Friends", PackIconKind.AccountMultiple, 300.0, 360.0),
            SocialPart.Messages => ("Messages", PackIconKind.MessageTextOutline, 420.0, 640.0),
            _ => ("Friends and messages", PackIconKind.AccountMultiple, 720.0, 900.0),
        };
        TitleText.Text = Title;
        LayoutIcon.Kind = Part == SocialPart.Both ? PackIconKind.ArrowSplitVertical
                                                  : PackIconKind.ArrowCollapseHorizontal;
        var tip = Part == SocialPart.Both ? "Split into a Friends window and a Messages window"
                                          : "Merge Friends and Messages into one window";
        LayoutButton.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(LayoutButton, tip);
    }

    // so the Messages window's taskbar button says whose conversation it is
    private void ShowFriendInTitle()
    {
        if (Part == SocialPart.Messages) Title = Vm?.SelectedFriend is { } f ? $"Messages with {f.Name}" : "Messages";
    }

    private void OnLayout(object sender, RoutedEventArgs e) =>
        Vm?.RequestLayout(Part == SocialPart.Both ? Layout.Split : Layout.Merge);

    // a redraw only follows to the end if the reader was already there, so scrolling back doesn't get undone
    private bool _stickToEnd = true;
    private string? _shownWith;

    private void OnConversationChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            var f = Vm?.SelectedFriend;
            var with = f is null ? null : f.Key.Length > 0 ? f.Key : f.Name;
            _stickToEnd = with != _shownWith || ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 24;
            _shownWith = with;
        }
        // a line just sent always comes into view, so nobody sends it twice by mistake
        if (e.Action == NotifyCollectionChangedAction.Add &&
            e.NewItems?[0] is ChatEntry { Outgoing: true, State: "sending" or "queued" } sent &&
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sent.Time < 10)
            _stickToEnd = true;
        if (_stickToEnd)
            Dispatcher.InvokeAsync(ChatScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnComposeKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        if (Vm is { } vm && vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
    }

    // the box stops at 300 bytes (Shift-JIS, what FFXI counts), a paste gets cut to what fits
    private static string WithoutSelection(TextBox box) => box.Text.Remove(box.SelectionStart, box.SelectionLength);

    private void OnComposeInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox box) return;
        var next = WithoutSelection(box).Insert(box.SelectionStart, e.Text);
        if (PolMessage.TextBytes(next) > PolMessage.MaxTextBytes) e.Handled = true;
    }

    private void OnComposePaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox box || e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text) return;
        int room = PolMessage.MaxTextBytes - PolMessage.TextBytes(WithoutSelection(box));
        if (PolMessage.TextBytes(text) <= room) return;
        e.CancelCommand();
        box.SelectedText = PolMessage.FitBytes(text, Math.Max(0, room));
        box.CaretIndex = box.SelectionStart + box.SelectionLength;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnAvatarClick(object sender, MouseButtonEventArgs e) => Vm?.ChangeProfilePictureCommand.Execute(null);

    private void OnResend(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is FrameworkElement { Tag: ChatEntry entry }) vm.Resend(entry);
    }

    private void OnRemoveFailed(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is FrameworkElement { Tag: ChatEntry entry }) vm.RemoveFailedLine(entry);
    }

    private void OnEditProfile(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) EditProfileWindow.ShowFor(vm, this);
    }

    // the viewer's Edit Comments, in place. Only Enter saves (it's a request to POL), Esc or leaving puts it back
    private string _savedComment = "";

    private void OnCommentFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Vm is { } vm) _savedComment = vm.MyComment;
    }

    private void OnCommentKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true;
        if (e.Key == Key.Enter) SaveComment();
        else UndoComment();
        Keyboard.ClearFocus();
    }

    // focus going to another app or the box's own right-click menu keeps the edit
    private void OnCommentLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // ContextMenu spelt out, on its own it's this window's property
        if (e.NewFocus is null or System.Windows.Controls.ContextMenu or MenuItem) return;
        UndoComment();
    }

    private void OnCommentPencil(object sender, MouseButtonEventArgs e)
    {
        CommentBox.Focus();
        CommentBox.CaretIndex = CommentBox.Text.Length;
    }

    // once a change finishes the box shows what POL actually has, so a refused one doesn't look like friends can see it
    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SocialViewModel.SelectedFriend)) ShowFriendInTitle();
        if (e.PropertyName != nameof(SocialViewModel.EditBusy)) return;
        if (sender is not SocialViewModel { EditBusy: false } vm) return;
        if (CommentBox.IsKeyboardFocusWithin) return;   // they're typing a new one
        CommentBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        _savedComment = vm.MyComment;
    }

    // SetCurrentValue keeps the binding, setting Text would drop it
    private void UndoComment() => CommentBox.SetCurrentValue(TextBox.TextProperty, _savedComment);

    private void SaveComment()
    {
        if (Vm is not { } vm) return;
        var text = CommentBox.Text.Trim();
        if (text == vm.MyComment.Trim()) return;
        if (!vm.CanEdit)
        {
            UndoComment();
            return;
        }
        _savedComment = text;   // what was sent stays in the box while POL answers
        vm.SetComment(text);
    }

    private void OnCreateGroup(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || !vm.CanEdit) return;
        if (vm.Groups.Count(g => !g.IsInvited) >= 4)
        {
            MessageDialog.ShowInfo(this, "Friend groups", "You can only be in 4 friend groups.");
            return;
        }
        var name = InputDialog.Prompt(this, "Create a group",
            "Give the new friend group a name (up to 20 English letters, or 10 Japanese characters).", "", maxLength: 20);
        if (!string.IsNullOrWhiteSpace(name)) vm.CreateGroup(name);
    }

    private static MenuItem Item(string header, bool enabled, Action click) => FriendsList.Item(header, enabled, click);
    private Action Confirmed(string title, string body, string ok, Action then) =>
        FriendsList.Confirmed(this, title, body, ok, then);

    // a click on the dots button of an open menu closes it, and this stops that click reopening it. Dumb, but it works
    private DateTime _menuClosedAt;
    private void OnMenuClosed(object sender, RoutedEventArgs e) => _menuClosedAt = DateTime.UtcNow;
    private bool MenuJustClosed => DateTime.UtcNow - _menuClosedAt < TimeSpan.FromMilliseconds(300);

    private void OnRowMenuButton(object sender, RoutedEventArgs e)
    {
        if (MenuJustClosed || sender is not FrameworkElement button) return;
        FrameworkElement? row = null;
        for (var p = VisualTreeHelper.GetParent(button); p != null && row is null; p = VisualTreeHelper.GetParent(p))
            if (p is FrameworkElement { ContextMenu: not null } el) row = el;
        if (row?.ContextMenu is not { } menu) return;
        menu.Closed -= OnMenuClosed;
        menu.Closed += OnMenuClosed;
        if (!BuildRowMenu(row)) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnConversationMenu(object sender, RoutedEventArgs e)
    {
        if (MenuJustClosed || sender is not FrameworkElement button || Vm is not { SelectedFriend: { } f }) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Closed += OnMenuClosed;
        FriendList.FillMenu(menu, f);
        if (menu.Items.Count > 0) menu.IsOpen = true;
    }

    // a right-click opens it at the pointer again, after the dots button put it below itself
    private void OnRowMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement row || !BuildRowMenu(row))
        {
            e.Handled = true;
            return;
        }
        row.ContextMenu.ClearValue(ContextMenu.PlacementTargetProperty);
        row.ContextMenu.ClearValue(ContextMenu.PlacementProperty);
    }

    private bool BuildRowMenu(FrameworkElement row)
    {
        if (Vm is not { } vm || row.ContextMenu is not { } menu) return false;
        if (row.Tag is SocialViewModel.GroupItem g)
        {
            menu.Items.Clear();
            FillGroupMenu(vm, menu, g);
            return true;
        }
        if (row.Tag is not SocialViewModel.GroupMemberItem m || m.Me ||
            vm.Groups.FirstOrDefault(x => x.Members.Any(y => ReferenceEquals(y, m))) is not { } mg) return false;
        menu.Items.Clear();
        FillMemberMenu(vm, menu, mg, m);
        return true;
    }

    private void FillGroupMenu(SocialViewModel vm, ContextMenu menu, SocialViewModel.GroupItem g)
    {
        // invitations get answered from their own row in the friend list, like the viewer, so two can't get mixed up
        if (g.IsInvited)
        {
            menu.Items.Add(Item("Answer it under Group invitations in your friend list", true,
                                () => vm.ShowFriendsTabCommand.Execute(null)));
            return;
        }
        bool can = vm.CanEdit;
        menu.Items.Add(Item("Edit my group comment...", can, () =>
        {
            var mine = g.Members.FirstOrDefault(m => m.Me)?.Comment ?? "";
            var text = InputDialog.Prompt(this, "Group comment", $"Your comment in \"{g.Name}\" (up to 49 characters).",
                                          mine, maxLength: 49, allowEmpty: true);
            if (text != null && text != mine) vm.SetGroupComment(g, text);
        }));
        if (g.CanInvite)
        {
            var sub = new MenuItem { Header = "Invite a friend", IsEnabled = can };
            foreach (var f in vm.Friends.Where(f => f.IsFriend && !f.Pending &&
                                                    !g.Members.Any(m => m.Key.Length > 0 && m.Key == f.Key)))
                sub.Items.Add(Item(f.Name, can, () => vm.InviteToGroup(g, f)));
            if (sub.Items.Count == 0) sub.IsEnabled = false;
            menu.Items.Add(sub);
        }
        menu.Items.Add(new Separator());
        var leave = Item("Leave group...", can && !g.IsMaster,
                         Confirmed("Leave group", $"Leave the group \"{g.Name}\"?", "LEAVE", () => vm.LeaveGroup(g)));
        if (g.IsMaster)
        {
            leave.ToolTip = "The group master can't leave. Make another member the group master first, or disband the group.";
            ToolTipService.SetShowOnDisabled(leave, true);
        }
        menu.Items.Add(leave);
        menu.Items.Add(Item("Disband group...", can && g.IsMaster, Confirmed("Disband group",
            $"Disband the group \"{g.Name}\"? The group will be gone for everyone in it.", "DISBAND", () => vm.DisbandGroup(g))));
    }

    private void FillMemberMenu(SocialViewModel vm, ContextMenu menu, SocialViewModel.GroupItem g,
                                SocialViewModel.GroupMemberItem m)
    {
        bool Can(int cls) => vm.CanEdit && !g.IsInvited && SocialViewModel.CanSetClass(g, m, cls);
        var friend = m.Key.Length > 0 ? vm.Friends.FirstOrDefault(f => f.IsFriend && f.Key == m.Key) : null;
        menu.Items.Add(Item("Send message", friend is { CanMessage: true }, () => vm.SelectedFriend = friend));
        menu.Items.Add(Item("View profile", friend is { IsFriend: true, Pending: false },
                            () => ProfileWindow.ShowFor(vm, friend!, this)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Make group master...", Can(5), Confirmed("Make group master",
            $"Make {m.Name} the master of \"{g.Name}\"? You'll become an ordinary member.", "MAKE MASTER",
            () => vm.SetMemberClass(g, m, 5))));
        if (m.Class == 4) menu.Items.Add(Item("Relieve sub-master", Can(3), () => vm.SetMemberClass(g, m, 3)));
        else menu.Items.Add(Item("Make sub-master", Can(4), () => vm.SetMemberClass(g, m, 4)));
        menu.Items.Add(Item("Remove from group...", Can(1), Confirmed("Remove from group",
            $"Remove {m.Name} from \"{g.Name}\"?", "REMOVE", () => vm.SetMemberClass(g, m, 1))));
    }

    private void OnAddFriend(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) new AddFriendDialog(vm) { Owner = this }.ShowDialog();
    }

    private void OnUnignore(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as FrameworkElement)?.Tag is not PolBlocked b) return;
        if (MessageDialog.Confirm(this, "Stop ignoring " + b.Name,
                $"Take {b.Name} off your ignore list? Their messages will reach you again. " +
                "This doesn't add them back to your friend list.", "STOP IGNORING") is null) return;
        vm.Unignore(b);
    }
}
