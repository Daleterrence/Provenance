namespace Provenance.Common.Game.Windower;

public sealed record WindowerPluginInfo
{
    public required string Name { get; init; }
    public bool Installed { get; init; }
    // false for a DLL without Windower's two exports (or not x86), which can't be turned on
    public bool IsWindowerPlugin { get; init; }
    public bool InManifest { get; init; }
    public Version? ManifestVersion { get; init; }
    public Version? FileVersion { get; init; }
    public string Description { get; init; } = "";
    public bool Required { get; init; }
    public bool Hidden { get; init; }
    public bool HasResources { get; init; }
    public string? Path { get; init; }
}

public static class WindowerPlugins
{
    private static readonly IReadOnlyList<string> RequiredExports = ["GetInterfaceVersion", "CreateInstance"];

    // manifest plugins first in the manifest's order (installed or not), then any other DLL in plugins\ by name
    public static IReadOnlyList<WindowerPluginInfo> Scan(string windowerDir)
    {
        var dir = Path.Combine(windowerDir, "plugins");
        List<string> files;
        try { files = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.dll").ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { files = []; }
        // a case-sensitive folder can hold Foo.dll and foo.dll, the first one wins
        var dlls = files.Where(f => System.IO.Path.GetExtension(f).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                        .GroupBy(f => System.IO.Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var list = new List<WindowerPluginInfo>();
        var manifest = PluginManifest.Load(windowerDir);
        foreach (var m in manifest?.Plugins ?? [])
        {
            if (list.Any(p => p.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase))) continue;
            dlls.Remove(m.Name, out var path);
            var pe = path is null ? null : PeImage.Read(path);
            list.Add(new WindowerPluginInfo
            {
                Name = m.Name, Installed = path is not null, IsWindowerPlugin = path is null || IsPlugin(pe), InManifest = true,
                ManifestVersion = m.Version, FileVersion = pe?.FileVersion, Description = m.Description,
                Required = m.Required, Hidden = m.Hidden, HasResources = m.HasResources, Path = path,
            });
        }
        foreach (var (name, path) in dlls.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            var pe = PeImage.Read(path);
            list.Add(new WindowerPluginInfo
            {
                Name = name, Installed = true, IsWindowerPlugin = IsPlugin(pe), FileVersion = pe?.FileVersion, Path = path,
            });
        }
        return list;
    }

    public static bool IsWindowerPlugin(string dllPath) => IsPlugin(PeImage.Read(dllPath));

    private static bool IsPlugin(PeImage? pe) =>
        pe is { Machine: PeImage.MachineI386 } && RequiredExports.All(x => pe.Exports.Contains(x, StringComparer.Ordinal));
}
