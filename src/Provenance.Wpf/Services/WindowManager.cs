using System.Windows;
using System.Windows.Media;
using Provenance.Common.PolFormat;
using Provenance.Common.Settings;
using Provenance.Wpf.ViewModels.Install;
using Provenance.Wpf.ViewModels.Settings;
using Provenance.Wpf.ViewModels.Social;
using Provenance.Wpf.ViewModels.Chat;
using Provenance.Wpf.Views.Chat;
using Provenance.Wpf.Views.Install;
using Provenance.Wpf.Views.Settings;
using Provenance.Wpf.Views.Social;

namespace Provenance.Wpf.Services;

public interface IWindowManager
{
    bool ProfileEditorOpen { get; }
    // "Friends", or "Friends and messages" while they're merged
    string FriendsTitle { get; }

    void ShowFriends(SocialViewModel social);
    void ShowMessages(SocialViewModel social);
    // only with the windows split. Without takeFocus it only opens one that isn't up yet
    void ShowMessagesAlongside(SocialViewModel social, bool takeFocus);
    void Split(SocialViewModel social);
    void Merge(SocialViewModel social);
    // closed runs when a window made here goes
    void ShowChatRooms(SocialViewModel social, Func<string, string, bool> savePassword, Action dropPassword,
                       Func<int, ImageSource?> emote, Action closed);
    void ShowGroupChat(GroupChatViewModel groupChat);
    void ShowProfile(SocialViewModel social, FriendItem friend);
    void EditProfile(SocialViewModel social);
    void AddFriend(SocialViewModel social, string? handleName = null, string? handleKey = null);
    bool ConfirmDropProfileEditor(string action);
    void CloseProfileEditor();
    // the session's going, so these go with it and say nothing
    void CloseSessionWindows();
    // these three hold the caller till the window closes
    void ShowInstall(InstallViewModel vm);
    void ShowSettings(SettingsViewModel vm, SettingsTab tab);
    void ShowRepair(RepairViewModel vm);
    int? PickProfilePicture(ProfilePictureCatalog.Catalog catalog, AvatarService avatars, int current);
}

public sealed class WindowManager(ISettingsService settings, IDialogs dialogs) : IWindowManager
{
    private const double FriendsWidth = 340, MessagesMinWidth = 420, BothMinWidth = 720, Gap = 8;

    private static T? Find<T>() where T : Window => Application.Current?.Windows.OfType<T>().FirstOrDefault();

    // Activate on its own doesn't bring back a minimised or hidden window
    internal static void BringBack(Window w, bool activate = true)
    {
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        if (activate) w.Activate();
    }

    private static List<SocialWindow> SocialWindows() => Application.Current.Windows.OfType<SocialWindow>().ToList();

    private SocialPart Friends => settings.Current.SocialSplit ? SocialPart.Friends : SocialPart.Both;

    private SocialPart Messages => settings.Current.SocialSplit ? SocialPart.Messages : SocialPart.Both;

    public bool ProfileEditorOpen => Find<EditProfileWindow>() is not null;

    public string FriendsTitle => Friends.Title();

    public void ShowFriends(SocialViewModel social) => ShowSocial(social, Friends, activate: true);

    public void ShowMessages(SocialViewModel social) => ShowSocial(social, Messages, activate: true);

    public void ShowMessagesAlongside(SocialViewModel social, bool takeFocus)
    {
        if (!settings.Current.SocialSplit) return;
        if (takeFocus || !SocialWindows().Any(x => x.Part == SocialPart.Messages))
            ShowSocial(social, SocialPart.Messages, takeFocus);
    }

