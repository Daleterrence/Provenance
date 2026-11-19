#pragma warning disable RS0030
using System.Windows;
using System.Windows.Media;
using Provenance.Common.PolFormat;
using Provenance.Common.Social;
using Provenance.Common.Settings;
using Provenance.Wpf.Controls;
using Provenance.Wpf.ViewModels.Install;
using Provenance.Wpf.ViewModels.Main;
using Provenance.Wpf.ViewModels.Settings;
using Provenance.Wpf.ViewModels.Social;
using Provenance.Wpf.ViewModels.Chat;
using Provenance.Wpf.Views.Chat;
using Provenance.Wpf.Views.Install;
using Provenance.Wpf.Views.Main;
using Provenance.Wpf.Views.Settings;
using Provenance.Wpf.Views.Social;

namespace Provenance.Wpf.Services;

public enum SecondWindow { Friends, Groups, ChatRooms, ProfileEditor }

public interface IWindowManager
{
    bool ProfileEditorOpen { get; }

    void ShowFriends(SocialViewModel social);
    void ShowGroups(SocialViewModel social);
    void ShowPopOut(SocialViewModel social, PoppedConversation conversation);
    void ClosePopOut(PoppedConversation conversation);
    void ShowChatRooms(SocialViewModel social, Func<string, string, bool> savePassword, Action dropPassword,
                       Func<int, ImageSource?> emote, Action closed);
    void ShowGroupChat(GroupChatViewModel groupChat);
    void ShowProfile(SocialViewModel social, FriendItem friend);
    void EditProfile(SocialViewModel social);
    bool CloseIfShown(SecondWindow which);
    void AddFriend(SocialViewModel social, string? handleName = null, string? handleKey = null);
    bool ConfirmDropProfileEditor(string action, bool leaveSaving = false);
    void CloseProfileEditor();
    void CloseSessionWindows();
    void ShowInstall(InstallViewModel vm);
    void ShowSettings(SettingsViewModel vm, SettingsTab tab);
    void ShowRepair(RepairViewModel vm);
    void ShowAccounts(AccountsViewModel accounts);
    int? PickProfilePicture(ProfilePictureCatalog.Catalog catalog, AvatarService avatars, int current);
    void ShowLeanStart(MainViewModel main);
    void CloseLeanStart();
    void SetBusy(string? text);
}

public sealed class WindowManager(ISettingsService settings, IDialogs dialogs) : IWindowManager
{
    private const double Gap = 8;

    static WindowManager() => BusyCover.Install(Covered);

    internal static bool Covered(Window w) =>
        w is MainWindow or SocialWindow or PopOutWindow or GroupsWindow or ChatRoomsWindow or GroupChatWindow or SettingsWindow or EditProfileWindow or ProfileWindow;

    public void SetBusy(string? text) => BusyCover.Set(text);

    internal static Func<(double Left, double Right)> ScreenEdges { get; set; } = () =>
        (SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth);

    private static T? Find<T>() where T : Window => Application.Current?.Windows.OfType<T>().FirstOrDefault();

    // Activate on its own doesn't bring back a minimised or hidden window
    internal static void BringBack(Window w, bool activate = true)
    {
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        if (activate) w.Activate();
    }

    private static SocialWindow? FriendsWindow() => Application.Current?.Windows.OfType<SocialWindow>().FirstOrDefault();

    private static PopOutWindow? PopOutFor(PoppedConversation c) =>
        Application.Current?.Windows.OfType<PopOutWindow>().FirstOrDefault(w => w.Conversation == c);

    public bool ProfileEditorOpen => Find<EditProfileWindow>() is not null;

    public void ShowFriends(SocialViewModel social)
    {
        if (FriendsWindow() is { } open)
        {
            BringBack(open);
            return;
        }
        var w = new SocialWindow();
        if (settings.Current.MessagesPaneWidth > 0) w.SavedPaneWidth = settings.Current.MessagesPaneWidth;
        w.PaneWidthChanged += width => settings.SaveChange(s => s.MessagesPaneWidth = width);
        if (!WindowPlaces.Restore(settings, w, "friends")) w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.DataContext = social;
        WindowPlaces.Track(settings, w, "friends", w.ListBounds);
        w.Show();
    }

