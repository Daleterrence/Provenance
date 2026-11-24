using System.Buffers.Binary;
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
    public uint? InterfaceVersion { get; init; }
    public bool Outdated { get; init; }
    public bool NeedsNewerWindower { get; init; }
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
            return pe is null ? null : Facts(pe);
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
                Required = m.Required, Hidden = m.Hidden, HasResources = m.Resources, InterfaceVersion = facts?.Interface,
            });
        }
        foreach (var (name, found) in dlls.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            var facts = Probe(found);
            list.Add(new WindowerPluginInfo
            {
                Listing = new AddonListing(name, name, facts?.Version?.ToString(), null, null, found.Path),
                Installed = true, IsWindowerPlugin = facts is { IsPlugin: true }, FileVersion = facts?.Version, InterfaceVersion = facts?.Interface,
            });
        }
        // Windower's hook skips any plugin whose interface isn't its own, and LuaCore is always built for the hook that's there
        var current = list.FirstOrDefault(p => p.Installed && p.Listing.Key.Equals("LuaCore", StringComparison.OrdinalIgnoreCase))?.InterfaceVersion;
        if (current is { } now)
            for (int i = 0; i < list.Count; i++)
                if (list[i] is { Installed: true, IsWindowerPlugin: true, Required: false, InterfaceVersion: { } v } && v != now)
                    list[i] = list[i] with { Outdated = v < now, NeedsNewerWindower = v > now };
        return list;
    }

    // 0x0004xxxx is an early Windower 4 hook's, later ones pack a byte a part (0x04070300 is 4.7.3.0)
    public static string InterfaceName(uint v) =>
        v >> 16 == 0x0004 ? "an older version of Windower 4" : $"Windower {v >> 24}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";

    public static bool IsWindowerPlugin(string dllPath) => IsPlugin(PeFile.Load(dllPath));

    internal static bool IsPlugin(PeFile? pe) =>
        pe is { Machine: PeFile.MachineI386 } && RequiredExports.All(x => pe.Exports.ContainsKey(x));

    internal static PeFacts Facts(PeFile pe) => IsPlugin(pe) ? new PeFacts(true, pe.FileVersion, InterfaceVersion(pe)) : new PeFacts(false, pe.FileVersion);

    // GetInterfaceVersion is "mov eax, imm32" then ret, maybe behind a push ebp frame or a jmp or two
    internal static uint? InterfaceVersion(PeFile pe)
    {
        if (!pe.Exports.TryGetValue("GetInterfaceVersion", out var rva)) return null;
        ulong at = rva;
        for (int hops = 0; hops < 3; hops++)
        {
            var b = pe.AtRva(at, 16);
            if (b.Length >= 5 && b[0] == 0xE9)
            {
                at = (ulong)((long)at + 5 + BinaryPrimitives.ReadInt32LittleEndian(b[1..]));
                continue;
            }
            int i = b.Length >= 3 && b[0] == 0x55 && b[1] == 0x8B && b[2] == 0xEC ? 3 : 0;
            if (b.Length < i + 6 || b[i] != 0xB8) return null;
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(b[(i + 1)..]);
            int end = i + 5;
            if (i == 3)
            {
                if (b.Length <= end || b[end] != 0x5D) return null;
                end++;
            }
            return b.Length > end && b[end] == 0xC3 ? value : null;
        }
        return null;
    }
}
