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

public sealed record PolFriend(string Name, string Comment, PolStatus Status,
                               bool Online, bool Pending, int Row = -1, int ProfilePictureId = -1, string Key = "")
{
    public bool HasComment => !string.IsNullOrWhiteSpace(Comment);
    public string StatusLabel => PolStatusText.Label(Status);
    // another of my own handles (same POL ID), can't be ignored or invited
    public bool IsMe { get; init; }
    // null unless they're online and in FFXI right now
    public PolFriendGame? Game { get; init; }
}

// the FFXI character a friend's on, from their presence: World is the game's world id, Ctsid and Ctid pick out the
// character (the FFXI profile's z_ctsid and z_ctid)
public sealed record PolFriendGame(int World, uint Ctsid, ulong Ctid)
{
    public string CharacterKey => $"{Ctsid}:{Ctid}";
}

// the hook's answer for one character's FFXI profile. NameState is PolProfile's Shown, Private or NotSet
public sealed record PolCharacterLookup(uint Ctsid, ulong Ctid, string Name, int NameState, int Level, string WorldName)
{
    public string CharacterKey => $"{Ctsid}:{Ctid}";
}

// Location has 3 lines, Interest 2, the rest 1, and only Portrait has a pic
public sealed record PolProfileRow(string Label, string Field, int State, IReadOnlyList<string> Lines,
                                   int ProfilePictureId);

public sealed record PolProfile(string Name, string Mail, int MailState, string Hid, long Updated, int Level,
                                IReadOnlyList<PolProfileRow> Rows, string Away, string AwayFrom)
{
    public const int Shown = 0, Private = 1, NotSet = 2, UnderTen = 3;

    public int NameState { get; init; }
    // the handle's FFXI characters, one tab each
    public IReadOnlyList<PolFfxiCharacter> Characters { get; init; } = [];
}

// one of the handle's game slots with an FFXI character in it. World is the game's world id
public sealed record PolFfxiCharacter(int Slot, uint Ctsid, ulong Ctid, int World)
{
    public string CharacterKey => $"{Ctsid}:{Ctid}";
}

// a character's FFXI profile as the hook reads it, raw numbers with PolProfile's Shown, Private or NotSet each
public sealed record PolFfxiProfile(int Slot, int Level, string Name, int NameState, string WorldName, int WorldNameState)
{
    public (int Value, int State) Nation { get; init; }
    public (int Value, int State) Area { get; init; }
    public (int Value, int State) Job { get; init; }
    public (int Value, int State) JobLevel { get; init; }
    public (int Value, int State) Race { get; init; }
    // the character the hook read, from the friend's slot
    public uint Ctsid { get; init; }
    public ulong Ctid { get; init; }
    public string CharacterKey => $"{Ctsid}:{Ctid}";
}

// Class: 2 invited, 3 member, 4 sub-master, 5 master
public sealed record PolGroupMember(int Index, string Name, string Key, int ProfilePictureId, int Class, int Status,
                                    bool Me, bool Ignored, string Text, string Comment)
{
    // 1 online, 2 away, 3 busy, 0 and 4 offline
    public bool Online => Status is 1 or 2 or 3;
    // the member's handle slot on its own account (-1 when the hook didn't say) and Handle ID, in the handles' key
    // format. Mine is my POL ID's row, which the viewer goes by
    public int Slot { get; init; } = -1;
    public string Hid { get; init; } = "";
    public bool Mine { get; init; }
    // in the group's chat, from the channel's own member list. Only means anything while I'm in it
    public bool InChat { get; init; }
}

public sealed record PolGroup(string Id, string Name, int MyClass, int MyStatus, IReadOnlyList<PolGroupMember> Members);

// Name is the character's for a character search, Handle is always the handle's
public sealed record PolSearchHit(int Index, string Name, int ProfilePictureId, string Key, bool IsFriend)
{
    public bool IsMe { get; init; }
    public string Handle { get; init; } = "";
    public string World { get; init; } = "";
}

