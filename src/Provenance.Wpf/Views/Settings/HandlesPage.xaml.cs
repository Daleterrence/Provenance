using System.Windows;
using System.Windows.Controls;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.Views.Settings;

public partial class HandlesPage : UserControl
{
    public HandlesPage() => InitializeComponent();

    private void OnLinkCharacter(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HandleCharacterRow c } button ||
            DataContext is not HandlesViewModel handles) return;
        Menus.Toggle(button, () =>
        {
            var menu = new ContextMenu();
            Menus.Fill(menu, handles.LinkMenu(c));
            if (menu.Items.Count > 0) return menu;
            handles.NoTarget();
            return null;
        });
    }
}