    public void ShowPopOut(SocialViewModel social, PoppedConversation conversation)
    {
        if (PopOutFor(conversation) is { } open)
        {
            BringBack(open);
            return;
        }
        var w = new PopOutWindow(conversation) { DataContext = conversation.Messages };
        if (!PlaceAtLastPopOut(w))
        {
            if (FriendsWindow() is { IsVisible: true } fw)
            {
                var (screenLeft, screenRight) = ScreenEdges();
                double left = LeftBeside(fw.Left, fw.ActualWidth, w.Width, screenLeft, screenRight);
                (w.WindowStartupLocation, w.Left, w.Top) = (WindowStartupLocation.Manual, left, fw.Top);
            }
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        WindowPlaces.Track(settings, w, PopOutKey, live: true);
        PopOutOpening?.Invoke(w);
        w.Show();
    }

    internal const string PopOutKey = "pop-out";

    internal static Action<Window>? PopOutOpening { get; set; }

    private bool PlaceAtLastPopOut(PopOutWindow w)
    {
        if (!settings.Current.WindowPlaces.TryGetValue(PopOutKey, out var p)) return false;
        var saved = new Spot(p.Left, p.Top, p.Width, p.Height);
        if (!PopOutPlacement.Usable(saved)) return false;
        (w.WindowStartupLocation, w.Left, w.Top) = (WindowStartupLocation.Manual, saved.Left, saved.Top);
        var area = SocialWindow.WorkArea(w);
        var room = new Spot(area.Left, area.Top, area.Width, area.Height);
        var up = Application.Current.Windows.OfType<PopOutWindow>().Where(o => o != w && o.IsVisible)
                            .Select(o => o.WindowState == WindowState.Normal ? (o.Left, o.Top) : (o.RestoreBounds.Left, o.RestoreBounds.Top));
        var at = PopOutPlacement.Open(saved, up, room, w.MinWidth, w.MinHeight);
        (w.Left, w.Top, w.Width, w.Height) = (at.Left, at.Top, at.Width, at.Height);
        return true;
    }

    public void ClosePopOut(PoppedConversation conversation) => PopOutFor(conversation)?.Close();

    internal static double LeftBeside(double friendsLeft, double friendsWidth, double width, double screenLeft, double screenRight)
    {
        double right = friendsLeft + friendsWidth + Gap;
        if (right + width <= screenRight) return right;
        double left = friendsLeft - width - Gap;
        if (left >= screenLeft) return left;
        double farRight = Math.Max(screenLeft, screenRight - width);
        double Covered(double at) => Math.Max(0, Math.Min(at + width, friendsLeft + friendsWidth) - Math.Max(at, friendsLeft));
        return Covered(screenLeft) < Covered(farRight) ? screenLeft : farRight;
    }

    public void ShowGroups(SocialViewModel social)
    {
        if (Find<GroupsWindow>() is { } open)
        {
            BringBack(open);
            return;
        }
        var w = new GroupsWindow { DataContext = social };
        WindowPlaces.Restore(settings, w, "groups");
        WindowPlaces.Track(settings, w, "groups");
        w.Show();
    }

    public void ShowChatRooms(SocialViewModel social, Func<string, string, bool> savePassword, Action dropPassword,
                              Func<int, ImageSource?> emote, Action closed)
    {
        if (Find<ChatRoomsWindow>() is { } open)
        {
            BringBack(open);
            return;
        }
        var w = new ChatRoomsWindow(social, dialogs, savePassword, dropPassword, emote);
        w.Closed += (_, _) => closed();
        WindowPlaces.Restore(settings, w, "chat-rooms");
        WindowPlaces.Track(settings, w, "chat-rooms");
        w.Show();
    }

    public void ShowGroupChat(GroupChatViewModel groupChat)
    {
        if (Find<GroupChatWindow>() is not { } w)
        {
            w = new GroupChatWindow(groupChat);
            WindowPlaces.Restore(settings, w, "group-chat");
            WindowPlaces.Track(settings, w, "group-chat");
        }
        BringBack(w);
    }

    public void ShowProfile(SocialViewModel social, FriendItem friend)
    {
        if (Find<ProfileWindow>() is { } open)
        {
            open.SetFriend(friend);
            social.Profile.Request(friend);
            open.Activate();
            return;
        }
        var w = new ProfileWindow(social, dialogs, friend) { Owner = WindowOwner.Find() };
        WindowPlaces.Restore(settings, w, "profile");
        WindowPlaces.Track(settings, w, "profile");
        w.Show();
    }

    public void EditProfile(SocialViewModel social)
    {
        if (Find<EditProfileWindow>() is { } open)
        {
            BringBack(open);
            return;
        }
        var w = new EditProfileWindow(social, dialogs);
        WindowPlaces.Track(settings, w, "edit-profile");
        bool saved = WindowPlaces.Restore(settings, w, "edit-profile");
        if (!saved && WindowOwner.Active() is { IsVisible: true, WindowState: WindowState.Normal } near)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = near.Left + (near.ActualWidth - w.Width) / 2;
            w.Top = Math.Max(SystemParameters.VirtualScreenTop, near.Top + (near.ActualHeight - w.Height) / 2);
        }
        else if (!saved) w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Show();
    }

