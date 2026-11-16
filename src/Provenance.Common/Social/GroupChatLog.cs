using System.Text;
using Provenance.Common.Hook;
using Provenance.Common.IO;

namespace Provenance.Common.Social;

public sealed record GroupChatEntry(string Id, long Time, string Name, string Text, bool Me)
{
    public DateTimeOffset When => DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime();

    public static GroupChatEntry Mine(string name, string text) =>
        new(HookFiles.NewToken(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), name, text, true);

    // a line without a time of its own gets now
    public static GroupChatEntry From(GroupChatLine l) =>
        new(HookFiles.NewToken(), l.Time > 0 ? l.Time : DateTimeOffset.UtcNow.ToUnixTimeSeconds(), l.Name, l.Text, l.Me);
}

// every group's chat lines kept on this PC, so a chat opened again (or after a restart) shows what came before. It's
// only ever Provenance's own copy, POL keeps none
public sealed class GroupChatLog
{
    public const int KeepPerGroup = 1000;

    private readonly EncryptedJsonFile<Dictionary<string, List<GroupChatEntry>>>? _file;
    private readonly Dictionary<string, List<GroupChatEntry>> _groups = new(StringComparer.OrdinalIgnoreCase);

    // format constant, the same deal as the message log's
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("Provenance PlayOnline group chat log v1");

    public GroupChatLog(string profileId, string? root = null)
    {
        if (root is null)
        {
            if (AppPaths.UserData is not { } userData) return;
            root = Path.Combine(userData, "messages");
        }
        _file = new(Path.Combine(root, profileId + ".groups.dat"), Entropy, "Group chat log");
        if (_file.Load() is { } loaded)
            foreach (var (k, v) in loaded) _groups[k] = v;
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
        Dirty = true;
        Save();
        return true;
    }

    public bool Clear(string group)
    {
        if (!_groups.Remove(group)) return false;
        Dirty = true;
        Save();
        return true;
    }

    public bool Save()
    {
        if (_file is null || !_file.Save(_groups)) return false;
        Dirty = false;
        _savedAt = DateTime.UtcNow;
        return true;
    }
}
