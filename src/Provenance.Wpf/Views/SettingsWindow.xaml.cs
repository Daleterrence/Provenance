using System.Windows;

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
    }

    // Windower can change pol.exe while this window's open, so it's read again on every show and every switch back
    private void ReadLargeAddressAware()
    {
        if (DataContext is ViewModels.SettingsViewModel vm) _ = vm.ReadLargeAddressAwareAsync();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
