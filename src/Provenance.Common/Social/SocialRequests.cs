using System.Text;
using System.Text.Json;
using Provenance.Common.Hook;
using static Provenance.Common.JsonFields;

namespace Provenance.Common.Social;

// the hook's per-session files. Requests get written whole (temp file then move) so the hook never sees half of one
public static class SocialRequests
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
