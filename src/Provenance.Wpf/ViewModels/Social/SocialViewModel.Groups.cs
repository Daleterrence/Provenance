using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Hook;
using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels.Social;

public partial class SocialViewModel
{
    public sealed record GroupMemberItem(int Index, string Name, string Key, ImageSource? Avatar, GroupRole Role,
                                         string RoleText, string StatusLabel, bool Online, bool Me, string Comment);

    public sealed record GroupItem(string Id, string Name, GroupRole MyRole, string RoleText, int OnlineCount,
                                   IReadOnlyList<GroupMemberItem> Members)
    {
        public bool IsMaster => MyRole == GroupRole.Master;
        public bool CanInvite => MyRole is GroupRole.SubMaster or GroupRole.Master;
        public bool IsInvited => MyRole == GroupRole.Invited;
        // the viewer's picker only offers groups I'm a member, sub-master or master of
        public bool CanChat => MyRole is GroupRole.Member or GroupRole.SubMaster or GroupRole.Master;
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

    private static string RoleText(GroupRole role) => role switch
    {
        GroupRole.Master => "Group master",
        GroupRole.SubMaster => "Group sub-master",
        GroupRole.Invited => "Invited",
        _ => "Member",
    };

    private static string StatusText(PolGroupMember m) => m switch
    {
        { Me: true } => "You",
        { Role: GroupRole.Invited } => "Invited",
        { Online: false } => "Offline",
        { Status: MemberStatus.Away } => "Away",
        { Status: MemberStatus.Busy } => "Busy",
        _ => "Online",
    };

    private void RebuildGroups(PolSocialSnapshot snap)
    {
        static string MemberSig(PolGroupMember m) =>
            $"{m.Index}:{m.Name}:{(int)m.Role}:{(int)m.Status}:{m.ProfilePictureId}:{m.Comment}";
        var sig = snap.GroupList.Count + ":" + string.Join("|", snap.GroupList.Select(g => g.Id + "/" + g.Name + "/" + (int)g.MyRole + "/" + GroupChat.UnreadFor(g.Id) + "/" +
                                                              string.Join(",", g.Members.Select(MemberSig))));
        if (sig == _groupsSignature) return;
        _groupsSignature = sig;
        Groups.Clear();
        foreach (var g in snap.GroupList)
        {
            var members = g.Members
                .OrderByDescending(m => m.Role).ThenByDescending(m => m.Online)
                .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(m => new GroupMemberItem(m.Index, m.Name, m.Key, _avatars.ProfilePicture(m.ProfilePictureId),
                                                 m.Role, RoleText(m.Role), StatusText(m), m.Online, m.Me,
                                                 m.Me ? m.Comment : Filtered(m.Comment)))   // mine fills its edit box
                .ToList();
            int online = g.Members.Count(m => m.Online && !m.Me);
            Groups.Add(new GroupItem(g.Id, g.Name, g.MyRole, RoleText(g.MyRole), online, members)
                       { Unread = GroupChat.UnreadFor(g.Id) });
        }
        HasGroups = Groups.Count > 0;
    }

    // bySend: an invitation or its answer, which report back as sends. leaving: the group this takes me out of
    private void BeginGroup(Func<int, string, bool> write, string doneText, bool bySend = false, string? leaving = null,
                            FriendItem? invite = null)
    {
        if (!BeginEdit(out var pid, out var token)) return;
        _editToken = null;
        _groupToken = token;
        _groupDone = doneText;
        _groupAt = DateTime.UtcNow;
        _groupBySend = bySend;
        _groupLeaving = leaving;
        _answerFor = invite;   // only now, a refused click must never mark it answered
        if (write(pid, token)) return;
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
            EditNote = HookFailures.Groups.TooManyGroups;
            return;
        }
        BeginGroup((pid, token) => SocialRequests.CreateGroup(pid, token, name.Trim()), $"Group \"{name.Trim()}\" created.");
    }

    public void DisbandGroup(GroupItem g)
    {
        if (g.IsMaster)
            BeginGroup((pid, token) => SocialRequests.DisbandGroup(pid, token, g.Id), $"Group \"{g.Name}\" disbanded.", leaving: g.Id);
    }

    // a member or sub-master, an invitation gets answered instead
    public void LeaveGroup(GroupItem g)
    {
        if (g.MyRole is not (< GroupRole.Member or GroupRole.Master))
            BeginGroup((pid, token) => SocialRequests.LeaveGroup(pid, token, g.Id),
                       ViewerText(0x3eab, "You have left the group \"%s.\"").Replace("%s", g.Name), leaving: g.Id);
    }

