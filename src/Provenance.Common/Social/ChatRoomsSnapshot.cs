using System.Text.Json;
using Provenance.Common.Hook;
using static Provenance.Common.JsonFields;

namespace Provenance.Common.Social;

public sealed record ChatZone(int Code, string Group, string Name, int Rooms, int People);

// Group is "Target Members", "Purpose" or "Main Language"
public sealed record ChatCategory(int Code, string Group, string Name);

// Irc is the name the hook joins, Created is Unix seconds
public sealed record ChatRoomEntry(string Irc, string Name, bool Locked, int People, int Capacity,
                                   IReadOnlyList<int> Categories, long Created);

// Key only means something inside the one room, the hook never hands out ids
public sealed record ChatMember(int Key, string Name, bool Me, bool Master, bool Away);

// the snapshot's "chat", once chat's been used. States:
//   search  "searching" -> "done" or "failed:..."
//   room    "entering" -> "in" -> "left", or "failed:...", "kicked", "gone" (went without a kick), "lost"
public sealed record ChatRoomsSnapshot(long LineSeq,
                                       HookJob ZonesJob, IReadOnlyList<ChatZone> Zones,
                                       IReadOnlyList<ChatCategory> Categories,
                                       HookJob RoomsJob, int Zone, int Offset, int Total,
                                       IReadOnlyList<ChatRoomEntry> Rooms,
                                       HookJob RoomJob, string RoomIrc, int Limit, bool Locked,
                                       IReadOnlyList<ChatMember> Members,
                                       HookJob SendJob)
{
    public HookJob MasterJob { get; init; } = HookJob.None;

    public static ChatRoomsSnapshot? Read(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object) return null;
        var r = Obj(c, "rooms");
        var room = Obj(c, "room");
        return new ChatRoomsSnapshot(Long(c, "lineSeq"),
            Job(c, "zones"),
            Arr(Obj(c, "zones"), "list", x => new ChatZone(Int(x, "code", 0), Str(x, "group"), Str(x, "name"), Int(x, "rooms", 0), Int(x, "people", 0))),
            Arr(c, "categories", x => new ChatCategory(Int(x, "code", 0), Str(x, "group"), Str(x, "name"))),
            Job(c, "rooms"), Int(r, "zone", 0), Int(r, "offset", 0), Int(r, "total", 0),
            Arr(r, "list", x => new ChatRoomEntry(Str(x, "irc"), Str(x, "name"), Bool(x, "locked"),
                Int(x, "people", 0), Int(x, "capacity", 0), Arr(x, "categories", n => n.TryGetInt32(out var v) ? v : 0),
                Long(x, "created"))),
            Job(c, "room"), Str(room, "irc"), Int(room, "limit", 0), Bool(room, "locked"),
            Arr(room, "members", m => new ChatMember(Int(m, "key", 0), Str(m, "name"), Bool(m, "me"), Bool(m, "master"), Bool(m, "away"))),
            Job(c, "send"))
        {
            MasterJob = Job(c, "master"),
        };
    }
}
