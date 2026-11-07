using Provenance.Common.Social;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Social;

public partial class SocialViewModel
{
    public static string ClearConversationQuestion(string name) =>
        $"Delete every message with {name} that Provenance has kept on this PC? This can't be undone.";

    internal IDialogs DialogsForTest => _dialogs;

    public void AddFriend() => _windows.AddFriend(this);

    public void EditProfile() => _windows.EditProfile(this);

    public void ShowMessagesAlongside(bool takeFocus) => _windows.ShowMessagesAlongside(this, takeFocus);

    public void SetWindowsSplit(bool split)
    {
        if (split) _windows.Split(this);
        else _windows.Merge(this);
    }

    // a request or invitation gets the viewer's answer window (Ignore lives on the right-click menu)
    public void AnswerRow(FriendItem f)
    {
        if (!CanEdit) return;
        if (f.IsGroupInvite)
        {
            if (_dialogs.Choose("Group invitation from " + f.Name, f.Comment, "ANSWER",
                                "Accept: join the group", "Decline") is { } answer)
                AnswerGroupInvite(f, answer == 0);
            return;
        }
        if (_dialogs.Choose("Friend request from " + f.Name,
                $"{f.Name} would like to be your friend on PlayOnline.", "ANSWER",
                "Accept: add " + f.Name + " to your friend list", "Decline", ConfirmStyle.Destructive) is { } pick)
            AnswerRequest(f, pick == 0 ? RequestAnswer.Accept : RequestAnswer.Decline);
    }

    public void ShowGroupLimit() => _dialogs.Info("Friend groups", GroupLimitText);

    public string? AskGroupName() => _dialogs.Ask("Create a group",
        "Give the new friend group a name (up to 20 English letters, or 10 Japanese characters).", maxLength: PolLimits.GroupName);

    public void AskUnignore(PolBlocked b)
    {
        if (_dialogs.Confirm("Stop ignoring " + b.Name,
                $"Take {b.Name} off your ignore list? Their messages will reach you again. " +
                "This doesn't add them back to your friend list.", "STOP IGNORING"))
            Unignore(b);
    }

    public void AskSendFriendRequest(SearchHitItem hit)
    {
        if (_dialogs.Confirm("Send friend request",
                $"Send \"Let's be friends!\" to {hit.Name}? They'll show on your list" +
                (hit.IsCharacter ? " under their PlayOnline handle," : "") + " as \"Friend request sent\" until they accept.",
                "SEND"))
            SendFriendRequest(hit);
    }

    private bool ConfirmDestructive(string title, string body, string ok) =>
        _dialogs.Confirm(title, body, ok, ConfirmStyle.Destructive);

    // a line's own menu (Copy is the view's), delete is this PC's copy of it
    public IReadOnlyList<MenuAction> LineMenu(ChatEntry entry) =>
        [new("Delete message", ChatStore.CanDelete(entry), () => DeleteLine(entry))];

