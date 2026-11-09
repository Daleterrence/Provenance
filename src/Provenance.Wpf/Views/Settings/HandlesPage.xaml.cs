using System.Windows;
using System.Windows.Controls;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.Views.Settings;

public partial class HandlesPage : UserControl
{
    public HandlesPage() => InitializeComponent();

    private void OnSwitchHandle(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HandleRow row } && DataContext is HandlesViewModel handles)
            handles.Switch(row);
    }

    private void OnUnlinkCharacter(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HandleCharacterRow c } && DataContext is HandlesViewModel handles)
            handles.Unlink(c);
    }

    // the other handles to pick from, a full one greyed out with the limit under its name
    private void OnLinkCharacter(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HandleCharacterRow c } button ||
            DataContext is not HandlesViewModel handles) return;
        var menu = new ContextMenu();
        Menus.Fill(menu, handles.LinkMenu(c));
        if (menu.Items.Count > 0) Menus.ShowBelow(menu, button);
        else handles.NoTarget();
    }
}
