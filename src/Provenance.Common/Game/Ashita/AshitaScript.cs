using System.Text;
using System.Text.RegularExpressions;
using Provenance.Common.IO;
using Provenance.Common.Security;

namespace Provenance.Common.Game.Ashita;

// ByScript: the script loads it some way a toggle can't safely change (an /include or /exec, a ;-joined line, a %N%
// argument, or it's unloaded again further down)
public enum AshitaLoad { Off, On, ByScript }

public sealed record AshitaScriptLoad(string Name, AshitaLoad State, string? Args);

// scripts\<name>.txt, edited line by line. Ashita reads it as raw bytes up to each \n, so Latin1 keeps every byte and
// every line ending exactly as it was
public sealed partial class AshitaScript
{
    public sealed record Line(string Text, string Ending);

    private const string AddonsPlugin = "addons";
    private const int MaxIncludeDepth = 16, MaxScriptSize = 1 << 20, MaxWalked = 100_000;

    private readonly List<Line> lines;
    private readonly string? scriptsDir;
    private Analysis? analysis;

    public string? FilePath { get; }
    public IReadOnlyList<Line> Lines => lines;
    public bool Changed { get; private set; }

    private AshitaScript(byte[] bytes, string? filePath, string? scriptsDir)
    {
        lines = Split(Encoding.Latin1.GetString(bytes)).Select(l => new Line(l.Text, l.Ending)).ToList();
        FilePath = filePath;
        this.scriptsDir = scriptsDir is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(scriptsDir));
    }

    // a script that isn't there yet reads as empty, and Save makes it
    public static AshitaScript Load(string path, string? scriptsDir = null)
    {
        var full = Path.GetFullPath(path);
        return new AshitaScript(Read(full), full, scriptsDir ?? Path.GetDirectoryName(full));
    }

    public static AshitaScript? Open(string ashitaDir, string script) =>
        PathFor(ashitaDir, script) is { } path ? Load(path, Path.Combine(ashitaDir, "scripts")) : null;

    public static AshitaScript FromBytes(byte[] bytes, string? scriptsDir = null) => new(bytes, null, scriptsDir);

    private static byte[] Read(string full)
    {
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists) return [];
            if (info.Length > MaxScriptSize) throw Shown.Mark(new IOException($"{info.Name} is too big to be an Ashita script."));
            return File.ReadAllBytes(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException && !Shown.Is(e))
        {
            throw Shown.Mark(new IOException($"Provenance couldn't read the Ashita script {Path.GetFileName(full)}.", e));
        }
    }

    public byte[] ToBytes()
    {
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(l.Text).Append(l.Ending);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    public void Save() => Save(FilePath ?? throw new InvalidOperationException("This script has no file to save to."));

    public void Save(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        try
        {
            // the first .bak is the script from before Provenance touched it, so it's never written over
            SafeWrite.Replace(full, ToBytes(), Top(dir), refuse: FoldersTo(full),
                              backup: real => File.Exists(real + ".bak") ? SafeWrite.Backup.None : SafeWrite.Backup.Keep);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (Shown.Is(e)) throw;
            throw SaveFailed(full, e);
        }
        Changed = false;
    }

    private static IOException SaveFailed(string full, Exception e) =>
        Shown.Mark(new IOException($"Provenance couldn't save the Ashita script {Path.GetFileName(full)}.", e));

    private string Top(string dir) => scriptsDir is not null && PathInside.Contains(scriptsDir, dir) ? scriptsDir : Path.GetDirectoryName(dir) ?? dir;

    private List<string> FoldersTo(string full)
    {
        var dir = Path.GetDirectoryName(full)!;
        if (scriptsDir is null || !PathInside.Contains(scriptsDir, dir)) return [dir, Path.GetDirectoryName(dir) ?? dir];
        var folders = new List<string> { scriptsDir };
        var rest = Path.GetRelativePath(scriptsDir, dir);
        if (rest != ".")
        {
            var d = scriptsDir;
            foreach (var part in rest.Split('\\')) folders.Add(d = Path.Combine(d, part));
        }
        return folders;
    }

    
    public IReadOnlyList<AshitaScriptLoad> Plugins => Listed(false);
    public IReadOnlyList<AshitaScriptLoad> Addons => Listed(true);

    public AshitaLoad Plugin(string name) => State(Now(false), name);
    public AshitaLoad Addon(string name) => State(Now(true), name);

    // false when the script loads it in a way a toggle can't touch, or the name can't go in a script. Turning an addon
    // on turns the addons plugin on too
    public bool SetPlugin(string name, bool on) => Set(false, name, on);
    public bool SetAddon(string name, bool on) => Set(true, name, on);

    // Ashita cuts the name at the first .txt (case and all) and puts .txt back, so "default" and "default.txt" are the
    // same script but "Mine.TXT" is Mine.TXT.txt
    public static string FileName(string script)
    {
        var s = script.Trim().Replace('/', '\\');
        int at = s.IndexOf(".txt", StringComparison.Ordinal);
        return (at >= 0 ? s[..at] : s) + ".txt";
    }

    public static string? PathFor(string ashitaDir, string script) => Resolve(Path.Combine(ashitaDir, "scripts"), script);

    // default.txt ships with Ashita (a git pull fights any edit) and the examples use it, and editing a shared script
    // changes every boot config on it. Those get copied first
    public static bool NeedsCopy(string ashitaDir, string? script, string bootName, IEnumerable<(string Boot, string? Script)> configs)
    {
        if (string.IsNullOrWhiteSpace(script) || PathFor(ashitaDir, script) is not { } file || file.Equals(PathFor(ashitaDir, "default.txt")!, StringComparison.OrdinalIgnoreCase))
            return true;
        var me = BootStem(bootName);
        foreach (var (boot, other) in configs)
        {
            var stem = BootStem(boot);
            // the launch copies Provenance writes keep their source's script, they don't count as sharing it
            if (stem.Equals(me, StringComparison.OrdinalIgnoreCase) || stem.Equals("provenance", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("provenance.", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(other) && PathFor(ashitaDir, other) is { } path && path.Equals(file, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static string CopyFor(string ashitaDir, string? script, string bootName)
    {
        var stem = SafeStem(bootName);
        string? source = null;
        if (!string.IsNullOrWhiteSpace(script))
            source = PathFor(ashitaDir, script) ??
                     throw Shown.Mark(new ArgumentException($"The script {script} isn't in Ashita's scripts folder, so Provenance won't copy it."));
        var bytes = source is null ? [] : Read(source);
        var dir = Path.GetFullPath(Path.Combine(ashitaDir, "scripts"));
        try
        {
            Directory.CreateDirectory(dir);
            if (SafeWrite.CreateFree(dir, bytes, n => n == 1 ? stem + ".txt" : $"{stem}-{n}.txt") is { } made) return made;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException && !Shown.Is(e))
        {
            throw Shown.Mark(new IOException($"Provenance couldn't make a script for {bootName} in Ashita's scripts folder.", e));
        }
        throw Shown.Mark(new IOException($"Provenance couldn't find a free script name for {bootName}."));
    }

    private static string BootStem(string boot)
    {
        var name = Path.GetFileName(boot.Trim());
        return name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    // ASCII only, and no spaces: Ashita runs it as "/exec <script> <args>", so a space would split the name
    private static string SafeStem(string bootName)
    {
        var stem = new string(BootStem(bootName).Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-').ToArray()).Trim('-');
        return stem.Length == 0 ? "profile" : stem;
    }

    private static string? Resolve(string scriptsDir, string script) => PathInside.Collapse(scriptsDir, FileName(script).TrimStart('\\'));

    private enum Verb { Other, Load, Unload, UnloadAll, Include, Exec, Wait }

    private readonly record struct Command(Verb Verb, bool Addon, string Name, string Args)
    {
        public static readonly Command None = new(Verb.Other, false, "", "");
    }

    private enum Op { Load, Unload, UnloadAll }

    private readonly record struct Event(int Line, int Seq, Op Op, string Name, bool Toggle, bool Active, string Args, bool Unordered);

    // what a script does to plugins, or to addons. An /unloadall is kept apart from the named events, since it covers
    // every name
    private sealed class Loads(bool isAddons)
    {
        public readonly bool IsAddons = isAddons;
        public readonly Dictionary<string, List<Event>> ByName = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Loaded = [];
        public readonly List<Event> Toggles = [];
        public int UnloadAllSeq = -1, UnloadAllLine = -1;
        public bool UnloadAllUnordered;
        private readonly HashSet<string> _loadedSet = new(StringComparer.OrdinalIgnoreCase);

        public void Add(Event e)
        {
            if (!ByName.TryGetValue(e.Name, out var list)) ByName[e.Name] = list = [];
            if (e.Op == Op.Load && _loadedSet.Add(e.Name)) Loaded.Add(e.Name);
            list.Add(e);
            if (e.Toggle) Toggles.Add(e);
        }

        public void AddUnloadAll(Event e)
        {
            UnloadAllSeq = e.Seq;
            UnloadAllLine = Math.Max(UnloadAllLine, e.Line);
            UnloadAllUnordered |= e.Unordered;
        }
    }

    private sealed class Analysis
    {
        public readonly Dictionary<string, List<string>?> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly Loads Plugins = new(false), Addons = new(true);
        public int FirstWait = -1, Walked, Seq;

        public Loads For(bool addon) => addon ? Addons : Plugins;

        public void Add(int line, bool addon, Op op, string name, bool toggle, bool active, string args, bool unordered)
        {
            var e = new Event(line, Seq++, op, name, toggle, active, args, unordered);
            if (op == Op.UnloadAll) For(addon).AddUnloadAll(e);
            else For(addon).Add(e);
        }
    }

    private Analysis Analyse()
    {
        if (analysis is not null) return analysis;
        var a = new Analysis();
        var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (FilePath is not null) stack.Add(FilePath);
        Walk(lines.Select(l => l.Text).ToList(), null, false, a, stack);
        return analysis = a;
    }

    // under an /exec the order's gone, Ashita runs those on their own thread
    private void Walk(List<string> texts, int? from, bool unordered, Analysis a, HashSet<string> stack)
    {
        for (int i = 0; i < texts.Count; i++)
        {
            if (from is not null && a.Walked++ >= MaxWalked) return;
            int at = from ?? i;
            var text = texts[i].TrimEnd('\r');
            if (text.Length == 0) continue;
            if (text[0] == '#')
            {
                if (from is null && Toggle(text[1..]) is { } off) a.Add(at, off.Addon, Op.Load, off.Name, true, false, off.Args, false);
                continue;
            }
            if (from is null && Toggle(text) is { } on)
            {
                a.Add(at, on.Addon, Op.Load, on.Name, true, true, on.Args, false);
                continue;
            }
            // Ashita splits every line at ; apart from ones starting "/bind " (byte for byte), whose ; belongs to the key
            if (text.StartsWith("/bind ", StringComparison.Ordinal)) continue;
            foreach (var part in text.Split(';'))
            {
                var c = Parse(part.TrimStart(' ', '\t'));
                var name = c.Name.Trim('"');
                if (c.Verb is Verb.Load or Verb.Unload && ArgToken().IsMatch(name)) continue;
                switch (c.Verb)
                {
                    case Verb.Load: a.Add(at, c.Addon, Op.Load, name, false, true, c.Args, unordered); break;
                    case Verb.Unload:
                        a.Add(at, c.Addon, Op.Unload, name, false, true, "", unordered);
                        if (!c.Addon && name.Equals(AddonsPlugin, StringComparison.OrdinalIgnoreCase)) a.Add(at, true, Op.UnloadAll, "", false, true, "", unordered);
                        break;
                    case Verb.UnloadAll:
                        a.Add(at, c.Addon, Op.UnloadAll, "", false, true, "", unordered);
                        if (!c.Addon) a.Add(at, true, Op.UnloadAll, "", false, true, "", unordered);
                        break;
                    case Verb.Wait:
                        if (a.FirstWait < 0) a.FirstWait = at;
                        break;
                    case Verb.Include or Verb.Exec:
                        Nested(name, at, unordered || c.Verb == Verb.Exec, a, stack);
                        break;
                }
            }
        }
    }

    private void Nested(string name, int at, bool unordered, Analysis a, HashSet<string> stack)
    {
        if (scriptsDir is null || ArgToken().IsMatch(name) || stack.Count > MaxIncludeDepth) return;
        var path = Resolve(scriptsDir, name);
        if (path is null || stack.Contains(path)) return;
        if (!a.Files.TryGetValue(path, out var texts)) a.Files[path] = texts = ReadNested(path);
        if (texts is null) return;
        stack.Add(path);
        Walk(texts, at, unordered, a, stack);
        stack.Remove(path);
    }

    private static List<string>? ReadNested(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxScriptSize) return null;
            return Split(Encoding.Latin1.GetString(File.ReadAllBytes(path))).Select(l => l.Text).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static (bool Addon, string Name, string Args)? Toggle(string text)
    {
        if (text.Length == 0 || text[0] != '/' || text.Contains(';') || ArgToken().IsMatch(text)) return null;
        var c = Parse(text);
        return c.Verb == Verb.Load && !c.Name.Contains('"') ? (c.Addon, c.Name, c.Args) : null;
    }

    private static Command Parse(string text)
    {
        var (verb, rest) = Next(text);
        var (name, args) = Next(rest);
        if (verb.Equals("/addon", StringComparison.OrdinalIgnoreCase))
        {
            var (addon, addonArgs) = Next(args);
            return name.ToLowerInvariant() switch
            {
                "load" when addon.Length > 0 => new(Verb.Load, true, addon, addonArgs),
                "unload" or "kill" when addon.Length > 0 => new(Verb.Unload, true, addon, ""),
                "unloadall" => new(Verb.UnloadAll, true, "", ""),
                _ => Command.None,
            };
        }
        return verb.ToLowerInvariant() switch
        {
            "/load" when name.Length > 0 => new(Verb.Load, false, name, args),
            "/unload" when name.Length > 0 => new(Verb.Unload, false, name, ""),
            "/unloadall" => new(Verb.UnloadAll, false, "", ""),
            "/include" when name.Length > 0 => new(Verb.Include, false, name, args),
            "/exec" when name.Length > 0 => new(Verb.Exec, false, name, args),
            "/wait" or "/sleep" or "/pause" => new(Verb.Wait, false, "", ""),
            _ => Command.None,
        };
    }

    private static (string Token, string After) Next(string s)
    {
        s = s.TrimStart(' ', '\t');
        int end = s.IndexOfAny([' ', '\t']);
        return end < 0 ? (s, "") : (s[..end], s[(end + 1)..].Trim(' ', '\t'));
    }

    private List<AshitaScriptLoad> Listed(bool addon)
    {
        var loads = Now(addon);
        return loads.Loaded
            .Select(n => new AshitaScriptLoad(n, State(loads, n), Toggles(loads, n).OrderBy(e => !e.Active).Select(e => e.Args).FirstOrDefault() is
                                                  { Length: > 0 } args ? args : null))
            .ToList();
    }

    private Loads Now(bool addon) => Analyse().For(addon);

    private static List<Event> Toggles(Loads loads, string name) =>
        loads.ByName.GetValueOrDefault(name)?.Where(e => e.Toggle).ToList() ?? [];

    private AshitaLoad State(Loads loads, string name)
    {
        int firstLoad = int.MaxValue, lastUnload = loads.UnloadAllSeq, lastUnloadLine = loads.UnloadAllLine;
        bool active = false, other = false, unordered = loads.UnloadAllUnordered;
        foreach (var e in loads.ByName.GetValueOrDefault(name) ?? [])
        {
            if (e.Op == Op.Load)
            {
                firstLoad = Math.Min(firstLoad, e.Seq);
                active |= e.Active;
                other |= !e.Toggle;
            }
            else
            {
                lastUnload = Math.Max(lastUnload, e.Seq);
                lastUnloadLine = Math.Max(lastUnloadLine, e.Line);
                unordered |= e.Unordered;
            }
        }
        if (other || unordered) return AshitaLoad.ByScript;
        if (firstLoad != int.MaxValue) return lastUnload > firstLoad ? AshitaLoad.ByScript : active ? AshitaLoad.On : AshitaLoad.Off;
        return lastUnloadLine >= InsertAt(loads) ? AshitaLoad.ByScript : AshitaLoad.Off;
    }

    // each step asks for the analysis again, an edit throws the last one away
    private bool Set(bool addon, string name, bool on)
    {
        var loads = Now(addon);
        if (State(loads, name) == AshitaLoad.ByScript || on && !ValidName(name) && Toggles(loads, name).Count == 0) return false;
        if (on && addon && State(Now(false), AddonsPlugin) == AshitaLoad.Off && !Set(false, AddonsPlugin, true)) return false;
        loads = Now(addon);
        var state = State(loads, name);
        if (state == AshitaLoad.ByScript) return false;
        if (state == (on ? AshitaLoad.On : AshitaLoad.Off)) return true;
        var toggles = Toggles(loads, name);
        if (!on)
        {
            foreach (var e in toggles.Where(e => e.Active)) Edit(e.Line, "#" + lines[e.Line].Text);
            return true;
        }
        // a commented /addon load above the addons plugin would run before it's there, so that one gets a new line
        int after = addon ? AddonsLine() : -1;
        int off = toggles.FindIndex(e => !e.Active && e.Line > after);
        if (off >= 0) Edit(toggles[off].Line, lines[toggles[off].Line].Text[1..]);
        else Insert(InsertAt(loads), (addon ? "/addon load " : "/load ") + name);
        return true;
    }

    private int AddonsLine() =>
        Now(false).ByName.GetValueOrDefault(AddonsPlugin)?.Where(e => e.Op == Op.Load && e.Active).Select(e => e.Line).DefaultIfEmpty(-1).Min() ?? -1;

    // after the last top-level line of the same kind (and an addon after the addons plugin), before the first /wait,
    // which is there to give addons time to load
    private int InsertAt(Loads loads)
    {
        var wait = Analyse().FirstWait;
        if (wait < 0) wait = lines.Count;
        int last = loads.Toggles.Where(e => e.Line < wait).Select(e => e.Line).DefaultIfEmpty(-1).Max();
        if (loads.IsAddons)
        {
            last = Math.Max(last, AddonsLine());
            return last >= 0 ? last + 1 : wait;
        }
        if (last >= 0) return last + 1;
        int command = lines.FindIndex(l => l.Text.TrimEnd('\r') is { Length: > 0 } t && t[0] != '#');
        return Math.Min(command < 0 ? lines.Count : command, wait);
    }

    private static bool ValidName(string name) =>
        name.Length is > 0 and <= 128 && name.All(c => c is > ' ' and <= '~' and not (';' or '%' or '"' or '#'));

    private void Edit(int index, string text)
    {
        lines[index] = lines[index] with { Text = text };
        Changed = true;
        analysis = null;
    }

    private void Insert(int index, string text)
    {
        var eol = lines.Select(l => l.Ending).FirstOrDefault(e => e.Length > 0) ?? "\r\n";
        if (index == lines.Count && index > 0 && lines[index - 1].Ending.Length == 0)
        {
            var last = lines[index - 1];
            lines[index - 1] = new Line(eol == "\r\n" ? last.Text.TrimEnd('\r') : last.Text, eol);
            lines.Add(new Line(text, ""));
        }
        else lines.Insert(index, new Line(text, eol));
        Changed = true;
        analysis = null;
    }

    private static List<(string Text, string Ending)> Split(string text)
    {
        var list = new List<(string, string)>();
        int start = 0;
        while (start < text.Length)
        {
            int nl = text.IndexOf('\n', start);
            if (nl < 0)
            {
                list.Add((text[start..], ""));
                break;
            }
            int end = nl > start && text[nl - 1] == '\r' ? nl - 1 : nl;
            list.Add((text[start..end], text[end..(nl + 1)]));
            start = nl + 1;
        }
        return list;
    }

    [GeneratedRegex("%[0-9]+%")]
    private static partial Regex ArgToken();
}
