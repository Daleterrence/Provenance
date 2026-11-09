using System.Windows;
using System.Windows.Controls.Primitives;
using Provenance.Wpf.Controls;
using Provenance.Wpf.ViewModels.Settings;

namespace Provenance.Wpf.Views.Settings;

// General and Windower/Ashita save each change straight away, but the Game page holds its changes until Save, so
// closing with unsaved Game changes asks first
public partial class SettingsWindow : AppWindow
{
    private readonly Dictionary<SettingsTab, FrameworkElement> _pages = [];

    public SettingsWindow()
    {
        InitializeComponent();
        Tabs.ItemsSource = SettingsPages.All;
        Tabs.SelectedIndex = 0;
        Tabs.SelectionChanged += (_, _) => ShowPage(SelectedTab);
        Loaded += (_, _) => ShowPage(SelectedTab);
        Activated += (_, _) => { if (IsLoaded && SelectedTab == SettingsTab.General) ReadLargeAddressAware(); };
        Closing += OnClosing;
        DataContextChanged += (_, _) =>
        {
            Wire();
            if (IsLoaded) ShowPage(SelectedTab);
        };
    }

    private SettingsTab SelectedTab => Tabs.SelectedItem is SettingsPage page ? page.Tab : SettingsTab.General;

    internal FrameworkElement? CurrentPage => PageHost.Content as FrameworkElement;

    internal void SelectTab(SettingsTab tab) => Tabs.SelectedItem = SettingsPages.All.First(p => p.Tab == tab);

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

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (OwnerClosing || DataContext is not SettingsViewModel { Game.HasUnsavedChanges: true } vm) return;
        if (!vm.ConfirmClose()) e.Cancel = true;
    }

    private void ShowPage(SettingsTab tab)
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.ShownTab = tab;
        // a profile picked on another tab since the Game page was last shown
        if (tab == SettingsTab.Game && !vm.ShowingGame()) vm.PickGameProfile();
        if (!_pages.TryGetValue(tab, out var page))
            _pages[tab] = page = SettingsPages.All.First(p => p.Tab == tab).Create(vm);
        PageHost.Content = page;
        if (tab == SettingsTab.General) ReadLargeAddressAware();
        if (tab == SettingsTab.WindowerAshita) vm.Hooks.ReloadCards();
    }

    // Windower can change pol.exe while this window's open, so it's read again on every show and every switch back
    private void ReadLargeAddressAware()
    {
        if (DataContext is SettingsViewModel vm) _ = vm.ReadLargeAddressAwareAsync();
    }
}