public sealed record PolBlocked(int Index, string Name, string Key, bool AllHandles);

// one of the account's handles. ContentIds are the indexes of the Content IDs linked to it, 8 at most
public sealed record PolHandle(int Slot, string Name, string Key, int ProfilePictureId, int Order, bool Active,
                               IReadOnlyList<int> ContentIds);

// a Content ID record, type 1 is FFXI. Handle is the slot it's linked to or -1
public sealed record PolContentId(int Index, int Type, string Name, string Text, int Order, int Handle, string Key);

// State is "read" (body downloaded), "pending", "failed", "realtime" or "local" (from the game's message store, not
// on the server any more)
public sealed record PolMessage(string From, int FromSlot, int Kind, long Time, long Seq, string State,
                                string Subject, string Text, int Mid = 0, string FromKey = "")
{
    public DateTimeOffset? When => Time > 0 ? DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime() : null;

    // the in-memory ids are session-masked, this one's stable
    public string Key => $"{From}|{FromSlot}|{Seq}|{Time}";

    public bool HasBody => State is "read" or "realtime" or "local";
    public bool IsChat => Kind == 0;
    public bool IsLocal => State == "local";
    public bool IsMe { get; init; }
    // who it was addressed to (the hook's hashed POL ID + handle) and whether that's my own POL ID
    public string ToKey { get; init; } = "";
    public bool ToMe { get; init; }
    public bool Read { get; init; }
    // the group a removal or disband was about, from its attachment
    public string Group { get; init; } = "";

    public const int KindFriendRequest = 1, KindFriendAccepted = 9, KindFriendDeclined = 10;

    // 0x12 is the server's "removed from friend group", 0x13 "friend group disbanded"
    public const int KindGroupInvite = 0xe, KindGroupAccepted = 0xf, KindGroupDeclined = 0x10,
                     KindGroupRemoved = 0x12, KindGroupDisbanded = 0x13;

    // FFXI only prints a message's text when it's 300 bytes or less, in the Shift-JIS the hook sends
    public const int MaxTextBytes = 300;

    private static readonly Encoding Sjis = MakeSjis("??"), SjisOnly = MakeSjis("");

    // the hook turns what Shift-JIS hasn't got into '?', so 2 a char never lets one through. The fallback's per UTF-16
    // unit, so an emoji counts 4 in case polcore stands in for each half
    private static Encoding MakeSjis(string missing)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, new EncoderReplacementFallback(missing), DecoderFallback.ReplacementFallback);
    }

    public static int TextBytes(string text) => Sjis.GetByteCount(text.Replace("\r", ""));

    // a character Shift-JIS has as two bytes (Japanese, mostly), not one it hasn't got at all
    public static bool HasDoubleByte(string text) =>
        text.Any(c => !char.IsSurrogate(c) && SjisOnly.GetByteCount(new[] { c }) == 2);

    // never half a surrogate pair
    public static string FitBytes(string text, int bytes)
    {
        int used = 0, i = 0;
        while (i < text.Length)
        {
            int len = char.IsSurrogatePair(text, i) ? 2 : 1;
            int n = text[i] == '\r' ? 0 : Sjis.GetByteCount(text.ToCharArray(i, len));
            if (used + n > bytes) break;
            used += n;
            i += len;
        }
        return text[..i];
    }
}

public sealed record PolStoredLines(IReadOnlyList<PolMessage> Received, IReadOnlyList<PolSentLine> Sent);

// a line I sent from the game, out of its message store. State is "sent" (s\a) or "unsent" (still in its outbox)
public sealed record PolSentLine(string To, string ToKey, int ToSlot, long Time, long Seq, string State,
                                 string Subject, string Text, int Mid = 0)
{
    public string Key => $"game:{(ToKey.Length > 0 ? ToKey : To)}|{ToSlot}|{Seq}|{Time}";
    // the handle slot it went from, and whether it went to another POL ID (not one of my own handles)
    public int FromSlot { get; init; } = -1;
    public bool ToOther { get; init; }
}

