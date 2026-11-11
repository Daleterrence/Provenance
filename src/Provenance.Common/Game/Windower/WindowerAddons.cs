using System.Text.RegularExpressions;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.IO;

namespace Provenance.Common.Game.Windower;

public sealed record WindowerAddonInfo(AddonListing Listing, string MainFile);

public sealed record WindowerAddonEntry(string? Author, string? Description);

public static partial class WindowerAddons
{
    // the _addon lines sit at the top, nobody needs the rest of a 200 KB addon for them
    private const int MaxRead = 64 * 1024;
    private const int RegexMs = 500;

    // readmes looks in every addon's folder for one, which the launch has no use for
    public static IReadOnlyList<WindowerAddonInfo> Scan(string windowerDir, IReadOnlyDictionary<string, WindowerAddonEntry>? addonsXml = null, bool readmes = false)
    {
        var addonsDir = Path.Combine(windowerDir, "addons");
        if (!Directory.Exists(addonsDir)) return [];
        var listed = addonsXml ?? AddonsXml(windowerDir);
        var list = new List<WindowerAddonInfo>();
        foreach (var (name, folder, main) in LuaAddonFolders.Walk(addonsDir, skipDotted: false))
        {
            var h = ReadHeader(TextFiles.ReadHead(main, MaxRead) ?? "");
            listed.TryGetValue(name, out var x);
            list.Add(new WindowerAddonInfo(new AddonListing(name, h.Name ?? name, h.Version, h.Author ?? x?.Author, x?.Description, folder)
                                           { Docs = readmes ? ReadmeFiles.Find(folder) : null }, main));
        }
        return list.OrderBy(a => a.Listing.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal sealed record Header(string? Name, string? Author, string? Version);

    // regexes, not a Lua parser. First assignment not behind a -- wins
    internal static Header ReadHeader(string lua)
    {
        if (lua.Length > MaxRead) lua = lua[..MaxRead];
        string? name = null, author = null, version = null;
        try
        {
            foreach (Match m in Field().Matches(lua))
            {
                if (Commented(lua, m.Index)) continue;
                var key = m.Groups["k"].Value;
                var value = m.Groups["v"].Value;
                switch (key)
                {
                    case "name": name ??= Text(value); break;
                    case "author": author ??= Text(value); break;
                    case "version": version ??= Text(value) ?? (Number().IsMatch(value) ? value : null); break;
                }
            }
        }
        catch (RegexMatchTimeoutException) { }
        return new Header(AddonListing.Clean(name), AddonListing.Clean(author), AddonListing.Clean(version));
    }

    private static bool Commented(string lua, int at)
    {
        int start = lua.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        return lua.AsSpan(start, at - start).Contains("--", StringComparison.Ordinal);
    }

    private static string? Text(string value) => StringLiteral().Match(value) is { Success: true, Index: 0 } m && m.Length == value.Length ? Unquote(m) : null;

    private static string Unquote(Match m) =>
        m.Groups["long"].Success ? m.Groups["long"].Value
            : Regex.Replace(m.Groups["s"].Value, @"\\(.)", e => e.Groups[1].Value switch { "n" => "\n", "t" => "\t", var c => c }, RegexOptions.None, TimeSpan.FromMilliseconds(RegexMs));

    // every value has a length cap, so a file full of unclosed quotes or [[ can't make this quadratic
    [GeneratedRegex(@"(?<![\w.])_addon\s*\.\s*(?<k>name|author|version)\s*=\s*(?<v>'(?:\\.|[^'\\\r\n]){0,256}'|""(?:\\.|[^""\\\r\n]){0,256}""|\[(=*)\[[^\r\n]{0,256}?\]\1\]|[0-9][0-9.]{0,31})", RegexOptions.None, RegexMs)]
    private static partial Regex Field();

    [GeneratedRegex(@"'(?<s>(?:\\.|[^'\\\r\n]){0,256})'|""(?<s>(?:\\.|[^""\\\r\n]){0,256})""|\[(?<eq>=*)\[(?<long>[^\r\n]{0,256}?)\]\k<eq>\]", RegexOptions.None, RegexMs)]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"^[0-9][0-9.]*$", RegexOptions.None, RegexMs)]
    private static partial Regex Number();

    // updates\addons.xml, out of the Lua repo, by name in any case. First entry for a name wins, and the text comes
    // out cleaned
    public static IReadOnlyDictionary<string, WindowerAddonEntry> AddonsXml(string windowerDir)
    {
        var map = new Dictionary<string, WindowerAddonEntry>(StringComparer.OrdinalIgnoreCase);
        if (TextFiles.LoadXml(Path.Combine(windowerDir, "updates", "addons.xml")) is not { } doc) return map;
        foreach (var a in doc.Root?.Elements("addon") ?? [])
        {
            var name = ((string?)a.Element("name"))?.Trim();
            if (string.IsNullOrEmpty(name) || map.ContainsKey(name)) continue;
            var desc = (string?)a.Element("description");
            map[name] = new WindowerAddonEntry(AddonListing.Clean((string?)a.Element("author")), desc is null ? null : AddonListing.Clean(WindowerText.Spaces(desc)));
        }
        return map;
    }
}
