using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Provenance.Common.Game.Windower;
using Provenance.Wpf.ViewModels.Settings;
using Provenance.Wpf.Win32;

namespace Provenance.Wpf.Views.Settings;

public partial class WindowerAshitaPage : UserControl
{
    private Window? _window;

    public WindowerAshitaPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is null) return;
            _window.Deactivated += OnWindowDeactivated;
            _window.PreviewMouseDown += OnWindowMouseDown;
        };
        Unloaded += (_, _) =>
        {
            if (_window is null) return;
            _window.Deactivated -= OnWindowDeactivated;
            _window.PreviewMouseDown -= OnWindowMouseDown;
            _window = null;
        };
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => CancelConsoleKey();

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ConsoleKeyButton.IsMouseOver) CancelConsoleKey();
    }

    private void CancelConsoleKey() => (DataContext as SettingsViewModel)?.Windower.CancelConsoleKey();

    private void OnConsoleKeyFocusLost(object sender, KeyboardFocusChangedEventArgs e) => CancelConsoleKey();

    private void OnConsoleKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel { Windower.WaitingForConsoleKey: true } vm) return;
        e.Handled = true;
        if (!e.IsRepeat) vm.Windower.PressConsoleKey(DirectInputCode(e.Key == Key.System ? e.SystemKey : e.Key));
    }

    private void OnConsoleKeyUp(object sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel { Windower.WaitingForConsoleKey: true } vm) return;
        e.Handled = true;
        if ((e.Key == Key.System ? e.SystemKey : e.Key) == Key.Snapshot) vm.Windower.PressConsoleKey(DirectInputCode(Key.Snapshot));
    }

    // the scan code's in the key message, which tells Enter from the keypad's
    private static int DirectInputCode(Key key)
    {
        int vk = KeyInterop.VirtualKeyFromKey(key);
        var msg = ComponentDispatcher.CurrentKeyboardMessage;
        if (msg.message is WmKeyDown or WmSysKeyDown && (int)msg.wParam == vk)
        {
            long lp = msg.lParam.ToInt64();
            return WindowerKeyNames.DirectInputCode(vk, (int)(lp >> 16) & 0xFF, (lp & 0x1000000) != 0);
        }
        uint sc = User32.MapVirtualKeyW((uint)vk, MapVkToVscEx);
        return WindowerKeyNames.DirectInputCode(vk, (int)(sc & 0xFF), (sc & 0xFF00) is 0xE000 or 0xE100);
    }

    private const int WmKeyDown = 0x100, WmSysKeyDown = 0x104;
    private const uint MapVkToVscEx = 4;
}
