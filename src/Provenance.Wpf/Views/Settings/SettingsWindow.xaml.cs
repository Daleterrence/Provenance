using System.Windows;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using Provenance.Wpf.Controls;
using Provenance.Wpf.ViewModels.Settings;

namespace Provenance.Wpf.Views.Settings;

// every page saves each change straight away bar the FFXI one, which holds its changes until Save, so closing with
// unsaved ones asks first
public partial class SettingsWindow : AppWindow
{
    private readonly Dictionary<SettingsTab, FrameworkElement> _pages = [];

    public SettingsWindow()
    {
        InitializeComponent();
        var pages = new ListCollectionView(SettingsPages.All.ToList());
        pages.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SettingsPage.Group)));
        Tabs.ItemsSource = pages;
        Tabs.SelectedIndex = 0;
        Tabs.SelectionChanged += (_, _) => ShowPage(SelectedTab);
        Loaded += (_, _) => ShowPage(SelectedTab);
        Activated += (_, _) => { if (IsLoaded && DataContext is SettingsViewModel vm) vm.WindowActivated(); };
        Closing += OnClosing;
        DataContextChanged += OnDataContextChanged;
    }

    private SettingsTab SelectedTab => Tabs.SelectedItem is SettingsPage page ? page.Tab : SettingsTab.General;

    internal FrameworkElement? CurrentPage => PageHost.Content as FrameworkElement;

    internal void SelectTab(SettingsTab tab) => Tabs.SelectedItem = SettingsPages.All.First(p => p.Tab == tab);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsViewModel old)
        {
            old.Hooks.PropertyChanged -= OnHooksChanged;
            old.Hooks.CloseRequested -= Close;
        }
        _pages.Clear();
        if (e.NewValue is SettingsViewModel vm)
        {
            vm.Hooks.PropertyChanged += OnHooksChanged;
            vm.Hooks.CloseRequested += Close;
        }
        if (IsLoaded) ShowPage(SelectedTab);
    }

    // a box handed a new list keeps its old pick without it being in the list, so it's cleared first
    private void OnHooksChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HookSettings.SwitcherProfiles)) ProfileSwitcher.SetCurrentValue(Selector.SelectedItemProperty, null);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (OwnerClosing || DataContext is not SettingsViewModel { Game.HasUnsavedChanges: true } vm) return;
        if (!vm.ConfirmClose()) e.Cancel = true;
    }

    private void ShowPage(SettingsTab tab)
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.ShowTab(tab);
        if (!_pages.TryGetValue(tab, out var page))
            _pages[tab] = page = SettingsPages.All.First(p => p.Tab == tab).Create(vm);
        PageHost.Content = page;
    }
}
