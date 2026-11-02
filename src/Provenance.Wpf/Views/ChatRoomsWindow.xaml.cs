using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Provenance.Common.Social;
using Provenance.Wpf.ViewModels;

namespace Provenance.Wpf.Views;

// unowned like the friends window, so it stays up with the main window in the tray. Closing it leaves the room
public partial class ChatRoomsWindow : Window
{
    private const string DialogTitle = "PlayOnline Chat";
    private readonly ChatRoomsViewModel _vm;

    public ChatRoomsWindow(SocialViewModel social, Func<string, string, bool> savePassword, Action dropPassword,
                           Func<int, ImageSource?>? emote = null)
    {
        InitializeComponent();
        WindowFrame.Attach(this);
        DataContext = _vm = new ChatRoomsViewModel(social, savePassword, dropPassword, emote)
        {
            AskChatName = initial => InputDialog.Prompt(this, DialogTitle, "Enter chat name.", initial,
                                                        maxLength: ChatRoomFiles.MaxNameBytes, validate: ChatRoomFiles.NameProblem),
            AskPassword = () => InputDialog.PromptSecret(this, "Enter Room Password", "Enter password.",
                                                         ChatRoomFiles.MaxPasswordLength, ChatRoomFiles.PasswordProblem),
            ConfirmLeave = question => MessageDialog.Confirm(this, DialogTitle, question, "LEAVE") is not null,
            ShowError = text => MessageDialog.ShowError(this, DialogTitle, text),
            ShowInfo = text => MessageDialog.ShowInfo(this, DialogTitle, text),
            Confirm = question => MessageDialog.Confirm(this, DialogTitle, question, "YES", cancelLabel: "NO") == 0,
            AskCreate = (zones, zone, cats) => CreateRoomDialog.Prompt(this, zones, zone, cats,
                q => MessageDialog.Confirm(this, "Create Room", q, "YES", cancelLabel: "NO") == 0),
            AskNewPassword = AskNewPassword,
        };
        _vm.Lines.CollectionChanged += OnLinesChanged;
        Loaded += (_, _) => _vm.Start();
        Closed += (_, _) =>
        {
            _vm.Lines.CollectionChanged -= OnLinesChanged;
            _vm.Dispose();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _vm.InRoom) return;
            e.Handled = true;
            Close();
        };
    }

    public static ChatRoomsWindow? Current => Application.Current.Windows.OfType<ChatRoomsWindow>().FirstOrDefault();

    public static void ShowFor(SocialViewModel social, Func<string, string, bool> savePassword, Action dropPassword,
                               Func<int, ImageSource?>? emote = null)
    {
        if (Current is { } open)
        {
            if (!open.IsVisible) open.Show();
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        new ChatRoomsWindow(social, savePassword, dropPassword, emote).Show();
    }

    // the session's going, so the room goes with it and no PART is sent
    public void CloseQuietly()
    {
        _vm.CloseQuietly();
        Close();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            Dispatcher.InvokeAsync(LineScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnRoomDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(RoomList, d) is ListBoxItem
            && _vm.EnterCommand.CanExecute(null))
            _vm.EnterCommand.Execute(null);
    }

    private void OnComposeKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
    }

    // a line stops at 300 bytes of Shift-JIS, a paste gets cut to what fits
    private static string WithoutSelection(TextBox box) => box.Text.Remove(box.SelectionStart, box.SelectionLength);

    private void OnComposeInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is TextBox box && PolMessage.TextBytes(WithoutSelection(box).Insert(box.SelectionStart, e.Text)) > ChatRoomFiles.MaxTextBytes)
            e.Handled = true;
    }

    private void OnComposePaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox box || e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text) return;
        text = text.Replace("\r", "").Replace('\n', ' ');
        int room = ChatRoomFiles.MaxTextBytes - PolMessage.TextBytes(WithoutSelection(box));
        e.CancelCommand();
        box.SelectedText = PolMessage.FitBytes(text, Math.Max(0, room));
        box.CaretIndex = box.SelectionStart + box.SelectionLength;
    }

    // the viewer asks for the new one and then again to confirm it
    private string? AskNewPassword()
    {
        var pw = InputDialog.PromptSecret(this, "Set Password", "Enter password.", ChatRoomFiles.MaxNewPassword,
                                          ChatRoomFiles.NewPasswordProblem);
        if (pw is null) return null;
        var again = InputDialog.PromptSecret(this, "Confirm Password", "Enter password.", ChatRoomFiles.MaxNewPassword);
        if (again is null) return null;
        if (again == pw) return pw;
        MessageDialog.ShowError(this, DialogTitle, "The password entered in Room Password(Confirm) does not match.");
        return null;
    }

    // a member's Kick and Designate, only for the room master and never on my own row
    private void OnMemberMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatMemberItem m, ContextMenu: { } menu } || !_vm.CanKick(m))
        {
            e.Handled = true;
            return;
        }
        FillMemberMenu(menu, m);
    }

    internal void FillMemberMenu(ContextMenu menu, ChatMemberItem m)
    {
        menu.Items.Clear();
        menu.Items.Add(MenuItem("Kick...", () => _vm.Kick(m)));
        menu.Items.Add(MenuItem("Designate...", () => _vm.Designate(m)));
    }

    private void OnPasswordMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MenuItem(_vm.RoomLocked ? "Change password..." : "Set password...", () => _vm.ChangePassword(false)));
        if (_vm.RoomLocked) menu.Items.Add(MenuItem("Remove password", () => _vm.ChangePassword(true)));
        menu.IsOpen = true;
    }

    // the viewer's 12 text colours, and none
    private void OnColourMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement button) ColourMenu(button).IsOpen = true;
    }

    internal ContextMenu ColourMenu(FrameworkElement button)
    {
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        menu.Items.Add(MenuItem("No colour", () => _vm.Colour = -1));
        foreach (var c in _vm.ColourChoices)
        {
            var swatch = new System.Windows.Shapes.Rectangle { Width = 90, Height = 14, Fill = c.Swatch, RadiusX = 3, RadiusY = 3 };
            var item = new MenuItem { Header = swatch, IsChecked = _vm.Colour == c.Pick, ToolTip = c.Name };
            System.Windows.Automation.AutomationProperties.SetName(item, c.Name);
            item.Click += (_, _) => _vm.Colour = c.Pick;
            menu.Items.Add(item);
        }
        return menu;
    }

    // the viewer's face palette, its first page of 23
    private void OnEmoteMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement button) EmotePopup(button).IsOpen = true;
    }

    internal System.Windows.Controls.Primitives.Popup EmotePopup(FrameworkElement button)
    {
        var panel = new WrapPanel { Width = 8 * 38 };
        var none = new Button { Content = "NO", Width = 34, Height = 34, Margin = new Thickness(2), Padding = new Thickness(0),
                                Style = (Style)FindResource("MaterialDesignFlatButton"), FontSize = 11 };
        System.Windows.Automation.AutomationProperties.SetName(none, "No emoticon");
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = (Brush)FindResource("Surface.2"), BorderBrush = (Brush)FindResource("Border.Window"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6), Child = panel,
            },
        };
        none.Click += (_, _) => { _vm.Emoticon = 0; popup.IsOpen = false; };
        panel.Children.Add(none);
        foreach (var f in _vm.EmoteChoices)
        {
            var b = new Button
            {
                Content = new Image { Source = f.Image, Width = 32, Height = 32 }, Width = 34, Height = 34, Margin = new Thickness(2),
                Padding = new Thickness(0), Style = (Style)FindResource("MaterialDesignFlatButton"), ToolTip = f.Name,
            };
            System.Windows.Automation.AutomationProperties.SetName(b, f.Name);
            b.Click += (_, _) => { _vm.Emoticon = f.Index; popup.IsOpen = false; };
            panel.Children.Add(b);
        }
        return popup;
    }

    private static MenuItem MenuItem(string header, Action act)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => act();
        return item;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
}
