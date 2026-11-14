using System.Windows;
using Provenance.Wpf.Controls;
using Provenance.Wpf.ViewModels.Install;

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
        if (!vm.ConfirmStop())
        {
            e.Cancel = true;
            return;
        }
        vm.CancelCommand.Execute(null);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
