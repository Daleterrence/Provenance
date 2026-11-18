using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Chat;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Chat;

// unowned like the friends window, so it stays up with the main window in the tray. Closing it leaves the room
public partial class ChatRoomsWindow : AppWindow
{
    private readonly ChatRoomsViewModel _vm;
    private bool _quiet, _asking, _leaving, _closed;

    public ChatRoomsWindow(SocialViewModel social, IDialogs dialogs, Func<string, string, bool> savePassword, Action dropPassword,
                           Func<int, ImageSource?>? emote = null)
    {
        InitializeComponent();
        DataContext = _vm = new ChatRoomsViewModel(social, dialogs, savePassword, dropPassword, emote, () => social.ShowTimes);
        Activated += (_, _) => _vm.RefreshTimes();
        Loaded += (_, _) => _vm.Start();
        Closed += (_, _) =>
        {
            _closed = true;
            _vm.Dispose();
        };
    }

    // Esc in a room is for the room
    protected override bool OnEscape() => !_vm.InRoom && base.OnEscape();

    // the session's going, so the room goes with it and no PART is sent
    public void CloseQuietly()
    {
        _quiet = true;
        _vm.CloseQuietly();
        Close();
    }

    // in a room a close is turned down and the question asked after it, as a window stuck inside Closing can't be closed by the app exiting
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.InRoom && !_quiet && !_leaving && !WindowOwner.AppClosing)
        {
            e.Cancel = true;
            if (_asking) return;
            _asking = true;
            Dispatcher.BeginInvoke(AskToLeave);
            return;
        }
        base.OnClosing(e);
    }

    private void AskToLeave()
    {
        try
        {
            if (_closed) return;
            // closed from the taskbar it can be minimised or behind something, and the question goes over the active window
            WindowManager.BringBack(this);
            if (_vm.InRoom && !_vm.ConfirmLeave()) return;
        }
        finally { _asking = false; }
        // the app or the session can have taken the window while the question was up
        if (_closed) return;
        _leaving = true;
        try { Close(); }
        finally { _leaving = false; }
    }

    private void OnRoomDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(RoomList, d) is ListBoxItem
            && _vm.EnterCommand.CanExecute(null))
            _vm.EnterCommand.Execute(null);
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

    private void OnMemberMenuButton(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatMemberItem m } button || !_vm.CanKick(m)) return;
        Menus.Toggle(button, () =>
        {
            var menu = new ContextMenu();
            FillMemberMenu(menu, m);
            return menu;
        });
    }

    internal void FillMemberMenu(ContextMenu menu, ChatMemberItem m) => Menus.Fill(menu, _vm.MemberMenu(m));

    private void OnPasswordMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;
        Menus.Toggle(button, () =>
        {
            var menu = new ContextMenu();
            Menus.Fill(menu, _vm.PasswordMenu());
            return menu;
        });
    }

    // the viewer's 12 text colours, and none
    private void OnColourMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;
        Menus.Toggle(button, () => ColourMenu(button), System.Windows.Controls.Primitives.PlacementMode.Top);
    }

    internal ContextMenu ColourMenu(FrameworkElement button)
    {
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        menu.Items.Add(Menus.Item("No colour", true, () => _vm.Colour = -1));
        foreach (var c in _vm.ColourChoices)
        {
            var swatch = new System.Windows.Shapes.Rectangle { Width = 90, Height = 14, Fill = c.Swatch, RadiusX = 3, RadiusY = 3 };
            var item = new MenuItem { Header = swatch, IsChecked = _vm.Colour == c.Pick };
            System.Windows.Automation.AutomationProperties.SetName(item, c.Name);
            item.Click += (_, _) => _vm.Colour = c.Pick;
            menu.Items.Add(item);
        }
        return menu;
    }

    // the viewer's emoticon palette, its first page of 23
    private void OnEmoteMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement button) Menus.Toggle(button, () => EmotePopup(button));
    }

    internal System.Windows.Controls.Primitives.Popup EmotePopup(FrameworkElement button)
    {
        // a set width only fits 8 across until a scaled screen rounds the buttons up
        var panel = new System.Windows.Controls.Primitives.UniformGrid { Columns = 8 };
        var none = new Button { Content = "NO", Width = 34, Height = 34, Margin = new Thickness(2), Padding = new Thickness(0),
                                Style = (Style)FindResource("IconButton"), FontSize = 11 };
        System.Windows.Automation.AutomationProperties.SetName(none, "No emoticon");
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6), Child = panel };
        frame.SetResourceReference(Border.BackgroundProperty, "Surface.2");
        frame.SetResourceReference(Border.BorderBrushProperty, "Border.Window");
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, StaysOpen = false,
            AllowsTransparency = true, Child = frame,
        };
        none.Click += (_, _) => { _vm.Emoticon = 0; popup.IsOpen = false; };
        panel.Children.Add(none);
        foreach (var f in _vm.EmoteChoices)
        {
            var b = new Button
            {
                Content = new Image { Source = f.Image, Width = 32, Height = 32 }, Width = 34, Height = 34, Margin = new Thickness(2),
                Padding = new Thickness(0), Style = (Style)FindResource("IconButton"),
            };
            System.Windows.Automation.AutomationProperties.SetName(b, f.Name);
            b.Click += (_, _) => { _vm.Emoticon = f.Index; popup.IsOpen = false; };
            panel.Children.Add(b);
        }
        return popup;
    }
}
