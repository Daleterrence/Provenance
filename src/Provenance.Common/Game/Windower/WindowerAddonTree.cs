using System.Text.Json;

namespace Provenance.Common.Game.Windower;

public sealed record WindowerAddonFile(string Path, string Sha);

public sealed record WindowerAddon(string Name, string MainPath, IReadOnlyList<WindowerAddonFile> Files);

// updates\addons.json is GitHub's tree of the Windower/Lua repo, saved as it came. An addon is a folder in addons/ with
// <folder>.lua straight inside, everything in addons/libs is a library
public sealed class WindowerAddonTree
{
    private static readonly char[] Slashes = ['/', '\\'];

    private WindowerAddonTree(string sha, string url, IReadOnlyList<WindowerAddonFile> blobs)
    {
        Sha = sha;
        Url = url;
        var folders = new Dictionary<string, List<WindowerAddonFile>>(StringComparer.Ordinal);
        var addons = new List<WindowerAddon>();
        foreach (var b in blobs)
        {
            var parts = b.Path.Split(Slashes, 3);
            if (parts.Length < 3 || parts[0] != "addons") continue;
            if (!folders.TryGetValue(parts[1], out var files))
            {
                folders.Add(parts[1], files = []);
            }
            files.Add(b);
        }
        foreach (var (name, files) in folders)
        {
            var main = files.FirstOrDefault(f => f.Path.Split(Slashes, 3)[2].Equals(name + ".lua", StringComparison.OrdinalIgnoreCase));
            if (main is not null) addons.Add(new WindowerAddon(name, main.Path, files));
        }
        Addons = addons;
        Libs = blobs.Where(b => b.Path.Split(Slashes, 3) is [ "addons", "libs", _ ]).ToList();
    }

    public string Sha { get; }
    public string Url { get; }
    public IReadOnlyList<WindowerAddon> Addons { get; }
    public IReadOnlyList<WindowerAddonFile> Libs { get; }

    public static string CachePath(string windowerDir) => Path.Combine(windowerDir, "updates", "addons.json");

    public static string ZipPath(string windowerDir) => Path.Combine(windowerDir, "updates", "addons");

    public static string ZipEntry(string branch, string path) => $"Lua-{branch}/{path}";

    public static WindowerAddonTree? Load(string file)
    {
        try { return Parse(File.ReadAllBytes(file)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static WindowerAddonTree? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "sha") is not { } sha || Text(root, "url") is not { } url) return null;
            if (!root.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array) return null;
            var blobs = new List<WindowerAddonFile>();
            foreach (var e in tree.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || Text(e, "type") != "blob") continue;
                if (Text(e, "path") is { Length: > 0 } path && Text(e, "sha") is { } blobSha) blobs.Add(new WindowerAddonFile(path, blobSha));
            }
            return new WindowerAddonTree(sha, url, blobs);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    public WindowerAddon? Find(string name) =>
        Addons.FirstOrDefault(a => a.Name == name) ?? Addons.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static bool IsInstalled(WindowerAddon addon, string windowerDir) => File.Exists(Path.Combine(windowerDir, addon.MainPath));

    // addons/<name>/data/... is the addon's own settings, only written when it isn't there yet. Libs are always written
    public static bool OnlyIfMissing(string path) =>
        path.Split(Slashes, 4) is [_, var folder, "data", _] && folder != "libs";

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
