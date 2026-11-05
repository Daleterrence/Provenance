using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using Provenance.Common.Security;

namespace Provenance.Common.Game.Ashita;

// Ashita reads these through the ANSI profile API, so they stay in the ANSI code page. A line nobody edits goes back
// byte for byte
public sealed class AshitaIniFile
{
    // the profile API's line length wraps past this
    public const int MaxLine = 65534;

    private static readonly char[] Space = [' ', '\t', '\v', '\f'];

    private static readonly (string Section, string Key, int Max)[] Limits =
    [
        ("ashita.boot", "file", 259), ("ashita.boot", "command", 1023), ("import", "import", 255),
    ];

    // as Settings labels them
    private static readonly (string Section, string Key, string Label)[] Labels =
    [
        ("ashita.launcher", "name", "The name"), ("ashita.boot", "script", "The script"),
        ("ashita.boot", "args", "The script arguments"), ("ffxi.registry", "0042", "The screenshots and settings folder"),
    ];

    // the only four Ashita-cli reads, and it takes the first copy. Ashita.dll reads everything else and takes the last
    private static readonly (string Section, string Key)[] CliKeys =
    [
        ("ashita.boot", "file"), ("ashita.boot", "command"), ("ashita.language", "playonline"), ("ashita.language", "ashita"),
    ];

    private enum Kind { Blank, Comment, Section, Key, Other }

    private readonly record struct Parsed(Kind Kind, string Name, int ValueStart, int ValueEnd);

    private sealed class Line(string text, string ending, byte[]? raw)
    {
        public string Text = text;
        public string Ending = ending;
        public byte[]? Raw = raw;
    }

    private readonly List<Line> lines;
    private readonly Encoding ansi;
    private readonly Encoding encoding;
    private readonly string newline;
    private readonly bool looksUtf8;
    private bool utf8Bom;

    static AshitaIniFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private AshitaIniFile(List<Line> lines, Encoding ansi, bool utf16, bool utf8Bom, bool looksUtf8)
    {
        this.lines = lines;
        this.ansi = ansi;
        encoding = utf16 ? Encoding.Unicode : ansi;
        IsUtf16 = utf16;
        this.utf8Bom = utf8Bom;
        this.looksUtf8 = looksUtf8;
        Changed = utf8Bom;
        newline = new[] { "\r\n", "\n", "\r" }.MaxBy(e => lines.Count(l => l.Ending == e))!;
    }

    public bool IsUtf16 { get; }
    public bool Changed { get; private set; }

    [DllImport("kernel32.dll")]
    private static extern int GetACP();

    internal static Encoding Ansi(int codePage) => Encoding.GetEncoding(codePage == 0 ? GetACP() : codePage);

    private static bool Fits(Encoding ansi, string text) => ansi.GetString(ansi.GetBytes(text)) == text;

    public static bool FitsCodePage(string text, int codePage = 0) => Fits(Ansi(codePage), text);

    public static int AnsiLength(string text, int codePage = 0) => Ansi(codePage).GetByteCount(text);

    public static AshitaIniFile Load(string path, int codePage = 0) => Parse(File.ReadAllBytes(path), codePage);

    public static AshitaIniFile Parse(byte[] data, int codePage = 0)
    {
        var ansi = Ansi(codePage);
        bool utf16 = data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE;
        bool utf8Bom = !utf16 && data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF;
        int start = utf16 ? 2 : utf8Bom ? 3 : 0, unit = utf16 ? 2 : 1;
        var enc = utf16 ? Encoding.Unicode : ansi;
        bool Is(int at, char c) => data[at] == c && (!utf16 || data[at + 1] == 0);

        // Windows ends a line at a lone CR too. No DBCS code page has a trail byte under 0x40, so this is safe
        var lines = new List<Line>();
        int from = start;
        for (int i = start; i + unit <= data.Length; i += unit)
        {
            bool cr = Is(i, '\r');
            if (!cr && !Is(i, '\n')) continue;
            var raw = data[from..i];
            var ending = !cr ? "\n" : i + 2 * unit <= data.Length && Is(i + unit, '\n') ? "\r\n" : "\r";
            if (ending == "\r\n") i += unit;
            lines.Add(new Line(enc.GetString(raw), ending, raw));
            from = i + unit;
        }
        if (from < data.Length)
        {
            var raw = data[from..];
            lines.Add(new Line(enc.GetString(raw), "", raw));
        }
        var body = data.AsSpan(start);
        bool looksUtf8 = !utf16 && ansi.CodePage != 65001 && body.ContainsAnyInRange((byte)0x80, (byte)0xFF) && Utf8.IsValid(body);
        return new AshitaIniFile(lines, ansi, utf16, utf8Bom, looksUtf8);
    }

