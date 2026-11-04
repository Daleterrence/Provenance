using System.Text;
using System.Text.Json;
using static Provenance.Common.JsonFields;

namespace Provenance.Common.Social;

// a zone in the chat room picker. Group is the zone group ("PlayOnline Lounge"), People is everyone in its rooms
public sealed record ChatZone(int Code, string Group, string Name, int Rooms, int People);

// a room category from the room list's own table: Group is "Target Members", "Purpose" or "Main Language"
public sealed record ChatCategory(int Code, string Group, string Name);

// one row of the room list. Irc is the name the hook joins, Name what people see, Created is Unix seconds
public sealed record ChatRoomEntry(string Irc, string Name, bool Locked, int People, int Capacity,
                                   IReadOnlyList<int> Categories, long Created);

// Key only means something inside the one room, the hook never hands out ids
public sealed record ChatMember(int Key, string Name, bool Me, bool Master, bool Away);

// Kind is say, entered, left, away, back, master, kicked or whisper. From is the member key, 0 for my own lines.
// Colour is the line's colour character as a number (-1 when it was 'z'), Emote its emoticon (0 for none)
public sealed record ChatLine(long Seq, long Time, string Kind, int From, bool Me, string Name, string Text)
{
    public DateTimeOffset When => DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime();
    public int Colour { get; init; } = -1;
    public int Emote { get; init; }
}

// the viewer's text colours and emoticons. Its colour table (app.dll 0x10495828) is ARGB on a light window
public static class ChatLook
{
    public const int Emotes = 23, Colours = 12;

    private static readonly uint[] Table =
    [
        0xff101010, 0xff101010, 0xff101010, 0xffa02048, 0xff101010, 0xff101010, 0xff000079, 0xff103060, 0xff027aa3,
        0xff2b6e1f, 0xff003800, 0xff4d0808, 0xff800080, 0xffb6427c, 0xff9d2a04, 0xff808000, 0xff474747,
    ];

    // the colour a line's char stands for. Past the table (17-23) or 'z' the viewer reads whatever's there, so it's
    // the default here, which is null
    public static uint? Argb(int colourChar) =>
        colourChar is >= 0 and < 17 && Table[colourChar] != Table[0] ? Table[colourChar] : null;

    // the picker's 12, each the char 5 on from its place (app.dll 0x100984df)
    public static uint PickerArgb(int pick) => Table[pick + 5];

    private static readonly string[] Names =
        ["Black", "Navy", "Dark blue", "Blue", "Green", "Dark green", "Maroon", "Purple", "Pink", "Rust", "Olive", "Grey"];

    public static string ColourName(int pick) => pick >= 0 && pick < Names.Length ? Names[pick] : "No colour";

    // the one a received line shows: the viewer only draws a face for 1 or more off its first sheet
    public static int Emote(int e) => e is >= 1 and <= Emotes ? e : 0;

    // the room list's badge, from the room's language category (app.dll FUN_1008fc68, strings 0x4e90-0x4e93)
    public static string Language(IReadOnlyList<int> categories) => categories.Count > 2 ? categories[2] switch
    {
        300 => "JP",
        301 => "EN",
        302 => "FR",
        303 => "GE",
        _ => "",
    } : "";
}

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

// pol_hook.<pid>.chatroom.json, the open room's last 256 lines
public sealed record ChatRoomLines(string Room, IReadOnlyList<ChatLine> Lines);

// the hook's chat room requests and its lines file. Requests are written whole like the others
public static class ChatRoomFiles
{
    // the hook's own limits: 15 Shift-JIS bytes for a chat name, 23 characters of password, 300 bytes of text. A new
    // room's from the viewer's form: 30 characters of name, 16 letters and numbers of password, 2-20 people
    public const int MaxNameBytes = 15, MaxPasswordLength = 23, MaxTextBytes = 300, PageSize = 100;
    public const int MaxRoomName = 30, MaxNewPassword = 16, MinOccupancy = 2, MaxOccupancy = 20, DefaultOccupancy = 10;

    public static string LinesPathForPid(int pid) => HookFiles.PathFor(pid, "chatroom.json");

    public static void Delete(int pid)
    {
        try { File.Delete(LinesPathForPid(pid)); } catch { }
    }

    // the requests that carry a room, a chat name or a password entry's token, never left about once the session's gone
    public static void DeleteRequests(int pid)
    {
        foreach (var ext in new[] { "chatjoin", "chatcreate", "chatmaster" })
            foreach (var tail in new[] { "", ".taking", ".taking2", ".tmp" })
                try { File.Delete(HookFiles.PathFor(pid, ext) + tail); } catch { }
    }

    public static bool Zones(int pid, string token) => Write(pid, "chatzones", OneLine(token));

    public static bool Rooms(int pid, string token, int zone, int offset) =>
        Write(pid, "chatrooms", $"{OneLine(token)}\n{zone}\n{offset}");

