using System.Text.Json;
using static Provenance.Common.JsonFields;

namespace Provenance.Common.Social;

// a zone in the chat room picker. Group is the zone group it's listed under, People is everyone in its rooms
public sealed record ChatZone(int Code, string Group, string Name, int Rooms, int People);

// a room category from the room list's own table: Group is "Target Members", "Purpose" or "Main Language"
public sealed record ChatCategory(int Code, string Group, string Name);

// one row of the room list. Irc is the name the hook joins, Name what people see, Created is Unix seconds
public sealed record ChatRoomEntry(string Irc, string Name, bool Locked, int People, int Capacity,
                                   IReadOnlyList<int> Categories, long Created);

// Key only means something inside the one room, the hook never hands out ids
public sealed record ChatMember(int Key, string Name, bool Me, bool Master, bool Away);

// the snapshot's "chat", there once chat's been used this session. States run "searching" -> "done" or "failed:...",
// and the room's "entering" -> "in" -> "left", or "failed:...", "kicked", "gone" (the room went without a kick) or "lost"
public sealed record ChatRoomsSnapshot(long LineSeq,
                                       string ZonesToken, string ZonesState, IReadOnlyList<ChatZone> Zones,
                                       IReadOnlyList<ChatCategory> Categories,
                                       string RoomsToken, string RoomsState, int Zone, int Offset, int Total,
                                       IReadOnlyList<ChatRoomEntry> Rooms,
                                       string RoomToken, string RoomState, string RoomIrc, int Limit, bool Locked,
                                       IReadOnlyList<ChatMember> Members,
                                       string SendToken, string SendState)
{
    public string MasterToken { get; init; } = "";
    public string MasterState { get; init; } = "";

    public static ChatRoomsSnapshot? Read(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object) return null;
        var z = Obj(c, "zones");
        var r = Obj(c, "rooms");
        var room = Obj(c, "room");
        var send = Obj(c, "send");
        return new ChatRoomsSnapshot(Long(c, "lineSeq"),
            Str(z, "token"), Str(z, "state"),
            Arr(z, "list", x => new ChatZone(Int(x, "code", 0), Str(x, "group"), Str(x, "name"), Int(x, "rooms", 0), Int(x, "people", 0))),
            Arr(c, "categories", x => new ChatCategory(Int(x, "code", 0), Str(x, "group"), Str(x, "name"))),
            Str(r, "token"), Str(r, "state"), Int(r, "zone", 0), Int(r, "offset", 0), Int(r, "total", 0),
            Arr(r, "list", x => new ChatRoomEntry(Str(x, "irc"), Str(x, "name"), Bool(x, "locked"),
                Int(x, "people", 0), Int(x, "capacity", 0), Arr(x, "categories", n => n.TryGetInt32(out var v) ? v : 0),
                Long(x, "created"))),
            Str(room, "token"), Str(room, "state"), Str(room, "irc"), Int(room, "limit", 0), Bool(room, "locked"),
            Arr(room, "members", m => new ChatMember(Int(m, "key", 0), Str(m, "name"), Bool(m, "me"), Bool(m, "master"), Bool(m, "away"))),
            Str(send, "token"), Str(send, "state"))
        {
            MasterToken = Obj(c, "master") is { ValueKind: JsonValueKind.Object } m ? Str(m, "token") : "",
            MasterState = Obj(c, "master") is { ValueKind: JsonValueKind.Object } m2 ? Str(m2, "state") : "",
        };
    }
}