    public bool CloseIfShown(SecondWindow which)
    {
        Window? w = which switch
        {
            SecondWindow.Friends => FriendsWindow(),
            SecondWindow.Groups => Find<GroupsWindow>(),
            SecondWindow.ChatRooms => Find<ChatRoomsWindow>(),
            _ => Find<EditProfileWindow>(),
        };
        if (w is not { IsVisible: true } || w.WindowState == WindowState.Minimized) return false;
        if (w is EditProfileWindow { Editor.HasUnsavedWork: true }) BringBack(w);
        w.Close();
        return true;
    }

    public void AddFriend(SocialViewModel social, string? handleName = null, string? handleKey = null) =>
        new AddFriendDialog(social, handleName, handleKey).ShowDialogOver(WindowOwner.Find());

    public bool ConfirmDropProfileEditor(string action, bool leaveSaving = false)
    {
        if (Find<EditProfileWindow>() is not { } ed) return true;
        if (leaveSaving && ed.Editor.Saving) return true;
        if (ed.Editor.HasUnsavedWork)
        {
            BringBack(ed);
            if (!ed.Editor.ConfirmDrop(action)) return false;
        }
        ed.CloseQuietly();
        return true;
    }

    public void CloseProfileEditor() => Find<EditProfileWindow>()?.CloseQuietly();

    public void CloseSessionWindows()
    {
        CloseProfileEditor();
        Find<ChatRoomsWindow>()?.CloseQuietly();
        Find<GroupChatWindow>()?.CloseQuietly();
        Find<GroupsWindow>()?.Close();
        Find<ProfileWindow>()?.Close();
        FriendsWindow()?.Close();
        foreach (var w in Application.Current.Windows.OfType<PopOutWindow>().ToList()) w.Close();
    }

    internal static UpdateWindow ShowUpdate()
    {
        var w = new UpdateWindow();
        if (WindowOwner.Find() is { } owner) w.Owner = owner;
        w.Show();
        return w;
    }

    public void ShowInstall(InstallViewModel vm)
    {
        new InstallWindow { DataContext = vm }.ShowDialogOver(WindowOwner.Find());
    }

    public void ShowSettings(SettingsViewModel vm, SettingsTab tab)
    {
        var w = new SettingsWindow { DataContext = vm };
        w.SelectTab(tab);
        WindowPlaces.Track(settings, w, "settings");
        if (!WindowPlaces.Restore(settings, w, "settings")) w.ShowDialogOver(WindowOwner.Find());
        else
        {
            if (WindowOwner.Find() is { IsVisible: true } owner) w.Owner = owner;
            w.ShowDialog();
        }
    }

    public void ShowRepair(RepairViewModel vm) => new RepairWindow(vm).ShowDialogOver(WindowOwner.Find());

    public void ShowAccounts(AccountsViewModel accounts) => new AccountsWindow { DataContext = accounts }.ShowDialogOver(WindowOwner.Find());

    public void ShowLeanStart(MainViewModel main)
    {
        if (Find<LeanStartWindow>() is { } up)
        {
            BringBack(up);
            return;
        }
        Application.Current.MainWindow?.Hide();
        new LeanStartWindow { DataContext = main }.Show();
    }

    public void CloseLeanStart() => Find<LeanStartWindow>()?.CloseNow();

    public int? PickProfilePicture(ProfilePictureCatalog.Catalog catalog, AvatarService avatars, int current)
    {
        var w = new ProfilePicturePickerWindow(catalog, avatars, current);
        return w.ShowDialogOver(WindowOwner.Find()) == true ? w.Result : null;
    }
}