// pol_hook.<pid>.social.json, rewritten every couple of secs. FfxiService is the Content ID check ("active" or
// "none"), Notice the account notice status (221..252 = LM-01..LM-32)
public sealed record PolSocialSnapshot(PolStatus MyStatus, string MyHandle, int MyProfilePictureId,
                                       IReadOnlyList<PolFriend> Friends, IReadOnlyList<PolMessage> Messages,
                                       string FfxiService = "active", int Notice = 0,
                                       string SendToken = "", string SendState = "", string Presence = "")
{
    public bool FriendsLoaded { get; init; } = true;
    public string Mail { get; init; } = "";   // "pending", "ok" or "failed"
    public bool StatusBusy { get; init; }
    // LM-02's day count (svc 0xd24), 0 when the hook didn't read one
    public int NoticeDays { get; init; }
    // viewer message 0x7007
    public bool BadPassword { get; init; }
    // chat lines, answers and group invites share the send slots, so a result can get bumped between snapshots. Time and
    // Seq are the sent message's header ones, 0 when nothing went
    public IReadOnlyList<(string Token, string State, long Time, long Seq)> SendResults { get; init; } = [];
    // for the lines that went: the handle slot they went from and whether they went to another POL ID
    public IReadOnlyList<(string Token, int FromSlot, bool ToOther)> SentFrom { get; init; } = [];
    // the lines the hook has, "sending" once its PUT's started, "queued" before
    public IReadOnlyList<(string Token, string State)> SendQueue { get; init; } = [];
    // the header time and seq of chat lines from my own POL ID the hook got back, which never reached anyone
    public IReadOnlyList<(long Time, long Seq, int FromSlot)> Returned { get; init; } = [];

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
    public FriendSearchKind SearchKind { get; init; }
    // how many POL found, which can be more than came back
    public int SearchTotal { get; init; }
    public bool SearchTooMany { get; init; }
    public string ProfileToken { get; init; } = "";
    public string ProfileState { get; init; } = "";
    public PolProfile? Profile { get; init; }
    public IReadOnlyList<PolGroup> GroupList { get; init; } = [];
    public string GroupToken { get; init; } = "";
    public string GroupState { get; init; } = "";
    public string MeToken { get; init; } = "";
    public string MeState { get; init; } = "";
    // one of my FFXI characters on the profile editor. Key is the character's, Level 0 till one's read or saved
    public string XiToken { get; init; } = "";
    public string XiState { get; init; } = "";
    public string XiKey { get; init; } = "";
    public int XiLevel { get; init; }
    public string XiName { get; init; } = "";
    public string XiWorldName { get; init; } = "";
    public string CharacterToken { get; init; } = "";
    public string CharacterState { get; init; } = "";
    public PolCharacterLookup? Character { get; init; }
    public string FfxiProfileToken { get; init; } = "";
    public string FfxiProfileState { get; init; } = "";
    public PolFfxiProfile? FfxiProfile { get; init; }
    // the hook runs one profile server job at a time, so while one's going any other just waits its turn
    public bool ProfileServerBusy => SearchState == "searching" || ProfileState == "loading" || CharacterState == "loading" ||
                                     FfxiProfileState == "loading" || MeState is "loading" or "saving" || XiState == "loading" ||
                                     ProfilePictureState == "saving" ||
                                     Chat is { ZonesState: "searching" } or { RoomsState: "searching" } ||
                                     (HandleState == "working" && HandleOp == "switch");
    // the game's message store, read once at login (pol_hook.<pid>.store.json), null when there's none
    public PolStoredLines? Stored { get; init; }
    public IReadOnlyList<PolHandle> Handles { get; init; } = [];
    public IReadOnlyList<PolContentId> ContentIds { get; init; } = [];
    public string HandleToken { get; init; } = "";
    public string HandleState { get; init; } = "";
    public string HandleOp { get; init; } = "";
    // null till chat rooms get used this session
    public ChatRoomsSnapshot? Chat { get; init; }
    public GroupChatSnapshot? GroupChat { get; init; }

    public static readonly PolSocialSnapshot Empty = new(PolStatus.AppearOffline, "", -1, [], []);
}

