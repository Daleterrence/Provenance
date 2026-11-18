namespace Provenance.Common.Settings;

public enum NoticeKind { FriendOnline, Message, FriendRequest, GroupInvite, Answer, GroupChat }

public static class NoticeRules
{
    public static bool Shows(this LauncherSettings s, NoticeKind kind) => kind switch
    {
        NoticeKind.FriendOnline => s.NotifyFriendsOnline,
        NoticeKind.Message => s.NotifyMessages,
        NoticeKind.FriendRequest => s.NotifyFriendRequests,
        NoticeKind.GroupInvite => s.NotifyGroupInvites,
        NoticeKind.Answer => s.NotifyAnswers,
        _ => s.GroupChatNotice,
    };

    public static bool Flashes(this LauncherSettings s, NoticeKind kind) => kind switch
    {
        NoticeKind.FriendOnline => false,
        NoticeKind.Message => s.FlashMessages,
        NoticeKind.FriendRequest => s.FlashFriendRequests,
        NoticeKind.GroupInvite => s.FlashGroupInvites,
        NoticeKind.Answer => s.FlashAnswers,
        _ => s.GroupChatFlash,
    };

    public static void SetShows(this LauncherSettings s, NoticeKind kind, bool on)
    {
        switch (kind)
        {
            case NoticeKind.FriendOnline: s.NotifyFriendsOnline = on; break;
            case NoticeKind.Message: s.NotifyMessages = on; break;
            case NoticeKind.FriendRequest: s.NotifyFriendRequests = on; break;
            case NoticeKind.GroupInvite: s.NotifyGroupInvites = on; break;
            case NoticeKind.Answer: s.NotifyAnswers = on; break;
            default: s.GroupChatNotice = on; break;
        }
    }

    public static void SetFlashes(this LauncherSettings s, NoticeKind kind, bool on)
    {
        switch (kind)
        {
            case NoticeKind.FriendOnline: break;
            case NoticeKind.Message: s.FlashMessages = on; break;
            case NoticeKind.FriendRequest: s.FlashFriendRequests = on; break;
            case NoticeKind.GroupInvite: s.FlashGroupInvites = on; break;
            case NoticeKind.Answer: s.FlashAnswers = on; break;
            default: s.GroupChatFlash = on; break;
        }
    }
}