    // a locked room's password never goes in here, the app hands it over as a one-shot Credential Manager entry
    public static bool Join(int pid, string token, string irc, bool hasPassword, string chatName) =>
        Write(pid, "chatjoin", $"{OneLine(token)}\n{OneLine(irc)}\n{(hasPassword ? "1" : "")}\n{OneLine(chatName)}");

    public static bool Send(int pid, string token, string text, int colour = -1, int emote = 0) =>
        Write(pid, "chatsend", $"{OneLine(token)}\n{OneLine(text)}\n{colour}\n{emote}");

    // a new room. The password goes the same way an enter's does, never in here
    public static bool Create(int pid, string token, bool hasPassword, int zone, int occupancy, int members, int purpose,
                              int language, string chatName, string roomName) =>
        Write(pid, "chatcreate", $"{OneLine(token)}\n{(hasPassword ? "1" : "")}\n{zone}\n{occupancy}\n{members}\n{purpose}\n" +
                                 $"{language}\n{OneLine(chatName)}\n{OneLine(roomName)}");

    public enum MasterOp { Kick, Master, Password }

    // the room master's: kick or designate a member (by the room's key), or a new password (none turns it off)
    public static bool Master(int pid, string token, MasterOp op, int memberKey = 0, bool hasPassword = false) =>
        Write(pid, "chatmaster", $"{OneLine(token)}\n{(hasPassword ? "1" : "")}\n" +
                                 $"{op switch { MasterOp.Kick => "kick", MasterOp.Master => "master", _ => "password" }}\n{memberKey}");

    // joinToken calls off an enter the app couldn't withdraw, should the hook take it after this
    public static bool Leave(int pid, string token, string joinToken = "") =>
        Write(pid, "chatleave", joinToken.Length > 0 ? $"{OneLine(token)}\n{OneLine(joinToken)}" : OneLine(token));

    // what the hook would refuse anyway
    public static string? NameProblem(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Enter chat name."
        : name.Any(c => c < ' ' || c == '\t') ? "That chat name can't be used."
        : PolMessage.TextBytes(name) > MaxNameBytes ? "Chat names can be up to 15 characters (Japanese characters count as two)."
        : null;

    public static string? RoomNameProblem(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Room name is not set. Cannot create room."
        : name.Length > MaxRoomName ? "Room names can be up to 30 characters."
        : name.Any(c => c < ' ') || !ShiftJis.Fits(name) ? "The entered room name cannot be used. Please enter a different name."
        : null;

    // the viewer's new password box takes letters and numbers, 16 at most
    public static string? NewPasswordProblem(string password) =>
        password.Length == 0 ? "Room password is not set. Cannot create room."
        : password.Length > MaxNewPassword || !password.All(char.IsAsciiLetterOrDigit) ? "Room passwords are up to 16 letters and numbers."
        : null;

    public static string? PasswordProblem(string password) =>
        password.Length == 0 ? "Enter password."
        : password.Length > MaxPasswordLength ? "That password is too long."
        : password.Any(c => c <= ' ' || c >= 0x7f || c == ',') ? "Passwords can only have letters, numbers and symbols, no spaces or commas."
        : null;

    public static ChatRoomLines? ParseLines(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new ChatRoomLines(Str(root, "room"), Arr(root, "lines", l => new ChatLine(Long(l, "seq"), Long(l, "time"),
                Str(l, "kind"), Int(l, "from", 0), Bool(l, "me"), Str(l, "name"), Str(l, "text"))
                { Colour = Int(l, "colour", -1), Emote = Int(l, "emote", 0) }));
        }
        catch { return null; }
    }

    public static ChatRoomLines? TryReadLines(int pid)
    {
        var path = LinesPathForPid(pid);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return ParseLines(sr.ReadToEnd());
        }
        catch { return null; }
    }

    private static string OneLine(string s) => new(s.Replace("\r", "").Select(c => c < ' ' ? ' ' : c).ToArray());

    private static bool Write(int pid, string ext, string body)
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
}

// the app's half of the hook's send rule: one line in flight, and never two inside a second
public sealed class ChatSendGate
{
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(1);
    // the hook gives up on an ack after 120 s, a little past that the line counts as gone either way
    public static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(125);

    private DateTime? _sentAt;
    public string? Token { get; private set; }

    public bool CanSend(DateTime now) => Token == null && (_sentAt is not { } at || now - at >= Gap);

    public void Sent(string token, DateTime now)
    {
        Token = token;
        _sentAt = now;
    }

    // the hook's state for my token, or null while it's still going. A token it never took counts after GiveUp
    public string? Settle(string? hookToken, string? hookState, DateTime now)
    {
        if (Token == null) return null;
        string? done = null;
        if (hookToken == Token && hookState is { } s &&
            (s == "sent" || s.StartsWith("sent:", StringComparison.Ordinal) || s.StartsWith("failed", StringComparison.Ordinal)))
            done = s;
        else if (_sentAt is { } at && now - at > GiveUp) done = "failed:timeout";
        if (done != null) Token = null;
        return done;
    }

    public void Reset()
    {
        Token = null;
        _sentAt = null;
    }
}
