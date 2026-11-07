using System.Windows;
using System.Windows.Input;
using Provenance.Wpf.Controls;
using Provenance.Wpf.ViewModels.Install;
using Provenance.Wpf.Views.Dialogs;

namespace Provenance.Wpf.Views.Install;

public partial class InstallWindow : AppWindow
{
    public InstallWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Loaded += (_, _) => GrowForRegionChoice();
    }

    // the Region choice only shows with no viewer on the PC. The window grows by exactly its height then (still
    // centred), so the rest looks the same either way
    private void GrowForRegionChoice()
    {
        var grow = RegionChoice.ActualHeight;
        if (grow <= 0) return;
        Height += grow;
        Top -= grow / 2;
    }

    // closing mid-install asks, then stops the download and the installers so nothing keeps running with no window
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is not InstallViewModel { Running: true } vm) return;
        if (OwnerClosing) { vm.CancelCommand.Execute(null); return; }   // the app's closing, so no question
        // the view model's "Cancelled" note, worded for a window that's closing
        var what = vm.CurrentStep switch
        {
            0 => "Closing this window stops the download. It'll carry on from where it left off next time.",
            1 => "Closing this window stops unpacking. The download is kept, so you can carry on with the install next time.",
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
