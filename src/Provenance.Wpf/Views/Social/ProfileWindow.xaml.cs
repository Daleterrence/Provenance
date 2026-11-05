using System.Windows;
using Provenance.Wpf.ViewModels;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Views.Dialogs;

namespace Provenance.Wpf.Views.Social;

public partial class ProfileWindow : Window
{
    private FriendItem _friend;

    public ProfileWindow(SocialViewModel vm, FriendItem friend)
    {
        InitializeComponent();
        WindowFrame.Attach(this);
        DataContext = vm;
        _friend = friend;
        SetFriend(friend);
        vm.RequestProfile(friend);
        void Popup(string text) => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible) MessageDialog.ShowInfo(this, "PlayOnline profile", text);
        });
        vm.ProfilePopup += Popup;
        Closed += (_, _) => vm.ProfilePopup -= Popup;
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
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
        (Application.Current.MainWindow?.DataContext as MainViewModel)?.OpenChatWith(_friend);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
