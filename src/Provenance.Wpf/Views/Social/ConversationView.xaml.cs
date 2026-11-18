using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Provenance.Common.Social;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public partial class ConversationView : UserControl
{
    public static readonly DependencyProperty PoppedProperty = DependencyProperty.Register(
        nameof(Popped), typeof(bool), typeof(ConversationView), new PropertyMetadata(false));

    private ScrollViewer? _chatScroll;
    private string? _shownWith;

    static ConversationView() =>
        VisibilityProperty.OverrideMetadata(typeof(ConversationView), new FrameworkPropertyMetadata(Visibility.Visible,
            (d, e) => { if (e.NewValue is Visibility.Visible) ((ConversationView)d).ToEnd(); }));

    public ConversationView()
    {
        InitializeComponent();
        Loaded += (_, _) => ToEnd();
        FollowEnd.SetPin(ChatScroll, PinConversation);
        DataContextChanged += (_, e) => FollowEnd.SetSource(ChatScroll, (e.NewValue as SocialMessages)?.Conversation);
    }

    // in its own window, where the pop out button puts the conversation back
    public bool Popped
    {
        get => (bool)GetValue(PoppedProperty);
        set => SetValue(PoppedProperty, value);
    }

    private SocialMessages? Messages => DataContext as SocialMessages;

    internal ScrollViewer ChatScroll
    {
        get
        {
            if (_chatScroll is null)
            {
                ChatLines.ApplyTemplate();
                _chatScroll = (ScrollViewer)ChatLines.Template.FindName("PART_ChatScroll", ChatLines);
            }
            return _chatScroll;
        }
    }

    // a conversation that was already there when the window or pane came up starts at its newest line
    private void ToEnd() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => ChatScroll.ScrollToEnd());

    internal void FocusCompose() => ComposeBox.Focus();

    // another friend's conversation starts at the end, and so does a line just sent, so nobody sends it twice by mistake
    private bool PinConversation(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            var f = Messages?.Friend;
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
        if (Messages is { } m && sender is FrameworkElement { DataContext: ChatEntry entry }) m.Resend(entry);
    }

    private void OnRemoveFailed(object sender, RoutedEventArgs e)
    {
        if (Messages is { } m && sender is FrameworkElement { DataContext: ChatEntry entry }) m.RemoveFailedLine(entry);
    }

    private void OnConversationMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || Messages is not { Friend: { } f } m) return;
        Menus.Toggle(button, () =>
        {
            var menu = new ContextMenu();
            Menus.Fill(menu, m.ConversationMenu(f));
            return menu;
        });
    }

    private void OnPopOut(object sender, RoutedEventArgs e)
    {
        if (Popped) (Window.GetWindow(this) as PopOutWindow)?.PutBack();
        else if (Messages is { Friend: { } f } m) m.Social.PopOut(f);
    }

    private void OnLineMenu(object sender, ContextMenuEventArgs e)
    {
        if (Messages is not { } m || sender is not TextBox { DataContext: ChatEntry entry, ContextMenu: { } menu } box)
        {
            e.Handled = true;
            return;
        }
        Menus.FillLine(menu, box, m.LineMenu(entry));
    }

    internal static void FillLineMenu(SocialViewModel vm, ContextMenu menu, TextBox? box, ChatEntry entry) =>
        Menus.FillLine(menu, box, vm.Messages.LineMenu(entry));
}
