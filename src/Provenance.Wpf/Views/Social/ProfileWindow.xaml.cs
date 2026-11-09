using System.Windows;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public partial class ProfileWindow : AppWindow
{
    private readonly SocialViewModel _vm;
    private FriendItem _friend;

    public ProfileWindow(SocialViewModel vm, IDialogs dialogs, FriendItem friend)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm.Profile;
        _friend = friend;
        SetFriend(friend);
        vm.Profile.Request(friend);
        void Popup(string text) => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible) dialogs.Info("PlayOnline profile", text);
        });
        vm.Profile.Popup += Popup;
        Closed += (_, _) => vm.Profile.Popup -= Popup;
    }

    internal void SetFriend(FriendItem friend)
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
