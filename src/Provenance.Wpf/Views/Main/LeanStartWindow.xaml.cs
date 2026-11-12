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

    // the start's finished with it, so it goes without a word
    internal void CloseNow()
    {
        _done = true;
        Close();
    }

    // closed by hand it's cancel, and the start takes it down once that's gone through. With nothing left to cancel
    // it stays, this is the only window until the start's over
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
