using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Provenance.Common.IO;
using Serilog;

namespace Provenance.Common.Game.Windower;

internal readonly record struct PeFacts(bool IsPlugin, Version? Version);

internal sealed class WindowerScanCache
{
    private const int Format = 1;
    private const long MaxFileBytes = 4 << 20;
    private const int MaxRows = 20000;
    // a file touched this recently could still change within the clock's tick without its time moving
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(2);

    private sealed class FileRow
    {
        public string Name { get; set; } = "";
        public long Length { get; set; }
        public long Write { get; set; }
        public long Created { get; set; }
        public bool? Plugin { get; set; }
        public string? Version { get; set; }
        public string? Assembly { get; set; }
    }

    private sealed class AddonRow
    {
        public string Name { get; set; } = "";
        public long Write { get; set; }
        public bool Main { get; set; }
    }

    private sealed class Shape
    {
        public int Format { get; set; }
        public string? Folder { get; set; }
        public List<FileRow>? Files { get; set; }
        public List<AddonRow>? Addons { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly object _gate = new();
    private readonly string _folder;
    private readonly string? _path;
    private Dictionary<string, FileRow> _files = new(StringComparer.Ordinal);
    private Dictionary<string, AddonRow> _addons = new(StringComparer.Ordinal);
    private bool _dirty;

    internal Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    internal List<string> Reads { get; } = [];

    internal WindowerScanCache(string folder, string? path)
    {
        _folder = Normal(folder);
        _path = path;
        Load();
    }

    internal static string Normal(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    internal static string? FileFor(string folder, string? root)
    {
        if ((root ?? AppPaths.UserData) is not { } where) return null;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normal(folder).ToLowerInvariant())))[..16];
        return Path.Combine(where, "cache", $"windower-scan-{hash}.json");
    }

    private void Load()
    {
        try
        {
            if (_path is null || !File.Exists(_path) || new FileInfo(_path).Length > MaxFileBytes) return;
            var shape = JsonSerializer.Deserialize<Shape>(File.ReadAllText(_path, Encoding.UTF8), Json);
            if (shape is not { Format: Format, Files: { } files, Addons: { } addons } || !string.Equals(shape.Folder, _folder, StringComparison.OrdinalIgnoreCase)
                || files.Count + addons.Count > MaxRows)
                return;
            var fileMap = new Dictionary<string, FileRow>(StringComparer.Ordinal);
            foreach (var f in files)
                if (f is null || string.IsNullOrEmpty(f.Name) || !Sound(f.Version) || !Sound(f.Assembly) || !fileMap.TryAdd(f.Name, f)) return;
            var addonMap = new Dictionary<string, AddonRow>(StringComparer.Ordinal);
            foreach (var a in addons)
                if (a is null || string.IsNullOrEmpty(a.Name) || !addonMap.TryAdd(a.Name, a)) return;
            _files = fileMap;
            _addons = addonMap;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log.Information("Windower's scan cache couldn't be read ({Reason}), so the folder's scanned in full", e.GetType().Name);
            _files = new(StringComparer.Ordinal);
            _addons = new(StringComparer.Ordinal);
        }
    }

    private static bool Sound(string? version) => string.IsNullOrEmpty(version) || System.Version.TryParse(version, out _);

    private string? Key(string path)
    {
        var relative = Path.GetRelativePath(_folder, Path.GetFullPath(path));
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative.Replace('\\', '/');
    }

    private bool Cacheable(FileInfo fi) =>
        fi.Exists && (fi.Attributes & FileAttributes.ReparsePoint) == 0
        && new[] { fi.LastWriteTimeUtc, fi.CreationTimeUtc }.Max() < Now() - Settled;

    private static bool Same(FileRow row, FileInfo fi) =>
        row.Length == fi.Length && row.Write == fi.LastWriteTimeUtc.Ticks && row.Created == fi.CreationTimeUtc.Ticks;

    private static FileRow Fresh(string key, FileInfo fi) =>
        new() { Name = key, Length = fi.Length, Write = fi.LastWriteTimeUtc.Ticks, Created = fi.CreationTimeUtc.Ticks };

