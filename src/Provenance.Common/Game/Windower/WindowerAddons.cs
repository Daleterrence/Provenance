using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Provenance.Common.Game.Windower;

public sealed record WindowerAddonInfo
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? Author { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<string> Commands { get; init; } = [];
    public string? Description { get; init; }
    public string? BugTracker { get; init; }
    public string? Support { get; init; }
    public required string MainFile { get; init; }
}

public static partial class WindowerAddons
{
    // the _addon lines sit at the top, nobody needs the rest of a 200 KB addon for them
    private const int MaxRead = 64 * 1024;
    private const int RegexMs = 500;

    // addons\<name>\<name>.lua, the name matching its folder in any case. libs is Windower's shared code, never an
    // addon. A folder that can't be read (a dangling junction, no access) is just skipped
    public static IReadOnlyList<WindowerAddonInfo> Scan(string windowerDir)
    {
        var dir = Path.Combine(windowerDir, "addons");
        List<string> folders;
        try { folders = Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        var listed = ReadAddonsXml(windowerDir);
        var list = new List<WindowerAddonInfo>();
        foreach (var folder in folders)
        {
            var name = Path.GetFileName(folder);
            if (name.Equals("libs", StringComparison.OrdinalIgnoreCase)) continue;
            string? main;
            try
            {
                main = Directory.EnumerateFiles(folder, "*.lua")
                    .FirstOrDefault(f => Path.GetFileName(f).Equals(name + ".lua", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (main is null) continue;
            var h = ReadHeader(ReadStart(main));
            listed.TryGetValue(name, out var x);
            list.Add(new WindowerAddonInfo
            {
                Name = name, DisplayName = h.Name ?? name, Author = h.Author ?? Blank(x?.Author), Version = h.Version,
                Commands = h.Commands, Description = x?.Description, BugTracker = x?.BugTracker, Support = x?.Support,
                MainFile = main,
            });
        }
        return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ReadStart(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[(int)Math.Min(fs.Length, MaxRead)];
            fs.ReadExactly(buf);
            return new UTF8Encoding(false).GetString(buf.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]) ? buf[3..] : buf);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    public sealed record Header(string? Name, string? Author, string? Version, IReadOnlyList<string> Commands);

    // regexes, not a Lua parser. First assignment not behind a -- wins, and commands only count when they're all strings
    public static Header ReadHeader(string lua)
    {
        if (lua.Length > MaxRead) lua = lua[..MaxRead];
        string? name = null, author = null, version = null;
        var commands = new List<string>();
        bool commandsSeen = false, commandSeen = false;
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
                    case "commands" when !commandsSeen:
                    case "command" when !commandSeen:
                        if (key == "commands") commandsSeen = true; else commandSeen = true;
                        foreach (var c in Strings(value))
                            if (!commands.Contains(c, StringComparer.OrdinalIgnoreCase)) commands.Add(c);
                        break;
                }
            }
        }
        catch (RegexMatchTimeoutException) { }
        return new Header(Blank(name), Blank(author), Blank(version), commands);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool Commented(string lua, int at)
    {
        int start = lua.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        return lua.AsSpan(start, at - start).Contains("--", StringComparison.Ordinal);
    }

    private static string? Text(string value) => StringLiteral().Match(value) is { Success: true, Index: 0 } m && m.Length == value.Length ? Unquote(m) : null;

    private static IReadOnlyList<string> Strings(string value)
    {
        if (Text(value) is { } single) return [single];
        var table = Table().Match(value);
        if (!table.Success) return [];
        var items = new List<string>();
        var rest = table.Groups["t"].Value;
        int at = 0;
        foreach (Match m in StringLiteral().Matches(rest))
        {
            if (rest[at..m.Index].Trim().Trim(',', ';').Trim().Length > 0) return [];
            items.Add(Unquote(m));
            at = m.Index + m.Length;
        }
        return rest[at..].Trim().Trim(',', ';').Trim().Length > 0 ? [] : items.Where(s => s.Length > 0).ToList();
    }

    private static string Unquote(Match m) =>
        m.Groups["long"].Success ? m.Groups["long"].Value
            : Regex.Replace(m.Groups["s"].Value, @"\\(.)", e => e.Groups[1].Value switch { "n" => "\n", "t" => "\t", var c => c }, RegexOptions.None, TimeSpan.FromMilliseconds(RegexMs));

    // every value has a length cap, so a file full of unclosed quotes or [[ can't make this quadratic
    [GeneratedRegex(@"(?<![\w.])_addon\s*\.\s*(?<k>name|author|version|commands|command)\s*=\s*(?<v>'(?:\\.|[^'\\\r\n]){0,256}'|""(?:\\.|[^""\\\r\n]){0,256}""|\[(=*)\[[^\r\n]{0,256}?\]\1\]|\{[^{}]{0,1024}\}|[0-9][0-9.]{0,31})", RegexOptions.None, RegexMs)]
    private static partial Regex Field();

    [GeneratedRegex(@"'(?<s>(?:\\.|[^'\\\r\n]){0,256})'|""(?<s>(?:\\.|[^""\\\r\n]){0,256})""|\[(?<eq>=*)\[(?<long>[^\r\n]{0,256}?)\]\k<eq>\]", RegexOptions.None, RegexMs)]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"^\{(?<t>[^{}]*)\}$", RegexOptions.None, RegexMs)]
    private static partial Regex Table();

    [GeneratedRegex(@"^[0-9][0-9.]*$", RegexOptions.None, RegexMs)]
    private static partial Regex Number();

    private sealed record Listed(string Author, string? Description, string BugTracker, string Support);

    // updates\addons.xml, out of the Lua repo. First entry for a name wins, with Windower's own default links
    private static Dictionary<string, Listed> ReadAddonsXml(string windowerDir)
    {
        var map = new Dictionary<string, Listed>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(windowerDir, "updates", "addons.xml");
        try
        {
            if (!File.Exists(path)) return map;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var r = XmlReader.Create(fs, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            foreach (var a in XDocument.Load(r).Root?.Elements("addon") ?? [])
            {
                var name = ((string?)a.Element("name"))?.Trim();
                if (string.IsNullOrEmpty(name) || map.ContainsKey(name)) continue;
                var desc = (string?)a.Element("description");
                map[name] = new Listed(
                    ((string?)a.Element("author"))?.Trim() ?? "",
                    desc is null ? null : Spaces().Replace(desc, " ").Trim(),
                    ((string?)a.Element("bugtracker"))?.Trim() ?? "https://github.com/Windower/Lua/issues",
                    ((string?)a.Element("support"))?.Trim() ?? "https://discord.gg/aUrHCvk");
            }
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException) { }
        return map;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
