using System.Text.Json;

namespace Provenance.Common.Social;

public static class FfxiWorlds
{
    // the game's own world list, ids 98 to 131
    private static readonly string[] Names =
    [
        "Undine", "Atomos", "Bahamut", "Shiva", "Titan", "Ramuh", "Phoenix", "Carbuncle", "Fenrir", "Sylph", "Valefor",
        "Alexander", "Leviathan", "Odin", "Ifrit", "Diabolos", "Caitsith", "Quetzalcoatl", "Siren", "Unicorn", "Gilgamesh",
        "Ragnarok", "Pandemonium", "Garuda", "Cerberus", "Kujata", "Bismarck", "Seraph", "Lakshmi", "Asura", "Midgardsormr",
        "Fairy", "Remora", "Hades",
    ];

    public static string? Name(int id) => id is >= 98 and <= 131 ? Names[id - 98] : null;
}

public static class PlayingText
{
    // what goes next to a friend in FFXI: Long when there's room for it, Short when there isn't
    public static (string Long, string Short) For(string name, string? world)
    {
        if (name.Length > 0 && world is not null) return ($"{name} - {world}", name);
        if (name.Length > 0) return (name, name);
        if (world is not null) return ($"Playing on {world}", $"Playing on {world}");
        return ("", "");
    }
}

// Name is empty when there's none to show, Why says which: "private" or "not found"
public sealed record KnownCharacter(string Name, string Why = "");

// FFXI characters already looked up, keyed "ctsid:ctid", so each one's only asked for once ever. A Private record and
// no record at all are kept too, they won't answer any differently next time
public sealed class CharacterNames
{
    // bump it if what gets kept ever turns out wrong, older files then load as empty
    private const int Version = 1;
    private readonly string? _path;
    private readonly Dictionary<string, KnownCharacter> _known = new(StringComparer.Ordinal);
    private (DateTime, long) _fileWas;

    private sealed class Saved
    {
        public int Version { get; set; }
        public Dictionary<string, KnownCharacter> Characters { get; set; } = new();
    }

    public CharacterNames(string? path = null)
    {
        if (path is null)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData)) return;
            path = Path.Combine(appData, "Provenance", "characters.json");
        }
        _path = path;
        Refresh();
    }

    public Action<string>? Log { get; init; }

    public int Count => _known.Count;

    public KnownCharacter? Get(string key) => _known.TryGetValue(key, out var c) ? c : null;

    public void Remember(string key, KnownCharacter c)
    {
        _known[key] = c;
        Save();
    }

    // picks up what another copy of Provenance saved since, only reading the file when it's changed
    public void Refresh()
    {
        if (_path is null) return;
        var info = new FileInfo(_path);
        var now = info.Exists ? (info.LastWriteTimeUtc, info.Length) : default;
        if (now == _fileWas) return;
        _fileWas = now;
        foreach (var (key, c) in Read(_path)) _known.TryAdd(key, c);
    }

    // what's worth keeping from the hook's answer, null for anything that might go differently next time. Other error
    // codes and an empty name aren't kept, nobody's seen what those mean for an FFXI profile yet
    public static KnownCharacter? FromAnswer(string state, PolCharacterLookup? r) => state switch
    {
        "done" when r is { NameState: PolProfile.Shown, Name.Length: > 0 } => new(r.Name),
        "done" when r is { NameState: PolProfile.Private } => new("", "private"),
        // -0x14cc, the viewer's "Selected profile is hidden."
        "failed:-5324" => new("", "private"),
        "failed:no profile" => new("", "not found"),
        _ => null,
    };

    // a profile tab's read: whatever name it shows, so an old one never outlives a record that's gone private
    // like FromAnswer, a shown record with no name in it isn't kept, nobody knows what that means yet
    public static KnownCharacter? FromProfile(PolFfxiProfile r) =>
        r is { NameState: PolProfile.Shown, Name.Length: > 0 } ? new(r.Name)
        : r.NameState == PolProfile.Private ? new("", "private") : null;

    private static Dictionary<string, KnownCharacter> Read(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return new();
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path));
            if (saved is not { Version: Version }) return new();
            return saved.Characters.Where(kv => kv.Value is not null)
                                   .ToDictionary(kv => kv.Key, kv => kv.Value with { Name = kv.Value.Name ?? "", Why = kv.Value.Why ?? "" },
                                                 StringComparer.Ordinal);
        }
        catch { return new(); }
    }

    // another copy of Provenance may have added some since this one loaded, so theirs stay. The lock file stops two
    // saves at once each writing over the other's
    private void Save()
    {
        if (_path is null) return;
        string? tmp = null;
        using var gate = Lock(_path + ".lock");
        try
        {
            var all = Read(_path);
            foreach (var (key, c) in _known) all[key] = c;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            tmp = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Saved { Version = Version, Characters = all }));
            File.Move(tmp, _path, overwrite: true);
            foreach (var (key, c) in all) _known.TryAdd(key, c);
            var info = new FileInfo(_path);
            _fileWas = (info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception e)   // it just gets asked again next session
        {
            Log?.Invoke($"{Path.GetFileName(_path)} couldn't be saved ({e.GetType().Name}: {e.Message})");
            if (tmp is not null) try { File.Delete(tmp); } catch { }
        }
    }

    // null after 2 secs of trying, a save stuck that long behind another one goes ahead anyway
    private static FileStream? Lock(string path)
    {
        for (int i = 0; i < 40; i++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(50); }
        }
        return null;
    }
}

