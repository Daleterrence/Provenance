using Provenance.Common.Hook;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Social;

public static class SocialRequests
{
    // an empty request is one the hook takes and then reads as nothing, so this one has a line
    public static bool GoFull(int pid) => HookFiles.WriteRequest(pid, HookContract.Requests.Full, 1);

    public static bool SetStatus(int pid, PolStatus status) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Status, PolStatusText.Code(status).ToString());

    public static bool Send(int pid, int friendRow, string friendName, string token, string subject, string text) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Send, friendRow, token, friendName, Pol(subject), HookFiles.Body(Pol(text)));

    public static bool Ack(int pid, IEnumerable<int> mids)
    {
        try { return HookFiles.WriteRequest(pid, HookContract.Requests.Ack, Unread(pid, HookContract.Requests.Ack, mids.Select(m => m.ToString()))); }
        catch { return false; }
    }

    public static bool Read(int pid, IEnumerable<int> mids)
    {
        try { return HookFiles.WriteRequest(pid, HookContract.Requests.Read, Unread(pid, HookContract.Requests.Read, mids.Select(m => m.ToString()))); }
        catch { return false; }
    }

    public static bool DeleteFiles(int pid, IEnumerable<ChatEntry> entries)
    {
        var lines = entries.Where(e => e.Time > 0 && e.PeerKey.Length > 0 && e.PeerKey.All(Uri.IsHexDigit) &&
                                       !(e.Outgoing && e.State is not ("sent" or "unsent")))
                           .Select(e => $"{(e.Outgoing ? 's' : 'r')}|{e.Time}|{e.Seq}|{e.PeerKey}").ToList();
        if (lines.Count == 0) return true;
        try { return HookFiles.WriteRequest(pid, HookContract.Requests.DeleteFiles, Unread(pid, HookContract.Requests.DeleteFiles, lines)); }
        catch { return false; }
    }

    private static HookFiles.RequestBody Unread(int pid, string request, IEnumerable<string> lines)
    {
        var text = string.Join("\n", lines);
        try { text = File.ReadAllText(HookFiles.PathFor(pid, request)) + "\n" + text; } catch (FileNotFoundException) { }
        return HookFiles.Body(text);
    }

    public static bool SetProfilePicture(int pid, int picId, string token) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Pic, picId, token);

    public static bool EditFriend(int pid, FriendEdit op, string token, int row, string key, string arg = "")
    {
        var verb = op switch
        {
            FriendEdit.Rename => "rename",
            FriendEdit.Delete => "delete",
            FriendEdit.Ignore => "ignore",
            _ => "unignore",
        };
        return HookFiles.WriteRequest(pid, HookContract.Requests.Friend, verb, token, row, key, Pol(arg));
    }

    public static bool AnswerRequest(int pid, RequestAnswer answer, string token, int mid, string subject)
    {
        var verb = answer switch { RequestAnswer.Accept => "accept", RequestAnswer.Decline => "decline", _ => "ignore" };
        return HookFiles.WriteRequest(pid, HookContract.Requests.Answer, verb, token, mid, Pol(subject), "");
    }

    public static bool Search(int pid, string token, FriendSearchKind kind, string value) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Search, token, FriendSearch.Wire(kind),
                               Pol(value.Replace("\r", "").Replace("\n", "")));

    public static bool SendFriendRequest(int pid, string token, int index, string hitKey, string subject, string text) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Answer, "request", token, index, Pol(subject), Pol(text), hitKey);

    public static bool RequestProfile(int pid, string token, int friendRow, string friendKey) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Profile, token, friendRow, friendKey);

    public static bool CreateGroup(int pid, string token, string name) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Group, "create", token, "", Pol(name), "", "", "");

    public static bool DisbandGroup(int pid, string token, string groupId) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Group, "disband", token, groupId, "", "", "", "");

    public static bool LeaveGroup(int pid, string token, string groupId) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Group, "leave", token, groupId, "", "", "", "");

    public static bool SetMemberRole(int pid, string token, string groupId, int memberIndex, GroupRole role, string memberKey) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Group, "class", token, groupId, memberIndex, (int)role, "", memberKey);

    public static bool SetGroupComment(int pid, string token, string groupId, string text) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Group, "comment", token, groupId, Pol(text), "", "", "");

    public static bool InviteToGroup(int pid, string token, string groupId, int friendRow, string subject, string text, string friendKey) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Group, "invite", token, groupId, friendRow, Pol(subject), Pol(text), friendKey);

    public static bool AnswerGroupInvite(int pid, string token, bool accept, int messageId, string subject) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Group, accept ? "accept" : "decline", token, "", messageId, Pol(subject), "", "");

    public static bool SetComment(int pid, string token, string text) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Comment, token, Pol(text));

    public static bool RequestCharacter(int pid, string token, CharacterId id) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Character, token, id.Ctsid, (uint)id.Ctid, (uint)(id.Ctid >> 32));

    public static bool RequestFfxiProfile(int pid, string token, int friendRow, string friendKey, int slot) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.FfxiProfile, token, friendRow, friendKey, slot);

    public static bool RequestMyFfxiProfile(int pid, string token, string characterKey, int slot) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.MyFfxiProfile, token, characterKey, slot);

    public static bool RequestMyProfile(int pid, string token) => HookFiles.WriteRequest(pid, HookContract.Requests.MyProfile, token);

    public static bool SaveMyProfile(int pid, string token, PrivacyLevel level, IEnumerable<KeyValuePair<string, string>> fields,
                                     string noMailText = "")
    {
        var lines = new List<object> { token, (int)level };
        if (noMailText.Length > 0) lines.Add("nomail=" + Pol(noMailText));
        lines.AddRange(fields.Select(f => $"{f.Key}={Pol(f.Value)}"));
        return HookFiles.WriteRequest(pid, HookContract.Requests.SaveProfile, [.. lines]);
    }

    public static bool RenameHandle(int pid, string token, string name, string key) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Rename, token, Pol(name), key);

    public static string FailReason(string state) =>
        state.StartsWith("failed:", StringComparison.Ordinal) ? state[7..] : state;

    public static bool LinkAll(int pid, string token, PolHandle active, IReadOnlyList<PolContentId> contents)
    {
        if (contents.Count is 0 or > HandleList.MaxContentIds) return false;
        var lines = new List<object> { "linkall", token, active.Slot, active.Key };
        foreach (var c in contents) lines.AddRange([c.Index, c.Key]);
        return HookFiles.WriteRequest(pid, HookContract.Requests.Handle, [.. lines]);
    }

    public static bool SwitchHandle(int pid, string token, PolHandle handle) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Handle, "switch", token, handle.Slot, handle.Key);

    public static bool LinkCharacter(int pid, string token, PolHandle handle, PolContentId content) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Handle, "link", token, handle.Slot, handle.Key, content.Index, content.Key);

    public static bool CreateHandle(int pid, string token, string name) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Handle, "create", token, Pol(name));

    public static bool DeleteHandle(int pid, string token, PolHandle handle) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.Handle, "delete", token, handle.Slot, handle.Key);

    public static bool ReorderContentIds(int pid, string token, IReadOnlyList<ContentIdOrder> changes)
    {
        if (changes.Count is 0 or > HandleList.MaxContentIdRecords) return false;
        var lines = new List<object> { "order", token };
        foreach (var c in changes) lines.AddRange([c.Record.Index, c.Record.Key, c.Order]);
        return HookFiles.WriteRequest(pid, HookContract.Requests.Handle, [.. lines]);
    }

    public static bool SendPending(int pid) => HookFiles.RequestPaths(pid, HookContract.Requests.Send).Any(File.Exists);

    // whatever a box let through, nothing POL can't carry goes past here
    private static string Pol(string text) => PolCharacters.Sendable(text);
}
