using System.Text.Json;
using Provenance.Common.Hook;
using static Provenance.Common.JsonFields;
using Provenance.Common.IO;

namespace Provenance.Common.Social;

public static class SocialSnapshotFiles
{
    public static string PathForPid(int pid) => HookFiles.PathFor(pid, HookContract.Files.Social);

    public static string StorePathForPid(int pid) => HookFiles.PathFor(pid, HookContract.Files.Store);

    public static PolSocialSnapshot? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var friends = Arr(root, "friends", f => new PolFriend(Str(f, "name"),
                Str(f, "comment"), PolStatusText.Parse(Str(f, "status")), Bool(f, "online"), Bool(f, "pending"),
                Int(f, "row", -1), Int(f, "pic", -1), Str(f, "key")) { IsMe = Bool(f, "me"), Game = ReadGame(Obj(f, "game")) });
            var messages = Arr(root, "messages", ReadMessage);
            var search = Obj(root, "search");
            var searched = search.ValueKind == JsonValueKind.Object;
            var profileJob = Job(root, "profile");
            var ffxiJob = Job(root, "ffxiprofile");
            return new PolSocialSnapshot(PolStatusText.Parse(Str(root, "myStatus")),
                                         Str(root, "myHandle"), Int(root, "myPic", -1), friends, messages,
                                         Str(root, "ffxiService"), (int)Long(root, "notice"),
                                         Str(root, "presence"))
            {
                SendJob = Job(root, "send"),
                FriendsLoaded = Bool(root, "friendsLoaded"),
                Sampler = Str(root, "sampler"),
                GameWrites = Obj(root, "gameWrites") is { ValueKind: JsonValueKind.Object } gw
                    ? new GameWrites(Bool(gw, "send"), Bool(gw, "status"), Bool(gw, "edits")) : null,
                GameSent = Arr(root, "gameSent", ReadSent),
                GameFriends = Bool(root, "gameFriends"),
                Beat = Int(root, "beat", 0),
                Lean = Bool(root, "lean"),
                Mail = Str(root, "mail"),
                StatusBusy = Bool(root, "statusBusy"),
                NoticeDays = Int(root, "noticeDays", 0),
                BadPassword = Bool(root, "badPassword"),
                SendResults = Arr(root, "sends", s => new PolSendResult(Str(s, "token"), Str(s, "state"), Long(s, "time"),
                    Long(s, "seq"), Int(s, "fromSlot", -1), Bool(s, "toOther"))),
                SendQueue = Arr(root, "queue", q => new PolQueuedSend(Str(q, "token"), Str(q, "state"))),
                Returned = Arr(root, "returned", r => new PolReturnedSend(Long(r, "time"), Long(r, "seq"), Int(r, "fromSlot", -1))),
                EditJob = Job(root, "edit"),
                MyComment = Str(root, "myComment").Trim(),
                MyKey = Str(root, "myKey"),
                Blocked = Arr(root, "blocked", x =>
                    new PolBlocked((int)Long(x, "index"), Str(x, "name"), Str(x, "key"), Bool(x, "allHandles"))),
                PictureJob = Job(root, "pic"),
                SearchJob = Job(root, "search"),
                Search = new SearchResult(searched ? FriendSearch.FromWire(Str(search, "kind")) : FriendSearchKind.Handle,
                    searched ? Int(search, "total", 0) : 0, searched && Bool(search, "tooMany"),
                    Arr(search, "results", h => new PolSearchHit((int)Long(h, "index"),
                        Str(h, "name"), (int)Long(h, "pic"), Str(h, "key"), Bool(h, "friend"))
                        { IsMe = Bool(h, "me"), Handle = Str(h, "handle"), World = Str(h, "world") })),
                ProfileJob = profileJob,
                Profile = profileJob.State == "done" ? ReadProfile(Obj(root, "profile")) : null,
                GroupList = Arr(root, "groupList", ReadGroup),
                GroupJob = Job(root, "group"),
                MeJob = Job(root, "me"),
                CharacterJob = Job(root, "character"),
                Character = ReadCharacter(Obj(root, "character")),
                FfxiProfileJob = ffxiJob,
                FfxiProfile = ffxiJob.State == "done" ? ReadFfxiProfile(Obj(root, "ffxiprofile")) : null,
                Handles = Arr(root, "handles", h => new PolHandle(Int(h, "slot", -1), Str(h, "name"), Str(h, "key"),
                    Int(h, "pic", -1), (int)Long(h, "order"), Bool(h, "active"),
                    Arr(h, "contentIds", i => i.TryGetInt32(out var n) ? n : -1))),
                ContentIds = Arr(root, "contentIds", c => new PolContentId(Int(c, "index", -1), (ContentIdType)Long(c, "type"),
                    Str(c, "name"), Str(c, "text"), (int)Long(c, "order"), Int(c, "handle", -1), Str(c, "key"))),
                HandleJob = Job(root, "handleJob"),
                HandleOp = Obj(root, "handleJob") is { ValueKind: JsonValueKind.Object } hj ? Str(hj, "op") : "",
                HandleUnconfirmed = Bool(Obj(root, "handleJob"), "unconfirmed"),
                Chat = ChatRoomsSnapshot.Read(Obj(root, "chat")),
                GroupChat = GroupChatSnapshot.Read(Obj(root, "groupChat")),
            };
        }
        catch { return null; }
    }

    private static PolSentLine ReadSent(JsonElement s) =>
        new(Str(s, "to"), Str(s, "toKey"), (int)Long(s, "toSlot"), Long(s, "time"), Long(s, "seq"), Str(s, "state"),
            Str(s, "subject"), Str(s, "text")) { FromSlot = Int(s, "fromSlot", -1), ToOther = Bool(s, "toOther") };

    private static PolMessage ReadMessage(JsonElement m) =>
        new(Str(m, "from"), (int)Long(m, "fromSlot"), (int)Long(m, "kind"), Long(m, "time"), Long(m, "seq"), Str(m, "state"),
            Str(m, "subject"), Str(m, "text"), (int)Long(m, "mid"), Str(m, "fromKey"))
        {
            IsMe = Bool(m, "me"), ToKey = Str(m, "toKey"), ToMe = Bool(m, "toMe"), Read = Bool(m, "read"), Ignored = Bool(m, "ignored"), Group = Str(m, "group"),
        };

    private static PolProfile ReadProfile(JsonElement p) =>
        new(Str(p, "name"), Str(p, "mail"), (ProfileFieldState)Long(p, "mailState"), Str(p, "hid"), Long(p, "updated"),
            (PrivacyLevel)Long(p, "level"),
            Arr(p, "rows", r => new PolProfileRow(Str(r, "label"), Str(r, "field"), (ProfileFieldState)Long(r, "state"),
                                                  Arr(r, "lines", l => l.GetString() ?? ""), (int)Long(r, "pic"))),
            Str(p, "away").TrimEnd(), Str(p, "awayFrom"))
        {
            NameState = (ProfileFieldState)Long(p, "nameState"),
            Characters = Arr(p, "characters", c => new PolFfxiCharacter((int)Long(c, "slot"), ReadCharacterId(c),
                                                                        (int)Long(c, "world"))),
        };

    private static PolFfxiProfile? ReadFfxiProfile(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        (int, ProfileFieldState) Num(string n) => ((int)Long(p, n), FieldState(p, n + "State"));
        return new PolFfxiProfile(Int(p, "slot", -1), (PrivacyLevel)Long(p, "level"), Str(p, "name").Trim(),
                                  FieldState(p, "nameState"), Str(p, "worldName").Trim(), FieldState(p, "worldNameState"))
        {
            Nation = Num("countryId"),
            Area = Num("zoneId"),
            Job = Num("jobId"),
            JobLevel = Num("jobLevel"),
            Race = Num("raceId"),
            Character = ReadCharacterId(p),
        };
    }

    private static PolGroup ReadGroup(JsonElement g) =>
        new(Str(g, "id"), Str(g, "name"), (GroupRole)Long(g, "myClass"), (MemberStatus)Long(g, "myStatus"),
            Arr(g, "members", m => new PolGroupMember((int)Long(m, "index"), Str(m, "name"), Str(m, "key"),
                (int)Long(m, "pic"), (GroupRole)Long(m, "class"), (MemberStatus)Long(m, "status"), Bool(m, "me"),
                Bool(m, "ignored"), Str(m, "text"), Str(m, "comment"))
                { Slot = Int(m, "slot", -1), Hid = Str(m, "hid"), Mine = Bool(m, "mine"), InChat = Bool(m, "inChat") }));

    private static ProfileFieldState FieldState(JsonElement e, string n) =>
        (ProfileFieldState)Int(e, n, (int)ProfileFieldState.Private);

    private static CharacterId ReadCharacterId(JsonElement e) =>
        new(UInt(e, "ctsid"), (ulong)UInt(e, "ctidHigh") << 32 | UInt(e, "ctidLow"));

    private static PolFriendGame? ReadGame(JsonElement g) =>
        g.ValueKind == JsonValueKind.Object
            ? new PolFriendGame((int)Long(g, "world"), ReadCharacterId(g))
            : null;

    private static PolCharacterLookup? ReadCharacter(JsonElement c) =>
        c.ValueKind == JsonValueKind.Object
            ? new PolCharacterLookup(ReadCharacterId(c), Str(c, "name").Trim(),
                                     FieldState(c, "nameState"), (PrivacyLevel)Long(c, "level"), Str(c, "worldName"),
                                     (int)Long(c, "zoneId"), FieldState(c, "zoneIdState"))
            : null;

    public static PolStoredLines? ParseStore(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new PolStoredLines(Arr(root, "messages", ReadMessage), Arr(root, "gameSent", ReadSent));
        }
        catch { return null; }
    }

    public static PolStoredLines? TryReadStore(int pid) =>
        HookFiles.ReadJson(pid, HookContract.Files.Store, HookFiles.MaxBytes.Store, ParseStore);

    public static void DeleteStore(int pid)
    {
        FileDelete.Try(StorePathForPid(pid));
    }

    public static PolSocialSnapshot? TryRead(int pid) =>
        HookFiles.ReadJson(pid, HookContract.Files.Social, HookFiles.MaxBytes.Social, Parse);
}
