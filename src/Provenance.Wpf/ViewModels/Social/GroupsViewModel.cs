using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Hook;
using Provenance.Common.Social;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Social;

public sealed partial class GroupsViewModel : ObservableObject
{
    private readonly SocialViewModel _social;

    public GroupsViewModel(SocialViewModel social) => _social = social;

    public sealed record GroupMemberItem(int Index, string Name, string Key, ImageSource? Avatar, GroupRole Role,
                                         string RoleText, string StatusLabel, bool Online, bool Me, string Comment)
    {
        public PolStatus Status { get; init; }
        public bool ShowsStatus => Online && !Me && Role != GroupRole.Invited;
    }

    public sealed record GroupItem(string Id, string Name, GroupRole MyRole, string RoleText, int OnlineCount,
                                   IReadOnlyList<GroupMemberItem> Members)
    {
        public bool IsMaster => MyRole == GroupRole.Master;
        public bool CanInvite => MyRole is GroupRole.SubMaster or GroupRole.Master;
        public bool IsInvited => MyRole == GroupRole.Invited;
        // the viewer's picker only offers groups I'm a member, sub-master or master of
        public bool CanChat => MyRole is GroupRole.Member or GroupRole.SubMaster or GroupRole.Master;
        public string Summary => $"{Members.Count} {(Members.Count == 1 ? "member" : "members")}, {OnlineCount} online";
        public int Unread { get; init; }
        public bool HasUnread => Unread > 0;
        public bool Expanded { get; init; }
        public string MyComment => Members.FirstOrDefault(m => m.Me)?.Comment ?? "";
        public bool HasMyComment => MyComment.Length > 0;
        public string OpenText => $"Open {Name}'s chat";
    }

