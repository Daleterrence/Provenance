using System.Text.RegularExpressions;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.IO;

namespace Provenance.Common.Game.Ashita;

public sealed record AshitaAddon(AddonListing Listing, bool NotForAshitaV4);

public static partial class AshitaAddons
{
    private const int MaxMainFile = 4 << 20;

    public static IReadOnlyList<AshitaAddon> Scan(string ashitaDir, IReadOnlyDictionary<string, AddonDocs>? docs = null)
    {
        docs ??= AshitaDocs.Scan(ashitaDir);
        return LuaAddonFolders.Walk(Path.Combine(ashitaDir, "addons"), skipDotted: true)
            .Select(f => Read(f.Folder, f.Name, f.Main, docs)).OfType<AshitaAddon>()
            .OrderBy(a => a.Listing.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static AshitaAddon? Read(string dir, string name, string main, IReadOnlyDictionary<string, AddonDocs> docs)
    {
        if (TextFiles.ReadHead(main, MaxMainFile) is not { } text) return null;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string>? locals = null;
        bool foreign = false;
        try
        {
            foreach (Match m in Field().Matches(text))
            {
                var key = m.Groups["key"].Value;
                if (fields.ContainsKey(key)) continue;
                // cross-platform addons do addon.name = name off a local, so follow it one step
                var value = m.Groups["value"].Success ? Unescape(m.Groups["value"].Value)
                                                      : (locals ??= Locals(text)).GetValueOrDefault(m.Groups["local"].Value);
                if (AddonListing.Clean(value) is { } v) fields[key] = v;
            }
            // Windower and Ashita v3 addons fill in _addon instead
            foreign = OldTable().IsMatch(text) && !Assigned().IsMatch(text);
        }
        catch (RegexMatchTimeoutException) { }
        return new AshitaAddon(new AddonListing(name, fields.GetValueOrDefault("name") ?? name, fields.GetValueOrDefault("version"),
                                                fields.GetValueOrDefault("author"), fields.GetValueOrDefault("desc"), dir)
                               { Docs = ReadmeFiles.Find(dir) ?? docs.GetValueOrDefault(name) }, foreign);
    }

    private static Dictionary<string, string> Locals(string text)
    {
        var locals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in LocalString().Matches(text)) locals.TryAdd(m.Groups["id"].Value, Unescape(m.Groups["value"].Value));
        return locals;
    }

    private static string Unescape(string s) => Regex.Replace(s, @"\\(['""\\])", "$1");

    [GeneratedRegex(@"^[ \t]*addon\.(?<key>name|author|version|desc)[ \t]*=[ \t]*(?:(['""])(?<value>(?:\\.|(?!\1)[^\\\r\n])*)\1|(?<local>[A-Za-z_][A-Za-z0-9_]*)(?=[ \t]*(?:;|--|\r|$)))",
                    RegexOptions.Multiline, 1000)]
    private static partial Regex Field();

    [GeneratedRegex(@"^[ \t]*local[ \t]+(?<id>[A-Za-z_][A-Za-z0-9_]*)[ \t]*=[ \t]*(['""])(?<value>(?:\\.|(?!\1)[^\\\r\n])*)\1", RegexOptions.Multiline, 1000)]
    private static partial Regex LocalString();

    [GeneratedRegex(@"^[ \t]*addon\.(?:name|author|version)[ \t]*=", RegexOptions.Multiline, 1000)]
    private static partial Regex Assigned();

    [GeneratedRegex(@"(?<![\w.])_addon\b", RegexOptions.None, 1000)]
    private static partial Regex OldTable();
}
