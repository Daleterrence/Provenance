using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Provenance.Wpf.Views;

// General and Windower/Ashita save each change straight away, but the Game page holds its changes until Save, so
// closing with unsaved Game changes asks first
public partial class SettingsWindow : Window
{
    private bool _ownerClosing;   // tray Exit: no question that could leave a window up with the tray gone

    public SettingsWindow()
    {
        InitializeComponent();
        WindowFrame.Attach(this);
        Tabs.SelectionChanged += (_, _) => ShowPanel(Tabs.SelectedIndex);
        Loaded += (_, _) => ShowPanel(Tabs.SelectedIndex);
        Activated += (_, _) => { if (IsLoaded && Tabs.SelectedIndex == 0) ReadLargeAddressAware(); };
        Deactivated += (_, _) => CancelConsoleKey();
        PreviewMouseDown += (_, _) => { if (!ConsoleKeyButton.IsMouseOver) CancelConsoleKey(); };
        Closing += OnClosing;
        // KeyDown not PreviewKeyDown, so an open dropdown gets its Esc first and only closes itself
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        Loaded += (_, _) => { if (Owner != null) Owner.Closing += OnOwnerClosing; };
        Closed += (_, _) => { if (Owner != null) Owner.Closing -= OnOwnerClosing; };
        DataContextChanged += (_, _) => Wire();
    }

    private void Wire()
    {
        if (DataContext is not ViewModels.SettingsViewModel vm) return;
        vm.AskName = (title, prompt, start, check) =>
            InputDialog.Prompt(this, title, prompt, start, maxLength: WindowerNameLength, validate: check);
        vm.Confirm = (title, body, ok, destructive) => MessageDialog.Confirm(this, title, body, ok, destructive: destructive) == 0;
    }

    // settings.xml takes 255, and Ashita's file names stop well short of that
    private const int WindowerNameLength = 255;

    private void OnOwnerClosing(object? sender, System.ComponentModel.CancelEventArgs e) => _ownerClosing = true;

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_ownerClosing || DataContext is not ViewModels.SettingsViewModel { Game.HasUnsavedChanges: true } vm) return;
        var pick = MessageDialog.Confirm(this, "Unsaved game settings",
            "You've changed some FINAL FANTASY XI settings but haven't saved them yet.", "CLOSE",
            "Save the changes, then close", "Close without saving");
        if (pick is null) { e.Cancel = true; return; }
        if (pick == 0)
        {
            vm.Game.SaveCommand.Execute(null);
            e.Cancel = vm.Game.HasUnsavedChanges;   // the save failed, its error's on the page
        }
    }

    private void ShowPanel(int index)
    {
        // a profile picked under Windower/Ashita since the Game page was last shown
        if (index == 2 && DataContext is ViewModels.SettingsViewModel vm)
            vm.ShowingGame(which => MessageDialog.Confirm(this, "Unsaved game settings",
                $"You've changed some game settings{(which is null ? "" : " for " + which)} but haven't saved them yet.", "CONTINUE",
                "Save them first", "Don't save them") switch { 0 => true, 1 => false, _ => null });
        for (int i = 0; FindName($"Panel{i}") is UIElement panel; i++)
            panel.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        if (index == 0) ReadLargeAddressAware();
        if (index == 1 && DataContext is ViewModels.SettingsViewModel hooks) hooks.ShowingHooks();
    }

    // Windower can change pol.exe while this window's open, so it's read again on every show and every switch back
    private void ReadLargeAddressAware()
    {
        if (DataContext is ViewModels.SettingsViewModel vm) _ = vm.ReadLargeAddressAwareAsync();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void CancelConsoleKey() => (DataContext as ViewModels.SettingsViewModel)?.CancelConsoleKey();

    private void OnConsoleKeyFocusLost(object sender, KeyboardFocusChangedEventArgs e) => CancelConsoleKey();

    // while it waits every key is the button's, so Esc doesn't close Settings and Tab doesn't move on. A held key's
    // repeats don't count
    private void OnConsoleKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ViewModels.SettingsViewModel { WaitingForConsoleKey: true } vm) return;
        e.Handled = true;
        if (!e.IsRepeat) vm.PressConsoleKey(DirectInputCode(e.Key == Key.System ? e.SystemKey : e.Key));
    }

    private void OnConsoleKeyUp(object sender, KeyEventArgs e)
    {
        if (DataContext is not ViewModels.SettingsViewModel { WaitingForConsoleKey: true } vm) return;
        e.Handled = true;
        if ((e.Key == Key.System ? e.SystemKey : e.Key) == Key.Snapshot) vm.PressConsoleKey(DirectInputCode(Key.Snapshot));
    }

    // the scan code's in the key message itself, which tells Enter from the keypad's. A key that didn't come from one
    // (raised by hand) gets the scan code Windows would give it
    private static int DirectInputCode(Key key)
    {
        int vk = KeyInterop.VirtualKeyFromKey(key);
        var msg = ComponentDispatcher.CurrentKeyboardMessage;
        if (msg.message is WmKeyDown or WmSysKeyDown && (int)msg.wParam == vk)
        {
            long lp = msg.lParam.ToInt64();
            return ViewModels.WindowerKeyNames.DirectInputCode(vk, (int)(lp >> 16) & 0xFF, (lp & 0x1000000) != 0);
        }
        uint sc = MapVirtualKeyW((uint)vk, MapVkToVscEx);
        return ViewModels.WindowerKeyNames.DirectInputCode(vk, (int)(sc & 0xFF), (sc & 0xFF00) is 0xE000 or 0xE100);
    }

    private const int WmKeyDown = 0x100, WmSysKeyDown = 0x104;
    private const uint MapVkToVscEx = 4;

    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
}
