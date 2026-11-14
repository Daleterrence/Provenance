using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Provenance.Common.Social;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Services;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public static class SocialParts
{
    public static string Title(this SocialPart part) => part switch
    {
        SocialPart.Friends => "Friends",
        SocialPart.Messages => "Messages",
        _ => "Friends and messages",
    };

    public static (double Min, double Open) Widths(this SocialPart part) => part switch
    {
        // any narrower than 360 and the add a friend button sits on top of the title
        SocialPart.Friends => (360, 400),
        SocialPart.Messages => (420, 640),
        _ => (720, 900),
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
        FollowEnd.SetPin(ChatScroll, PinConversation);
        // split: a click shows the Messages window alongside (focus stays in the list), a double-click brings it up
        FriendList.FriendSelected += _ => { if (Part == SocialPart.Friends && Vm is { } vm) vm.ShowMessagesAlongside(takeFocus: false); };
        FriendList.FriendActivated += _ => { if (Part == SocialPart.Friends && Vm is { } vm) vm.ShowMessagesAlongside(takeFocus: true); };
        Loaded += (_, _) => { if (Part == SocialPart.Messages && ShowActivated) ComposeBox.Focus(); };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SocialViewModel old) Detach(old);
            FollowEnd.SetSource(ChatScroll, HasMessages ? (e.NewValue as SocialViewModel)?.Messages.Conversation : null);
            if (e.NewValue is SocialViewModel vm) Attach(vm);
            ShowFriendInTitle();
            ReportVisible();
        };
        IsVisibleChanged += (_, _) => ReportVisible();
        StateChanged += (_, _) => ReportVisible();
#pragma warning disable RS0030 // a view, not a view model
        if (Application.Current is { } app) app.Activated += OnAppActivated;
#pragma warning restore RS0030
        // the view model outlives the window, so a closed one lets go
        Closed += (_, _) =>
        {
            if (Vm is { } vm) Detach(vm);
#pragma warning disable RS0030 // a view, not a view model
            if (Application.Current is { } a) a.Activated -= OnAppActivated;
#pragma warning restore RS0030
        };
    }

    private void Attach(SocialViewModel vm)
    {
        vm.PropertyChanged += OnVmChanged;
        vm.MessageReceived += OnMessageLanded;
        vm.NoticeReceived += OnMessageLanded;
        vm.FriendRequestReceived += OnRequestLanded;
        vm.GroupInviteReceived += OnRequestLanded;
    }

    private void Detach(SocialViewModel vm)
    {
        if (HasMessages) vm.Messages.ConversationVisible = false;
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
        if (WindowOwner.Active() is not null) return;
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
        if (HasMessages) vm.Messages.ConversationVisible = shown;
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
        (MinWidth, Width) = Part.Widths();
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

    private void OnLayout(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.SetWindowsSplit(Part == SocialPart.Both);
    }

    private string? _shownWith;

    // another friend's conversation starts at the end, and so does a line just sent, so nobody sends it twice by mistake
    private bool PinConversation(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            var f = Vm?.SelectedFriend;
            var with = f is null ? null : f.Key.Length > 0 ? f.Key : f.Name;
            bool other = with != _shownWith;
            _shownWith = with;
            return other;
        }
        return e.Action == NotifyCollectionChangedAction.Add &&
               e.NewItems?[0] is ChatEntry { Outgoing: true, State: "sending" or "queued" } sent &&
               DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sent.Time < 10;
    }

    private void OnResend(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is FrameworkElement { DataContext: ChatEntry entry }) vm.Messages.Resend(entry);
    }

    private void OnRemoveFailed(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is FrameworkElement { DataContext: ChatEntry entry }) vm.Messages.RemoveFailedLine(entry);
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

    private void OnConversationMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || Vm is not { SelectedFriend: { } f } vm) return;
        Menus.Toggle(button, () =>
        {
            var menu = new ContextMenu();
            Menus.Fill(menu, vm.Messages.ConversationMenu(f, FriendList.OpenConversation));
            return menu;
        });
    }

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
        Menus.FillLine(menu, box, vm.Messages.LineMenu(entry));

    private void OnAddFriend(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) SvgIcon.OpenWhile(sender, () => vm.AddFriend());
    }

    private void OnUnignore(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as FrameworkElement)?.DataContext is not PolBlocked b) return;
        vm.AskUnignore(b);
    }
}
