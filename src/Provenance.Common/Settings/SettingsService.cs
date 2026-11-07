using System.Text.Json;
using System.Text.Json.Nodes;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.IO;
using Provenance.Common.Security;
using Serilog;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Settings;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private bool _saveFailedRaised;
    internal PolRegion? _region;
    // this copy's settings as it last read or saved them, which is what tells its own changes from another copy's
    private JsonObject _known;

    // the app's UI thread, where its settings change. Null with no app, like the tests
    private readonly SynchronizationContext? _owner;

    public LauncherSettings Current { get; private set; }

    public void OnOwnerThread(Action run)
    {
        if (_owner is null || SynchronizationContext.Current == _owner) run();
        else _owner.Post(_ => run(), null);
    }

    public event Action<string>? SaveFailed;

    public string? LoadProblem { get; private set; }

    // looked up again until it's found, so an install made while the app runs gets picked up
    public PolRegion? DetectedRegion => _region ??= PolPaths.TryAutoDetectRegion();

    public void RedetectRegion() => _region = null;

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
        if (ThirdPartyProfiles.MapOldCopies(Current)) Write(Current);
    }

    private LauncherSettings Load()
    {
        if (!File.Exists(_path))
        {
            Log.Information("No settings.json, using defaults");
            return new LauncherSettings();
        }
        try
        {
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(_path)) ?? new LauncherSettings();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not parse settings.json, falling back to defaults");
            // the next save replaces settings.json, account list and all
            var bad = _path + ".bad";
            bool kept;
            try { File.Copy(_path, bad, overwrite: true); kept = true; } catch { kept = false; }
            LoadProblem = "Provenance couldn't read your settings file, so it started with the default settings. "
                        + (kept ? $"There's a copy of the old file at {bad}." : "Provenance couldn't keep a copy of it.");
            return new LauncherSettings();
        }
    }

    public void Save(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Current = settings;
        Write(settings);
    }

    public void SaveChange(Action<LauncherSettings> change)
    {
        change(Current);
        using var gate = HoldLock();
        if (!File.Exists(_path)) Write(Current);
        else if (ReadSaved() is { } saved)
        {
            var before = JsonSerializer.Serialize(saved, JsonOptions);
            change(saved);
            if (JsonSerializer.Serialize(saved, JsonOptions) != before && !WriteFile(Node(saved))) return;
            var known = _known.Deserialize<LauncherSettings>(JsonOptions)!;
            change(known);
            _known = Node(known);
        }
        // the next save replaces a file that didn't load, as Save does
        else if (LoadProblem is not null) Write(Current);
        else Log.Warning("settings.json couldn't be read again, so a change is only kept until Provenance closes");
    }

    // what this copy changed since it last read or saved goes on top of the file as it is now, so another copy's
    // changes meanwhile stay. An unreadable file is replaced whole
    private void Write(LauncherSettings settings)
    {
        using var gate = HoldLock();
        var saved = ReadSaved();
        // another copy may have given out a slot since this one loaded, a number once given stays given
        if (saved is not null && saved.LastHomeSlot > settings.LastHomeSlot)
            settings.LastHomeSlot = saved.LastHomeSlot;
        var mine = Node(settings);
        var file = saved is null ? null : Node(saved);
        var merged = file is null ? mine : Merge(mine, _known, file);
        if ((file is not null && JsonNode.DeepEquals(merged, file)) || WriteFile(merged)) _known = mine;
    }

    // temp file then a move, a truncated settings.json would load as defaults and lose every account
    private bool WriteFile(JsonObject settings)
    {
        IDisposable? gate = null;
        try
        {
            gate = HoldLock();
            AtomicFile.Write(_path, settings.ToJsonString(JsonOptions));
            Log.Debug("Settings saved to {Path}", _path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save settings.json");
            if (!_saveFailedRaised)
            {
                _saveFailedRaised = true;
                SaveFailed?.Invoke(ex.Message);
            }
            return false;
        }
        finally { gate?.Dispose(); }
    }

    private static JsonObject Node(LauncherSettings settings) => JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();

    // accounts and their multi-account rows match up by id and the Windower lists by profile name, so one added,
    // edited or removed in either copy keeps that
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

    // one gone from the file was removed by another copy, one this copy never had was added by one
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
        // the multi-account order can be changed, so this copy's goes when it moved one and the file's otherwise
        bool moved = !m.Keys.Where(before.ContainsKey).SequenceEqual(before.Keys.Where(m.ContainsKey));
        var order = (moved ? m.Keys.Concat(f.Keys) : f.Keys.Concat(m.Keys)).Distinct().Where(merged.ContainsKey);
        if (key.Length > 0) return new JsonArray(order.Select(id => merged[id]?.DeepClone()).ToArray());
        var map = new JsonObject();
        foreach (var id in order) map[id] = merged[id]?.DeepClone();
        return map;
    }

    // null when it isn't the shape expected or an id turns up twice, and then the whole list goes by the plain rule
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

    // Save's move fails while anything has the file open, whatever it shares, so this waits its turn at the same lock
    public LauncherSettings? ReadSaved()
    {
        try
        {
            using var gate = AdminLock.TryAcquire(AdminLock.PerUser("settings"), TimeSpan.FromMilliseconds(500), reentrant: true);
            if (gate is null || !File.Exists(_path)) return null;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<LauncherSettings>(fs);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read settings.json again");
            return null;
        }
    }
}