    public IReadOnlyList<MenuAction> FriendMenu(FriendItem f, Action<FriendItem>? openConversation = null)
    {
        bool edit = CanEdit && f.IsFriend && f.Key.Length > 0;
        if (f.IsGroupInvite)
            return [new("Accept group invitation", CanEdit, () => AnswerGroupInvite(f, true)),
                    new("Decline group invitation", CanEdit, () => AnswerGroupInvite(f, false))];
        if (f.IsRequest)
            return [new("Accept friend request", CanEdit && !f.IsMe, () => AnswerRequest(f, RequestAnswer.Accept)),
                    new("Decline friend request...", CanEdit, () =>
                    {
                        if (ConfirmDestructive("Decline friend request", $"Decline the friend request from {f.Name}?", "DECLINE"))
                            AnswerRequest(f, RequestAnswer.Decline);
                    }),
                    MenuAction.Separator,
                    // the viewer's request window only ignores this one handle
                    new("Ignore...", CanEdit && !f.IsMe, () =>
                    {
                        if (ConfirmDestructive("Ignore " + f.Name,
                                $"Add {f.Name} to your ignore list? PlayOnline will decline the request, and this handle's messages won't reach you any more. You can stop ignoring them later from your ignore list.",
                                "IGNORE"))
                            AnswerRequest(f, RequestAnswer.Ignore);
                    })];
        if (!f.IsFriend && !f.Pending)
            return [new($"Add {f.Name} as a friend...", CanEdit, () => _windows.AddFriend(this, f.Name, f.Key))];
        var menu = new List<MenuAction>
        {
            new("Send message", f.CanMessage, () =>
            {
                SelectedFriend = f;
                openConversation?.Invoke(f);
            }),
            new("View profile", f.IsFriend && !f.Pending, () => _windows.ShowProfile(this, f)),
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
            var name = _dialogs.Ask("Rename friend",
                $"Choose a new name for {f.Name} on your friend list (up to 15 English letters, numbers or symbols). " +
                "PlayOnline saves it with your friend list.", f.Name, maxLength: PolLimits.HandleName);
            // the same name has nothing to save, the viewer skips it too
            if (name is not null && name.Trim() != f.Name) RenameFriend(f, name);
        }));
        var removeBody = f.Pending
            ? $"Delete your friend request to {f.Name}? This takes it off your own friend list."
            : $"Remove {f.Name} from your PlayOnline friend list? Your saved messages with {f.Name} stay in Provenance.";
        menu.Add(new(f.Pending ? "Delete friend request..." : "Remove from friends...", edit, () =>
        {
            if (ConfirmDestructive(f.Pending ? "Delete friend request" : "Remove friend", removeBody, f.Pending ? "DELETE" : "REMOVE"))
                RemoveFriend(f);
        }));
        menu.Add(new("Ignore...", edit && !f.Pending && !f.IsMe, () =>
        {
            var pick = _dialogs.Choose("Ignore " + f.Name,
                $"PlayOnline will take {f.Name} off your friend list, and their messages won't reach you any more. " +
                "You can stop ignoring them later, but that won't put them back on your friend list.", "IGNORE",
                "Ignore this handle only", "Ignore every handle of this PlayOnline ID", ConfirmStyle.Destructive);
            if (pick is { } which) IgnoreFriend(f, which == 1);
        }));
        return menu;
    }

    // the friends window's conversation header shows the friend's menu too, so everything's reachable from a conversation
    public IReadOnlyList<MenuAction> ConversationMenu(FriendItem f, Action<FriendItem>? openConversation = null)
    {
        var menu = FriendMenu(f, openConversation).ToList();
        if (menu.Count > 0) menu.Add(MenuAction.Separator);
        menu.Add(new("Clear conversation...", Conversation.Any(ChatStore.CanDelete), () =>
        {
            var shown = Conversation.Select(e => e.Key).ToList();
            if (ConfirmDestructive("Clear conversation", ClearConversationQuestion(f.Name), "CLEAR")) ClearConversation(shown);
        }));
        return menu;
    }

    public GroupItem? GroupOf(GroupMemberItem m) => Groups.FirstOrDefault(g => g.Members.Any(x => ReferenceEquals(x, m)));

    // a group row's own menu, or a member's (never my own row), null when there isn't one
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
            var text = _dialogs.Ask("Group comment", $"Your comment in \"{g.Name}\" (up to 49 characters).", mine,
                                    maxLength: PolLimits.Comment, allowEmpty: true);
            if (text != null && text != mine) SetGroupComment(g, text);
        }));
        if (g.CanInvite)
        {
            var invite = Friends.Where(f => f.IsFriend && !f.Pending && !g.Members.Any(m => m.Key.Length > 0 && m.Key == f.Key))
                                .Select(f => new MenuAction(f.Name, can, () => InviteToGroup(g, f))).ToList();
            menu.Add(new("Invite a friend", can && invite.Count > 0) { Items = invite });
        }
        menu.Add(MenuAction.Separator);
        menu.Add(new("Leave group...", can && !g.IsMaster, () =>
        {
            if (ConfirmDestructive("Leave group", $"Leave the group \"{g.Name}\"?", "LEAVE")) LeaveGroup(g);
        })
        {
            Tip = g.IsMaster ? "The group master can't leave. Make another member the group master first, or disband the group." : null,
        });
        menu.Add(new("Disband group...", can && g.IsMaster, () =>
        {
            if (ConfirmDestructive("Disband group", $"Disband the group \"{g.Name}\"? The group will be gone for everyone in it.", "DISBAND"))
                DisbandGroup(g);
        }));
        return menu;
    }

    public IReadOnlyList<MenuAction> MemberMenu(GroupItem g, GroupMemberItem m)
    {
        bool Can(GroupRole role) => CanEdit && !g.IsInvited && CanSetRole(g, m, role);
        var friend = m.Key.Length > 0 ? Friends.FirstOrDefault(f => f.IsFriend && f.Key == m.Key) : null;
        return
        [
            new("Send message", friend is { CanMessage: true }, () => SelectedFriend = friend),
            new("View profile", friend is { IsFriend: true, Pending: false }, () => _windows.ShowProfile(this, friend!)),
            MenuAction.Separator,
            new("Make group master...", Can(GroupRole.Master), () =>
            {
                if (ConfirmDestructive("Make group master", $"Make {m.Name} the master of \"{g.Name}\"? You'll become an ordinary member.", "MAKE MASTER"))
                    SetMemberRole(g, m, GroupRole.Master);
            }),
            m.Role == GroupRole.SubMaster
                ? new("Relieve sub-master", Can(GroupRole.Member), () => SetMemberRole(g, m, GroupRole.Member))
                : new("Make sub-master", Can(GroupRole.SubMaster), () => SetMemberRole(g, m, GroupRole.SubMaster)),
            new("Remove from group...", Can(GroupRole.Remove), () =>
            {
                if (ConfirmDestructive("Remove from group", $"Remove {m.Name} from \"{g.Name}\"?", "REMOVE"))
                    SetMemberRole(g, m, GroupRole.Remove);
            }),
        ];
    }
}
