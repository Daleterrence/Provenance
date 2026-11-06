using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Provenance.Common.IO;
using Provenance.Common.Security;

namespace Provenance.Common.Game.Windower;

// Windower's own settings.xml, read the lenient way Windower.exe reads it and saved exactly the way it saves it, so
// going back to Windower.exe never notices Provenance was there
public sealed class WindowerSettingsFile
{
    public const string FileName = "settings.xml";
    private const int MaxNameLength = 255;
    private const string LockName = "windower-settings";
    private const string OldCopySuffix = " (Provenance)", OldDefaultCopy = "(Provenance)";
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    private readonly List<WindowerProfile> _profiles = [];
    private readonly List<string> _warnings = [];
    private readonly List<XElement> _unknownRoot = [];
    private readonly List<XElement> _unknownLauncher = [];
    private readonly List<XElement> _unknownAutoload = [];

    public bool Dev { get; set; }
    public string Branch => Dev ? "dev" : "stable";

    public List<string> Addons { get; } = [];
    public List<string> Plugins { get; } = [];

    public IReadOnlyList<WindowerProfile> Profiles => _profiles;
    public IReadOnlyList<string> Warnings => _warnings;

    // the file was there but wasn't XML. It reads as empty like it does for Windower, but it won't be saved over until
    // MoveBadFileAside has kept a copy
    public string? LoadError { get; private set; }
    public int? LoadErrorLine { get; private set; }
    public bool CanSave => LoadError is null;

    // LoadError is .NET's wording, for the log. This is the player's, then whatever else they can do about it
    public string Unreadable(string orElse) => LoadErrorLine is { } line
        ? $"Windower's settings.xml has a mistake on line {line}, so Provenance can't read it. Fix that line, or {orElse}"
        : $"Windower's settings.xml has a mistake in it, so Provenance can't read it. Fix the file, or {orElse}";

    private WindowerSettingsFile() { }

    public static WindowerSettingsFile Empty() => FromDocument(null);

    public static string PathIn(string windowerDir) => Path.Combine(windowerDir, FileName);

    public static WindowerSettingsFile Load(string windowerDir) => LoadFile(PathIn(windowerDir));

    // read-only sharing, so a file another program is half way through writing throws instead of reading as empty
    public static WindowerSettingsFile LoadFile(string path)
    {
        if (!File.Exists(path)) return Empty();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return FromStream(fs);
    }