    // masters and sub-masters remove non-masters and relieve sub-masters (back to Member), only the master promotes to
    // sub-master or master, and never on my own row
    public static bool CanSetRole(GroupItem g, GroupMemberItem m, GroupRole role) => !m.Me && role switch
    {
        GroupRole.Remove => g.CanInvite && m.Role != GroupRole.Master,
        GroupRole.Member => g.CanInvite && m.Role == GroupRole.SubMaster,
        GroupRole.SubMaster => g.IsMaster && m.Role == GroupRole.Member,
        GroupRole.Master => g.IsMaster && m.Role is GroupRole.Member or GroupRole.SubMaster,
        _ => false,
    };

    public void SetMemberRole(GroupItem g, GroupMemberItem m, GroupRole role)
    {
        if (CanSetRole(g, m, role))
            BeginGroup((pid, token) => SocialRequests.SetMemberRole(pid, token, g.Id, m.Index, role, m.Key),
                       role == GroupRole.Remove ? $"{m.Name} was removed from the group." : "The group was changed.");
    }

    public void SetGroupComment(GroupItem g, string text)
    {
        if (g.MyRole >= GroupRole.Member)
            BeginGroup((pid, token) => SocialRequests.SetGroupComment(pid, token, g.Id, text.Trim()), "Your group comment was saved.");
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
            EditNote = HookFailures.Groups.Full;
            return;
        }
        var text = ViewerText(0x46d5, "Would you like to join the group \"%s\"?").Replace("%s", g.Name);
        var subject = ViewerText(0x46d4, "Would you like to join a friend group?");
        BeginGroup((pid, token) => SocialRequests.InviteToGroup(pid, token, g.Id, f.Row, subject, text, f.Key),
                   $"Invitation to \"{g.Name}\" sent to {f.Name}.", bySend: true);
    }

    // an invitation (kind 0xe) gets a kind 0xf or 0x10 message back
    public void AnswerGroupInvite(FriendItem invite, bool accept)
    {
        if (!invite.IsGroupInvite || !CanEdit) return;   // refused before the viewer's string table gets read
        var subject = accept ? ViewerText(0x46d6, "Group registration accepted")
                             : ViewerText(0x46d1, "Friend registration declined");
        BeginGroup((pid, token) => SocialRequests.AnswerGroupInvite(pid, token, accept, invite.RequestMid, subject),
                   accept ? "You accepted the group invitation." : "You declined the group invitation.",
                   bySend: true, invite: invite);
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
            "timeout" => HookFailures.Groups.NoAnswer,
            "failed:busy" => HookFailures.Busy,
            "failed:not master" => HookFailures.Groups.DisbandMasterOnly,
            "failed:handle changing" => HookFailures.Groups.HandleChanging,
            "failed:not allowed" => HookFailures.Groups.NotAllowed,
            "failed:self" => HookFailures.Groups.InviteSelf,
            "failed:not a member" => HookFailures.Groups.OnlyInvited,
            "failed:already in group" => HookFailures.Groups.AlreadyIn,
            "failed:group full" => HookFailures.Groups.Full,
            "failed:-7206" or "failed:-5315" => HookFailures.Groups.TooManyGroups,
            "failed:master cannot leave" => HookFailures.Groups.MasterCannotLeave,
            "failed:bad name" => HookFailures.Groups.NeedName,
            "failed:name too long" => HookFailures.Groups.NameTooLong,
            "failed:already a member" => HookFailures.Groups.AlreadyMember,
            "failed:no such group" or "failed:no such member" or "failed:no such invitation" => HookFailures.Groups.Changed,
            "failed:no handle" when invite != null => HookFailures.Groups.HandleGone,
            "failed:no handle" => HookFailures.Groups.Changed,
            "failed:no such friend" => HookFailures.Groups.FriendGone,
            "failed:-5316" => HookFailures.Groups.NameInUse,
            "failed:-5428" or "failed:-261" => HookFailures.Groups.NameRejected,
            "failed:stopped" => HookFailures.Groups.Stopped,
            "failed:hourly limit" => HookFailures.Groups.HourlyLimit,
            var s => HookFailures.ChangeRejected(SocialRequests.FailReason(s)),
        };
        // the hook leaves the group's chat before the group, so a refused leave can still have closed it
        if (left != null && GroupChat.LeaveFailed(left, snap)) EditNote += " " + ChatClosedNote;
    }
}
