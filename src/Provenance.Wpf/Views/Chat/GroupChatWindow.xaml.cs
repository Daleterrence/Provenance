using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Provenance.Common.Settings;
using Provenance.Common.Social;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Views.Dialogs;
using Provenance.Wpf.ViewModels.Chat;

namespace Provenance.Wpf.Views.Chat;

// one for the session, unowned like the chat rooms window. Close only hides it (the viewer's Close), the chat stays in
public partial class GroupChatWindow : Window
{
    private const string DialogTitle = "Group Chat";
    private readonly GroupChatViewModel _vm;
    private readonly ISettingsService _settings;
    private readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _quiet, _flashing;

    public GroupChatWindow(GroupChatViewModel vm, ISettingsService settings)
    {
        InitializeComponent();
        WindowFrame.Attach(this);
        DataContext = _vm = vm;
        _settings = settings;
        _vm.Lines.CollectionChanged += OnLinesChanged;
        _vm.Ended += OnEnded;
        _vm.LineArrived += OnLineArrived;
        _tick.Tick += (_, _) => _vm.Tick(DateTime.UtcNow);
        _tick.Start();
        Activated += (_, _) =>
        {
            _vm.WindowActive = true;
            _vm.ShowTimes = _settings.Current.GroupChatTimes;
            _vm.Seen();
            if (_flashing) TaskbarFlash.Stop(this);
            _flashing = false;
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
            _vm.Lines.CollectionChanged -= OnLinesChanged;
            _vm.Ended -= OnEnded;
            _vm.LineArrived -= OnLineArrived;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Hide();
        };
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
        _flashing = true;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            Dispatcher.InvokeAsync(LineScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
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
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MenuItem("Clear chat...", _vm.Lines.Count > 0, ClearChat));
        menu.IsOpen = true;
    }

    internal void ClearChat()
    {
        var shown = _vm.Lines.Select(l => l.Id).ToList();
        if (MessageDialog.Confirm(this, "Clear chat", ClearQuestion(_vm.GroupName), "CLEAR", destructive: true) != 0) return;
        _vm.ClearLines(shown);
    }

    public static string ClearQuestion(string group) =>
        $"Delete every line of the \"{group}\" group chat that Provenance has kept on this PC? This can't be undone.";

    private void OnLineMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TextBox { Tag: GroupChatLineItem line, ContextMenu: { } menu } box)
        {
            e.Handled = true;
            return;
        }
        FillLineMenu(menu, box, line);
    }

    internal void FillLineMenu(ContextMenu menu, TextBox? box, GroupChatLineItem line)
    {
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy, CommandTarget = box });
        menu.Items.Add(MenuItem("Delete message", true, () => _vm.DeleteLine(line)));
    }

    private static MenuItem MenuItem(string header, bool enabled, Action act)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => act();
        return item;
    }

    private void OnComposeKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
    }

    // characters Shift-JIS can't carry never get in, typed or pasted
    private void OnComposeInput(object sender, TextCompositionEventArgs e)
    {
        var kept = _vm.FilterInput(e.Text);
        if (kept == e.Text || sender is not TextBox box) return;
        e.Handled = true;
        int room = GroupChatFiles.MaxText - (box.Text.Length - box.SelectionLength);
        box.SelectedText = kept.Length > room ? kept[..Math.Max(0, room)] : kept;
        box.CaretIndex = box.SelectionStart + box.SelectionLength;
    }

    private void OnComposePaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox box || e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text) return;
        var kept = _vm.FilterInput(text.Replace("\r", "").Replace('\n', ' '));
        e.CancelCommand();
        int room = GroupChatFiles.MaxText - (box.Text.Length - box.SelectionLength);
        box.SelectedText = kept.Length > room ? kept[..Math.Max(0, room)] : kept;
        box.CaretIndex = box.SelectionStart + box.SelectionLength;
    }

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
}
