using System.Text;
using Provenance.Common.Hook;
using Provenance.Common.IO;

namespace Provenance.Common.Social;

// the hook's per-session files. Requests get written whole (temp file then move) so the hook never sees half of one
public static class SocialRequests
{
    public static void SetStatus(int pid, PolStatus status) => WriteRequest(pid, "status", PolStatusText.Code(status).ToString());

    // no control characters but line breaks, the viewer's rule. The hook checks again, and checks the name against the
    // friend row before it sends
    public static bool Send(int pid, int friendRow, string friendName, string token, string subject, string text)
    {
        try
        {
            var body = new string(text.Replace("\r", "").Select(ch => ch < ' ' && ch != '\n' ? ' ' : ch).ToArray());
            return WriteRequest(pid, "send", $"{friendRow}\n{OneLine(token)}\n{OneLine(friendName)}\n{OneLine(subject)}\n{body}");
        }
        catch { return false; }
    }

    // mids safely in the chat log, so the hook can delete them the viewer's way (GET, save, DELETE)
    public static bool Ack(int pid, IEnumerable<int> mids)
    {
        try
        {
            var path = HookFiles.PathFor(pid, "ack");
            var text = string.Join("\n", mids);
            // ids the hook hasn't read yet stay. If it takes the file between the check and the read, nothing's lost
            try { text = File.ReadAllText(path) + "\n" + text; } catch (FileNotFoundException) { }
            return WriteRequest(pid, "ack", text);
        }
        catch { return false; }
    }

