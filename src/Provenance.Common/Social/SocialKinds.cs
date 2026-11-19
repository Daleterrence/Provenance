namespace Provenance.Common.Social;

public enum GroupRole { None = 0, Remove = 1, Invited = 2, Member = 3, SubMaster = 4, Master = 5 }

public enum MemberStatus { Offline = 0, Online = 1, Away = 2, Busy = 3, AppearOffline = 4 }

public static class MemberStatusExtensions
{
    public static PolStatus ToPolStatus(this MemberStatus status) => status switch
    {
        MemberStatus.Away => PolStatus.Away,
        MemberStatus.Busy => PolStatus.Busy,
        _ => PolStatus.Online,
    };
}

public enum ProfileFieldState { Shown = 0, Private = 1, NotSet = 2, UnderTen = 3 }

// who can see a profile. Keep is 0, which on a save leaves the profile's own level alone
public enum PrivacyLevel { Keep = 0, Private = 1, FriendsOnly = 2, Everyone = 3 }

public enum ContentIdType { Ffxi = 1 }
