using System.Text;
using System.Text.Json;
using Provenance.Common.Credentials;
using static Provenance.Common.JsonFields;

namespace Provenance.Common.Social;

public sealed record GroupChatLine(long Seq, long Time, int From, bool Me, string Name, string Text);

public sealed record GroupChatLines(string Group, IReadOnlyList<GroupChatLine> Lines);

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
        Write(pid, "groupchatjoin", $"{OneLine(token)}\n{OneLine(group)}\n{slot}\n{OneLine(hid)}");

    public static bool Send(int pid, string token, string text) => Write(pid, "groupchatsend", $"{OneLine(token)}\n{OneLine(text)}");

    // join is an untaken join this calls off, when the app couldn't withdraw it
    public static bool Leave(int pid, string token, string join = "") => Write(pid, "groupchatleave", $"{OneLine(token)}\n{OneLine(join)}");

    public static string LinesPathForPid(int pid) => HookFiles.PathFor(pid, "groupchat.json");

    public static void Delete(int pid)
    {
        try { File.Delete(LinesPathForPid(pid)); } catch { }
        foreach (var ext in new[] { "groupchatjoin", "groupchatsend", "groupchatleave" })
            HookFiles.DeleteRequest(pid, ext);
    }

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

    public static GroupChatLines? TryReadLines(int pid)
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

public sealed record GroupChatEntry(string Id, long Time, string Name, string Text, bool Me)
{
    public DateTimeOffset When => DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime();
}

// every group's chat lines kept on this PC, so a chat opened again (or after a restart) shows what came before. It's
// only ever Provenance's own copy, POL keeps none
public sealed class GroupChatLog
{
    public const int KeepPerGroup = 1000;

    private readonly string? _path;
    private readonly Dictionary<string, List<GroupChatEntry>> _groups = new(StringComparer.OrdinalIgnoreCase);

    // format constant, the same deal as the message log's
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("Provenance PlayOnline group chat log v1");

    public GroupChatLog(string profileId, string? root = null)
    {
        if (root is null)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData)) return;
            root = Path.Combine(appData, "Provenance", "messages");
        }
        _path = Path.Combine(root, profileId + ".groups.dat");
        if (!File.Exists(_path)) return;
        try
        {
            var plain = Dpapi.Unprotect(File.ReadAllBytes(_path), Entropy);
            if (plain != null && JsonSerializer.Deserialize<Dictionary<string, List<GroupChatEntry>>>(plain) is { } loaded)
            {
                foreach (var (k, v) in loaded) _groups[k] = v;
                return;
            }
        }
        catch { }
        try { File.Move(_path, _path + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".unreadable"); } catch { }
    }

    public IReadOnlyList<GroupChatEntry> Lines(string group) =>
        _groups.TryGetValue(group, out var l) ? l : [];

    public bool Dirty { get; private set; }
    private DateTime _savedAt;
    // a busy chat brings lines every second, and every save re-encrypts every group's whole log
    public static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(5);

    public void Add(string group, IEnumerable<GroupChatEntry> lines)
    {
        if (!_groups.TryGetValue(group, out var l)) _groups[group] = l = new List<GroupChatEntry>();
        int before = l.Count;
        l.AddRange(lines);
        if (l.Count == before) return;
        if (l.Count > KeepPerGroup) l.RemoveRange(0, l.Count - KeepPerGroup);
        Dirty = true;
        SaveIfDue(DateTime.UtcNow);
    }

    // from the last try, not the last save that worked, so one that keeps failing isn't tried every half second
    public void SaveIfDue(DateTime now)
    {
        if (!Dirty || now - _savedAt < SaveEvery) return;
        _savedAt = now;
        Save();
    }

    public void Flush()
    {
        if (Dirty) Save();
    }

    public bool Delete(string group, string id) => Delete(group, [id]);

    public bool Delete(string group, IReadOnlyCollection<string> ids)
    {
        if (!_groups.TryGetValue(group, out var l) || l.RemoveAll(e => ids.Contains(e.Id)) == 0) return false;
        Save();
        return true;
    }

    public bool Clear(string group)
    {
        if (!_groups.Remove(group)) return false;
        Save();
        return true;
    }

    public Action<string>? Log { get; init; }

    public bool Save()
    {
        if (_path is null) return false;
        string? tmp = null;
        try
        {
            var cipher = Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(_groups), Entropy);
            if (cipher is null)
            {
                Log?.Invoke($"{Path.GetFileName(_path)} couldn't be encrypted, so it wasn't saved");
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            tmp = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tmp, cipher);
            File.Move(tmp, _path, overwrite: true);
            Dirty = false;
            _savedAt = DateTime.UtcNow;
            return true;
        }
        catch (Exception e)
        {
            Log?.Invoke($"{Path.GetFileName(_path)} couldn't be saved ({e.GetType().Name}: {e.Message})");
            if (tmp is not null) try { File.Delete(tmp); } catch { }
            return false;
        }
    }
}