    public ObservableCollection<GroupItem> Items { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NoGroups))] private bool _hasGroups;
    public bool NoGroups => !HasGroups;

    // group invitations waiting for an answer, the same rows the friend list shows
    public ObservableCollection<FriendItem> Invites { get; } = new();
    [ObservableProperty] private bool _hasInvites;

    private sealed class PerLogin
    {
        public bool Dirty = true;
        public readonly HookJobWatch Group = new();
        public string Done = "";
        public string? Leaving;
        public bool BySend;
        public FriendItem? InviteFor;
        public readonly HashSet<string> Expanded = [];
    }

    private PerLogin _login = new();
    internal string? LeavingGroupId => _login.Leaving;

    internal bool Writing => _login.Group.Running;

    internal void MarkDirty() => _login.Dirty = true;

    internal void Reset()
    {
        _login = new();
        Items.Clear();
        HasGroups = false;
        Invites.Clear();
        HasInvites = false;
    }

    internal void SyncInvites(IEnumerable<FriendItem> friends)
    {
        Invites.Patch(friends.Where(f => f.IsGroupInvite).ToList(), (a, b) => a.RequestMid == b.RequestMid, (a, b) => a == b);
        HasInvites = Invites.Count > 0;
    }

    [RelayCommand]
    private void ToggleMembers(GroupItem? g)
    {
        int i = g is null ? -1 : Items.ToList().FindIndex(x => x.Id == g.Id);
        if (i < 0) return;
        bool open = _login.Expanded.Add(g!.Id);
        if (!open) _login.Expanded.Remove(g.Id);
        Items[i] = Items[i] with { Expanded = open };
    }

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

    internal void Rebuild(PolSocialSnapshot snap)
    {
        var groups = snap.GroupList.Select(g =>
        {
            var members = g.Members
                .OrderByDescending(m => m.Role).ThenByDescending(m => m.Online)
                .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(m => new GroupMemberItem(m.Index, m.Name, m.Key, _social.Avatars.ProfilePicture(m.ProfilePictureId),
                                                 m.Role, RoleText(m.Role), StatusText(m), m.Online, m.Me,
                                                 m.Me ? m.Comment : _social.Viewer.Filtered(m.Comment))   // mine fills its edit box
                    { Status = m.Status.ToPolStatus() })
                .ToList();
            int online = g.Members.Count(m => m.Online && !m.Me);
            return new GroupItem(g.Id, g.Name, g.MyRole, RoleText(g.MyRole), online, members) { Unread = _social.GroupChat.UnreadFor(g.Id), Expanded = _login.Expanded.Contains(g.Id) };
        }).ToList();
        if (!_login.Dirty && groups.Count == Items.Count && groups.Zip(Items).All(p => SameGroup(p.First, p.Second))) return;
        _login.Dirty = false;
        // a group that didn't change keeps its row (and an open menu on it)
        Items.Patch(groups, (a, b) => a.Id == b.Id, SameGroup);
        HasGroups = Items.Count > 0;
    }

    // the members are a list, so the record's own == would only ever say they differ
    private static bool SameGroup(GroupItem a, GroupItem b) =>
        a.Id == b.Id && a.Name == b.Name && a.MyRole == b.MyRole && a.OnlineCount == b.OnlineCount && a.Unread == b.Unread && a.Expanded == b.Expanded &&
        a.Members.SequenceEqual(b.Members);

    // bySend: an invitation or its answer, which report back as sends. leaving: the group this takes me out of
    private void BeginGroup(Func<int, string, bool> write, string doneText, bool bySend = false, string? leaving = null,
                            FriendItem? invite = null)
    {
        if (!_social.BeginEdit(out var pid, out var token, followSend: true)) return;
        _login.Group.Begin(token, HookLimits.Group, _social.Now);
        _login.Done = doneText;
        _login.BySend = bySend;
        _login.Leaving = leaving;
        _login.InviteFor = invite;   // only now, a refused click must never mark it answered
        if (write(pid, token)) return;
        _login.Group.Forget();
        _login.InviteFor = null;
        _social.WriteOrFail(false);
    }

    public bool CanCreate => Items.Count(x => !x.IsInvited) < PolLimits.MaxGroups;
    public string LimitText => HookFailures.Groups.TooManyGroups;

    // each one checks the viewer's rule as well as the menus do, so no caller can send what the viewer wouldn't

    public void Create(string name)
    {
        if (!CanCreate)
        {
            _social.EditNote.Fail(LimitText);
            return;
        }
        BeginGroup((pid, token) => SocialRequests.CreateGroup(pid, token, name.Trim()), $"Group \"{name.Trim()}\" created.");
    }

    public void Disband(GroupItem g)
    {
        if (g.IsMaster)
            BeginGroup((pid, token) => SocialRequests.DisbandGroup(pid, token, g.Id), $"Group \"{g.Name}\" disbanded.", leaving: g.Id);
    }

    // a member or sub-master, an invitation gets answered instead
    public void Leave(GroupItem g)
    {
        if (g.MyRole is not (< GroupRole.Member or GroupRole.Master))
            BeginGroup((pid, token) => SocialRequests.LeaveGroup(pid, token, g.Id),
                       _social.Viewer.Text(0x3eab, "You have left the group \"%s.\"").Replace("%s", g.Name), leaving: g.Id);
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

    public void SetComment(GroupItem g, string text)
    {
        if (g.MyRole >= GroupRole.Member)
            BeginGroup((pid, token) => SocialRequests.SetGroupComment(pid, token, g.Id, text.Trim()), "Your group comment was saved.");
    }

    public void Invite(GroupItem g, FriendItem f)
    {
        if (!f.IsFriend || f.Pending || f.IsMe || !g.CanInvite) return;
        if (f.Key.Length > 0 && g.Members.Any(m => m.Key == f.Key))
        {
            _social.EditNote.Fail($"{f.Name} is already in this group, or already invited.");
            return;
        }
        if (g.Members.Count >= PolLimits.MaxGroupMembers)
        {
            _social.EditNote.Fail(HookFailures.Groups.Full);
            return;
        }
        var text = _social.Viewer.Text(0x46d5, "Would you like to join the group \"%s\"?").Replace("%s", g.Name);
        var subject = _social.Viewer.Text(0x46d4, "Would you like to join a friend group?");
        BeginGroup((pid, token) => SocialRequests.InviteToGroup(pid, token, g.Id, f.Row, subject, text, f.Key),
                   $"Invitation to \"{g.Name}\" sent to {f.Name}.", bySend: true);
    }

    public void AnswerInvite(FriendItem invite, bool accept)
    {
        if (!invite.IsGroupInvite || !_social.CanEdit) return;   // refused before the viewer's string table gets read
        var subject = accept ? _social.Viewer.Text(0x46d6, "Group registration accepted")
                             : _social.Viewer.Text(0x46d1, "Friend registration declined");
        BeginGroup((pid, token) => SocialRequests.AnswerGroupInvite(pid, token, accept, invite.RequestMid, subject),
                   accept ? "You accepted the group invitation." : "You declined the group invitation.",
                   bySend: true, invite: invite);
    }

    internal const string ChatClosedNote = "The group chat was closed, but you're still in the group.";

    internal void Apply(PolSocialSnapshot snap)
    {
        if (!_login.Group.Running) return;
        bool mine = snap.GroupJob.Token == _login.Group.Token;
        string state;
        if (!_login.BySend) state = mine ? snap.GroupJob.State : "";
        else if (snap.SendStateFor(_login.Group.Token) is { } sent) state = sent;
        // a send refused before it went out reports in the group slot
        else if (mine && snap.GroupJob.State.StartsWith("failed", StringComparison.Ordinal)) state = snap.GroupJob.State;
        else state = "";
        bool done = state is "sent" or "saved";
        if (_login.Group.Settle(done ? "ok" : state, "ok", _social.Now) is not { } st) return;
        var invite = _login.InviteFor is { IsGroupInvite: true } inv ? inv : null;
        var left = _login.Leaving;
        _login.Leaving = null;
        _login.InviteFor = null;
        _social.EditBusy = false;
        // a timed-out answer leaves the list for this session too, so it can't be answered twice
        if (invite != null && st is "ok" or "timeout") _social.Answered(invite.RequestMid, ack: st == "ok");
        if (st == "ok")
        {
            // with its chat open, the chat says it (and only once), and the lines kept for it go either way
            if (left != null && _social.GroupChat.GroupLeft(left, _login.Done)) return;
            _social.EditNote.Show(_login.Done, TimeSpan.FromSeconds(12));
            return;
        }
        _social.EditNote.Fail(st switch
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
        });
        // the hook leaves the group's chat before the group, so a refused leave can still have closed it
        if (left != null && _social.GroupChat.LeaveFailed(left, snap)) _social.EditNote.Fail(_social.EditNote.Text + " " + ChatClosedNote);
    }

    public void ShowLimit() => _social.Dialogs.Info("Friend groups", LimitText);

    public string? AskName() => _social.Dialogs.Ask("Create a group",
        "Give the new friend group a name (up to 20 English letters, or 10 Japanese characters).", maxLength: PolLimits.GroupName);

    public GroupItem? GroupOf(GroupMemberItem m) => Items.FirstOrDefault(g => g.Members.Any(x => ReferenceEquals(x, m)));

    public IReadOnlyList<MenuAction>? RowMenu(object? row) => row switch
    {
        GroupItem g => GroupMenu(g),
        GroupMemberItem { Me: false } m when GroupOf(m) is { } mg => MemberMenu(mg, m),
        _ => null,
    };

    public IReadOnlyList<MenuAction> GroupMenu(GroupItem g)
    {
        // invitations get answered from their own row in the friend list, like the viewer, so two can't get mixed up
        if (g.IsInvited)
            return [new("Answer it under Group invitations in your friend list", true, _social.ShowFriendsWindow)];
        bool can = _social.CanEdit;
        var menu = new List<MenuAction>();
        if (g.CanChat)
        {
            menu.Add(new("Group Chat", _social.CanGroupChat, () => _social.OpenGroupChatCommand.Execute(g)));
            menu.Add(MenuAction.Separator);
        }
        menu.Add(new("Edit my group comment...", can, () =>
        {
            var mine = g.Members.FirstOrDefault(m => m.Me)?.Comment ?? "";
            var text = _social.Dialogs.Ask("Group comment", $"Your comment in \"{g.Name}\" (up to 49 characters).", mine,
                                    maxLength: PolLimits.Comment, allowEmpty: true);
            if (text != null && text != mine) SetComment(g, text);
        }));
        if (g.CanInvite)
        {
            var invite = _social.Friends.Where(f => f.IsFriend && !f.Pending && !g.Members.Any(m => m.Key.Length > 0 && m.Key == f.Key))
                                .Select(f => new MenuAction(f.Name, can, () => Invite(g, f))).ToList();
            menu.Add(new("Invite a friend", can && invite.Count > 0) { Items = invite });
        }
        menu.Add(MenuAction.Separator);
        menu.Add(new("Leave group...", can && !g.IsMaster, () =>
        {
            if (_social.ConfirmDestructive("Leave group", $"Leave the group \"{g.Name}\"?", "LEAVE")) Leave(g);
        })
        {
            Tip = g.IsMaster ? "The group master can't leave. Make another member the group master first, or disband the group." : null,
        });
        menu.Add(new("Disband group...", can && g.IsMaster, () =>
        {
            if (_social.ConfirmDestructive("Disband group", $"Disband the group \"{g.Name}\"? The group will be gone for everyone in it.", "DISBAND"))
                Disband(g);
        }));
        return menu;
    }

    public IReadOnlyList<MenuAction> MemberMenu(GroupItem g, GroupMemberItem m)
    {
        bool Can(GroupRole role) => _social.CanEdit && !g.IsInvited && CanSetRole(g, m, role);
        var friend = m.Key.Length > 0 ? _social.Friends.FirstOrDefault(f => f.IsFriend && f.Key == m.Key) : null;
        return
        [
            new("Send message", friend is { CanMessage: true }, () =>
            {
                _social.SelectedFriend = friend;
                _social.ShowMessagesWindow();
            }),
            new("View profile", friend is { IsFriend: true, Pending: false }, () => _social.ShowProfile(friend!)),
            MenuAction.Separator,
            new("Make group master...", Can(GroupRole.Master), () =>
            {
                if (_social.ConfirmDestructive("Make group master", $"Make {m.Name} the master of \"{g.Name}\"? You'll become an ordinary member.", "MAKE MASTER"))
                    SetMemberRole(g, m, GroupRole.Master);
            }),
            m.Role == GroupRole.SubMaster
                ? new("Relieve sub-master", Can(GroupRole.Member), () => SetMemberRole(g, m, GroupRole.Member))
                : new("Make sub-master", Can(GroupRole.SubMaster), () => SetMemberRole(g, m, GroupRole.SubMaster)),
            new("Remove from group...", Can(GroupRole.Remove), () =>
            {
                if (_social.ConfirmDestructive("Remove from group", $"Remove {m.Name} from \"{g.Name}\"?", "REMOVE"))
                    SetMemberRole(g, m, GroupRole.Remove);
            }),
        ];
    }
}
