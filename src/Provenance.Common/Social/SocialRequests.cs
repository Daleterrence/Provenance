using Provenance.Common.Hook;

namespace Provenance.Common.Social;

// the hook's per-session requests, each one a list of its fields in the order the hook reads them
public static class SocialRequests
{
    public static bool SetStatus(int pid, PolStatus status) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Status, PolStatusText.Code(status).ToString());

    // no control characters but line breaks, the viewer's rule. The hook checks again, and checks the name against the
    // friend row before it sends
    public static bool Send(int pid, int friendRow, string friendName, string token, string subject, string text) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Send, friendRow, token, friendName, subject, HookFiles.Body(text));

    // mids safely in the chat log, so the hook can delete them the viewer's way (GET, save, DELETE)
    public static bool Ack(int pid, IEnumerable<int> mids)
    {
        try
        {
            var path = HookFiles.PathFor(pid, HookContract.Requests.Ack);
            var text = string.Join("\n", mids);
            // ids the hook hasn't read yet stay. If it takes the file between the check and the read, nothing's lost
            try { text = File.ReadAllText(path) + "\n" + text; } catch (FileNotFoundException) { }
            return HookFiles.WriteRequest(pid, HookContract.Requests.Ack, HookFiles.Body(text));
        }
        catch { return false; }
    }

    public static bool SetProfilePicture(int pid, int picId, string token) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Pic, picId, token);

    // row is the ignore-list index for Unignore. The key's there so a changed list gets refused
    public static bool EditFriend(int pid, FriendEdit op, string token, int row, string key, string arg = "")
    {
        var verb = op switch
        {
            FriendEdit.Rename => "rename",
            FriendEdit.Delete => "delete",
            FriendEdit.Ignore => "ignore",
            _ => "unignore",
        };
        return HookFiles.WriteRequest(pid, HookContract.Requests.Friend, verb, token, row, key, arg);
    }

    public static bool AnswerRequest(int pid, RequestAnswer answer, string token, int mid, string subject, bool allHandles = false)
    {
        var verb = answer switch { RequestAnswer.Accept => "accept", RequestAnswer.Decline => "decline", _ => "ignore" };
        return HookFiles.WriteRequest(pid, HookContract.Requests.Answer, verb, token, mid, subject, allHandles ? "1" : "0");
    }

    // the hook checks the value again and builds the query itself
    public static bool Search(int pid, string token, FriendSearchKind kind, string value) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Search, token, FriendSearch.Wire(kind),
                               value.Replace("\r", "").Replace("\n", ""));

    public static bool SendFriendRequest(int pid, string token, int index, string hitKey, string subject, string text) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Answer, "request", token, index, subject, text, hitKey);

    public static bool RequestProfile(int pid, string token, int friendRow, string friendKey) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Profile, token, friendRow, friendKey);

    // the group requests all have the same seven lines: the operation, token, group id, three operation fields and a key
    // the hook checks (blank where an operation has none)
    public static bool CreateGroup(int pid, string token, string name) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Group, "create", token, "", name, "", "", "");

    public static bool DisbandGroup(int pid, string token, string groupId) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Group, "disband", token, groupId, "", "", "", "");

    public static bool LeaveGroup(int pid, string token, string groupId) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Group, "leave", token, groupId, "", "", "", "");

    // class is the viewer's number for it, memberKey the member's, so a changed list gets refused
    public static bool SetMemberClass(int pid, string token, string groupId, int memberIndex, int memberClass, string memberKey) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Group, "class", token, groupId, memberIndex, memberClass, "", memberKey);

    public static bool SetGroupComment(int pid, string token, string groupId, string text) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Group, "comment", token, groupId, text, "", "", "");

    // goes out as a message to the friend's row, friendKey is there so a changed list gets refused
    public static bool InviteToGroup(int pid, string token, string groupId, int friendRow, string subject, string text, string friendKey) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Group, "invite", token, groupId, friendRow, subject, text, friendKey);

    // messageId is the invitation's, subject what's sent back
    public static bool AnswerGroupInvite(int pid, string token, bool accept, int messageId, string subject) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Group, accept ? "accept" : "decline", token, "", messageId, subject, "", "");

    public static bool SetComment(int pid, string token, string text) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Comment, token, text);

    // the hook only looks it up while a friend's on that character
    public static bool RequestCharacter(int pid, string token, uint ctsid, ulong ctid) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Character, token, ctsid, (uint)ctid, (uint)(ctid >> 32));

    // which of the friend's game slots, the hook takes the character's ids from the slot itself
    public static bool RequestFfxiProfile(int pid, string token, int friendRow, string friendKey, int slot) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.FfxiProfile, token, friendRow, friendKey, slot);

    public static bool RequestMyProfile(int pid, string token) => HookFiles.WriteRequest(pid, HookContract.Requests.MyProfile, token);

    // one of the characters from the loaded profile, by its key there. handleKey is the profile's, so neither ever
    // lands on a handle that's become active since
    public static bool RequestXiProfile(int pid, string token, string handleKey, string characterKey) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.XiProfile, token, handleKey, characterKey);

    // load first, the save keeps each field's own level. level is 1 Private, 2 Friends only, 3 POL
    public static bool SaveMyProfile(int pid, string token, int level, IEnumerable<KeyValuePair<string, string>> fields,
                                     string noMailText = "")
    {
        var lines = new List<object> { token, level };
        if (noMailText.Length > 0) lines.Add("nomail=" + noMailText);
        lines.AddRange(fields.Select(f => $"{f.Key}={f.Value}"));
        return HookFiles.WriteRequest(pid, HookContract.Requests.SaveProfile, [.. lines]);
    }

    // key is the loaded profile's, so a rename never lands on a handle that's become active since
    public static bool RenameHandle(int pid, string token, string name, string key) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Rename, token, name, key);

    public static string FailReason(string state) =>
        state.StartsWith("failed:", StringComparison.Ordinal) ? state[7..] : state;

    // the unlinked ones go on the active handle in one save, the hook refuses the lot if they don't all fit
    public static bool LinkAll(int pid, string token, PolHandle active, IReadOnlyList<PolContentId> contents)
    {
        if (contents.Count is 0 or > HandleList.MaxContentIds) return false;
        var lines = new List<object> { "linkall", token, active.Slot, active.Key };
        foreach (var c in contents) lines.AddRange([c.Index, c.Key]);
        return HookFiles.WriteRequest(pid, HookContract.Requests.Handle, [.. lines]);
    }

    // the keys are the snapshot's, so the hook refuses one aimed at a record that's changed since
    public static bool SwitchHandle(int pid, string token, PolHandle handle) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Handle, "switch", token, handle.Slot, handle.Key);

    public static bool LinkCharacter(int pid, string token, PolHandle handle, PolContentId content) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Handle, "link", token, handle.Slot, handle.Key, content.Index, content.Key);

    public static bool UnlinkCharacter(int pid, string token, PolHandle handle, PolContentId content) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Handle, "unlink", token, handle.Slot, handle.Key, content.Index, content.Key);

    public static bool SendPending(int pid) => HookFiles.RequestPaths(pid, HookContract.Requests.Send).Any(File.Exists);
}
