namespace Provenance.Wpf.ViewModels.Social;

public enum FriendSection { FriendRequests, GroupInvitations, Online, Offline, RequestsSent, Others }

public static class FriendSections
{
    public static string Title(this FriendSection section) => section switch
    {
        FriendSection.FriendRequests => "FRIEND REQUESTS",
        FriendSection.GroupInvitations => "GROUP INVITATIONS",
        FriendSection.Online => "ONLINE",
        FriendSection.Offline => "OFFLINE",
        FriendSection.RequestsSent => "REQUESTS SENT",
        _ => "OTHERS",
    };
}
