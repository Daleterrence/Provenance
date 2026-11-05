using System.Text;
using System.Text.Json;
using static Provenance.Common.JsonFields;

namespace Provenance.Common.Social;

// Kind: say, entered, left, away, back, master, kicked or whisper. From's the member key (0 mine), Colour the colour
// char as a number (-1 for 'z'), Emote 0 for none
public sealed record ChatLine(long Seq, long Time, string Kind, int From, bool Me, string Name, string Text)
{
    public DateTimeOffset When => DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime();
    public int Colour { get; init; } = -1;
    public int Emote { get; init; }
}

// pol_hook.<pid>.chatroom.json, the open room's last 256 lines
public sealed record ChatRoomLines(string Room, IReadOnlyList<ChatLine> Lines);

// the hook's chat room requests and its lines file. Requests are written whole like the others
public static class ChatRoomFiles
{
    // the hook's limits: chat name 15 Shift-JIS bytes, password 23 chars, text 400 (the viewer's box). The viewer's new
    // room form: name 30, password 16 letters and numbers, 2-20 people
    public const int MaxNameBytes = 15, MaxPasswordLength = 23, MaxTextLength = 400, PageSize = 100;
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
            HookFiles.DeleteRequest(pid, ext);
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
