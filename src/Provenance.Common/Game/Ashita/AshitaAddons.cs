using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Provenance.Common.Game.Ashita;

public sealed record AshitaAddon(string LoadName, string Folder, string Name, string? Author, string? Version, string? Description, bool NotForAshitaV4);

public static partial class AshitaAddons
{
    private const int MaxMainFile = 4 << 20;

    public static IReadOnlyList<AshitaAddon> Scan(string ashitaDir)
    {
        var list = new List<AshitaAddon>();
        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(Path.Combine(ashitaDir, "addons")).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return list; }
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (name.Length == 0 || name[0] == '.' || name.Equals("libs", StringComparison.OrdinalIgnoreCase)) continue;
            if (Read(dir, name) is { } addon) list.Add(addon);
        }
        return list.OrderBy(a => a.LoadName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static AshitaAddon? Read(string dir, string name)
    {
        string text;
        try
        {
            using var fs = new FileStream(Path.Combine(dir, name + ".lua"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(fs.Length, MaxMainFile)];
            buffer = buffer[..fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)];
            text = new UTF8Encoding(false).GetString(buffer).TrimStart((char)0xFEFF);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

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
                if (Clean(value) is { } v) fields[key] = v;
            }
            // Windower and Ashita v3 addons fill in _addon instead. Some v4 ones mention it too, but they set addon.* as well
            foreign = OldTable().IsMatch(text) && !Assigned().IsMatch(text);
        }
        catch (RegexMatchTimeoutException) { }
        return new AshitaAddon(name, dir, fields.GetValueOrDefault("name") ?? name, fields.GetValueOrDefault("author"),
                               fields.GetValueOrDefault("version"), fields.GetValueOrDefault("desc"), foreign);
    }

    private static Dictionary<string, string> Locals(string text)
    {
        var locals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in LocalString().Matches(text)) locals.TryAdd(m.Groups["id"].Value, Unescape(m.Groups["value"].Value));
        return locals;
    }

    private static string Unescape(string s) => Regex.Replace(s, @"\\(['""\\])", "$1");

    // no control, format or line break characters, so a name can't bring a bidi override, a zero-width space or a tag
    // character into the UI. Runes, since tags are surrogate pairs
    internal static string? Clean(string? s)
    {
        if (s is null) return null;
        var sb = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
            if (!Rune.IsControl(r) && Rune.GetUnicodeCategory(r) is not (UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
                sb.Append(r.ToString());
        var t = sb.ToString().Trim();
        if (t.Length > 500) t = char.IsHighSurrogate(t[499]) ? t[..499] : t[..500];
        return t.Length == 0 ? null : t;
    }

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
