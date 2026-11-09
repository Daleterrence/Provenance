using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf.Views.Main;

public partial class SocialFooter : UserControl
{
    public SocialFooter() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnEditProfile(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.Social.EditProfile();
    }

    private void OnAvatarClick(object sender, MouseButtonEventArgs e) =>
        Vm?.Social.ChangeProfilePictureCommand.Execute(null);
}
