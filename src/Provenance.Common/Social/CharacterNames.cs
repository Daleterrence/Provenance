using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Provenance.Common.Hook;
using Provenance.Common.IO;
using Provenance.Common.Security;
using Serilog;

namespace Provenance.Common.Social;

// Heard is a name from the search server for a character whose profile gives none. Only the game's chat lines use it
public sealed record KnownCharacter(string Name, string Why = "", string Heard = "");

public sealed class CharacterNames
{
    private const int Version = 1;
    private readonly string? _path;
    private readonly Dictionary<string, KnownCharacter> _known = new(StringComparer.Ordinal);
    private (DateTime, long) _fileWas;
    private readonly bool _forHook;

    private sealed class Saved
    {
        public int Version { get; set; }
        public Dictionary<string, KnownCharacter> Characters { get; set; } = new();
    }

    public CharacterNames(string? path = null, bool forHook = false)
    {
        _forHook = forHook || path is null;
        if (path is null)
        {
            if (AppPaths.UserData is not { } userData) return;
            path = Path.Combine(userData, "characters.json");
        }
        _path = path;
        Load();
        TellHook();
    }

    private static readonly Regex Plain = new(@"^[A-Za-z]{1,15}\z");

    // the hook reads only the first 256KB, and a name that isn't plain letters never reaches the game's chat
    internal static string HookText(IEnumerable<KeyValuePair<string, KnownCharacter>> known)
    {
        var text = new StringBuilder();
        foreach (var (id, name) in FfxiWorlds.All) text.Append("w ").Append(id).Append(' ').Append(name).Append('\n');
        int written = 0;
        foreach (var (key, c) in known)
        {
            var parts = key.Split(':');
            if (parts.Length != 2 || !uint.TryParse(parts[0], out var ctsid) || !ulong.TryParse(parts[1], out var ctid)) continue;
            if ((c?.Name is { Length: > 0 } shown ? shown : c?.Heard) is not { } name || !Plain.IsMatch(name)) continue;
            if (++written > 5000) break;
            text.Append($"c {ctsid:x8} {(uint)ctid:x8} {(uint)(ctid >> 32):x8} {name}\n");
        }
        return text.ToString();
    }

    private void TellHook()
    {
        if (!_forHook) return;
        try { HookFiles.WriteShared(HookContract.Files.Characters, HookText(_known)); }
        catch (Exception e)
        {
            Log.Warning("Character names: {Line}", $"the names for the game's chat lines couldn't be written ({e.GetType().Name}: {e.Message})");
        }
    }

    public int Count => _known.Count;

    public KnownCharacter? Get(string key) => _known.TryGetValue(key, out var c) ? c : null;

    public void Remember(string key, KnownCharacter c)
    {
        _known[key] = c;
        Save();
    }

    public bool Learn(string key, string? name)
    {
        if (name is null || !Plain.IsMatch(name)) return false;
        var known = Get(key);
        if (known is { Name.Length: > 0 } || known?.Heard == name) return false;
        Remember(key, known is null ? new KnownCharacter(name) : known with { Heard = name });
        return true;
    }

    public void Refresh()
    {
        if (Load()) TellHook();
    }

    private bool Load()
    {
        if (_path is null) return false;
        var info = new FileInfo(_path);
        var now = info.Exists ? (info.LastWriteTimeUtc, info.Length) : default;
        if (now == _fileWas) return false;
        _fileWas = now;
        int had = _known.Count;
        foreach (var (key, c) in Read(_path)) _known.TryAdd(key, c);
        return _known.Count != had;
    }

    // other error codes and an empty name aren't kept, nobody's seen what those mean for an FFXI profile yet
    public static KnownCharacter? FromAnswer(string state, PolCharacterLookup? r) => state switch
    {
        "done" when r is { NameState: ProfileFieldState.Shown, Name.Length: > 0 } => new(r.Name),
        "done" when r is { NameState: ProfileFieldState.Private } => new("", "private"),
        // -0x14cc, the viewer's "Selected profile is hidden."
        "failed:-5324" => new("", "private"),
        "failed:no profile" => new("", "not found"),
        _ => null,
    };

    public static KnownCharacter? FromProfile(PolFfxiProfile r) =>
        r is { NameState: ProfileFieldState.Shown, Name.Length: > 0 } ? new(r.Name)
        : r.NameState == ProfileFieldState.Private ? new("", "private") : null;

    private static Dictionary<string, KnownCharacter> Read(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return new();
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path));
            if (saved is not { Version: Version }) return new();
            return saved.Characters.Where(kv => kv.Value is not null)
                                   .ToDictionary(kv => kv.Key, kv => kv.Value with { Name = kv.Value.Name ?? "", Why = kv.Value.Why ?? "", Heard = kv.Value.Heard ?? "" },
                                                 StringComparer.Ordinal);
        }
        catch { return new(); }
    }

    private void Save()
    {
        if (_path is null) return;
        using var gate = Lock(_path + ".lock");
        try
        {
            var all = Read(_path);
            foreach (var (key, c) in _known) all[key] = c;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.Write(_path, JsonSerializer.Serialize(new Saved { Version = Version, Characters = all }));
            foreach (var (key, c) in all) _known.TryAdd(key, c);
            var info = new FileInfo(_path);
            _fileWas = (info.LastWriteTimeUtc, info.Length);
            TellHook();
        }
        catch (Exception e)
        {
            Log.Warning("Character names: {Line}", $"{Path.GetFileName(_path)} couldn't be saved ({e.GetType().Name}: {e.Message})");
        }
    }

    private static IDisposable? Lock(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return AdminLock.TryAcquireFile(path, TimeSpan.FromSeconds(2));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
