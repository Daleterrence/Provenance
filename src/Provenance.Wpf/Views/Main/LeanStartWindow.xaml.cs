using System.ComponentModel;
using Provenance.Wpf.Controls;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf.Views.Main;

public partial class LeanStartWindow : AppWindow
{
    private bool _done;

    public LeanStartWindow()
    {
        InitializeComponent();
    }

    internal void CloseNow()
    {
        _done = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_done && DataContext is MainViewModel main)
        {
            e.Cancel = true;
            if (main.CanCancel) main.CancelLaunchCommand.Execute(null);
        }
        base.OnClosing(e);
    }
}
