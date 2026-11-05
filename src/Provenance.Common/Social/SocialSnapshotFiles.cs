using System.Text.Json;
using Provenance.Common.Hook;
using static Provenance.Common.JsonFields;

namespace Provenance.Common.Social;

public static class SocialSnapshotFiles
{
    public static string PathForPid(int pid) => HookFiles.PathFor(pid, "social.json");

    public static string StorePathForPid(int pid) => HookFiles.PathFor(pid, "store.json");

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
