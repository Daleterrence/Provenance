using System.Windows;
using Provenance.Wpf.Controls;
using Provenance.Wpf.ViewModels.Install;

namespace Provenance.Wpf.Views.Install;

public partial class RepairWindow : AppWindow
{
    private readonly RepairViewModel _vm;

    public RepairWindow(RepairViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Loaded += async (_, _) => await vm.RunAsync();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_vm.Running) return;
        e.Cancel = true;
        if (_vm.StopRequested) return;
        if (_vm.ConfirmStop()) _vm.CancelCommand.Execute(null);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
