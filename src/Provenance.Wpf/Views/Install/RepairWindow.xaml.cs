using System.Windows;
using System.Windows.Input;
using Provenance.Wpf.ViewModels;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Views.Dialogs;

namespace Provenance.Wpf.Views.Install;

public partial class RepairWindow : Window
{
    private readonly RepairViewModel _vm;

    public RepairWindow(RepairViewModel vm)
    {
        InitializeComponent();
        WindowFrame.Attach(this);
        DataContext = _vm = vm;
        // Enter and Esc both answer no, the yes takes DAT mods away for good
        vm.Ask = (title, body) => MessageDialog.Confirm(this, title, body, "CONTINUE", safeKeys: true) is not null;
        Loaded += async (_, _) => await vm.RunAsync();
        Closing += OnClosing;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    // closing mid-repair asks, then stops it. The window stays up until the repair's really ended (it can be mid-step)
    // and shows how it went
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_vm.Running) return;
        e.Cancel = true;
        if (_vm.StopRequested) return;
        if (MessageDialog.Confirm(this, "Stop the repair?",
                "The repair will stop after its current step. Run it again later to finish it.", "STOP", destructive: true) is null)
            return;
        _vm.CancelCommand.Execute(null);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
