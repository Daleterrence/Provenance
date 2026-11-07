using Provenance.Common.Social;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Social;

public partial class SocialViewModel
{
    public static string ClearConversationQuestion(string name) =>
        $"Delete every message with {name} that Provenance has kept on this PC? This can't be undone.";

    // a line's own menu (Copy is the view's), delete is this PC's copy of it
    public IReadOnlyList<MenuAction> LineMenu(ChatEntry entry) =>
        [new("Delete message", ChatStore.CanDelete(entry), () => DeleteLine(entry))];

    public IReadOnlyList<MenuAction> FriendMenu(FriendItem f, ISocialMenuHost host)
    {
        bool edit = CanEdit && f.IsFriend && f.Key.Length > 0;
        if (f.IsGroupInvite)
            return [new("Accept group invitation", CanEdit, () => AnswerGroupInvite(f, true)),
                    new("Decline group invitation", CanEdit, () => AnswerGroupInvite(f, false))];
        if (f.IsRequest)
            return [new("Accept friend request", CanEdit && !f.IsMe, () => AnswerRequest(f, RequestAnswer.Accept)),
                    new("Decline friend request...", CanEdit, MenuAction.Asking(host.Confirm,
                        new("Decline friend request", $"Decline the friend request from {f.Name}?", "DECLINE"),
                        () => AnswerRequest(f, RequestAnswer.Decline))),
                    MenuAction.Separator,
                    // the viewer's request window only ignores this one handle
                    new("Ignore...", CanEdit && !f.IsMe, MenuAction.Asking(host.Confirm,
                        new("Ignore " + f.Name,
                            $"Add {f.Name} to your ignore list? PlayOnline will decline the request, and this handle's messages won't reach you any more. You can stop ignoring them later from your ignore list.",
                            "IGNORE"),
                        () => AnswerRequest(f, RequestAnswer.Ignore)))];
        if (!f.IsFriend && !f.Pending)
            return [new($"Add {f.Name} as a friend...", CanEdit, () => host.AddFriend(f))];
        var menu = new List<MenuAction>
        {
            new("Send message", f.CanMessage, () =>
            {
                SelectedFriend = f;
                host.OpenConversation(f);
            }),
            new("View profile", f.IsFriend && !f.Pending, () => host.ViewProfile(f)),
        };
        // the viewer's Invite into Group: only groups I'm master or sub-master of, and never myself
        var inviteTo = Groups.Where(g => g.CanInvite && !g.Members.Any(m => m.Key.Length > 0 && m.Key == f.Key)).ToList();
        if (inviteTo.Count > 0 && f.IsFriend && !f.Pending && !f.IsMe)
            menu.Add(new("Invite into group", CanEdit)
            {
                Items = inviteTo.Select(g => new MenuAction(g.Name, CanEdit, () => InviteToGroup(g, f))).ToList(),
            });
        menu.Add(MenuAction.Separator);
        menu.Add(new("Rename...", edit && !f.Pending, () =>
        {
            var name = host.AskRename(f);
            // the same name has nothing to save, the viewer skips it too
            if (name is not null && name.Trim() != f.Name) RenameFriend(f, name);
        }));
        var removeBody = f.Pending
            ? $"Delete your friend request to {f.Name}? This takes it off your own friend list."
            : $"Remove {f.Name} from your PlayOnline friend list? Your saved messages with {f.Name} stay in Provenance.";
        menu.Add(new(f.Pending ? "Delete friend request..." : "Remove from friends...", edit, MenuAction.Asking(host.Confirm,
            new(f.Pending ? "Delete friend request" : "Remove friend", removeBody, f.Pending ? "DELETE" : "REMOVE"),
            () => RemoveFriend(f))));
        menu.Add(new("Ignore...", edit && !f.Pending && !f.IsMe, () =>
        {
            if (host.AskIgnore(f) is { } pick) IgnoreFriend(f, pick == 1);
        }));
        return menu;
    }

    // the friends window's conversation header shows the friend's menu too, so everything's reachable from a conversation
    public IReadOnlyList<MenuAction> ConversationMenu(FriendItem f, ISocialMenuHost host)
    {
        var menu = FriendMenu(f, host).ToList();
        if (menu.Count > 0) menu.Add(MenuAction.Separator);
        menu.Add(new("Clear conversation...", Conversation.Any(ChatStore.CanDelete), () =>
        {
            var shown = Conversation.Select(e => e.Key).ToList();
            if (host.Confirm(new("Clear conversation", ClearConversationQuestion(f.Name), "CLEAR"))) ClearConversation(shown);
        }));
        return menu;
    }

