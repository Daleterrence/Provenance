using System.Windows.Controls;
using Provenance.Wpf.ViewModels.Settings;

namespace Provenance.Wpf.Views.Settings;

public partial class AddonsPluginsView : UserControl
{
    public AddonsPluginsView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => (DataContext as AddonsPluginsViewModel)?.VisibleChanged((bool)e.NewValue);
    }
}