    public static WindowerSettingsFile FromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        return FromStream(ms);
    }

    private static WindowerSettingsFile FromStream(Stream s)
    {
        XDocument doc;
        try
        {
            var rs = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true };
            using var r = XmlReader.Create(s, rs);
            doc = XDocument.Load(r);
        }
        catch (XmlException e)
        {
            var empty = Empty();
            empty.LoadError = e.Message;
            empty.LoadErrorLine = e.LineNumber > 0 ? e.LineNumber : null;
            empty._warnings.Add($"Windower's settings.xml couldn't be read ({e.Message}).");
            return empty;
        }
        return FromDocument(doc);
    }

    private static WindowerSettingsFile FromDocument(XDocument? doc)
    {
        var f = new WindowerSettingsFile();
        var root = doc?.Root;
        if (root is not null)
        {
            var launcher = root.Element("launcher");
            f.Dev = (string?)launcher?.Element("branch") == "dev";
            if (launcher is not null)
                f._unknownLauncher.AddRange(launcher.Elements().Where(e => e.Name != "branch").Select(e => new XElement(e)));
            var autoload = root.Element("autoload");
            if (autoload is not null)
            {
                foreach (var a in autoload.Elements("addon").Select(e => e.Value))
                    if (!string.IsNullOrWhiteSpace(a) && !f.Addons.Contains(a)) f.Addons.Add(a);
                foreach (var p in autoload.Elements("plugin").Select(e => e.Value))
                    if (!string.IsNullOrWhiteSpace(p) && !f.Plugins.Contains(p)) f.Plugins.Add(p);
                f._unknownAutoload.AddRange(autoload.Elements().Where(e => e.Name != "addon" && e.Name != "plugin").Select(e => new XElement(e)));
            }
            f._profiles.AddRange(root.Elements("profile").Select(WindowerProfile.FromXml));
            f.RenameDuplicates();
            f._unknownRoot.AddRange(root.Elements().Where(e => e.Name.LocalName is not ("launcher" or "autoload" or "profile") || e.Name.Namespace != XNamespace.None)
                                        .Select(e => new XElement(e)));
        }
        if (f._profiles.Count == 0) f._profiles.Add(new WindowerProfile());
        return f;
    }

    // two names the same once trimmed crash Windower.exe on start, and its own name box can make them. The later one
    // gets a new name instead of being lost
    private void RenameDuplicates()
    {
        var taken = new HashSet<string>(_profiles.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in _profiles)
        {
            if (seen.Add(p.Name)) continue;
            var stem = p.Name.Length == 0 ? "Unnamed" : p.Name;
            var name = Enumerable.Range(2, int.MaxValue - 2).Select(n => $"{stem} ({n})").First(n => !taken.Contains(n));
            _warnings.Add(p.Name.Length == 0
                ? $"Windower's settings.xml had more than one unnamed profile, so one of them is now called \"{name}\"."
                : $"Windower's settings.xml had two profiles called \"{p.Name}\", so the second one is now called \"{name}\".");
            p.Name = name;
            taken.Add(name);
            seen.Add(name);
        }
    }

    public byte[] ToBytes()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in _profiles)
            if (!names.Add(p.Name)) throw new InvalidOperationException($"Two Windower profiles are called \"{p.Name}\".");

        var root = new XElement("settings", new XElement("launcher", new XElement("branch", Branch), _unknownLauncher.Select(e => new XElement(e))));
        var addons = Distinct(Addons);
        var plugins = Distinct(Plugins);
        if (addons.Count > 0 || plugins.Count > 0 || _unknownAutoload.Count > 0)
            root.Add(new XElement("autoload", addons.Select(a => new XElement("addon", a)), plugins.Select(p => new XElement("plugin", p)),
                                  _unknownAutoload.Select(e => new XElement(e))));
        foreach (var p in _profiles) root.Add(p.ToXml());
        root.Add(_unknownRoot.Select(e => new XElement(e)));

        // what XDocument.Save(path) does on .NET Framework: version 1.0 whatever the declaration says, UTF-8 with a BOM,
        // two-space indent, CRLF and nothing after the last >
        var ws = new XmlWriterSettings { Encoding = new UTF8Encoding(true), Indent = true, IndentChars = "  ", NewLineChars = "\r\n" };
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, ws)) new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(w);
        return ms.ToArray();
    }

    private static List<string> Distinct(List<string> names)
    {
        var seen = new List<string>();
        foreach (var n in names)
            if (!string.IsNullOrWhiteSpace(n) && !seen.Contains(n)) seen.Add(n);
        return seen;
    }

    public void Save(string windowerDir) => SaveFile(PathIn(windowerDir));

    private void SaveFile(string path)
    {
        ThrowIfUnreadable();
        using (Lock()) Write(path, ToBytes());
    }

    // re-reads under the lock, so another copy of Provenance saving in between doesn't lose its changes (or this one's)
    public static WindowerSettingsFile Update(string windowerDir, Action<WindowerSettingsFile> change) =>
        Update(windowerDir, new Dictionary<string, WindowerAddonList>(), (f, _) => change(f));

    // the change gets a copy of the lists, which only replaces them once settings.xml is written, so a failed write
    // can't leave a list moved to a name the file never got
    public static WindowerSettingsFile Update(string windowerDir, IDictionary<string, WindowerAddonList> lists,
                                              Action<WindowerSettingsFile, IDictionary<string, WindowerAddonList>> change)
    {
        var path = PathIn(windowerDir);
        using (Lock())
        {
            var f = LoadFile(path);
            f.ThrowIfUnreadable();
            var working = lists.ToDictionary(kv => kv.Key, kv => kv.Value?.Copy()!, StringComparer.Ordinal);
            f.DropOldCopies(working);
            change(f, working);
            Write(path, f.ToBytes());
            WindowerAddonList.Prune(working, f);
            lists.Clear();
            foreach (var kv in working) lists.Add(kv);
            return f;
        }
    }

    // only once it's been read again under the lock and still won't parse, so a file fixed in the meantime stays put
    public string? MoveBadFileAside(string windowerDir)
    {
        var path = PathIn(windowerDir);
        // the rename runs as admin too, so it never goes through a link
        LinkCheck.Refuse(Path.GetFullPath(windowerDir), path);
        using (Lock())
        {
            if (File.Exists(path))
            {
                if (LoadFile(path).CanSave) return null;
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var moved = $"{path}.bad-{stamp}";
                for (int n = 2; File.Exists(moved); n++) moved = $"{path}.bad-{stamp}-{n}";
                using (var pins = FolderPins.HoldFollowingLinksAboveOrThrow(windowerDir, windowerDir))
                {
                    pins.Recheck(windowerDir, pins.Real(path), pins.Real(moved));
                    File.Move(pins.Real(path), pins.Real(moved));
                }
                Recovered();
                return moved;
            }
            Recovered();
            return null;
        }
    }

    private void Recovered()
    {
        LoadError = null;
        LoadErrorLine = null;
        _warnings.Clear();
    }

    private void ThrowIfUnreadable()
    {
        if (LoadError is not null)
            throw Shown.Mark(new InvalidOperationException(
                "Windower's settings.xml couldn't be read, so Provenance won't save over it. Choose Start a new settings file under Windower/Ashita to set it aside and start again."));
    }

    // if the lock doesn't come it writes anyway, the move swaps settings.xml whole
    private static IDisposable? Lock()
    {
        try { return AdminLock.TryAcquire(LockName, LockWait); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void Write(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? path;
        SafeWrite.Replace(path, bytes, dir, refuse: [dir], flushToDisk: true);
    }

    // a "<name> (Provenance)" profile is an old launch copy. They go on the first save, and a name that still points at
    // one means its source
    public static bool IsOldCopy(string? name)
    {
        var n = (name ?? "").Trim();
        return n == OldDefaultCopy || n.EndsWith(OldCopySuffix, StringComparison.Ordinal);
    }

    public static string OldCopySource(string? name)
    {
        var n = (name ?? "").Trim();
        return n == OldDefaultCopy ? "" : n.EndsWith(OldCopySuffix, StringComparison.Ordinal) ? n[..^OldCopySuffix.Length] : name ?? "";
    }

    private void DropOldCopies(IDictionary<string, WindowerAddonList> lists)
    {
        foreach (var p in _profiles.Where(p => IsOldCopy(p.Name)).ToList())
        {
            _profiles.Remove(p);
            lists.Remove(p.Name);
        }
        if (_profiles.Count == 0) _profiles.Add(new WindowerProfile());
    }

    // trimmed, then the first match in any case in file order, like Windower's -p
    public WindowerProfile? Find(string? name)
    {
        var n = (name ?? "").Trim();
        return _profiles.FirstOrDefault(p => p.Name.Equals(n, StringComparison.CurrentCultureIgnoreCase));
    }

    public WindowerProfile? Unnamed => _profiles.FirstOrDefault(p => p.Name.Length == 0);

    public WindowerProfile GetOrAddUnnamed()
    {
        if (Unnamed is { } u) return u;
        var p = new WindowerProfile();
        _profiles.Add(p);
        return p;
    }

    public string? NameProblem(string? name, WindowerProfile? renaming = null)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return "A profile needs a name.";
        if (n.Length > MaxNameLength) return $"A profile name can be {MaxNameLength} characters at most.";
        try { XmlConvert.VerifyXmlChars(n); }
        catch (XmlException) { return "That name has a character settings.xml can't hold."; }
        if (IsOldCopy(n)) return "Choose a name that doesn't end in (Provenance).";
        if (_profiles.Any(p => !ReferenceEquals(p, renaming) && p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            return $"There's already a profile called {n}.";
        return null;
    }

    public string NewProfileName()
    {
        for (int i = 1; ; i++)
            if (NameProblem($"New Profile {i}") is null) return $"New Profile {i}";
    }

    public WindowerProfile Add(string name, IDictionary<string, WindowerAddonList>? lists = null)
    {
        var p = new WindowerProfile { Name = CheckedName(name, null) };
        _profiles.Add(p);
        lists?.Remove(p.Name);
        return p;
    }

    public WindowerProfile Duplicate(string source, string newName, IDictionary<string, WindowerAddonList>? lists = null)
    {
        var from = Require(source);
        var p = from.Clone();
        p.Name = CheckedName(newName, null);
        _profiles.Insert(_profiles.IndexOf(from) + 1, p);
        if (lists is not null) WindowerAddonList.Duplicated(lists, from.Name, p.Name);
        return p;
    }

    public WindowerProfile Rename(string name, string newName, IDictionary<string, WindowerAddonList>? lists = null)
    {
        var p = Require(name);
        if (p.Name.Length == 0) throw new InvalidOperationException("The unnamed profile can't be renamed.");
        var old = p.Name;
        p.Name = CheckedName(newName, p);
        if (lists is not null) WindowerAddonList.Renamed(lists, old, p.Name);
        return p;
    }

    public void Delete(string name, IDictionary<string, WindowerAddonList>? lists = null)
    {
        var p = Require(name);
        if (p.Name.Length == 0) throw new InvalidOperationException("The unnamed profile can't be deleted.");
        _profiles.Remove(p);
        lists?.Remove(p.Name);
    }

    private WindowerProfile Require(string name) =>
        _profiles.FirstOrDefault(p => p.Name == (name ?? "").Trim())
        ?? throw new ArgumentException($"There's no Windower profile called {name}.", nameof(name));

    private string CheckedName(string name, WindowerProfile? renaming) =>
        NameProblem(name, renaming) is { } problem ? throw new ArgumentException(problem, nameof(name)) : name.Trim();
}
