using System.Windows.Controls;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf.Views.Main;

public partial class SocialFooter : UserControl
{
    public SocialFooter() => InitializeComponent();

    internal bool FillHandleMenu(ContextMenu menu)
    {
        if (DataContext is not MainViewModel vm) return false;
        Menus.Fill(menu, vm.HandleMenu());
        return menu.Items.Count > 0;
    }

    private void OnHandleMenu(object sender, ContextMenuEventArgs e)
    {
        if (MyPicture.ContextMenu is not { } menu || !FillHandleMenu(menu)) e.Handled = true;
    }
}