    internal PeFacts? Pe(string path, FileInfo? info = null)
    {
        lock (_gate)
        {
            var fi = info ?? new FileInfo(path);
            if (!fi.Exists) return null;
            var key = Key(path);
            FileRow? row = null;
            if (key is not null && _files.TryGetValue(key, out row) && Same(row, fi) && row.Plugin is { } plugin && Sound(row.Version))
                return new PeFacts(plugin, row.Version is null ? null : System.Version.Parse(row.Version));
            Reads.Add(key ?? path);
            if (PeFile.Load(path) is not { } pe) return null;
            var facts = new PeFacts(WindowerPlugins.IsPlugin(pe), pe.FileVersion);
            if (key is not null && Cacheable(fi))
            {
                var next = row is not null && Same(row, fi) ? row : Fresh(key, fi);
                next.Plugin = facts.IsPlugin;
                next.Version = facts.Version?.ToString();
                _files[key] = next;
                _dirty = true;
            }
            return facts;
        }
    }

    internal Version? Assembly(string path, Func<Version?> read)
    {
        lock (_gate)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            var key = Key(path);
            FileRow? row = null;
            if (key is not null && _files.TryGetValue(key, out row) && Same(row, fi) && row.Assembly is { } text && Sound(text))
                return text.Length == 0 ? null : System.Version.Parse(text);
            Reads.Add((key ?? path) + " (assembly)");
            var version = read();
            if (key is not null && Cacheable(fi))
            {
                var next = row is not null && Same(row, fi) ? row : Fresh(key, fi);
                next.Assembly = version?.ToString() ?? "";
                _files[key] = next;
                _dirty = true;
            }
            return version;
        }
    }

    internal bool AddonMain(DirectoryInfo folder, Func<bool> read)
    {
        lock (_gate)
        {
            bool settled = (folder.Attributes & FileAttributes.ReparsePoint) == 0 && folder.LastWriteTimeUtc < Now() - Settled;
            if (settled && _addons.TryGetValue(folder.Name, out var row) && row.Write == folder.LastWriteTimeUtc.Ticks) return row.Main;
            Reads.Add("addons/" + folder.Name);
            var main = read();
            if (settled)
            {
                _addons[folder.Name] = new AddonRow { Name = folder.Name, Write = folder.LastWriteTimeUtc.Ticks, Main = main };
                _dirty = true;
            }
            return main;
        }
    }

    internal void KeepPlugins(IReadOnlySet<string> names)
    {
        lock (_gate)
            foreach (var key in _files.Keys.Where(k => k.StartsWith("plugins/", StringComparison.Ordinal) && !names.Contains(k["plugins/".Length..])).ToList())
            {
                _files.Remove(key);
                _dirty = true;
            }
    }

    internal void KeepAddons(IReadOnlySet<string> names)
    {
        lock (_gate)
            foreach (var name in _addons.Keys.Where(k => !names.Contains(k)).ToList())
            {
                _addons.Remove(name);
                _dirty = true;
            }
    }

    internal void Save()
    {
        try
        {
            lock (_gate)
            {
                if (!_dirty || _path is null) return;
                var text = JsonSerializer.Serialize(new Shape { Format = Format, Folder = _folder, Files = [.. _files.Values], Addons = [.. _addons.Values] }, Json);
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                AtomicFile.Write(_path, text);
                _dirty = false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Information("Windower's scan cache couldn't be saved ({Reason})", e.GetType().Name);
        }
    }

    internal void Drop()
    {
        lock (_gate)
        {
            _files = new(StringComparer.Ordinal);
            _addons = new(StringComparer.Ordinal);
            _dirty = false;
            if (_path is not null) FileDelete.Try(_path);
        }
    }

    internal void DropIfChanged(WindowerUpdateResult r)
    {
        if (r.HookChanged || r.LauncherChanged || r.PluginsUpdated.Count > 0 || r.AddonsUpdated.Count > 0 || r.LibFilesWritten > 0 || r.AddonTreeChanged)
            Drop();
    }
}

internal static class WindowerScanCaches
{
    internal static string? RootOverride { get; set; }
    internal static Func<DateTime>? NowOverride { get; set; }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, WindowerScanCache> Open = new(StringComparer.OrdinalIgnoreCase);

    internal static WindowerScanCache For(string folder)
    {
        var key = WindowerScanCache.Normal(folder);
        lock (Gate)
        {
            if (!Open.TryGetValue(key, out var cache)) Open[key] = cache = new WindowerScanCache(key, WindowerScanCache.FileFor(key, RootOverride)) { Now = NowOverride ?? (() => DateTime.UtcNow) };
            return cache;
        }
    }
}