    public static AshitaIniFile FromText(string text, int codePage = 0)
    {
        var ansi = Ansi(codePage);
        if (!Fits(ansi, text)) throw new ArgumentException("The text has characters the ANSI code page can't hold.", nameof(text));
        var f = Parse(ansi.GetBytes(text), codePage);
        f.Changed = true;
        return f;
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        if (IsUtf16) ms.Write([0xFF, 0xFE]);
        foreach (var l in lines)
        {
            ms.Write(l.Raw ?? encoding.GetBytes(l.Text));
            ms.Write(encoding.GetBytes(l.Ending));
        }
        return ms.ToArray();
    }

    public void Save(string path, bool backup = true)
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        if (AshitaBootFiles.IsShippedExample(name))
            throw Shown.Mark(new IOException($"{name} is one of Ashita's example files. Duplicate it and change the copy instead."));
        var dir = Path.GetDirectoryName(full)!;
        var bak = full + ".bak";
        LinkCheck.Refuse(full, bak, dir, Path.GetDirectoryName(dir) ?? dir);
        using var pins = LinkCheck.Hold(Path.GetDirectoryName(dir) ?? dir, dir);
        full = pins.Real(full);
        bak = pins.Real(bak);
        // a backup from before a token was taken out would keep it on disk
        bool dropBackup = File.Exists(full) && Parse(File.ReadAllBytes(full), ansi.CodePage).Secrets().Except(Secrets()).Any();
        var tmp = $"{full}.{Guid.NewGuid():N}.tmp";
        bool replacing = false;
        try
        {
            File.WriteAllBytes(tmp, ToBytes());
            LinkCheck.Recheck(pins, dir, full, bak);
            replacing = File.Exists(full);
            if (replacing) File.Replace(tmp, full, backup && !dropBackup ? bak : null);
            else File.Move(tmp, full);
        }
        catch
        {
            // a Replace that failed halfway can leave the temp file as the only copy
            if (!replacing || File.Exists(full))
                try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        if (dropBackup) File.Delete(bak);
        Changed = false;
        utf8Bom = false;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsSpace(char c) => Array.IndexOf(Space, c) >= 0;

    private static Parsed Classify(string text)
    {
        var t = text.AsSpan().TrimStart(Space);
        if (t.IsEmpty) return new(Kind.Blank, "", 0, 0);
        if (t[0] == ';') return new(Kind.Comment, "", 0, 0);
        // "[ashita.boot" with no ] still opens the section for Windows
        if (t[0] == '[')
        {
            int close = t.IndexOf(']');
            return new(Kind.Section, (close >= 0 ? t[1..close] : t[1..]).Trim(Space).ToString(), 0, 0);
        }
        int eq = text.IndexOf('=');
        if (eq < 0) return new(Kind.Other, "", 0, 0);
        int start = eq + 1, end = text.Length;
        while (start < end && IsSpace(text[start])) start++;
        while (end > start && IsSpace(text[end - 1])) end--;
        return new(Kind.Key, text[..eq].Trim(Space), start, end);
    }

    private static (string Key, string Value)? KeyOf(string text)
    {
        var t = text.AsSpan().TrimStart(Space);
        if (t.IsEmpty || t[0] == '[') return null;
        t = t.TrimStart([';', '#', ' ', '\t', '\v', '\f']);
        int eq = t.IndexOf('=');
        return eq < 0 ? null : (t[..eq].Trim(Space).ToString(), t[(eq + 1)..].Trim(Space).ToString());
    }

    private Parsed[] Scan() => lines.Select(l => Classify(l.Text)).ToArray();

    private static List<(int Header, int End)> Blocks(Parsed[] p, string section)
    {
        var found = new List<(int, int)>();
        int open = -1;
        for (int i = 0; i < p.Length; i++)
        {
            if (p[i].Kind != Kind.Section) continue;
            if (open >= 0) found.Add((open, i));
            open = Same(p[i].Name, section) ? i : -1;
        }
        if (open >= 0) found.Add((open, p.Length));
        return found;
    }

    private static (int Header, int End)? First(Parsed[] p, string section) =>
        Blocks(p, section) is [var first, ..] ? first : null;

    private string Value(int i, Parsed p) => lines[i].Text[p.ValueStart..p.ValueEnd];

    public IReadOnlyList<string> Sections => Scan().Where(p => p.Kind == Kind.Section).Select(p => p.Name).ToList();

    public string? Get(string section, string key)
    {
        var p = Scan();
        if (First(p, section) is not (var header, var end)) return null;
        bool cli = CliKeys.Any(k => Same(k.Section, section) && Same(k.Key, key));
        string? found = null;
        for (int i = header + 1; i < end; i++)
        {
            if (p[i].Kind != Kind.Key || !Same(p[i].Name, key)) continue;
            found = Value(i, p[i]);
            if (cli) break;
        }
        return found;
    }

    public IReadOnlyList<(string Key, string Value)> Entries(string section)
    {
        var p = Scan();
        var found = new List<(string, string)>();
        if (First(p, section) is (var header, var end))
            for (int i = header + 1; i < end; i++)
                if (p[i].Kind == Kind.Key) found.Add((p[i].Name, Value(i, p[i])));
        return found;
    }

    private static string Label(string section, string key) =>
        Labels.FirstOrDefault(l => Same(l.Section, section) && Same(l.Key, key)).Label ?? "That setting";

    private static string TooLong(string section, string key) => $"{Label(section, key)} can't be this long, Ashita can't read that much.";

    public string? ValueProblem(string section, string key, string value)
    {
        var label = Label(section, key);
        if (value.Any(c => char.IsControl(c) && c != '\t')) return $"{label} can't have line breaks or control characters.";
        if (value.Length > 0 && (IsSpace(value[0]) || IsSpace(value[^1]))) return $"{label} can't start or end with a space.";
        if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
            return $"{label} can't be wrapped in quotes, Ashita would keep the quotes.";
        if (value.Contains(" ;") || value.Contains("\t;"))
            return $"{label} can't have a ; after a space. It looks like a comment, but Ashita keeps it as part of the value.";
        if (!Fits(ansi, value))
            return $"{label} can only use characters Ashita can read with this PC's language for non-Unicode programs.";
        int bytes = ansi.GetByteCount(value);
        foreach (var (s, k, max) in Limits)
            if (Same(s, section) && Same(k, key) && bytes > max) return $"{label} can't be longer than {max} characters for Ashita.";
        if (ansi.GetByteCount(key) + 3 + bytes > MaxLine) return TooLong(section, key);
        return null;
    }

    private static void CheckName(string name, bool section)
    {
        bool bad = name.Length == 0 || name != name.Trim(Space) || name.Any(char.IsControl)
                   || (section ? name.Contains(']') : name.Contains('=') || name[0] is ';' or '[' or '#');
        if (bad) throw Shown.Mark(new ArgumentException($"\"{name}\" can't be used as a {(section ? "section" : "setting")} name in a boot configuration."));
    }

    // a second command line or plugin argument goes rather than staying behind as a comment, either can hold a token
    private static bool Secret(string section, string key) => Same(key, "command") || Same(section, "ashita.polplugins.args");

    public void Set(string section, string key, string value)
    {
        CheckName(section, true);
        CheckName(key, false);
        if (ValueProblem(section, key, value) is { } problem) throw Shown.Mark(new ArgumentException(problem));
        if (!Fits(ansi, section + key))
            throw Shown.Mark(new ArgumentException("That setting's name has characters Ashita can't read with this PC's language for non-Unicode programs."));
        var p = Scan();
        if (First(p, section) is not (var header, var end))
        {
            int at = lines.Count;
            if (at > 0 && p[^1].Kind != Kind.Blank) Insert(at++, "");
            Insert(at++, $"[{section.ToLowerInvariant()}]");
            Insert(at, $"{key.ToLowerInvariant()} = {value}");
            return;
        }
        var copies = Enumerable.Range(header + 1, end - header - 1).Where(i => p[i].Kind == Kind.Key && Same(p[i].Name, key)).ToList();
        if (copies.Count == 0)
        {
            int last = end;
            while (last > header + 1 && p[last - 1].Kind == Kind.Blank) last--;
            Insert(last, $"{key.ToLowerInvariant()} = {value}");
            return;
        }
        var text = lines[copies[0]].Text;
        var before = text[..p[copies[0]].ValueStart];
        if (p[copies[0]].ValueStart == p[copies[0]].ValueEnd && value.Length > 0 && before.Length >= 2 && before[^1] == '=' && IsSpace(before[^2]))
            before += " ";
        var updated = before + value + text[p[copies[0]].ValueEnd..];
        if (ansi.GetByteCount(updated) > MaxLine) throw Shown.Mark(new ArgumentException(TooLong(section, key)));
        if (updated != text) Replace(copies[0], updated);
        foreach (var i in Enumerable.Reverse(copies.Skip(1)))
            if (Secret(section, key)) RemoveAt(i);
            else Replace(i, ";" + lines[i].Text);
    }

    public int Remove(string section, string key)
    {
        var p = Scan();
        var gone = Blocks(p, section).SelectMany(b => Enumerable.Range(b.Header + 1, b.End - b.Header - 1))
                                     .Where(i => p[i].Kind == Kind.Key && Same(p[i].Name, key)).ToList();
        foreach (var i in Enumerable.Reverse(gone)) RemoveAt(i);
        return gone.Count;
    }

    // every line setting the key, commented out with ; or # or not, in one section or (null) the whole file
    public int Scrub(string? section, string key)
    {
        var gone = LinesIn(section).Where(i => KeyOf(lines[i].Text) is { } k && Same(k.Key, key)).ToList();
        foreach (var i in Enumerable.Reverse(gone)) RemoveAt(i);
        return gone.Count;
    }

    private IEnumerable<int> LinesIn(string? section)
    {
        var p = Scan();
        string? current = null;
        for (int i = 0; i < p.Length; i++)
        {
            if (p[i].Kind == Kind.Section) current = p[i].Name;
            else if (section is null || (current is not null && Same(current, section))) yield return i;
        }
    }

    private HashSet<string> Secrets() =>
        LinesIn(null).Where(i => KeyOf(lines[i].Text) is { } k && Same(k.Key, "command"))
                     .Concat(LinesIn("ashita.polplugins.args"))
                     .Select(i => KeyOf(lines[i].Text)?.Value).OfType<string>().Where(v => v.Length > 0).ToHashSet();

    // Ashita.dll looks names up by their exact bytes, so it never sees [Ashita.Boot]. Plugins' sections and polplugin
    // names aren't its to look up
    private static bool NeedsLowercase(Parsed p, string? section)
    {
        static bool Ashitas(string s) => s.StartsWith("ashita.", StringComparison.OrdinalIgnoreCase)
                                         || s.StartsWith("ffxi.", StringComparison.OrdinalIgnoreCase)
                                         || s.StartsWith("sandbox.", StringComparison.OrdinalIgnoreCase) || Same(s, "import");
        bool upper = p.Name.Any(char.IsUpper);
        return p.Kind switch
        {
            Kind.Section => upper && Ashitas(p.Name),
            Kind.Key => upper && section is not null && Ashitas(section) && !p.Name.StartsWith('#')
                        && !Same(section, "ashita.polplugins") && !Same(section, "ashita.polplugins.args"),
            _ => false,
        };
    }

    // never quotes a value, a boot config can hold a password
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var w = new List<string>();
            if (utf8Bom) w.Add("This file starts with a UTF-8 marker that hides its first section from Ashita. Provenance takes it out when it saves.");
            if (looksUtf8) w.Add($"This file has UTF-8 text in it, but Ashita reads it as {ansi.WebName}, so accented and non-English characters come out wrong.");
            var p = Scan();
            var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string? section = null;
            bool first = false;
            for (int i = 0; i < p.Length; i++)
            {
                int n = i + 1;
                var name = p[i].Name;
                if (p[i].Kind == Kind.Section)
                {
                    section = name;
                    first = sections.Add(name);
                    keys.Clear();
                    if (!first) w.Add($"Line {n}: [{name}] is in this file more than once. Ashita only reads the first one, so that's the one Provenance edits.");
                }
                if (NeedsLowercase(p[i], section))
                    w.Add($"Line {n}: Ashita won't see {(p[i].Kind == Kind.Section ? $"[{name}]" : name)} unless it's written in lowercase.");
                if (IsUtf16 && p[i].Kind is Kind.Key or Kind.Section && !Fits(ansi, lines[i].Text))
                    w.Add($"Line {n}: {name} has characters Ashita can't read with this PC's language for non-Unicode programs, so they come out wrong.");
                if (p[i].Kind == Kind.Key && section is not null)
                {
                    var value = Value(i, p[i]);
                    if (name.StartsWith('#')) w.Add($"Line {n}: # doesn't start a comment in a boot configuration, use ; instead.");
                    else if (first && !keys.TryAdd(name, n))
                        w.Add($"Line {n}: {name} is set more than once in [{section}]. Provenance keeps the first one when it changes {name}.");
                    if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
                        w.Add($"Line {n}: Ashita keeps the quotes around {name}'s value, so they become part of it.");
                    if (value.Contains(" ;") || value.Contains("\t;"))
                        w.Add($"Line {n}: Ashita doesn't allow comments after a value, so everything after the ; is part of {name}.");
                }
                if (ansi.GetByteCount(lines[i].Text) > MaxLine) w.Add($"Line {n} is too long for Ashita to read.");
            }
            return w;
        }
    }

    private void Replace(int i, string text)
    {
        lines[i].Text = text;
        lines[i].Raw = null;
        Changed = true;
    }

    private void RemoveAt(int i)
    {
        if (i == lines.Count - 1 && i > 0 && lines[i].Ending.Length == 0) lines[i - 1].Ending = "";
        lines.RemoveAt(i);
        Changed = true;
    }

    private void Insert(int at, string text)
    {
        var line = new Line(text, newline, null);
        if (at == lines.Count && at > 0 && lines[at - 1].Ending.Length == 0)
        {
            lines[at - 1].Ending = newline;
            line.Ending = "";
        }
        lines.Insert(at, line);
        Changed = true;
    }
}
