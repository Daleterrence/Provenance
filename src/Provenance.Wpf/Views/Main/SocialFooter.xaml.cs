using System.Windows.Controls;
using System.Windows.Input;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf.Views.Main;

public partial class SocialFooter : UserControl
{
    public SocialFooter() => InitializeComponent();

    internal bool FillHandleMenu(ContextMenu menu)
    {
        if (DataContext is not MainViewModel { GameOpen: false } vm) return false;
        Menus.Fill(menu, vm.HandleMenu());
        return menu.Items.Count > 0;
    }

    private void OnHandleMenu(object sender, ContextMenuEventArgs e)
    {
        if (MyPicture.ContextMenu is not { } menu || !FillHandleMenu(menu)) e.Handled = true;
    }

    // a double-click would open the window and shut it again, so its second press goes nowhere
    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1) e.Handled = true;
    }
}
