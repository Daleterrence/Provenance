using System.Windows;
using System.Windows.Input;

namespace Provenance.Wpf.Views;

public partial class InstallWindow : Window
{
    private bool _ownerClosing;

    public InstallWindow()
    {
        InitializeComponent();
        WindowFrame.Attach(this);
        Closing += OnClosing;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Loaded += (_, _) => { if (Owner != null) Owner.Closing += OnOwnerClosing; };
        Closed += (_, _) => { if (Owner != null) Owner.Closing -= OnOwnerClosing; };
    }

    private void OnOwnerClosing(object? sender, System.ComponentModel.CancelEventArgs e) => _ownerClosing = true;

    // closing mid-install asks, then stops the download and the installers so nothing keeps running with no window
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is not ViewModels.InstallViewModel { Running: true } vm) return;
        if (_ownerClosing) { vm.CancelCommand.Execute(null); return; }   // the app's closing, so no question
        // the view model's "Cancelled" note, worded for a window that's closing
        var what = vm.CurrentStep switch
        {
            0 => "Closing this window stops the download. It'll carry on from where it left off next time.",
            1 => "Closing this window stops unpacking. The download is kept, so you can unpack it next time.",
            _ => "Closing this window stops the install before it finishes. You can finish it next time.",
        };
        if (MessageDialog.Confirm(this, "Stop the install?", what, "STOP", destructive: true) is null)
        {
            e.Cancel = true;
            return;
        }
        vm.CancelCommand.Execute(null);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;   // let text selection work
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
