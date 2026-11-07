using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Provenance.Common.Hook;
using Provenance.Common.PolFormat;
using Provenance.Common.Social;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.Views.Dialogs;
using Provenance.Wpf.ViewModels.Social;
using Layout = Provenance.Wpf.ViewModels.Social.SocialViewModel.LayoutAction;

namespace Provenance.Wpf.Views.Social;

public static class SocialParts
{
    public static string Title(this SocialPart part) => part switch
    {
        SocialPart.Friends => "Friends",
        SocialPart.Messages => "Messages",
        _ => "Friends and messages",
    };
}

// split, it's two windows on one view model, neither owned by the main window so each lives its own life
public partial class SocialWindow : AppWindow
{
    public SocialPart Part { get; }
    private bool HasMessages => Part != SocialPart.Friends;
    private bool HasFriends => Part != SocialPart.Messages;
    private SocialViewModel? Vm => DataContext as SocialViewModel;

    public SocialWindow(SocialPart part)
    {
        Part = part;
        InitializeComponent();
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
        if (Application.Current is { } app) app.Activated += OnAppActivated;
        // the view model outlives the window, so let go or a closed window keeps scrolling in the background
        Closed += (_, _) =>
        {
            if (Vm is { } vm) Detach(vm);
            if (Application.Current is { } a) a.Activated -= OnAppActivated;
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

    private void FlashIfBehind()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        if (Application.Current?.Windows.OfType<Window>().Any(w => w.IsActive) == true) return;
        TaskbarFlash.Start(this);
    }

    // it only flashes while it's on screen, so with Provenance back in front it's done its job (the request might've
    // been answered from the main window, say)
    private void OnAppActivated(object? sender, EventArgs e) => TaskbarFlash.StopIfFlashing(this);

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
        (TitleIcon.Glyph, MinWidth, Width) = Part switch
        {
            SocialPart.Friends => ("smiley", 300.0, 360.0),
            SocialPart.Messages => ("chat", 420.0, 640.0),
            _ => ("smiley", 720.0, 900.0),
        };
        Bar.Title = Title = Part.Title();
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
        if (ShiftJis.ByteCount(next) > PolMessage.MaxTextBytes) e.Handled = true;
    }

    private void OnComposePaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox box || e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text) return;
        int room = PolMessage.MaxTextBytes - ShiftJis.ByteCount(WithoutSelection(box));
        if (ShiftJis.ByteCount(text) <= room) return;
        e.CancelCommand();
        box.SelectedText = ShiftJis.Fit(text, Math.Max(0, room));
        box.CaretIndex = box.SelectionStart + box.SelectionLength;
    }

    private void OnAvatarClick(object sender, MouseButtonEventArgs e) => Vm?.ChangeProfilePictureCommand.Execute(null);

    private void OnResend(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is FrameworkElement { DataContext: ChatEntry entry }) vm.Resend(entry);
    }

    private void OnRemoveFailed(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is FrameworkElement { DataContext: ChatEntry entry }) vm.RemoveFailedLine(entry);
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
        if (!vm.CanCreateGroup)
        {
            MessageDialog.ShowInfo(this, "Friend groups", vm.GroupLimitText);
            return;
        }
        string? name = null;
        SvgIcon.OpenWhile(sender, () => name = InputDialog.Prompt(this, "Create a group",
            "Give the new friend group a name (up to 20 English letters, or 10 Japanese characters).", "", maxLength: PolLimits.GroupName));
        if (!string.IsNullOrWhiteSpace(name)) vm.CreateGroup(name);
    }

    private readonly ReopenGuard _menuGuard = new();

    private ISocialMenuHost Host(SocialViewModel vm) => new SocialMenuDialogs(vm, this);

    private void OnRowMenuButton(object sender, RoutedEventArgs e)
    {
        if (_menuGuard.JustClosed || sender is not FrameworkElement button) return;
        FrameworkElement? row = null;
        for (var p = VisualTreeHelper.GetParent(button); p != null && row is null; p = VisualTreeHelper.GetParent(p))
            if (p is FrameworkElement { ContextMenu: not null } el) row = el;
        if (row?.ContextMenu is not { } menu) return;
        menu.Closed -= _menuGuard.OnClosed;
        menu.Closed += _menuGuard.OnClosed;
        if (!BuildRowMenu(row)) return;
        Menus.ShowBelow(menu, button);
    }

    private void OnConversationMenu(object sender, RoutedEventArgs e)
    {
        if (_menuGuard.JustClosed || sender is not FrameworkElement button || Vm is not { SelectedFriend: { } f } vm) return;
        var menu = new ContextMenu();
        menu.Closed += _menuGuard.OnClosed;
        Menus.Fill(menu, vm.ConversationMenu(f, FriendList.Host(vm)));
        Menus.ShowBelow(menu, button);
    }

    // a line's own menu: copy, or delete this PC's copy of it
    private void OnLineMenu(object sender, ContextMenuEventArgs e)
    {
        if (Vm is not { } vm || sender is not TextBox { DataContext: ChatEntry entry, ContextMenu: { } menu } box)
        {
            e.Handled = true;
            return;
        }
        FillLineMenu(vm, menu, box, entry);
    }

    internal static void FillLineMenu(SocialViewModel vm, ContextMenu menu, TextBox? box, ChatEntry entry) =>
        Menus.FillLine(menu, box, vm.LineMenu(entry));

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
        if (Vm is not { } vm || row.ContextMenu is not { } menu || vm.RowMenu(row.DataContext, Host(vm)) is not { } actions)
            return false;
        Menus.Fill(menu, actions);
        return true;
    }

    private void OnAddFriend(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) SvgIcon.OpenWhile(sender, () => new AddFriendDialog(vm) { Owner = this }.ShowDialog());
    }

    private void OnUnignore(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as FrameworkElement)?.DataContext is not PolBlocked b) return;
        if (MessageDialog.Confirm(this, "Stop ignoring " + b.Name,
                $"Take {b.Name} off your ignore list? Their messages will reach you again. " +
                "This doesn't add them back to your friend list.", "STOP IGNORING") is null) return;
        vm.Unignore(b);
    }
}
