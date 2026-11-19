using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.ViewModels.Main;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Main;

public partial class PlayCard : UserControl
{
    public PlayCard() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    internal void DeselectUnlessIn(DependencyObject? pressed)
    {
        var list = FriendList.List;
        if (list.SelectedItem is null) return;
        for (var at = pressed; at is not null; at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (at == list) return;
        list.SelectedItem = null;
    }

    private void OnFriendActivated(FriendItem friend) => Vm?.OpenChatWith(friend);

    private void OnPlayChoices(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not FrameworkElement cog) return;
        Menus.Toggle(cog, () =>
        {
            var menu = new ContextMenu();
            Menus.Fill(menu, vm.PlayPlatform.ChoicesMenu());
            PlayChoicesIcon.IsOpen = true;
            menu.Closed += (_, _) => PlayChoicesIcon.IsOpen = Menus.OpenFrom(cog);
            return menu;
        });
    }
}