// the hook's per-session files. Requests get written whole (temp file then move) so the hook never sees half of one
public static class SocialSnapshot
{
    public static string PathForPid(int pid) => HookFiles.PathFor(pid, "social.json");

    public static string StorePathForPid(int pid) => HookFiles.PathFor(pid, "store.json");

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

    // the hook checks the value again and builds the query itself
    public static bool Search(int pid, string token, FriendSearchKind kind, string value) =>
        WriteRequest(pid, "search", $"{token.Replace("\n", "")}\n{FriendSearch.Wire(kind)}\n{value.Replace("\r", "").Replace("\n", "")}");

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

    public enum HandleChange { Switch, Link, Unlink }

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

    public enum Withdrawal { Withdrawn, Taken, Unknown }

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

    // not the .tmp, one a failed write left would hold every send up
    public static bool SendPending(int pid) => HookFiles.RequestPaths(pid, "send", tmp: false).Any(File.Exists);

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
            var friends = Arr(root, "friends", f => new PolFriend(Str(f, "name"),
                Str(f, "comment"), PolStatusText.Parse(Str(f, "status")), Bool(f, "online"), Bool(f, "pending"),
                Int(f, "row", -1), Int(f, "pic", -1), Str(f, "key")) { IsMe = Bool(f, "me"), Game = ReadGame(Obj(f, "game")) });
            var messages = Arr(root, "messages", ReadMessage);
            var (sendToken, sendState) = Job(root, "send");
            var (editToken, editState) = Job(root, "edit");
            var (picToken, picState) = Job(root, "pic");
            var (searchToken, searchState) = Job(root, "search");
            var search = Obj(root, "search");
            var searched = search.ValueKind == JsonValueKind.Object;
            var (profileToken, profileState) = Job(root, "profile");
            var (groupToken, groupState) = Job(root, "group");
            var (meToken, meState) = Job(root, "me");
            var (xiToken, xiState) = Job(root, "xi");
            var xi = Obj(root, "xi");
            var (charToken, charState) = Job(root, "character");
            var (ffxiToken, ffxiState) = Job(root, "ffxiprofile");
            var (handleToken, handleState) = Job(root, "handleJob");
            return new PolSocialSnapshot(PolStatusText.Parse(Str(root, "myStatus")),
                                         Str(root, "myHandle"), Int(root, "myPic", -1), friends, messages,
                                         Str(root, "ffxiService"), (int)Long(root, "notice"),
                                         sendToken, sendState, Str(root, "presence"))
            {
                FriendsLoaded = Bool(root, "friendsLoaded"),
                Mail = Str(root, "mail"),
                StatusBusy = Bool(root, "statusBusy"),
                NoticeDays = Int(root, "noticeDays", 0),
                BadPassword = Bool(root, "badPassword"),
                SendResults = Arr(root, "sends", s => (Str(s, "token"), Str(s, "state"), Long(s, "time"), Long(s, "seq"))),
                SentFrom = Arr(root, "sends", s => (Str(s, "token"), Int(s, "fromSlot", -1), Bool(s, "toOther")))
                           .Where(x => x.Item2 >= 0).ToList(),
                SendQueue = Arr(root, "queue", q => (Str(q, "token"), Str(q, "state"))),
                Returned = Arr(root, "returned", r => (Long(r, "time"), Long(r, "seq"), Int(r, "fromSlot", -1))),
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
                SearchResults = Arr(search, "results", h => new PolSearchHit((int)Long(h, "index"),
                    Str(h, "name"), (int)Long(h, "pic"), Str(h, "key"), Bool(h, "friend"))
                    { IsMe = Bool(h, "me"), Handle = Str(h, "handle"), World = Str(h, "world") }),
                SearchKind = searched ? FriendSearch.FromWire(Str(search, "kind")) : FriendSearchKind.Handle,
                SearchTotal = searched ? Int(search, "total", 0) : 0,
                SearchTooMany = searched && Bool(search, "tooMany"),
                ProfileToken = profileToken,
                ProfileState = profileState,
                Profile = profileState == "done" ? ReadProfile(Obj(root, "profile")) : null,
                GroupList = Arr(root, "groupList", ReadGroup),
                GroupToken = groupToken,
                GroupState = groupState,
                MeToken = meToken,
                MeState = meState,
                XiToken = xiToken,
                XiState = xiState,
                XiKey = xi.ValueKind == JsonValueKind.Object ? Str(xi, "key") : "",
                XiLevel = xi.ValueKind == JsonValueKind.Object ? Int(xi, "level", 0) : 0,
                XiName = xi.ValueKind == JsonValueKind.Object ? Str(xi, "name") : "",
                XiWorldName = xi.ValueKind == JsonValueKind.Object ? Str(xi, "worldName") : "",
                CharacterToken = charToken,
                CharacterState = charState,
                Character = ReadCharacter(Obj(root, "character")),
                FfxiProfileToken = ffxiToken,
                FfxiProfileState = ffxiState,
                FfxiProfile = ffxiState == "done" ? ReadFfxiProfile(Obj(root, "ffxiprofile")) : null,
                Handles = Arr(root, "handles", h => new PolHandle(Int(h, "slot", -1), Str(h, "name"), Str(h, "key"),
                    Int(h, "pic", -1), (int)Long(h, "order"), Bool(h, "active"),
                    Arr(h, "contentIds", i => i.TryGetInt32(out var n) ? n : -1))),
                ContentIds = Arr(root, "contentIds", c => new PolContentId(Int(c, "index", -1), (int)Long(c, "type"),
                    Str(c, "name"), Str(c, "text"), (int)Long(c, "order"), Int(c, "handle", -1), Str(c, "key"))),
                HandleToken = handleToken,
                HandleState = handleState,
                HandleOp = Obj(root, "handleJob") is { ValueKind: JsonValueKind.Object } hj ? Str(hj, "op") : "",
                Chat = ChatRoomsSnapshot.Read(Obj(root, "chat")),
                GroupChat = GroupChatSnapshot.Read(Obj(root, "groupChat")),
            };
        }
        catch
        {
            return PolSocialSnapshot.Empty;
        }
    }

    private static PolMessage ReadMessage(JsonElement m) =>
        new(Str(m, "from"), (int)Long(m, "fromSlot"), (int)Long(m, "kind"), Long(m, "time"), Long(m, "seq"), Str(m, "state"),
            Str(m, "subject"), Str(m, "text"), (int)Long(m, "mid"), Str(m, "fromKey"))
        {
            IsMe = Bool(m, "me"), ToKey = Str(m, "toKey"), ToMe = Bool(m, "toMe"), Read = Bool(m, "read"), Group = Str(m, "group"),
        };

    private static PolProfile ReadProfile(JsonElement p) =>
        new(Str(p, "name"), Str(p, "mail"), (int)Long(p, "mailState"), Str(p, "hid"), Long(p, "updated"),
            (int)Long(p, "level"),
            Arr(p, "rows", r => new PolProfileRow(Str(r, "label"), Str(r, "field"), (int)Long(r, "state"),
                                                  Arr(r, "lines", l => l.GetString() ?? ""), (int)Long(r, "pic"))),
            Str(p, "away").TrimEnd(), Str(p, "awayFrom"))
        {
            NameState = (int)Long(p, "nameState"),
            Characters = Arr(p, "characters", c => new PolFfxiCharacter((int)Long(c, "slot"), UInt(c, "ctsid"),
                                                                        (ulong)UInt(c, "ctidHigh") << 32 | UInt(c, "ctidLow"),
                                                                        (int)Long(c, "world"))),
        };

    // anything missing reads as private, never as a value
    private static PolFfxiProfile? ReadFfxiProfile(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        (int, int) Num(string n) => ((int)Long(p, n), Int(p, n + "State", PolProfile.Private));
        return new PolFfxiProfile(Int(p, "slot", -1), (int)Long(p, "level"), Str(p, "name").Trim(),
                                  Int(p, "nameState", PolProfile.Private), Str(p, "worldName").Trim(),
                                  Int(p, "worldNameState", PolProfile.Private))
        {
            Nation = Num("countryId"),
            Area = Num("zoneId"),
            Job = Num("jobId"),
            JobLevel = Num("jobLevel"),
            Race = Num("raceId"),
            Ctsid = UInt(p, "ctsid"),
            Ctid = (ulong)UInt(p, "ctidHigh") << 32 | UInt(p, "ctidLow"),
        };
    }

    private static PolGroup ReadGroup(JsonElement g) =>
        new(Str(g, "id"), Str(g, "name"), (int)Long(g, "myClass"), (int)Long(g, "myStatus"),
            Arr(g, "members", m => new PolGroupMember((int)Long(m, "index"), Str(m, "name"), Str(m, "key"),
                (int)Long(m, "pic"), (int)Long(m, "class"), (int)Long(m, "status"), Bool(m, "me"),
                Bool(m, "ignored"), Str(m, "text"), Str(m, "comment"))
                { Slot = Int(m, "slot", -1), Hid = Str(m, "hid"), Mine = Bool(m, "mine"), InChat = Bool(m, "inChat") }));

    private static PolFriendGame? ReadGame(JsonElement g) =>
        g.ValueKind == JsonValueKind.Object
            ? new PolFriendGame((int)Long(g, "world"), UInt(g, "ctsid"), (ulong)UInt(g, "ctidHigh") << 32 | UInt(g, "ctidLow"))
            : null;

    private static PolCharacterLookup? ReadCharacter(JsonElement c) =>
        c.ValueKind == JsonValueKind.Object
            ? new PolCharacterLookup(UInt(c, "ctsid"), (ulong)UInt(c, "ctidHigh") << 32 | UInt(c, "ctidLow"), Str(c, "name").Trim(),
                                     Int(c, "nameState", PolProfile.Private), (int)Long(c, "level"), Str(c, "worldName"))
            : null;

    private static (string Token, string State) Job(JsonElement e, string n) =>
        Obj(e, n) is { ValueKind: JsonValueKind.Object } o ? (Str(o, "token"), Str(o, "state")) : ("", "");

    public static PolStoredLines? ParseStore(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new PolStoredLines(Arr(root, "messages", ReadMessage),
                Arr(root, "gameSent", s => new PolSentLine(Str(s, "to"), Str(s, "toKey"), (int)Long(s, "toSlot"),
                    Long(s, "time"), Long(s, "seq"), Str(s, "state"), Str(s, "subject"), Str(s, "text"))
                    { FromSlot = Int(s, "fromSlot", -1), ToOther = Bool(s, "toOther") }));
        }
        catch { return null; }
    }

    // read once and taken (deleted) once it's in the chat log. The hook caps it at 3 MB
    public static PolStoredLines? TryReadStore(int pid)
    {
        var path = StorePathForPid(pid);
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 8 * 1024 * 1024) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return ParseStore(sr.ReadToEnd());
        }
        catch { return null; }
    }

    public static void DeleteStore(int pid)
    {
        try { File.Delete(StorePathForPid(pid)); } catch { }
    }

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