    // unowned, so they stay up while the main window's minimised or in the tray
    private static void ShowSocial(SocialViewModel social, SocialPart part, bool activate, Rect? place = null)
    {
        var w = SocialWindows().FirstOrDefault(x => x.Part == part);
        if (w != null)
        {
            BringBack(w, activate);
            return;
        }
        foreach (var old in SocialWindows().Where(x => (x.Part == SocialPart.Both) != (part == SocialPart.Both))) old.Close();
        w = new SocialWindow(part) { DataContext = social, ShowActivated = activate };
        if (place is null && part == SocialPart.Messages
            && SocialWindows().FirstOrDefault(x => x.Part == SocialPart.Friends) is { IsVisible: true } fw)
        {
            double left = fw.Left + fw.ActualWidth + Gap;
            if (left + w.Width > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth) left = fw.Left - w.Width - Gap;
            place = new Rect(left, fw.Top, w.Width, fw.ActualHeight);
        }
        if (place is { } r)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            (w.Left, w.Top, w.Width, w.Height) = (r.Left, r.Top, r.Width, r.Height);
        }
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Show();
    }

    public void Split(SocialViewModel social) => SetSplit(social, true);

    public void Merge(SocialViewModel social) => SetSplit(social, false);

    private void SetSplit(SocialViewModel social, bool split)
    {
        if (settings.Current.SocialSplit == split) return;
        var open = SocialWindows().Where(w => w.IsVisible).ToList();
        Rect? area = open.Count == 0 ? null
            : open.Select(w => w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight) : w.RestoreBounds)
                  .Aggregate(Rect.Union);
        settings.Current.SocialSplit = split;
        settings.Save(settings.Current);
        foreach (var w in SocialWindows()) w.Close();
        if (split)
        {
            bool conversation = social.SelectedFriend != null;
            ShowSocial(social, SocialPart.Friends, activate: !conversation,
                       area is { } a ? new Rect(a.Left, a.Top, FriendsWidth, a.Height) : null);
            if (conversation)
                ShowSocial(social, SocialPart.Messages, activate: true,
                           area is { } b ? new Rect(b.Left + FriendsWidth + Gap, b.Top, Math.Max(MessagesMinWidth, b.Width - FriendsWidth - Gap), b.Height) : null);
        }
        else
            ShowSocial(social, SocialPart.Both, activate: true, area is { } a ? new Rect(a.Left, a.Top, Math.Max(BothMinWidth, a.Width), a.Height) : null);
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
        w.Show();
    }

    public void ShowGroupChat(GroupChatViewModel groupChat) => BringBack(Find<GroupChatWindow>() ?? new GroupChatWindow(groupChat));

    // the view model only holds one profile, so an open window gets reused
    public void ShowProfile(SocialViewModel social, FriendItem friend)
    {
        if (Find<ProfileWindow>() is { } open)
        {
            open.SetFriend(friend);
            social.RequestProfile(friend);
            open.Activate();
            return;
        }
        new ProfileWindow(social, friend) { Owner = WindowOwner.Find() }.Show();
    }

    // never parented to the friends window, so closing that can't take unsaved changes down with it
    public void EditProfile(SocialViewModel social)
    {
        if (Find<EditProfileWindow>() is { } open)
        {
            BringBack(open);
            return;
        }
        var w = new EditProfileWindow(social, dialogs);
        if (WindowOwner.Active() is { IsVisible: true, WindowState: WindowState.Normal } near)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = near.Left + (near.ActualWidth - w.Width) / 2;
            w.Top = Math.Max(SystemParameters.VirtualScreenTop, near.Top + (near.ActualHeight - w.Height) / 2);
        }
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Show();
    }

    public void AddFriend(SocialViewModel social, string? handleName = null, string? handleKey = null) =>
        new AddFriendDialog(social, handleName, handleKey) { Owner = WindowOwner.Find() }.ShowDialog();

    public bool ConfirmDropProfileEditor(string action)
    {
        if (Find<EditProfileWindow>() is not { } ed) return true;
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
        foreach (var w in SocialWindows()) w.Close();
    }

    // before the app's services exist, so it's a static
    internal static UpdateWindow ShowUpdate()
    {
        var w = new UpdateWindow();
        if (WindowOwner.Find() is { } owner) w.Owner = owner;
        w.Show();
        return w;
    }

    public void ShowInstall(InstallViewModel vm)
    {
        var w = new InstallWindow { DataContext = vm };
        if (WindowOwner.Find() is { } owner) w.Owner = owner;
        w.ShowDialog();
    }

    public void ShowSettings(SettingsViewModel vm, SettingsTab tab)
    {
        var w = new SettingsWindow { DataContext = vm };
        w.Tabs.SelectedIndex = (int)tab;
        if (WindowOwner.Find() is { } owner) w.Owner = owner;
        w.ShowDialog();
    }

    public void ShowRepair(RepairViewModel vm) => new RepairWindow(vm).ShowDialogOver(WindowOwner.Find());

    public int? PickProfilePicture(ProfilePictureCatalog.Catalog catalog, AvatarService avatars, int current)
    {
        var w = new ProfilePicturePickerWindow(catalog, avatars, current);
        if (WindowOwner.Find() is { } owner) w.Owner = owner;
        return w.ShowDialog() == true ? w.Result : null;
    }
}
