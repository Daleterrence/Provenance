using System.Windows.Controls;
using Provenance.Wpf.ViewModels;

namespace Provenance.Wpf.Views.Settings;

// scans when the tab comes up, so opening Settings doesn't read every DLL in the folder
public partial class AddonsPluginsView : UserControl
{
    public AddonsPluginsView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => (DataContext as AddonsPluginsViewModel)?.VisibleChanged((bool)e.NewValue);
    }
}
