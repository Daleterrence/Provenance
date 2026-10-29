using System.Text;
using System.Text.Json;
using static Provenance.Common.JsonFields;

namespace Provenance.Common.Social;

public enum PolStatus { Online, Away, Busy, AppearOffline }

public static class PolStatusText
{
    public static PolStatus Parse(string? s) => s switch
    {
        "online" => PolStatus.Online,
        "away" => PolStatus.Away,
        "busy" => PolStatus.Busy,
        _ => PolStatus.AppearOffline,
    };

    public static string Label(PolStatus s) => s switch
    {
        PolStatus.Online => "Online",
        PolStatus.Away => "Away",
        PolStatus.Busy => "Busy",
        _ => "Appear offline",
    };

    // the digit the hook's status file wants
    public static int Code(PolStatus s) =>
        s switch { PolStatus.Online => 0, PolStatus.Away => 1, PolStatus.Busy => 2, _ => 4 };
}

public sealed record PolFriend(string Name, string Character, string Comment, PolStatus Status,
                               bool Online, bool Pending, uint IdLow, uint IdHigh, int Row = -1,
                               int ProfilePictureId = -1, int Location = 0, string Key = "")
{
    public bool HasComment => !string.IsNullOrWhiteSpace(Comment);
    public string StatusLabel => PolStatusText.Label(Status);
    // another of my own handles (same POL ID), can't be ignored or invited
    public bool IsMe { get; init; }
}

// Location has 3 lines, Interest 2, the rest 1, and only Portrait has a pic
public sealed record PolProfileRow(string Label, string Field, int State, IReadOnlyList<string> Lines,
                                   int ProfilePictureId);

public sealed record PolProfile(string Name, string Mail, int MailState, string Hid, long Updated, int Level,
                                IReadOnlyList<PolProfileRow> Rows, string Away, string AwayFrom)
{
    public const int Shown = 0, Private = 1, NotSet = 2, UnderTen = 3;
}

// Class: 2 invited, 3 member, 4 sub-master, 5 master
public sealed record PolGroupMember(int Index, string Name, string Key, int ProfilePictureId, int Class, int Status,
                                    bool Me, bool Ignored, string Text, string Comment)
{
    // 1 online, 2 away, 3 busy, 0 and 4 offline
    public bool Online => Status is 1 or 2 or 3;
}

public sealed record PolGroup(string Id, string Name, int MyClass, int MySlot, int MyStatus, int MyIndex,
                              IReadOnlyList<PolGroupMember> Members);

public sealed record PolSearchHit(int Index, string Name, int ProfilePictureId, string Key, bool IsFriend)
{
    public bool IsMe { get; init; }
}

public sealed record PolBlocked(int Index, string Name, string Key, bool AllHandles);

// State is "read" (body downloaded), "pending", "failed" or "realtime"
public sealed record PolMessage(string From, int FromSlot, int Kind, long Time, long Seq, string State,
                                string Subject, string Text, int Mid = 0, string FromKey = "")
{
    // looks like Unix seconds, though that's a guess tbh
    public DateTimeOffset? When => Time > 0 ? DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime() : null;

    // the in-memory ids are session-masked, this one's stable
    public string Key => $"{From}|{FromSlot}|{Seq}|{Time}";

    public bool HasBody => State is "read" or "realtime";
    public bool IsChat => Kind == 0;
    public bool IsMe { get; init; }

    public const int KindFriendRequest = 1, KindFriendAccepted = 9, KindFriendDeclined = 10;

    // 0x12 is the server's "removed from friend group", 0x13 "friend group disbanded"
    public const int KindGroupInvite = 0xe, KindGroupAccepted = 0xf, KindGroupDeclined = 0x10,
                     KindGroupRemoved = 0x12, KindGroupDisbanded = 0x13;
}

