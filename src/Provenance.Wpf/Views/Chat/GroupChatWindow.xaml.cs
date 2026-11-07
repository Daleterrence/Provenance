using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Provenance.Common.Settings;
using Provenance.Common.Social;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.Views.Dialogs;
using Provenance.Wpf.ViewModels.Chat;

namespace Provenance.Wpf.Views.Chat;

// one for the session, unowned like the chat rooms window. Close only hides it (the viewer's Close), the chat stays in
public partial class GroupChatWindow : AppWindow
{
    private const string DialogTitle = "Group Chat";
    private readonly GroupChatViewModel _vm;
    private readonly ISettingsService _settings;
    private readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _quiet;

    public GroupChatWindow(GroupChatViewModel vm, ISettingsService settings)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _settings = settings;
        _vm.Ended += OnEnded;
        _vm.LineArrived += OnLineArrived;
        _tick.Tick += (_, _) => _vm.Tick(DateTime.UtcNow);
        _tick.Start();
        Activated += (_, _) =>
        {
            _vm.WindowActive = true;
            _vm.ShowTimes = _settings.Current.GroupChatTimes;
            _vm.Seen();
            TaskbarFlash.StopIfFlashing(this);
        };
        Deactivated += (_, _) => _vm.WindowActive = false;
        IsVisibleChanged += (_, _) => { if (!IsVisible) _vm.WindowActive = false; };
        Closing += (_, e) =>
        {
            // the frame's X and Alt+F4 are the viewer's Close too, the window only really goes with the session
            if (_quiet) return;
            e.Cancel = true;
            Hide();
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            _vm.Ended -= OnEnded;
            _vm.LineArrived -= OnLineArrived;
        };
    }

    protected override bool OnEscape()
    {
        Hide();
        return true;
    }

    public static GroupChatWindow? Current => Application.Current.Windows.OfType<GroupChatWindow>().FirstOrDefault();

    public static void ShowFor(GroupChatViewModel vm, ISettingsService settings)
    {
        var w = Current ?? new GroupChatWindow(vm, settings);
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    // the session's going, the chat's already dropped with it
    public void CloseQuietly()
    {
        _quiet = true;
        _vm.CloseQuietly();
        Close();
    }

    private void OnEnded(string? message)
    {
        bool shown = IsVisible;
        Hide();
        if (message != null) MessageDialog.ShowInfo(shown ? this : Application.Current.MainWindow, DialogTitle, message);
    }

    // hidden or behind: the taskbar button flashes if that's wanted, the notice is the main window's
    private void OnLineArrived(string group, string who, string text, int count)
    {
        if (!_settings.Current.GroupChatFlash || !IsVisible || IsActive) return;
        TaskbarFlash.Start(this);
    }

    private void OnHide(object sender, RoutedEventArgs e) => Hide();

    private void OnExit(object sender, RoutedEventArgs e)
    {
        if (!_vm.Open) { Hide(); return; }
        if (MessageDialog.Confirm(this, DialogTitle, GroupChatViewModel.ExitQuestion, "YES", cancelLabel: "NO") != 0) return;
        _vm.Exit();
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        Menus.Fill(menu, _vm.MoreMenu(Menus.Confirm(this)));
        Menus.ShowBelow(menu, MoreButton);
    }

    private void OnLineMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TextBox { DataContext: GroupChatLineItem line, ContextMenu: { } menu } box)
        {
            e.Handled = true;
            return;
        }
        FillLineMenu(menu, box, line);
    }

    internal void FillLineMenu(ContextMenu menu, TextBox? box, GroupChatLineItem line) =>
        Menus.FillLine(menu, box, _vm.LineMenu(line));
}
