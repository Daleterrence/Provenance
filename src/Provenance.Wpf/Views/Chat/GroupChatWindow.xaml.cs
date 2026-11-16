using System.Windows;
using System.Windows.Controls;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.ViewModels.Chat;

namespace Provenance.Wpf.Views.Chat;

// one for the session, unowned like the chat rooms window. Close only hides it (the viewer's Close), the chat stays in
public partial class GroupChatWindow : AppWindow
{
    private readonly GroupChatViewModel _vm;
    private readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _quiet;

    public GroupChatWindow(GroupChatViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _vm.Ended += OnEnded;
        _vm.LineArrived += OnLineArrived;
        _tick.Tick += (_, _) =>
        {
            _vm.Tick();
            if (!_vm.NeedsTick) _tick.Stop();
        };
        _vm.TickNeeded += OnTickNeeded;
        if (_vm.NeedsTick) _tick.Start();
        Activated += (_, _) =>
        {
            _vm.WindowActivated();
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
            _vm.TickNeeded -= OnTickNeeded;
            _vm.Ended -= OnEnded;
            _vm.LineArrived -= OnLineArrived;
        };
    }

    private void OnTickNeeded()
    {
        if (!_tick.IsEnabled) _tick.Start();
    }

    protected override bool OnEscape()
    {
        Hide();
        return true;
    }

    public void CloseQuietly()
    {
        _quiet = true;
        _vm.CloseQuietly();
        Close();
    }

    private void OnEnded(string? message)
    {
        Hide();
        if (message != null) _vm.Tell(message);
    }

    // up but behind: the taskbar button flashes if that's wanted. A hidden window has no button, and the notice is the
    // main window's
    private void OnLineArrived(string group, string who, string text, int count)
    {
        if (!_vm.FlashWanted || !IsVisible || IsActive) return;
        TaskbarFlash.Start(this);
    }

    private void OnHide(object sender, RoutedEventArgs e) => Hide();

    private void OnExit(object sender, RoutedEventArgs e)
    {
        if (!_vm.Open) { Hide(); return; }
        if (_vm.ConfirmExit()) _vm.Exit();
    }

    private void OnMore(object sender, RoutedEventArgs e) => Menus.Toggle(MoreButton, () =>
    {
        var menu = new ContextMenu();
        Menus.Fill(menu, _vm.MoreMenu());
        return menu;
    });

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