// one lookup out at a time, 3 secs apart like the hook, only for a character someone's just turned up on that the cache
// hasn't got. A failed one goes again when that friend's presence next changes, never on a timer
public sealed class CharacterLookups(CharacterNames names)
{
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(3);
    // polcore fails a read at 100 secs
    public static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(130);

    private Dictionary<string, string> _seen = new(StringComparer.Ordinal);
    private readonly List<PolFriendGame> _queue = new();
    // ones given up on, in case the hook took them late and still answers
    private readonly List<(string Token, PolFriendGame Game)> _late = new();
    private DateTime _last = DateTime.MinValue, _askedAt;

    public PolFriendGame? Asking { get; private set; }
    public string Token { get; private set; } = "";
    public int Queued => _queue.Count;

    // an empty list says nothing about who's on what (the hook hasn't loaded it yet), so it changes nothing
    public void Seen(IReadOnlyCollection<PolFriend> friends)
    {
        if (friends.Count == 0) return;
        names.Refresh();
        var now = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in friends)
        {
            var id = f.Key.Length > 0 ? f.Key : "n:" + f.Name;
            var game = f.Online && !f.Pending ? f.Game : null;
            var key = game?.CharacterKey ?? "";
            now[id] = key;
            if (game is null || (_seen.TryGetValue(id, out var was) && was == key)) continue;
            if (names.Get(key) is not null || Asking?.CharacterKey == key || _queue.Exists(q => q.CharacterKey == key)) continue;
            _queue.Add(game);
        }
        _seen = now;
    }

    // the one to ask the hook for now, if any. Anyone who's left that character meanwhile drops out of the queue
    public PolFriendGame? Next(DateTime now)
    {
        if (Asking is { } stuck && now - _askedAt > GiveUp)
        {
            _late.Add((Token, stuck));
            if (_late.Count > 8) _late.RemoveAt(0);
            Asking = null;
        }
        if (Asking is not null || now - _last < Gap) return null;
        names.Refresh();
        _queue.RemoveAll(q => names.Get(q.CharacterKey) is not null || !_seen.ContainsValue(q.CharacterKey));
        if (_queue.Count == 0) return null;
        Asking = _queue[0];
        _queue.RemoveAt(0);
        Token = Guid.NewGuid().ToString("N")[..12];
        _askedAt = _last = now;
        return Asking;
    }

    // true once the hook's settled one, kept or not. A late answer to one given up on still counts, as long as it's for
    // the same character
    public bool Answer(string token, string state, PolCharacterLookup? r)
    {
        if (token.Length == 0 || state is "" or "loading") return false;
        bool current = Asking is not null && token == Token;
        int late = current ? -1 : _late.FindIndex(x => x.Token == token);
        if (!current && late < 0) return false;
        var asked = current ? Asking! : _late[late].Game;
        if (CharacterNames.FromAnswer(state, r) is { } known && r?.CharacterKey == asked.CharacterKey)
            names.Remember(asked.CharacterKey, known);
        if (current) Asking = null;
        else _late.RemoveAt(late);
        return true;
    }

    // the request file couldn't be written
    public void Failed() => Asking = null;

    // the give-up clock only starts once the hook's taken it, it waits while the hook's busy with another read
    public void Waiting(string hookToken, bool hookBusy, DateTime now)
    {
        if (Asking is not null && hookToken != Token && hookBusy) _askedAt = now;
    }

    // a new session. Who's on what carries over, so a failed one still waits for a real change
    public void Reset()
    {
        _queue.Clear();
        _late.Clear();
        Asking = null;
        Token = "";
    }
}
