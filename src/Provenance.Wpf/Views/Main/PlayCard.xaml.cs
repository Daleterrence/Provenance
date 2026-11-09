using System.Windows;
using System.Windows.Controls;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.ViewModels.Main;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Main;

public partial class PlayCard : UserControl
{
    public PlayCard() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnFriendActivated(FriendItem friend) => Vm?.OpenChatWith(friend);

    // read fresh each time, so it follows Settings and any profile added since
    private void OnPlayChoices(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not FrameworkElement cog) return;
        var menu = new ContextMenu();
        Menus.Fill(menu, vm.PlayChoicesMenu());
        PlayChoicesIcon.IsOpen = true;
        menu.Closed += (_, _) => PlayChoicesIcon.IsOpen = false;
        Menus.ShowBelow(menu, cog);
    }
}
