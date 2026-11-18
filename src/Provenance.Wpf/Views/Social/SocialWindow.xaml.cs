using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Provenance.Common.Settings;
using Provenance.Common.Social;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Services;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public partial class SocialWindow : AppWindow
{
    private const double Handle = 5;
    private const double PaneMin = 360;
    private bool _paneShown;
    private bool _holding;
    private double _paneAsked = 480;

    public SocialWindow()
    {
        InitializeComponent();
        Bar.Title = Title;
        FriendList.FriendActivated += OnActivated;
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SocialViewModel old) Detach(old);
            if (e.NewValue is SocialViewModel vm) Attach(vm);
            ApplyPane();
            ReportVisible();
        };
        IsVisibleChanged += (_, e) =>
        {
            ReportVisible();
            // a Friends window that's gone leaves nobody picked, so it comes back with no pane
            if (e.NewValue is false) Vm?.HidePane();
        };
        StateChanged += (_, _) => ReportVisible();
        Activated += (_, _) => Vm?.RefreshTimes();
        SizeChanged += OnSized;
#pragma warning disable RS0030 // a view, not a view model
        if (Application.Current is { } app) app.Activated += OnAppActivated;
#pragma warning restore RS0030
        // the view model outlives the window, so a closed one lets go
        Closing += (_, _) =>
        {
            if (_paneAsked != PaneWidth) PaneWidthChanged?.Invoke(PaneWidth);
        };
        Closed += (_, _) =>
        {
            if (Vm is { } vm)
            {
                vm.HidePane();
                Detach(vm);
            }
#pragma warning disable RS0030 // a view, not a view model
            if (Application.Current is { } a) a.Activated -= OnAppActivated;
#pragma warning restore RS0030
        };
    }

    private SocialViewModel? Vm => DataContext as SocialViewModel;

    // how wide the conversation pane opens, and told when the user drags it to something else
    public double PaneWidth { get; set; } = 480;

    public double SavedPaneWidth { set => PaneWidth = _paneAsked = value; }

    public event Action<double>? PaneWidthChanged;

    internal ConversationView ConversationPane => Conversation;

    internal bool PaneShown => _paneShown;

    private void Attach(SocialViewModel vm)
    {
        vm.PropertyChanged += OnVmChanged;
        vm.MessageReceived += OnMessageLanded;
        vm.NoticeReceived += OnAnswerLanded;
        vm.FriendRequestReceived += OnFriendRequestLanded;
        vm.GroupInviteReceived += OnGroupInviteLanded;
    }

    private void Detach(SocialViewModel vm)
    {
        vm.Messages.ConversationVisible = false;
        vm.FriendsListVisible = false;
        vm.PropertyChanged -= OnVmChanged;
        vm.MessageReceived -= OnMessageLanded;
        vm.NoticeReceived -= OnAnswerLanded;
        vm.FriendRequestReceived -= OnFriendRequestLanded;
        vm.GroupInviteReceived -= OnGroupInviteLanded;
    }

    // Landing in this window counts as read or seen, so the main window stays quiet. With Provenance in the
    // background this one flashes instead, till it's brought up
    private void OnMessageLanded(string who, string _) => OnLineLanded(NoticeKind.Message, who);

    private void OnAnswerLanded(string who, string _) => OnLineLanded(NoticeKind.Answer, who);

    private void OnLineLanded(NoticeKind kind, string who)
    {
        if (Vm?.PaneFriend is { } f && string.Equals(f.Name, who, StringComparison.OrdinalIgnoreCase))
            FlashIfBehind(kind);
    }

    private void OnFriendRequestLanded(string who, int mid) => FlashIfBehind(NoticeKind.FriendRequest);

    private void OnGroupInviteLanded(string who, int mid) => FlashIfBehind(NoticeKind.GroupInvite);

    private void FlashIfBehind(NoticeKind kind)
    {
        if (Vm?.Flashes(kind) != true || !IsVisible || WindowState == WindowState.Minimized) return;
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
        vm.Messages.ConversationVisible = shown;
        vm.FriendsListVisible = shown;
    }

    // a double-click or Enter takes the cursor to the box, so the reply can just be typed
    private void OnActivated(FriendItem f)
    {
        if (Vm is not { } vm) return;
        vm.OpenConversation(f);
        if (vm.HasPane) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => Conversation.FocusCompose());
    }

    private void OnToggleMessages(object sender, RoutedEventArgs e) => Vm?.ToggleMessages();

    private void OnFriendsTab(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.RequestsTab = false;
    }

    private void OnRequestsTab(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.RequestsTab = true;
    }

    // the work area of the monitor this window's on, swapped in tests
    internal static Func<Window, Rect> WorkArea { get; set; } = WindowFrame.WorkAreaOf;

    // the pane's only there with a friend picked. It opens to the right and keeps the left edge, shifted left when that
    // would run off the screen, and closing it narrows the window back
    private void ApplyPane()
    {
        bool want = Vm?.HasPane == true;
        if (want == _paneShown) return;
        double chrome = Body.ActualWidth > 0 ? ActualWidth - Body.ActualWidth : 0;
        bool resize = WindowState == WindowState.Normal;
        // the window's own resizing, which isn't the user giving the pane a width, settles by the time the layout's idle
        _holding = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => _holding = false);
        ApplyPaneCore(want, chrome, resize, WorkArea(this));
    }

    private void ApplyPaneCore(bool want, double chrome, bool resize, Rect room)
    {
        if (want)
        {
            double list = FriendsColumn.ActualWidth > 0 ? FriendsColumn.ActualWidth : Width - chrome;
            _paneShown = true;
            FriendsColumn.Width = new GridLength(list);
            SplitterColumn.Width = new GridLength(Handle);
            MessagesColumn.MinWidth = PaneMin;
            MessagesColumn.Width = new GridLength(1, GridUnitType.Star);
            PartSplitter.Visibility = Conversation.Visibility = Visibility.Visible;
            MinWidth = Math.Min(FriendsColumn.MinWidth + Handle + PaneMin + chrome, room.Width);
            if (!resize) return;
            double width = Math.Min(list + Handle + Math.Max(PaneMin, PaneWidth) + chrome, room.Width);
            if (double.IsNaN(Left))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = room.Left + (room.Width - width) / 2;
                Top = room.Top + Math.Max(0, (room.Height - ActualHeight) / 2);
            }
            double left = Left;
            if (left + width > room.Right) left = Math.Max(room.Left, room.Right - width);
            (Left, Width) = (left, width);
        }
        else
        {
            double list = FriendsColumn.ActualWidth > 0 ? FriendsColumn.ActualWidth : MinWidth;
            _paneShown = false;
            PartSplitter.Visibility = Conversation.Visibility = Visibility.Collapsed;
            MessagesColumn.MinWidth = 0;
            MessagesColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            FriendsColumn.Width = new GridLength(1, GridUnitType.Star);
            MinWidth = 360;
            if (resize) Width = Math.Max(list + chrome, MinWidth);
        }
    }

    // saved as the list's width, so it comes back the same however the pane was (and when it was maximised, as the
    // normal bounds have the pane at the width it last had normal)
    internal Rect ListBounds(Rect b) =>
        _paneShown ? new Rect(b.Left, b.Top, Math.Max(FriendsColumn.MinWidth, b.Width - Handle - Math.Max(PaneMin, PaneWidth)), b.Height) : b;

    // the user's own width is kept for the next time the pane opens, whether they dragged the splitter or the window's edge
    private void OnSized(object sender, SizeChangedEventArgs e)
    {
        if (_holding || !_paneShown || WindowState != WindowState.Normal || !e.WidthChanged || MessagesColumn.ActualWidth < PaneMin) return;
        PaneWidth = Math.Round(MessagesColumn.ActualWidth);
    }

    private void OnPaneDragged(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        PaneWidth = Math.Round(MessagesColumn.ActualWidth);
        _paneAsked = PaneWidth;
        PaneWidthChanged?.Invoke(PaneWidth);
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
        if (e.PropertyName == nameof(SocialViewModel.HasPane)) ApplyPane();
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
