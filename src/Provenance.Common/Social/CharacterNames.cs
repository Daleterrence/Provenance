using System.Text.Json;
using Provenance.Common.IO;
using Provenance.Common.Security;
using Serilog;

namespace Provenance.Common.Social;

// Name is empty when there's none to show, Why says which: "private" or "not found"
public sealed record KnownCharacter(string Name, string Why = "");

// keyed "ctsid:ctid". A name or no record at all is only asked for once, a private one's asked about again each login
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
            if (AppPaths.UserData is not { } userData) return;
            path = Path.Combine(userData, "characters.json");
        }
        _path = path;
        Refresh();
    }

    public int Count => _known.Count;

    public KnownCharacter? Get(string key) => _known.TryGetValue(key, out var c) ? c : null;

    public void Remember(string key, KnownCharacter c)
    {
        _known[key] = c;
        Save();
    }

    public void Refresh()
    {
        if (_path is null) return;
        var info = new FileInfo(_path);
        var now = info.Exists ? (info.LastWriteTimeUtc, info.Length) : default;
        if (now == _fileWas) return;
        _fileWas = now;
        foreach (var (key, c) in Read(_path)) _known.TryAdd(key, c);
    }

    // null for anything that might go differently next time.
    // Other error codes and an empty name aren't kept, nobody's seen what those mean for an FFXI profile yet
    public static KnownCharacter? FromAnswer(string state, PolCharacterLookup? r) => state switch
    {
        "done" when r is { NameState: ProfileFieldState.Shown, Name.Length: > 0 } => new(r.Name),
        "done" when r is { NameState: ProfileFieldState.Private } => new("", "private"),
        // -0x14cc, the viewer's "Selected profile is hidden."
        "failed:-5324" => new("", "private"),
        "failed:no profile" => new("", "not found"),
        _ => null,
    };

    // whatever name the tab shows, so an old one never outlives a record gone private.
    // A shown record with no name isn't kept (nobody knows what that means yet)
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
        }
        catch (Exception e)   // it just gets asked again next session
        {
            Log.Warning("Character names: {Line}", $"{Path.GetFileName(_path)} couldn't be saved ({e.GetType().Name}: {e.Message})");
        }
    }

    // null after 2s of trying, a save stuck that long behind another one goes ahead anyway
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
