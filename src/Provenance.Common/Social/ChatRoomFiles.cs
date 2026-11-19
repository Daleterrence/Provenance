using System.Text.Json;
using Provenance.Common.Hook;
using Provenance.Common.PolFormat;
using static Provenance.Common.JsonFields;

namespace Provenance.Common.Social;

public sealed record ChatLine(long Seq, long Time, string Kind, int From, bool Me, string Name, string Text)
{
    public DateTimeOffset When => DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime();
    public int Colour { get; init; } = -1;
    public int Emote { get; init; }
}

public sealed record ChatRoomLines(string Room, IReadOnlyList<ChatLine> Lines);

public static class ChatRoomFiles
{
    // the hook's limits: chat name 15 Shift-JIS bytes, password 23 chars, text 400
    public const int MaxNameBytes = 15, MaxPasswordLength = 23, MaxTextLength = 400, PageSize = 100;
    public const int MaxRoomName = 30, MaxNewPassword = 16, MinOccupancy = 2, MaxOccupancy = 20, DefaultOccupancy = 10;

    // polcore's line is 0x200 bytes, minus what goes around the text (as Shift-JIS):
    //   "PRIVMSG <room> :", checksum and line end   20 + the room's name
    //   type, emoticon and colour                   6
    //   chat name, a tab and the language           name + 3
    public static int MaxTextBytes(string room, string chatName) =>
        0x200 - (20 + room.Length) - (9 + ShiftJis.ByteCount(chatName));

    public static string LinesPathForPid(int pid) => HookFiles.PathFor(pid, HookContract.Files.ChatRoom);

    public static bool Zones(int pid, string token) => HookFiles.WriteRequest(pid, HookContract.Requests.ChatZones, token);

    public static bool Rooms(int pid, string token, int zone, int offset) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.ChatRooms, token, zone, offset);

    public static bool Join(int pid, string token, string irc, bool hasPassword, string chatName) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.ChatJoin, token, irc, hasPassword ? "1" : "", chatName);

    public static bool Send(int pid, string token, string text, int colour = -1, int emote = 0) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.ChatSend, token, text, colour, emote);

    public static bool Create(int pid, string token, bool hasPassword, int zone, int occupancy, int members, int purpose,
                              int language, string chatName, string roomName) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.ChatCreate, token, hasPassword ? "1" : "", zone, occupancy, members,
                               purpose, language, chatName, roomName);

    public enum MasterOp { Kick, Master, Password }

    public static bool Master(int pid, string token, MasterOp op, int memberKey = 0, bool hasPassword = false) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.ChatMaster, token, hasPassword ? "1" : "",
                               op switch { MasterOp.Kick => "kick", MasterOp.Master => "master", _ => "password" }, memberKey);

    public static bool Leave(int pid, string token, string joinToken = "") =>
        joinToken.Length > 0 ? HookFiles.WriteRequest(pid, HookContract.Requests.ChatLeave, token, joinToken)
                             : HookFiles.WriteRequest(pid, HookContract.Requests.ChatLeave, token);

    public static string? NameProblem(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Enter chat name."
        : name.Any(c => c < ' ') ? "That chat name can't be used."
        : ShiftJis.ByteCount(name) > MaxNameBytes ? "Chat names can be up to 15 characters (Japanese characters count as two)."
        : null;

    public static string? RoomNameProblem(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Room name is not set. Cannot create room."
        : name.Length > MaxRoomName ? "Room names can be up to 30 characters."
        : name.Any(c => c < ' ') || !ShiftJis.Fits(name) ? HookFailures.ChatRooms.BadName
        : null;

    public static string? NewPasswordProblem(string password) =>
        password.Length == 0 ? "Room password is not set. Cannot create room."
        : password.Length > MaxNewPassword || !password.All(char.IsAsciiLetterOrDigit) ? "Room passwords are up to 16 letters and numbers."
        : null;

    public const string ConfirmMismatch = "The password entered in Confirm password does not match.";

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

    public static ChatRoomLines? TryReadLines(int pid) =>
        HookFiles.ReadJson(pid, HookContract.Files.ChatRoom, HookFiles.MaxBytes.ChatRoom, ParseLines);
}