// pol_hook.<pid>.social.json, rewritten every couple of secs. FfxiService is the Content ID check ("active", "none",
// "unknown"), Notice the account notice status (221..252 = LM-01..LM-32)
public sealed record PolSocialSnapshot(PolStatus MyStatus, int Groups, string MyHandle, int MyProfilePictureId,
                                       IReadOnlyList<PolFriend> Friends, IReadOnlyList<PolMessage> Messages,
                                       string FfxiService = "unknown", int Notice = 0,
                                       string SendToken = "", string SendState = "", string Presence = "")
{
    public bool FriendsLoaded { get; init; } = true;
    public bool GroupsLoaded { get; init; } = true;
    public string Mail { get; init; } = "";   // "pending", "ok" or "failed"
    public bool StatusBusy { get; init; }
    // viewer message 0x7007
    public bool BadPassword { get; init; }
    public string Play { get; init; } = "";   // "refused:..." when the hook's gate stopped a Play
    // chat lines, answers and group invites share one send slot, so a result can get bumped between snapshots
    public IReadOnlyList<(string Token, string State)> SendResults { get; init; } = [];

    public string? SendStateFor(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (SendToken == token) return SendState;
        for (int i = SendResults.Count - 1; i >= 0; i--)
            if (SendResults[i].Token == token) return SendResults[i].State;
        return null;
    }

    // job states run "saving"/"searching"/"loading" -> "saved"/"done"/"ready", or "failed:..."
    public string EditToken { get; init; } = "";
    public string EditState { get; init; } = "";
    public string MyComment { get; init; } = "";
    public string MyKey { get; init; } = "";
    public IReadOnlyList<PolBlocked> Blocked { get; init; } = [];
    public string ProfilePictureToken { get; init; } = "";
    public string ProfilePictureState { get; init; } = "";
    public string SearchToken { get; init; } = "";
    public string SearchState { get; init; } = "";
    public IReadOnlyList<PolSearchHit> SearchResults { get; init; } = [];
    public string ProfileToken { get; init; } = "";
    public string ProfileState { get; init; } = "";
    public PolProfile? Profile { get; init; }
    public IReadOnlyList<PolGroup> GroupList { get; init; } = [];
    public string GroupToken { get; init; } = "";
    public string GroupState { get; init; } = "";
    public string MeToken { get; init; } = "";
    public string MeState { get; init; } = "";

    public static readonly PolSocialSnapshot Empty = new(PolStatus.AppearOffline, 0, "", -1, [], []);
}

// the hook's per-session files. Requests get written whole (temp file then move) so the hook never sees half of one
public static class SocialSnapshot
{
    public static string PathForPid(int pid) => HookFiles.PathFor(pid, "social.json");

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

    public enum FriendEdit { Rename, Delete, Ignore, Unignore }

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

    public enum RequestAnswer { Accept, Decline, Ignore }

    public static bool AnswerRequest(int pid, RequestAnswer answer, string token, int mid, string subject, bool allHandles = false)
    {
        var verb = answer switch { RequestAnswer.Accept => "accept", RequestAnswer.Decline => "decline", _ => "ignore" };
        return WriteRequest(pid, "answer", $"{verb}\n{OneLine(token)}\n{mid}\n{OneLine(subject)}\n{(allHandles ? "1" : "0")}");
    }

    // letters, digits, space, '-', '_' and '.' only, the hook refuses anything else
    public static bool Search(int pid, string token, string handleName) =>
        WriteRequest(pid, "search", $"{token.Replace("\n", "")}\n{handleName.Replace("\r", "").Replace("\n", "")}");

    public static bool SendFriendRequest(int pid, string token, int index, string hitKey, string subject, string text) =>
        WriteRequest(pid, "answer", $"request\n{OneLine(token)}\n{index}\n{OneLine(subject)}\n{OneLine(text)}\n{OneLine(hitKey)}");

    public static bool RequestProfile(int pid, string token, int friendRow, string friendKey) =>
        WriteRequest(pid, "profile", $"{token.Replace("\n", "")}\n{friendRow}\n{friendKey.Replace("\n", "")}");

    public enum GroupOp
    {
        Create,          // a = name
        Disband,
        Leave,
        SetClass,        // a = member index, b = class, key
        Comment,         // a = text
        Invite,          // a = friend row, b = subject, c = text, key
        AcceptInvite,    // a = message id, b = reply subject
        DeclineInvite,   // same as AcceptInvite
    }

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

    public static bool RequestMyProfile(int pid, string token) => WriteRequest(pid, "myprofile", OneLine(token));

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

    public static bool RenameHandle(int pid, string token, string name) =>
        WriteRequest(pid, "rename", $"{OneLine(token)}\n{OneLine(name)}");

    // an abandoned edit mustn't run later, so the .taking copy the hook would retry goes too
    public static bool CancelRequest(int pid, string ext)
    {
        var path = HookFiles.PathFor(pid, ext);
        bool removed = false;
        foreach (var p in new[] { path, path + ".taking" })
            try { if (File.Exists(p)) { File.Delete(p); removed = true; } } catch { }
        return removed;
    }

    public static void RefreshMail(int pid) => WriteRequest(pid, "mail", "1");

    // a .taking file stops holding the queue after 15 secs, the hook reads newer requests in place after 10
    public static bool SendPending(int pid)
    {
        var path = HookFiles.PathFor(pid, "send");
        var taking = path + ".taking";
        return File.Exists(path) ||
               (File.Exists(taking) && DateTime.UtcNow - File.GetLastWriteTimeUtc(taking) < TimeSpan.FromSeconds(15));
    }

    private static string OneLine(string s) => s.Replace("\r", "").Replace("\n", " ");