    public static bool SetProfilePicture(int pid, int picId, string token) =>
        WriteRequest(pid, "pic", $"{picId}\n{token.Replace("\n", "")}");

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
        return WriteRequest(pid, "friend", $"{verb}\n{OneLine(token)}\n{row}\n{OneLine(key)}\n{OneLine(arg)}");
    }

    public static bool AnswerRequest(int pid, RequestAnswer answer, string token, int mid, string subject, bool allHandles = false)
    {
        var verb = answer switch { RequestAnswer.Accept => "accept", RequestAnswer.Decline => "decline", _ => "ignore" };
        return WriteRequest(pid, "answer", $"{verb}\n{OneLine(token)}\n{mid}\n{OneLine(subject)}\n{(allHandles ? "1" : "0")}");
    }

    // the hook checks the value again and builds the query itself
    public static bool Search(int pid, string token, FriendSearchKind kind, string value) =>
        WriteRequest(pid, "search", $"{token.Replace("\n", "")}\n{FriendSearch.Wire(kind)}\n{value.Replace("\r", "").Replace("\n", "")}");

    public static bool SendFriendRequest(int pid, string token, int index, string hitKey, string subject, string text) =>
        WriteRequest(pid, "answer", $"request\n{OneLine(token)}\n{index}\n{OneLine(subject)}\n{OneLine(text)}\n{OneLine(hitKey)}");

    public static bool RequestProfile(int pid, string token, int friendRow, string friendKey) =>
        WriteRequest(pid, "profile", $"{token.Replace("\n", "")}\n{friendRow}\n{friendKey.Replace("\n", "")}");

    public static bool ChangeGroup(int pid, GroupOp op, string token, string groupId, string a = "", string b = "", string c = "",
                                   string key = "")
    {
        var verb = op switch
        {
            GroupOp.Create => "create", GroupOp.Disband => "disband", GroupOp.Leave => "leave",
            GroupOp.SetClass => "class", GroupOp.Comment => "comment", GroupOp.Invite => "invite",
            GroupOp.AcceptInvite => "accept", _ => "decline",
        };
        return WriteRequest(pid, "group",
            $"{verb}\n{OneLine(token)}\n{OneLine(groupId)}\n{OneLine(a)}\n{OneLine(b)}\n{OneLine(c)}\n{OneLine(key)}");
    }

    public static bool SetComment(int pid, string token, string text) =>
        WriteRequest(pid, "comment", $"{token.Replace("\n", "")}\n{text.Replace("\r", "").Replace("\n", " ")}");

    // the hook only looks it up while a friend's on that character
    public static bool RequestCharacter(int pid, string token, uint ctsid, ulong ctid) =>
        WriteRequest(pid, "character", $"{OneLine(token)}\n{ctsid}\n{(uint)ctid}\n{(uint)(ctid >> 32)}");

    // which of the friend's game slots, the hook takes the character's ids from the slot itself
    public static bool RequestFfxiProfile(int pid, string token, int friendRow, string friendKey, int slot) =>
        WriteRequest(pid, "ffxiprofile", $"{OneLine(token)}\n{friendRow}\n{OneLine(friendKey)}\n{slot}");

    public static bool RequestMyProfile(int pid, string token) => WriteRequest(pid, "myprofile", OneLine(token));

    // one of the characters from the loaded profile, by its key there. handleKey is the profile's, so neither ever
    // lands on a handle that's become active since
    public static bool RequestXiProfile(int pid, string token, string handleKey, string characterKey) =>
        WriteRequest(pid, "xiprofile", $"{OneLine(token)}\n{OneLine(handleKey)}\n{OneLine(characterKey)}");

    // load first, the save keeps each field's own level. level is 1 Private, 2 Friends only, 3 POL
    public static bool SaveMyProfile(int pid, string token, int level, IEnumerable<KeyValuePair<string, string>> fields,
                                     string noMailText = "")
    {
        var sb = new StringBuilder();
        sb.Append(OneLine(token)).Append('\n').Append(level);
        if (noMailText.Length > 0) sb.Append("\nnomail=").Append(OneLine(noMailText));
        foreach (var (name, value) in fields) sb.Append('\n').Append(OneLine(name)).Append('=').Append(OneLine(value));
        return WriteRequest(pid, "saveprofile", sb.ToString());
    }

    // key is the loaded profile's, so a rename never lands on a handle that's become active since
    public static bool RenameHandle(int pid, string token, string name, string key) =>
        WriteRequest(pid, "rename", $"{OneLine(token)}\n{OneLine(name)}\n{OneLine(key)}");

    public static string FailReason(string state) =>
        state.StartsWith("failed:", StringComparison.Ordinal) ? state[7..] : state;

    // the unlinked ones go on the active handle in one save, the hook refuses the lot if they don't all fit
    public static bool LinkAll(int pid, string token, PolHandle active, IReadOnlyList<PolContentId> contents)
    {
        if (contents.Count is 0 or > HandleList.MaxContentIds) return false;
        var body = new StringBuilder($"linkall\n{OneLine(token)}\n{active.Slot}\n{OneLine(active.Key)}");
        foreach (var c in contents) body.Append($"\n{c.Index}\n{OneLine(c.Key)}");
        return WriteRequest(pid, "handle", body.ToString());
    }

    // the keys are the snapshot's, so the hook refuses one aimed at a record that's changed since
    public static bool ChangeHandle(int pid, HandleChange op, string token, PolHandle handle, PolContentId? content = null)
    {
        var verb = op switch { HandleChange.Switch => "switch", HandleChange.Link => "link", _ => "unlink" };
        var body = $"{verb}\n{OneLine(token)}\n{handle.Slot}\n{OneLine(handle.Key)}";
        if (op != HandleChange.Switch)
        {
            if (content is null) return false;
            body += $"\n{content.Index}\n{OneLine(content.Key)}";
        }
        return WriteRequest(pid, "handle", body);
    }

    // an abandoned edit mustn't run later, so the .taking copy the hook would retry goes too
    public static void CancelRequest(int pid, string ext) => HookFiles.DeleteRequest(pid, ext);

    // the hook takes a request by renaming it, so this rename and a take can't both happen. A file a scanner has open
    // gets a few more tries
    public static Withdrawal WithdrawRequest(int pid, string ext)
    {
        var path = HookFiles.PathFor(pid, ext);
        for (int i = 0; ; i++)
        {
            try
            {
                File.Move(path, path + ".withdrawn", overwrite: true);
                try { File.Delete(path + ".withdrawn"); } catch { }
                return Withdrawal.Withdrawn;
            }
            catch (FileNotFoundException) { return Withdrawal.Taken; }
            catch (IOException) when (i < 4 && File.Exists(path)) { Thread.Sleep(50); }
            catch { return File.Exists(path) ? Withdrawal.Unknown : Withdrawal.Taken; }
        }
    }

    public static bool SendPending(int pid) => HookFiles.RequestPaths(pid, "send").Any(File.Exists);

    private static string OneLine(string s) => s.Replace("\r", "").Replace("\n", " ");

    private static bool WriteRequest(int pid, string ext, string body)
    {
        try
        {
            var path = HookFiles.PathFor(pid, ext);
            AtomicFile.Write(path, body);
            return true;
        }
        catch { return false; }
    }
}
