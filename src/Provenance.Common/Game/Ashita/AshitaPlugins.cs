using System.Globalization;
using Provenance.Common.Game.ThirdParty;

namespace Provenance.Common.Game.Ashita;

public enum AshitaPluginKind { Plugin, PolPlugin }

public sealed record AshitaPlugin(AddonListing Listing, AshitaPluginKind Kind, double? Version, double? InterfaceVersion, bool ForOtherAshita);

public static class AshitaPlugins
{
    // what Ashita.dll 4.3.2.1 answers, used when the installed one can't be read
    public const double ExpectedInterfaceVersion = 4.30;

    private const long MaxDll = 64L << 20;

    // a plugin's documentation is only looked for in docs\, a readme sat in plugins\ belongs to nobody
    public static IReadOnlyList<AshitaPlugin> Scan(string ashitaDir, IReadOnlyDictionary<string, AddonDocs>? docs = null)
    {
        docs ??= AshitaDocs.Scan(ashitaDir);
        double core = CoreInterfaceVersion(ashitaDir);
        var list = new List<AshitaPlugin>();
        foreach (var folder in (string[])["plugins", "polplugins"])
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, dll) in Dlls(Path.Combine(ashitaDir, folder)))
            {
                if (!seen.Add(name)) continue;
                try { if (Read(dll, name, core) is { } plugin) list.Add(plugin with { Listing = plugin.Listing with { Docs = docs.GetValueOrDefault(name) } }); }
                catch (Exception) { }   // one odd file never takes the whole list down with it
            }
        }
        return list.OrderBy(p => p.Kind).ThenBy(p => p.Listing.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Ashita refuses any plugin whose expGetInterfaceVersion isn't exactly its own
    public static double CoreInterfaceVersion(string ashitaDir)
    {
        try
        {
            var core = AshitaPluginMetadata.Load(Path.Combine(ashitaDir, "Ashita.dll"), MaxDll);
            if (core is not null && core.Exports.TryGetValue("GetInterfaceVersion", out var rva) && core.ExportedDouble(rva) is { } v) return v;
        }
        catch (Exception) { }   // can't read the core, so go with the expected version
        return ExpectedInterfaceVersion;
    }

    // Ashita looks for <name>.dll, then <name>\<name>.dll
    private static List<(string Name, string Dll)> Dlls(string dir)
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                if (Path.GetExtension(f).Equals(".dll", StringComparison.OrdinalIgnoreCase)) list.Add((Path.GetFileNameWithoutExtension(f), f));
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(d);
                var dll = Path.Combine(d, name + ".dll");
                if (File.Exists(dll)) list.Add((name, dll));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return list;
    }

    private static AshitaPlugin? Read(string dll, string loadName, double core) => Read(AshitaPluginMetadata.Load(dll, MaxDll), dll, loadName, core);

    internal static AshitaPlugin? Read(byte[] bytes, string dll, string loadName, double core) => Read(AshitaPluginMetadata.Parse(bytes), dll, loadName, core);

    private static AshitaPlugin? Read(AshitaPluginMetadata? pe, string dll, string loadName, double core)
    {
        if (pe is null) return null;
        var exports = pe.Exports;
        AshitaPluginKind kind;
        if (exports.ContainsKey("expCreatePolPlugin")) kind = AshitaPluginKind.PolPlugin;
        else if (exports.ContainsKey("expCreatePlugin")) kind = AshitaPluginKind.Plugin;
        else return null;
        double? iface = exports.TryGetValue("expGetInterfaceVersion", out var rva) ? pe.ExportedDouble(rva) : null;
        var meta = pe.Vtable();
        // the getters hand back float constants widened to double (4.0999...), Ashita prints them with %.2f
        var listing = new AddonListing(loadName, AddonListing.Clean(meta?.Name) ?? loadName, meta?.Version.ToString("0.00", CultureInfo.InvariantCulture),
                                       AddonListing.Clean(meta?.Author), AddonListing.Clean(meta?.Description), dll);
        return new AshitaPlugin(listing, kind, meta?.Version, iface, iface is { } i && i != core);
    }
}
