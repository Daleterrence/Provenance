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

    private void GrowForRegionChoice()
    {
        var grow = RegionChoice.ActualHeight;
        if (grow <= 0) return;
        var area = WindowFrame.WorkAreaOf(this);
        var height = Math.Min(Math.Min(Height + grow, MaxHeight), area.Height);
        Top = Math.Clamp(Top - (height - Height) / 2, area.Top, Math.Max(area.Top, area.Bottom - height));
        Height = height;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is not InstallViewModel { Running: true } vm) return;
        if (OwnerClosing) { vm.CancelCommand.Execute(null); return; }
        if (!vm.ConfirmStop())
        {
            e.Cancel = true;
            return;
        }
        vm.CancelCommand.Execute(null);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