    public GroupItem? GroupOf(GroupMemberItem m) => Groups.FirstOrDefault(g => g.Members.Any(x => ReferenceEquals(x, m)));

    // a group row's own menu, or a member's (never my own row), null when there isn't one
    public IReadOnlyList<MenuAction>? RowMenu(object? row, ISocialMenuHost host) => row switch
    {
        GroupItem g => GroupMenu(g, host),
        GroupMemberItem { Me: false } m when GroupOf(m) is { } mg => MemberMenu(mg, m, host),
        _ => null,
    };

    public IReadOnlyList<MenuAction> GroupMenu(GroupItem g, ISocialMenuHost host)
    {
        // invitations get answered from their own row in the friend list, like the viewer, so two can't get mixed up
        if (g.IsInvited)
            return [new("Answer it under Group invitations in your friend list", true, () => ShowFriendsTabCommand.Execute(null))];
        bool can = CanEdit;
        var menu = new List<MenuAction>();
        if (g.CanChat)
        {
            menu.Add(new("Group Chat", CanGroupChat, () => OpenGroupChatCommand.Execute(g)));
            menu.Add(MenuAction.Separator);
        }
        menu.Add(new("Edit my group comment...", can, () =>
        {
            var mine = g.Members.FirstOrDefault(m => m.Me)?.Comment ?? "";
            var text = host.AskGroupComment(g, mine);
            if (text != null && text != mine) SetGroupComment(g, text);
        }));
        if (g.CanInvite)
        {
            var invite = Friends.Where(f => f.IsFriend && !f.Pending && !g.Members.Any(m => m.Key.Length > 0 && m.Key == f.Key))
                                .Select(f => new MenuAction(f.Name, can, () => InviteToGroup(g, f))).ToList();
            menu.Add(new("Invite a friend", can && invite.Count > 0) { Items = invite });
        }
        menu.Add(MenuAction.Separator);
        menu.Add(new("Leave group...", can && !g.IsMaster, MenuAction.Asking(host.Confirm,
            new("Leave group", $"Leave the group \"{g.Name}\"?", "LEAVE"), () => LeaveGroup(g)))
        {
            Tip = g.IsMaster ? "The group master can't leave. Make another member the group master first, or disband the group." : null,
        });
        menu.Add(new("Disband group...", can && g.IsMaster, MenuAction.Asking(host.Confirm,
            new("Disband group", $"Disband the group \"{g.Name}\"? The group will be gone for everyone in it.", "DISBAND"),
            () => DisbandGroup(g))));
        return menu;
    }

    public IReadOnlyList<MenuAction> MemberMenu(GroupItem g, GroupMemberItem m, ISocialMenuHost host)
    {
        bool Can(GroupRole role) => CanEdit && !g.IsInvited && CanSetRole(g, m, role);
        var friend = m.Key.Length > 0 ? Friends.FirstOrDefault(f => f.IsFriend && f.Key == m.Key) : null;
        return
        [
            new("Send message", friend is { CanMessage: true }, () => SelectedFriend = friend),
            new("View profile", friend is { IsFriend: true, Pending: false }, () => host.ViewProfile(friend!)),
            MenuAction.Separator,
            new("Make group master...", Can(GroupRole.Master), MenuAction.Asking(host.Confirm,
                new("Make group master", $"Make {m.Name} the master of \"{g.Name}\"? You'll become an ordinary member.", "MAKE MASTER"),
                () => SetMemberRole(g, m, GroupRole.Master))),
            m.Role == GroupRole.SubMaster
                ? new("Relieve sub-master", Can(GroupRole.Member), () => SetMemberRole(g, m, GroupRole.Member))
                : new("Make sub-master", Can(GroupRole.SubMaster), () => SetMemberRole(g, m, GroupRole.SubMaster)),
            new("Remove from group...", Can(GroupRole.Remove), MenuAction.Asking(host.Confirm,
                new("Remove from group", $"Remove {m.Name} from \"{g.Name}\"?", "REMOVE"), () => SetMemberRole(g, m, GroupRole.Remove))),
        ];
    }
}
