using System.Windows;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Views.Dialogs;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public partial class ProfileWindow : AppWindow
{
    private readonly SocialViewModel _vm;
    private FriendItem _friend;

    public ProfileWindow(SocialViewModel vm, FriendItem friend)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _friend = friend;
        SetFriend(friend);
        vm.RequestProfile(friend);
        void Popup(string text) => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible) MessageDialog.ShowInfo(this, "PlayOnline profile", text);
        });
        vm.ProfilePopup += Popup;
        Closed += (_, _) => vm.ProfilePopup -= Popup;
    }

    // the view model only holds one profile, so an open window gets reused rather than two fighting over it
    public static void ShowFor(SocialViewModel vm, FriendItem friend, Window? owner)
    {
        if (Application.Current.Windows.OfType<ProfileWindow>().FirstOrDefault() is { } open)
        {
            open.SetFriend(friend);
            vm.RequestProfile(friend);
            open.Activate();
            return;
        }
        new ProfileWindow(vm, friend) { Owner = owner }.Show();
    }

    private void SetFriend(FriendItem friend)
    {
        _friend = friend;
        Picture.InGame = friend.InGame;
        SendButton.Visibility = friend.CanMessage ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSendMessage(object sender, RoutedEventArgs e)
    {
        _vm.RequestChat(_friend);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
