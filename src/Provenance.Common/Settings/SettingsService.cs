using System.Text.Json;
using System.Text.Json.Nodes;
using Provenance.Common.IO;
using Provenance.Common.Security;
using Serilog;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Settings;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private const int ReadTries = 5, ReadRetryDelay = 300;
    private bool _saveFailedRaised;
    private bool _unreadable;
    private PolRegion? _region;
    private JsonObject _known;

    private readonly SynchronizationContext? _owner;

    public LauncherSettings Current { get; private set; }

    public void OnOwnerThread(Action run)
    {
        if (_owner is null || SynchronizationContext.Current == _owner) run();
        else _owner.Post(_ => run(), null);
    }

    public event Action<string>? SaveFailed;

    public string? LoadProblem { get; private set; }

    public string? SaveProblem { get; private set; }

    public PolRegion? DetectedRegion => _region ??= PolPaths.TryAutoDetectRegion();

    public void RedetectRegion() => _region = null;

    internal void SetDetectedRegion(PolRegion region) => _region = region;

    public static string DefaultPath =>
        Path.Combine(AppPaths.UserData ?? "Provenance", "settings.json");

    public SettingsService() : this(DefaultPath, null) { }

    internal SettingsService(string path) : this(path, null) { }

    public SettingsService(string path, SynchronizationContext? owner)
    {
        _owner = owner;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _path = path;
        Current = Load();
        _known = Node(Current);
    }

    private LauncherSettings Load()
    {
        if (!File.Exists(_path))
        {
            Log.Information("No settings.json, starting as a new install");
            return LauncherSettings.FirstStart();
        }
        string text;
        try
        {
            using var gate = HoldLock();
            text = ReadText();
        }
        catch (FileNotFoundException)
        {
            return LauncherSettings.FirstStart();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not read settings.json, leaving it as it is");
            _unreadable = true;
            LoadProblem = $"Provenance couldn't read your settings file ({_path}), so it started with the default settings. "
                        + "It won't save over the file, so changes you make this time won't be kept. Close anything else using it and start Provenance again.";
            return LauncherSettings.FirstStart();
        }
        Exception? error = null;
        try
        {
            if (JsonSerializer.Deserialize<LauncherSettings>(text) is { } loaded) return loaded;
        }
        catch (Exception ex) { error = ex; }
        Log.Warning(error, "Could not parse settings.json, falling back to defaults");
        var bad = _path + ".bad";
        bool kept;
        try { File.Copy(_path, bad, overwrite: true); kept = true; } catch { kept = false; }
        LoadProblem = "Provenance couldn't read your settings file, so it started with the default settings. "
                    + (kept ? $"There's a copy of the old file at {bad}." : "Provenance couldn't keep a copy of it.");
        return LauncherSettings.FirstStart();
    }

    private string ReadText()
    {
        for (int i = 1; ; i++)
        {
            try { return SharedFile.ReadAllText(_path); }
            catch (Exception ex) when (i < ReadTries && ex is IOException and not FileNotFoundException or UnauthorizedAccessException)
            {
                Thread.Sleep(ReadRetryDelay);
            }
        }
    }

    public bool Save(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Current = settings;
        return Write(settings);
    }

    public void SaveChange(Action<LauncherSettings> change)
    {
        change(Current);
        using var gate = HoldLock();
        if (!File.Exists(_path)) Write(Current);
        else if (ReadSaved() is { } saved)
        {
            var before = Node(saved);
            change(saved);
            var after = Node(saved);
            if (!JsonNode.DeepEquals(before, after) && !WriteFile(after, before)) return;
            var known = _known.Deserialize<LauncherSettings>(JsonOptions)!;
            change(known);
            _known = Node(known);
        }
        else if (LoadProblem is not null) Write(Current);
        else Log.Warning("settings.json couldn't be read again, so a change is only kept until Provenance closes");
    }

    private bool Write(LauncherSettings settings)
    {
        using var gate = HoldLock();
        var saved = ReadSaved();
        if (saved is not null && saved.LastHomeSlot > settings.LastHomeSlot)
            settings.LastHomeSlot = saved.LastHomeSlot;
        var mine = Node(settings);
        var file = saved is null ? null : Node(saved);
        var merged = file is null ? mine : Merge(mine, _known, file);
        if ((file is not null && JsonNode.DeepEquals(merged, file)) || WriteFile(merged, file))
        {
            _known = mine;
            return true;
        }
        return false;
    }

    // temp file then a move, a truncated settings.json would load as defaults and lose every account
    private bool WriteFile(JsonObject settings, JsonObject? before)
    {
        if (_unreadable) return false;
        IDisposable? gate = null;
        try
        {
            gate = HoldLock();
            AtomicFile.Write(_path, settings.ToJsonString(JsonOptions));
            SaveProblem = null;
            _saveFailedRaised = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save settings.json");
            SaveProblem ??= ex.Message;
            if (!_saveFailedRaised)
            {
                _saveFailedRaised = true;
                SaveFailed?.Invoke(ex.Message);
            }
            return false;
        }
        finally { gate?.Dispose(); }
        try { Log.Debug("Settings saved: {Changed:l}", SettingsDiff.Changed(before, settings)); }
        catch (Exception ex) { Log.Debug(ex, "Couldn't say what the settings save changed"); }
        return true;
    }

    private static JsonObject Node(LauncherSettings settings) => JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();

    private static JsonObject Merge(JsonObject mine, JsonObject? was, JsonObject file, bool lists = true)
    {
        var merged = new JsonObject();
        foreach (var (name, value) in mine)
        {
            var before = was?[name];
            string? key = !lists ? null : name switch
            {
                nameof(LauncherSettings.Profiles) => nameof(Account.Id),
                nameof(LauncherSettings.MultiAccounts) => nameof(MultiAccountEntry.ProfileId),
                nameof(LauncherSettings.WindowerAddonLists) => "",
                _ => null,
            };
            var pick = !file.TryGetPropertyValue(name, out var now) ? value
                     : key is not null ? MergeKeyed(value, before, now, key)
                     : JsonNode.DeepEquals(value, before) ? now : value;
            merged[name] = pick?.DeepClone();
        }
        return merged;
    }

    private static JsonNode? MergeKeyed(JsonNode? mine, JsonNode? was, JsonNode? file, string key)
    {
        if (Entries(mine, key) is not { } m || Entries(file, key) is not { } f)
            return JsonNode.DeepEquals(mine, was) ? file : mine;
        var before = Entries(was, key) ?? new();
        var merged = new Dictionary<string, JsonNode?>();
        foreach (var (id, item) in m)
        {
            if (!before.TryGetValue(id, out var old)) merged[id] = item;
            else if (f.TryGetValue(id, out var now))
                merged[id] = item is JsonObject a && now is JsonObject b ? Merge(a, old as JsonObject, b, lists: false)
                           : JsonNode.DeepEquals(item, old) ? now : item;
        }
        foreach (var (id, item) in f)
            if (!before.ContainsKey(id) && !m.ContainsKey(id)) merged[id] = item;
        bool moved = !m.Keys.Where(before.ContainsKey).SequenceEqual(before.Keys.Where(m.ContainsKey));
        var order = (moved ? m.Keys.Concat(f.Keys) : f.Keys.Concat(m.Keys)).Distinct().Where(merged.ContainsKey);
        if (key.Length > 0) return new JsonArray(order.Select(id => merged[id]?.DeepClone()).ToArray());
        var map = new JsonObject();
        foreach (var id in order) map[id] = merged[id]?.DeepClone();
        return map;
    }

    private static OrderedDictionary<string, JsonNode?>? Entries(JsonNode? node, string key)
    {
        var entries = new OrderedDictionary<string, JsonNode?>();
        if (key.Length == 0 && node is JsonObject map)
        {
            foreach (var (name, item) in map) entries.Add(name, item);
            return entries;
        }
        if (key.Length == 0 || node is not JsonArray list) return null;
        foreach (var item in list)
            if (item is not JsonObject o || o[key] is not JsonValue v || !v.TryGetValue(out string? id) || !entries.TryAdd(id, item))
                return null;
        return entries;
    }

    public IDisposable? HoldLock()
    {
        try { return AdminLock.TryAcquire(AdminLock.PerUser("settings"), TimeSpan.FromSeconds(3), reentrant: true); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not take the settings lock; going on without it");
            return null;
        }
    }

    public LauncherSettings? ReadSaved()
    {
        try
        {
            using var gate = AdminLock.TryAcquire(AdminLock.PerUser("settings"), TimeSpan.FromMilliseconds(500), reentrant: true);
            if (gate is null || !File.Exists(_path)) return null;
            using var fs = SharedFile.OpenRead(_path);
            return JsonSerializer.Deserialize<LauncherSettings>(fs);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read settings.json again");
            return null;
        }
    }
}