    private static bool WriteRequest(int pid, string ext, string body)
    {
        try
        {
            var path = HookFiles.PathFor(pid, ext);
            File.WriteAllText(path + ".tmp", body, new UTF8Encoding(false));
            File.Move(path + ".tmp", path, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    public static PolSocialSnapshot Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var friends = Arr(root, "friends", f => new PolFriend(Str(f, "name"), Str(f, "character"),
                Str(f, "comment"), PolStatusText.Parse(Str(f, "status")), Bool(f, "online"), Bool(f, "pending"),
                UInt(f, "idLow"), UInt(f, "idHigh"), Int(f, "row", -1), Int(f, "pic", -1), (int)Long(f, "location"),
                Str(f, "key")) { IsMe = Bool(f, "me") });
            var messages = Arr(root, "messages", m => new PolMessage(Str(m, "from"), (int)Long(m, "fromSlot"),
                (int)Long(m, "kind"), Long(m, "time"), Long(m, "seq"), Str(m, "state"), Str(m, "subject"),
                Str(m, "text"), (int)Long(m, "mid"), Str(m, "fromKey")) { IsMe = Bool(m, "me") });
            var (sendToken, sendState) = Job(root, "send");
            var (editToken, editState) = Job(root, "edit");
            var (picToken, picState) = Job(root, "pic");
            var (searchToken, searchState) = Job(root, "search");
            var (profileToken, profileState) = Job(root, "profile");
            var (groupToken, groupState) = Job(root, "group");
            var (meToken, meState) = Job(root, "me");
            var ffxi = Str(root, "ffxiService");
            return new PolSocialSnapshot(PolStatusText.Parse(Str(root, "myStatus")), Int(root, "groups", 0),
                                         Str(root, "myHandle"), Int(root, "myPic", -1), friends, messages,
                                         ffxi.Length > 0 ? ffxi : "unknown", (int)Long(root, "notice"),
                                         sendToken, sendState, Str(root, "presence"))
            {
                FriendsLoaded = Bool(root, "friendsLoaded"),
                GroupsLoaded = Bool(root, "groupsLoaded"),
                Mail = Str(root, "mail"),
                StatusBusy = Bool(root, "statusBusy"),
                BadPassword = Bool(root, "badPassword"),
                Play = Str(root, "play"),
                SendResults = Arr(root, "sends", s => (Str(s, "token"), Str(s, "state"))),
                EditToken = editToken,
                EditState = editState,
                MyComment = Str(root, "myComment").Trim(),
                MyKey = Str(root, "myKey"),
                Blocked = Arr(root, "blocked", x =>
                    new PolBlocked((int)Long(x, "index"), Str(x, "name"), Str(x, "key"), Bool(x, "allHandles"))),
                ProfilePictureToken = picToken,
                ProfilePictureState = picState,
                SearchToken = searchToken,
                SearchState = searchState,
                SearchResults = Arr(Obj(root, "search"), "results", h => new PolSearchHit((int)Long(h, "index"),
                    Str(h, "name"), (int)Long(h, "pic"), Str(h, "key"), Bool(h, "friend")) { IsMe = Bool(h, "me") }),
                ProfileToken = profileToken,
                ProfileState = profileState,
                Profile = profileState == "done" ? ReadProfile(Obj(root, "profile")) : null,
                GroupList = Arr(root, "groupList", ReadGroup),
                GroupToken = groupToken,
                GroupState = groupState,
                MeToken = meToken,
                MeState = meState,
            };
        }
        catch
        {
            return PolSocialSnapshot.Empty;
        }
    }

    private static PolProfile ReadProfile(JsonElement p) =>
        new(Str(p, "name"), Str(p, "mail"), (int)Long(p, "mailState"), Str(p, "hid"), Long(p, "updated"),
            (int)Long(p, "level"),
            Arr(p, "rows", r => new PolProfileRow(Str(r, "label"), Str(r, "field"), (int)Long(r, "state"),
                                                  Arr(r, "lines", l => l.GetString() ?? ""), (int)Long(r, "pic"))),
            Str(p, "away").TrimEnd(), Str(p, "awayFrom"));

    private static PolGroup ReadGroup(JsonElement g) =>
        new(Str(g, "id"), Str(g, "name"), (int)Long(g, "myClass"), (int)Long(g, "mySlot"), (int)Long(g, "myStatus"),
            (int)Long(g, "myIndex"),
            Arr(g, "members", m => new PolGroupMember((int)Long(m, "index"), Str(m, "name"), Str(m, "key"),
                (int)Long(m, "pic"), (int)Long(m, "class"), (int)Long(m, "status"), Bool(m, "me"),
                Bool(m, "ignored"), Str(m, "text"), Str(m, "comment"))));

    private static (string Token, string State) Job(JsonElement e, string n) =>
        Obj(e, n) is { ValueKind: JsonValueKind.Object } o ? (Str(o, "token"), Str(o, "state")) : ("", "");

    public static PolSocialSnapshot? TryRead(int pid)
    {
        var path = PathForPid(pid);
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) return null;   // never sit there reading a huge file
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return Parse(sr.ReadToEnd());
        }
        catch { return null; }
    }
}
