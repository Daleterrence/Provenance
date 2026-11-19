using Provenance.Common.Game.ThirdParty;

namespace Provenance.Common.Game.Windower;

public sealed record WindowerPluginInfo
{
    public required AddonListing Listing { get; init; }
    public bool Installed { get; init; }
    public bool IsWindowerPlugin { get; init; }
    public bool InManifest { get; init; }
    public Version? ManifestVersion { get; init; }
    public Version? FileVersion { get; init; }
    public bool Required { get; init; }
    public bool Hidden { get; init; }
    public bool HasResources { get; init; }
}

public static class WindowerPlugins
{
    private static readonly IReadOnlyList<string> RequiredExports = ["GetInterfaceVersion", "CreateInstance"];

    public static IReadOnlyList<WindowerPluginInfo> Scan(string windowerDir) => Scan(windowerDir, null);

    internal static IReadOnlyList<WindowerPluginInfo> Scan(string windowerDir, WindowerScanCache? cache)
    {
        var dir = Path.Combine(windowerDir, "plugins");
        List<(string Path, FileInfo Info)> files;
        try
        {
            files = Directory.Exists(dir)
                ? new DirectoryInfo(dir).EnumerateFiles("*.dll").Select(f => (System.IO.Path.Combine(dir, f.Name), f)).ToList() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { files = []; }
        cache?.KeepPlugins(files.Select(f => f.Info.Name).ToHashSet(StringComparer.Ordinal));
        // a case-sensitive folder can hold two DLLs whose names only differ in case, the first one wins
        var dlls = files.Where(f => System.IO.Path.GetExtension(f.Path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                        .GroupBy(f => System.IO.Path.GetFileNameWithoutExtension(f.Path), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        PeFacts? Probe((string Path, FileInfo Info) f)
        {
            if (cache is not null) return cache.Pe(f.Path, f.Info);
            var pe = PeFile.Load(f.Path);
            return pe is null ? null : new PeFacts(IsPlugin(pe), pe.FileVersion);
        }
        var list = new List<WindowerPluginInfo>();
        var manifest = WindowerManifest.LoadCached(windowerDir);
        foreach (var m in manifest?.Plugins ?? [])
        {
            if (list.Any(p => p.Listing.Key.Equals(m.Name, StringComparison.OrdinalIgnoreCase))) continue;
            dlls.Remove(m.Name, out var found);
            var path = found.Path;
            var facts = path is null ? null : Probe(found);
            var version = facts?.Version;
            list.Add(new WindowerPluginInfo
            {
                Listing = new AddonListing(m.Name, m.Name, ((version ?? m.Version)?.ToString()), null, string.IsNullOrWhiteSpace(m.Description) ? null : m.Description, path),
                Installed = path is not null, IsWindowerPlugin = path is null || facts is { IsPlugin: true }, InManifest = true,
                ManifestVersion = m.Version, FileVersion = version,
                Required = m.Required, Hidden = m.Hidden, HasResources = m.Resources,
            });
        }
        foreach (var (name, found) in dlls.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            var facts = Probe(found);
            list.Add(new WindowerPluginInfo
            {
                Listing = new AddonListing(name, name, facts?.Version?.ToString(), null, null, found.Path),
                Installed = true, IsWindowerPlugin = facts is { IsPlugin: true }, FileVersion = facts?.Version,
            });
        }
        return list;
    }

    public static bool IsWindowerPlugin(string dllPath) => IsPlugin(PeFile.Load(dllPath));

    internal static bool IsPlugin(PeFile? pe) =>
        pe is { Machine: PeFile.MachineI386 } && RequiredExports.All(x => pe.Exports.ContainsKey(x));
}
