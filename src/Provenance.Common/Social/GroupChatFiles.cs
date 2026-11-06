using System.Text.Json;
using Provenance.Common.Hook;
using static Provenance.Common.JsonFields;

namespace Provenance.Common.Social;

public sealed record GroupChatLine(long Seq, long Time, int From, bool Me, string Name, string Text);

public sealed record GroupChatLines(string Group, IReadOnlyList<GroupChatLine> Lines)
{
    public List<GroupChatLine> After(long seq) => Lines.Where(l => l.Seq > seq).OrderBy(l => l.Seq).ToList();
}

// the snapshot's "groupChat". States run "joining" -> "in" -> "leaving" -> "left", or "failed:...", "left:handle" (a
// handle switch took it out), "gone" (the group went), "removed" (I was taken out of it) or "lost"
public sealed record GroupChatSnapshot(string Token, string State, string Group, long LineSeq, string SendToken, string SendState)
{
    public static GroupChatSnapshot? Read(JsonElement g)
    {
        if (g.ValueKind != JsonValueKind.Object) return null;
        var s = Obj(g, "send");
        bool hasSend = s.ValueKind == JsonValueKind.Object;
        return new GroupChatSnapshot(Str(g, "token"), Str(g, "state"), Str(g, "group"), Long(g, "lineSeq"),
                                     hasSend ? Str(s, "token") : "", hasSend ? Str(s, "state") : "");
    }
}

// the hook's group chat files, one group's chat at a time like the viewer
public static class GroupChatFiles
{
    // the IRC line polcore builds has to fit 0x200 bytes, past this it drops the line without a word
    public const int MaxText = 174;

    public static bool Join(int pid, string token, string group, int slot, string hid) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.GroupChatJoin, token, group, slot, hid);

    public static bool Send(int pid, string token, string text) =>
        HookFiles.WriteRequest(pid, HookContract.Requests.GroupChatSend, token, text);

    // join is an untaken join this calls off, when the app couldn't withdraw it
    public static bool Leave(int pid, string token, string join = "") =>
        HookFiles.WriteRequest(pid, HookContract.Requests.GroupChatLeave, token, join);

    public static string LinesPathForPid(int pid) => HookFiles.PathFor(pid, HookContract.Files.GroupChat);

    public static GroupChatLines? ParseLines(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new GroupChatLines(Str(root, "group"), Arr(root, "lines", l => new GroupChatLine(Long(l, "seq"), Long(l, "time"),
                Int(l, "from", -1), Bool(l, "me"), Str(l, "name"), Str(l, "text"))));
        }
        catch { return null; }
    }

    public static GroupChatLines? TryReadLines(int pid) =>
        HookFiles.ReadJson(pid, HookContract.Files.GroupChat, HookFiles.MaxBytes.GroupChat, ParseLines);
}
