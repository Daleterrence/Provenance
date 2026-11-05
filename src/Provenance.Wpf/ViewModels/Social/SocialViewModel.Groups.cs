using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels.Social;

public partial class SocialViewModel
{
    public sealed record GroupMemberItem(int Index, string Name, string Key, ImageSource? Avatar, int Class,
                                         string RoleText, string StatusLabel, bool Online, bool Me, string Comment);

    // classes: 5 master, 4 sub-master, 3 member, 2 invited
    public sealed record GroupItem(string Id, string Name, int MyClass, string RoleText, int OnlineCount,
                                   IReadOnlyList<GroupMemberItem> Members)
    {
        public bool IsMaster => MyClass == 5;
        public bool CanInvite => MyClass is 4 or 5;
        public bool IsInvited => MyClass == 2;
        // the viewer's picker only offers groups I'm a member, sub-master or master of
        public bool CanChat => MyClass is 3 or 4 or 5;
        public string Summary => $"{Members.Count} {(Members.Count == 1 ? "member" : "members")}, {OnlineCount} online";
        // group chat lines that came in with its window hidden or behind
        public int Unread { get; init; }
        public bool HasUnread => Unread > 0;
    }

    public ObservableCollection<GroupItem> Groups { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NoGroups))] private bool _hasGroups;
    public bool NoGroups => !HasGroups;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowFriends), nameof(FriendsTabVisible), nameof(GroupsTabVisible))]
    private bool _showGroups;
    public bool ShowFriends => !ShowGroups;
    public bool FriendsTabVisible => FriendsListVisible && !ShowGroups;
    public bool GroupsTabVisible => FriendsListVisible && ShowGroups;

    [RelayCommand] private void ShowFriendsTab() => ShowGroups = false;
    [RelayCommand] private void ShowGroupsTab() => ShowGroups = true;

    private string _groupsSignature = "";
    internal string? _groupToken;
    private string _groupDone = "";
    private string? _groupLeaving;
    internal string? LeavingGroupId => _groupLeaving;
    private DateTime _groupAt;
    private bool _groupBySend;

    private static string RoleText(int cls) => cls switch
    {
        5 => "Group master",
        4 => "Group sub-master",
        2 => "Invited",
        _ => "Member",
    };

    private static string MemberStatus(PolGroupMember m) => m switch
    {
        { Me: true } => "You",
        { Class: 2 } => "Invited",
        { Online: false } => "Offline",
        { Status: 2 } => "Away",
        { Status: 3 } => "Busy",
        _ => "Online",
    };

    private void RebuildGroups(PolSocialSnapshot snap)
    {
        static string MemberSig(PolGroupMember m) =>
            $"{m.Index}:{m.Name}:{m.Class}:{m.Status}:{m.ProfilePictureId}:{m.Comment}";
        var sig = snap.GroupList.Count + ":" + string.Join("|", snap.GroupList.Select(g => g.Id + "/" + g.Name + "/" + g.MyClass + "/" + GroupChat.UnreadFor(g.Id) + "/" +
                                                              string.Join(",", g.Members.Select(MemberSig))));
        if (sig == _groupsSignature) return;
        _groupsSignature = sig;
        Groups.Clear();
        foreach (var g in snap.GroupList)
        {
            var members = g.Members
                .OrderByDescending(m => m.Class).ThenByDescending(m => m.Online)
                .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(m => new GroupMemberItem(m.Index, m.Name, m.Key, _avatars.ProfilePicture(m.ProfilePictureId),
                                                 m.Class, RoleText(m.Class), MemberStatus(m), m.Online, m.Me,
                                                 m.Me ? m.Comment : Filtered(m.Comment)))   // mine fills its edit box
                .ToList();
            int online = g.Members.Count(m => m.Online && !m.Me);
            Groups.Add(new GroupItem(g.Id, g.Name, g.MyClass, RoleText(g.MyClass), online, members)
                       { Unread = GroupChat.UnreadFor(g.Id) });
        }
        HasGroups = Groups.Count > 0;
    }

    private void BeginGroup(GroupOp op, string groupId, string doneText, string a = "", string b = "", string c = "",
                            string key = "", FriendItem? invite = null)
    {
        if (!BeginEdit(out var pid, out var token)) return;
        _editToken = null;
        _groupToken = token;
        _groupDone = doneText;
        _groupAt = DateTime.UtcNow;
        _groupBySend = op is GroupOp.Invite or GroupOp.AcceptInvite or GroupOp.DeclineInvite;   // these report as sends
        _groupLeaving = op is GroupOp.Leave or GroupOp.Disband ? groupId : null;
        _answerFor = invite;   // only now, a refused click must never mark it answered
        if (SocialRequests.ChangeGroup(pid, op, token, groupId, a, b, c, key)) return;
        _groupToken = null;
        _answerFor = null;
        EditBusy = false;
        EditNote = RequestWriteFailed;
    }

    // each one checks the viewer's rule as well as the menus do, so no caller can send what the viewer wouldn't

    public void CreateGroup(string name)
    {
        if (Groups.Count(x => !x.IsInvited) >= 4)
        {
            EditNote = "You can only be in 4 friend groups.";
            return;
        }
        BeginGroup(GroupOp.Create, "", $"Group \"{name.Trim()}\" created.", name.Trim());
    }

    public void DisbandGroup(GroupItem g)
    {
        if (g.MyClass == 5) BeginGroup(GroupOp.Disband, g.Id, $"Group \"{g.Name}\" disbanded.");
    }

    // a member or sub-master, an invitation gets answered instead
    public void LeaveGroup(GroupItem g)
    {
        if (g.MyClass is not (< 3 or 5))
            BeginGroup(GroupOp.Leave, g.Id, ViewerText(0x3eab, "You have left the group \"%s.\"").Replace("%s", g.Name));
    }

    // masters and sub-masters remove (1) non-masters and relieve (3) sub-masters, only the master promotes to
    // sub-master (4) or master (5), and never on my own row
    public static bool CanSetClass(GroupItem g, GroupMemberItem m, int cls) => !m.Me && cls switch
    {
        1 => g.MyClass is 4 or 5 && m.Class != 5,
        3 => g.MyClass is 4 or 5 && m.Class == 4,
        4 => g.MyClass == 5 && m.Class == 3,
        5 => g.MyClass == 5 && m.Class is 3 or 4,
        _ => false,
    };

    public void SetMemberClass(GroupItem g, GroupMemberItem m, int cls)
    {
        if (CanSetClass(g, m, cls))
            BeginGroup(GroupOp.SetClass, g.Id,
                       cls == 1 ? $"{m.Name} was removed from the group." : "The group was changed.",
                       m.Index.ToString(), cls.ToString(), "", m.Key);
    }

    public void SetGroupComment(GroupItem g, string text)
    {
        if (g.MyClass >= 3) BeginGroup(GroupOp.Comment, g.Id, "Your group comment was saved.", text.Trim());
    }

    public void InviteToGroup(GroupItem g, FriendItem f)
    {
        if (!f.IsFriend || f.Pending || f.IsMe || !g.CanInvite) return;
        if (f.Key.Length > 0 && g.Members.Any(m => m.Key == f.Key))
        {
            EditNote = $"{f.Name} is already in this group, or already invited.";
            return;
        }
        if (g.Members.Count >= 64)
        {
            EditNote = "This group is full (64 members).";
            return;
        }
        var text = ViewerText(0x46d5, "Would you like to join the group \"%s\"?").Replace("%s", g.Name);
        BeginGroup(GroupOp.Invite, g.Id, $"Invitation to \"{g.Name}\" sent to {f.Name}.", f.Row.ToString(),
                   ViewerText(0x46d4, "Would you like to join a friend group?"), text, f.Key);
    }

    // an invitation (kind 0xe) gets a kind 0xf or 0x10 message back
    public void AnswerGroupInvite(FriendItem invite, bool accept)
    {
        if (!invite.IsGroupInvite || !CanEdit) return;   // refused before the viewer's string table gets read
        BeginGroup(accept ? GroupOp.AcceptInvite : GroupOp.DeclineInvite, "",
                   accept ? "You accepted the group invitation." : "You declined the group invitation.",
                   invite.RequestMid.ToString(),
                   accept ? ViewerText(0x46d6, "Group registration accepted")
                          : ViewerText(0x46d1, "Friend registration declined"),
                   invite: invite);
    }

    internal const string ChatClosedNote = "The group chat was closed, but you're still in the group.";

    private void ApplyGroupState(PolSocialSnapshot snap)
    {
        if (_groupToken is null) return;
        bool mine = snap.GroupToken == _groupToken;
        string state;
        if (!_groupBySend) state = mine ? snap.GroupState : "";
        else if (snap.SendStateFor(_groupToken) is { } sent) state = sent;
        // a send refused before it went out reports in the group slot
        else if (mine && snap.GroupState.StartsWith("failed", StringComparison.Ordinal)) state = snap.GroupState;
        else state = "";
        bool done = state is "sent" or "saved";
        if (Settled(done ? "ok" : state, "ok", _groupAt, 130) is not { } st) return;
        var invite = _answerFor is { IsGroupInvite: true } inv ? inv : null;
        var left = _groupLeaving;
        _groupLeaving = null;
        _groupToken = null;
        _answerFor = null;
        EditBusy = false;
        // a timed-out answer leaves the list for this session too, so it can't be answered twice
        if (invite != null && st is "ok" or "timeout") Answered(invite.RequestMid, ack: st == "ok");
        if (st == "ok")
        {
            // with its chat open, the chat says it (and only once), and the lines kept for it go either way
            if (left != null && GroupChat.GroupLeft(left, _groupDone)) return;
            _noteIsSuccess = true;
            EditNote = _groupDone;
            return;
        }
        EditNote = st switch
        {
            "timeout" => "PlayOnline didn't answer.",
            "failed:not master" => "Only the group master can disband the group.",
            "failed:handle changing" => "Not done: your handle was changing. Try again.",
            "failed:not allowed" => "Only the group master (or a sub-master) can do that.",
            "failed:self" => "You can't invite yourself to a group.",
            "failed:not a member" => "You've only been invited to that group. Accept the invitation first.",
            "failed:already in group" => "That person is already in this group, or already invited.",
            "failed:group full" => "This group is full (64 members).",
            "failed:-7206" or "failed:-5315" => "You can only be in 4 friend groups.",
            "failed:master cannot leave" => "The group master can't leave the group. Make someone else master first, or disband it.",
            "failed:bad name" => "Type a group name.",
            "failed:name too long" => "That group name is too long (20 English letters, or 10 Japanese characters).",
            "failed:already a member" => "You're already in this group with another handle.",
            "failed:no such group" or "failed:no such member" or "failed:no such invitation" or "failed:no handle" => "The group changed. Try again.",
            "failed:no such friend" => "Your friend list changed, so the invitation wasn't sent. Try again.",
            "failed:-5316" => "That group name is already in use.",
            "failed:-5428" or "failed:-261" => "That group name can't be used.",
            "failed:stopped" => "PlayOnline didn't take your recent messages, so invitations can't be sent or answered until you log in again.",
            "failed:hourly limit" => "You've sent 600 messages in the last hour. Try again later.",
            var s => $"PlayOnline didn't accept the change ({SocialRequests.FailReason(s)}).",
        };
        // the hook leaves the group's chat before the group, so a refused leave can still have closed it
        if (left != null && GroupChat.LeaveFailed(left, snap)) EditNote += " " + ChatClosedNote;
    }
}
