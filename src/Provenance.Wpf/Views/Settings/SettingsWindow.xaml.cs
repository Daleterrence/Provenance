using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Provenance.Common.Game.Windower;
using Provenance.Common.Social;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.ViewModels.Settings;
using Provenance.Wpf.ViewModels.Shared;
using Provenance.Wpf.Win32;

namespace Provenance.Wpf.Views.Settings;

// General and Windower/Ashita save each change straight away, but the Game page holds its changes until Save, so
// closing with unsaved Game changes asks first
public partial class SettingsWindow : AppWindow
{
    public SettingsWindow()
    {
        InitializeComponent();
        Tabs.SelectionChanged += (_, _) => ShowPanel(SelectedTab);
        Loaded += (_, _) => ShowPanel(SelectedTab);
        Activated += (_, _) => { if (IsLoaded && SelectedTab == SettingsTab.General) ReadLargeAddressAware(); };
        Deactivated += (_, _) => CancelConsoleKey();
        PreviewMouseDown += (_, _) => { if (!ConsoleKeyButton.IsMouseOver) CancelConsoleKey(); };
        Closing += OnClosing;
        DataContextChanged += (_, _) => Wire();
    }

    private SettingsTab SelectedTab => Tabs.SelectedItem is FrameworkElement { Tag: SettingsTab tab } ? tab : SettingsTab.General;

    internal void SelectTab(SettingsTab tab) => Tabs.SelectedItem = Tabs.Items.OfType<FrameworkElement>().First(i => Equals(i.Tag, tab));

    private void Wire()
    {
        if (DataContext is not SettingsViewModel vm) return;
        // a box handed a new list keeps its old pick without it being in the list, so it's cleared first
        vm.Hooks.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HookSettings.SwitcherProfiles)) ProfileSwitcher.SetCurrentValue(Selector.SelectedItemProperty, null);
        };
        vm.Hooks.CloseRequested += Close;
    }

    private void OnSwitchHandle(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HandleRow row } && DataContext is SettingsViewModel vm)
            vm.Handles.Switch(row);
    }

    private void OnUnlinkCharacter(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HandleCharacterRow c } && DataContext is SettingsViewModel vm)
            vm.Handles.Unlink(c);
    }

    // the other handles to pick from, a full one greyed out with the limit under its name
    private void OnLinkCharacter(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HandleCharacterRow c } button ||
            DataContext is not SettingsViewModel vm) return;
        var menu = new ContextMenu();
        Menus.Fill(menu, vm.Handles.LinkMenu(c));
        if (menu.Items.Count > 0) Menus.ShowBelow(menu, button);
        else vm.Handles.NoTarget();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (OwnerClosing || DataContext is not SettingsViewModel { Game.HasUnsavedChanges: true } vm) return;
        if (!vm.ConfirmClose()) e.Cancel = true;
    }

    private void ShowPanel(SettingsTab tab)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.ShownTab = tab;
            // a profile picked on another tab since the Game page was last shown
            if (tab == SettingsTab.Game && !vm.ShowingGame()) vm.PickGameProfile();
        }
        foreach (FrameworkElement panel in Panels.Children)
            panel.Visibility = Equals(panel.Tag, tab) ? Visibility.Visible : Visibility.Collapsed;
        if (tab == SettingsTab.General) ReadLargeAddressAware();
        if (tab == SettingsTab.WindowerAshita && DataContext is SettingsViewModel hooks) hooks.Hooks.ReloadCards();
    }

    // Windower can change pol.exe while this window's open, so it's read again on every show and every switch back
    private void ReadLargeAddressAware()
    {
        if (DataContext is SettingsViewModel vm) _ = vm.ReadLargeAddressAwareAsync();
    }

    private void CancelConsoleKey() => (DataContext as SettingsViewModel)?.Windower.CancelConsoleKey();

    private void OnConsoleKeyFocusLost(object sender, KeyboardFocusChangedEventArgs e) => CancelConsoleKey();

    // while it waits every key is the button's, so Esc doesn't close Settings and Tab doesn't move on. A held key's
    // repeats don't count
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

    // the scan code's in the key message itself, which tells Enter from the keypad's. A key that didn't come from one
    // (raised by hand) gets the scan code Windows would give it
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
