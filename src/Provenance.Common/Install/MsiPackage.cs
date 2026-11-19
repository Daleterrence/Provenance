using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Provenance.Common.IO;
using Provenance.Common.Progress;
using Provenance.Common.Security;

namespace Provenance.Common.Install;

// applies an MSI straight from its tables, skipping Windows Installer and its 61,353 components (ugh)
public sealed class MsiPackage : IDisposable
{
    private static readonly Regex TokenRx = new(@"\[(\\.|[^\[\]]*)\]");
    private static readonly Regex ConditionRx = new(@"^\s*([A-Za-z_][A-Za-z0-9_.]*)\s*=\s*""([^""]*)""\s*$");
    private readonly MsiDatabase _db;

    public MsiPackage(string path) => _db = new MsiDatabase(path);

    public void Dispose() => _db.Dispose();

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "_Validation", "AdminExecuteSequence", "AdminUISequence", "AdvtExecuteSequence", "AppSearch", "Binary", "CheckBox",
        "Class", "ComboBox", "Component", "Control", "ControlCondition", "ControlEvent", "CreateFolder", "CustomAction", "Dialog",
        "Directory", "Error", "EventMapping", "Feature", "FeatureComponents", "File", "Icon", "InstallExecuteSequence",
        "InstallUISequence", "LaunchCondition", "ListBox", "ListView", "Media", "MsiFileHash", "ProgId", "Property",
        "RadioButton", "Registry", "RegLocator", "RemoveFile", "Shortcut", "Signature", "TextStyle", "TypeLib", "UIText",
        "ActionText", "Upgrade",
    };

    private static readonly HashSet<string> KnownActions = new(StringComparer.Ordinal) { "SetNEEDDIRECTPLAY", "EnableDirectPlay" };

    private static readonly HashSet<string> KnownRoots = new(StringComparer.Ordinal)
    {
        "TARGETDIR", "ProgramFilesFolder", "CommonFilesFolder", "ProgramMenuFolder", "StartMenuFolder", "DesktopFolder", "SystemFolder", "WindowsFolder",
    };

    public static string? ProductFolder(IEnumerable<string> targets, string installRoot, string keyFile, out string folder)
    {
        folder = "";
        var dirs = targets.Where(t => Path.GetFileName(t).Equals(keyFile, StringComparison.OrdinalIgnoreCase))
            .Select(t => Path.GetDirectoryName(t)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (dirs.Count != 1) return $"installs {dirs.Count} copies of {keyFile}";
        if (!PathInside.IsUnder(installRoot, dirs[0])) return $"installs outside {installRoot}";
        folder = dirs[0];
        return null;
    }

    public string? Supported()
    {
        foreach (var t in _db.Query("SELECT `Name` FROM `_Tables`").Select(r => r[0]))
            if (!Known.Contains(t) && _db.Query($"SELECT * FROM `{t}`").Count > 0) return "the " + t + " table";
        var custom = _db.Query("SELECT `Action` FROM `CustomAction`").Select(r => r[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var a in _db.Query("SELECT `Action` FROM `InstallExecuteSequence`").Select(r => r[0]))
            if (custom.Contains(a) && !KnownActions.Contains(a)) return "the custom action " + a;
        if (_db.Query("SELECT `Level` FROM `Feature`").Any(r => !int.TryParse(r[0], out var l) || l < 1)) return "a feature that is not installed by default";
        foreach (var r in _db.Query("SELECT `Directory`,`Directory_Parent` FROM `Directory`"))
            if ((r[1].Length == 0 || r[1] == "TARGETDIR") && !KnownRoots.Contains(r[0])) return "the folder " + r[0];
        var fileKeys = _db.Query("SELECT `File` FROM `File`").Select(r => r[0]).ToHashSet(StringComparer.Ordinal);
        var keyPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in _db.Query("SELECT `Component`,`Condition`,`Attributes`,`KeyPath` FROM `Component`"))
        {
            // only simple PROPERTY="value" conditions, and attributes 4 (registry key path) and 8 (shared DLL count)
            if (c[1].Length > 0 && !ConditionRx.IsMatch(c[1])) return "the component condition " + c[1];
            if (!int.TryParse(c[2].Length == 0 ? "0" : c[2], out var attrs) || (attrs & ~(4 | 8)) != 0) return $"component attributes {c[2]}";
            keyPaths[c[0]] = c[3];
        }
        if (_db.Query("SELECT `Attributes` FROM `File`").Any(r => r[0].Length > 0 && (!int.TryParse(r[0], out var a) || (a & ~(1 | 2 | 4 | 512 | 8192 | 16384)) != 0)))
            return "file attributes";
        foreach (var r in _db.Query("SELECT `Component_`,`AppId_`,`FileTypeMask`,`Icon_`,`DefInprocHandler`,`Argument` FROM `Class`"))
            if (!keyPaths.TryGetValue(r[0], out var kp) || !fileKeys.Contains(kp) || r.Skip(1).Any(x => x.Length > 0)) return "a COM class this does not reproduce";
        foreach (var r in _db.Query("SELECT `Component_` FROM `TypeLib`"))
            if (!keyPaths.TryGetValue(r[0], out var kp) || !fileKeys.Contains(kp)) return "a type library this does not reproduce";
        if (_db.Query("SELECT `Icon_` FROM `ProgId`").Any(r => r[0].Length > 0)) return "a ProgID icon";
        if (_db.Query("SELECT `Icon_`,`Hotkey`,`ShowCmd` FROM `Shortcut`").Any(r => r.Any(x => x.Length > 0))) return "a shortcut icon, hotkey or window state";
        return null;
    }

    public sealed record Sandbox(string RegistryKey, string Folder);

    public sealed record UninstallSetup(string UninstallerExe, string Folder, string InstallSource, string? RegionKey = null,
                                        string? FolderValue = null, string? SettingsKey = null);

    public sealed record Result(int Files, long Bytes, int RegistryValues, int Classes, int TypeLibs, int Shortcuts, int ProgIds = 0,
                                bool Uninstall = false)
    {
        public int Written => RegistryValues + Classes + TypeLibs + Shortcuts + ProgIds + (Uninstall ? 1 : 0);
    }

    private enum Keep { Kind, Path, Exact }

    public Result Apply(string installDir, IReadOnlyDictionary<string, string> properties,
                        IProgress<ByteProgress>? progress, CancellationToken ct, int parallel = 4, Sandbox? sandbox = null,
                        UninstallSetup? uninstall = null, Action<string>? status = null, bool payloadInPlace = false,
                        bool keepExisting = false)
    {
        if (Supported() is { } missing) throw new NotSupportedException($"The package uses {missing}.");
        var props = new Dictionary<string, string>(properties, StringComparer.Ordinal)
        {
            ["INSTALLDIR"] = installDir.TrimEnd('\\') + "\\",
        };
        var dirs = ResolveDirectories(props, sandbox);
        var comps = _db.Query("SELECT `Component`,`Directory_`,`Condition`,`KeyPath`,`Attributes` FROM `Component`")
            .ToDictionary(r => r[0], r => (Dir: r[1], Condition: r[2], KeyPath: r[3], Attrs: int.TryParse(r[4], out var a) ? a : 0));
        bool On(string comp) => comps.TryGetValue(comp, out var c) && Condition(c.Condition, props);
        var files = new Dictionary<string, (string Path, long Size)>(StringComparer.Ordinal);
        var fileAttrs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in _db.Query("SELECT `File`,`Component_`,`FileName`,`FileSize`,`Attributes` FROM `File`"))
        {
            if (!On(r[1])) continue;
            var path = Path.Combine(dirs[comps[r[1]].Dir], MsiDatabase.LongName(r[2]));
            files[r[0]] = (path, long.TryParse(r[3], out var sz) ? sz : 0);
            if (int.TryParse(r[4], out var fa) && (fa & 7) != 0) fileAttrs[r[0]] = fa & 7;
        }

        long total = files.Values.Sum(f => f.Size), done = 0, lastReport = 0;
        var gate = new object();
        void Written(long n)
        {
            lock (gate)
            {
                done += n;
                if (done < total && Environment.TickCount64 - lastReport < 200) return;
                lastReport = Environment.TickCount64;
                progress?.Report(new ByteProgress(done, total));
            }
        }
        var cabs = payloadInPlace ? [] : _db.Query("SELECT `Cabinet` FROM `Media`").Select(r => r[0]).Where(c => c.Length > 0).ToList();
        var temps = new List<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var cabPaths = cabs.Select(c => c.StartsWith('#') ? _db.ExtractStream(c[1..], temps) : Path.Combine(Path.GetDirectoryName(_db.Location)!, c)).ToList();
            Parallel.ForEach(cabPaths, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = stop.Token }, cab =>
            {
                try { CabExtractor.Extract(cab, name => files.TryGetValue(name, out var f) ? f.Path : null, Written, stop.Token); }
                catch { stop.Cancel(); throw; }
            });
        }
        catch (AggregateException ae) when (ae.Flatten().InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(ae.Flatten().InnerExceptions[0]).Throw();
        }
        finally { foreach (var t in temps) FileDelete.Try(t); }
        ct.ThrowIfCancellationRequested();
        status?.Invoke("Checking the installed files...");
        foreach (var f in files.Values)
        {
            if (payloadInPlace && !File.Exists(f.Path))
                throw new InvalidDataException($"{f.Path} is missing: the package installs it, and it was not in place.");
            if (!payloadInPlace && (!File.Exists(f.Path) || new FileInfo(f.Path).Length != f.Size))
                throw new InvalidDataException($"{Path.GetFileName(f.Path)} was not extracted completely.");
        }
        foreach (var (key, a) in fileAttrs)
            File.SetAttributes(files[key].Path, File.GetAttributes(files[key].Path) | (FileAttributes)a);

        string Format(string s) => FormatValue(s, props, dirs, files);
        bool NamesPath(string s) => TokenRx.Matches(s).Any(m => m.Groups[1].Value is var t
            && (t.StartsWith('#') || t.StartsWith('!') || t.StartsWith('$') || dirs.ContainsKey(t)));
        Keep How(int root, string key, string value) => key.EndsWith(@"\Interface", StringComparison.OrdinalIgnoreCase) ? Keep.Exact
            : NamesPath(value) ? Keep.Path : root == 0 ? Keep.Exact : Keep.Kind;
        // SE's viewer package writes some values twice, a full apply leaves the last so a repair checks only that
        IEnumerable<(int Root, string Key, string Name, string Value)> Final(List<(int Root, string Key, string Name, string Value)> list) =>
            !keepExisting ? list
            : list.Select((r, i) => (r, i))
                  .GroupBy(x => (x.r.Root == 1, (x.r.Root == 0 ? @"SOFTWARE\Classes\" + x.r.Key : x.r.Key).ToUpperInvariant(), x.r.Name.ToUpperInvariant()))
                  .Select(g => g.Last()).OrderBy(x => x.i).Select(x => x.r);

        var registry = _db.Query("SELECT `Root`,`Key`,`Name`,`Value`,`Component_` FROM `Registry`");
        bool wroteUninstall = false;
        if (uninstall != null && !(keepExisting && UninstallRecord.Intact(_db, uninstall, sandbox)))
        {
            status?.Invoke("Saving the uninstall information...");
            UninstallRecord.Write(_db, uninstall, props, dirs, comps, files, On, Format, registry, sandbox);
            wroteUninstall = true;
        }
        status?.Invoke("Finishing the setup in Windows...");
        int values = 0;
        var rows = new List<(int Root, string Key, string Name, string Value)>();
        var last = new List<(int Root, string Key, string Name, string Value)>();
        foreach (var r in registry)
        {
            if (!On(r[4])) continue;
            var key = Format(r[1]);
            if (key.Contains(@"\Installer\UserData\", StringComparison.OrdinalIgnoreCase)) continue;
            (key.EndsWith(@"\InstallFolder", StringComparison.OrdinalIgnoreCase) ? last : rows).Add((int.Parse(r[0]), key, Format(r[2]), r[3]));
        }
        foreach (var (root, key, name, value) in Final(rows))
            values += WriteRegistry(root, key, name, value, Format, sandbox, keepExisting, How(root, key, value));
        foreach (var (comp, c) in comps)
            if ((c.Attrs & 8) != 0 && On(comp) && files.TryGetValue(c.KeyPath, out var shared))
            {
                using var hive = MsiRegistry.Hive(sandbox);
                using var k = hive.CreateSubKey(MsiRegistry.SharedDlls, true);
                if (keepExisting && k.GetValue(shared.Path) is not null) continue;
                k.SetValue(shared.Path, (k.GetValue(shared.Path) is int n ? n : 0) + 1, RegistryValueKind.DWord);
                if (keepExisting) values++;
            }

        string KeyFile(string comp) => files[comps[comp].KeyPath].Path;
        var (classes, progIdsWritten) = ComRegistration.RegisterClasses(_db, On, KeyFile, sandbox, keepExisting);
        int typeLibs = ComRegistration.RegisterTypeLibs(_db, On, KeyFile, sandbox, keepExisting);

        int shortcuts = 0;
        status?.Invoke("Creating the shortcuts...");
        foreach (var r in _db.Query("SELECT `Directory_`,`Name`,`Component_`,`Target`,`Arguments`,`Description`,`WkDir` FROM `Shortcut`"))
        {
            if (!On(r[2]) || !dirs.TryGetValue(r[0], out var dir)) continue;
            var lnk = Path.Combine(dir, MsiDatabase.LongName(r[1]) + ".lnk");
            if (keepExisting && File.Exists(lnk)) continue;
            var wk = r[6].Length > 0 && dirs.TryGetValue(r[6], out var w) ? w.TrimEnd('\\') : "";
            Shortcut.Create(lnk, Format(r[3]), Format(r[4]), Format(r[5]), wk);
            shortcuts++;
        }

        foreach (var (root, key, name, value) in Final(last))
            values += WriteRegistry(root, key, name, value, Format, sandbox, keepExisting, How(root, key, value));
        return new Result(files.Count, total, values, classes, typeLibs, shortcuts, progIdsWritten, wroteUninstall);
    }

    public List<(string Path, byte[]? Md5)> Targets(string installDir)
    {
        var props = new Dictionary<string, string> { ["INSTALLDIR"] = installDir.TrimEnd('\\') + "\\" };
        var dirs = ResolveDirectories(props, null);
        var comps = _db.Query("SELECT `Component`,`Directory_` FROM `Component`").ToDictionary(r => r[0], r => r[1]);
        var hashes = _db.Query("SELECT `File_`,`HashPart1`,`HashPart2`,`HashPart3`,`HashPart4` FROM `MsiFileHash`")
            .ToDictionary(r => r[0], r => r.Skip(1).SelectMany(h => BitConverter.GetBytes(int.Parse(h))).ToArray());
        return _db.Query("SELECT `File`,`Component_`,`FileName` FROM `File`")
            .Select(r => (Path.Combine(dirs[comps[r[1]]], MsiDatabase.LongName(r[2])), hashes.TryGetValue(r[0], out var h) ? h : null))
            .ToList();
    }

    public (string Path, byte[]? Md5) ProductFile(string installDir, string keyFile, string rel)
    {
        var targets = Targets(installDir);
        if (ProductFolder(targets.Select(t => t.Path), installDir, keyFile, out var folder) is { } problem)
            throw new InvalidDataException($"The package {problem}.");
        var want = Path.GetFullPath(Path.Combine(folder, rel.Replace('/', '\\')));
        var found = targets.Where(t => Path.GetFullPath(t.Path).Equals(want, StringComparison.OrdinalIgnoreCase)).ToList();
        return found.Count == 1 ? found[0] : throw new InvalidDataException($"The package doesn't list {rel} exactly once.");
    }

    public void ExtractFile(string installDir, string path, string to, CancellationToken ct)
    {
        var props = new Dictionary<string, string> { ["INSTALLDIR"] = installDir.TrimEnd('\\') + "\\" };
        var dirs = ResolveDirectories(props, null);
        var comps = _db.Query("SELECT `Component`,`Directory_` FROM `Component`").ToDictionary(r => r[0], r => r[1]);
        var want = Path.GetFullPath(path);
        var rows = _db.Query("SELECT `File`,`Component_`,`FileName`,`Sequence` FROM `File`");
        var found = rows.Where(r => Path.GetFullPath(Path.Combine(dirs[comps[r[1]]], MsiDatabase.LongName(r[2]))).Equals(want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count != 1 || !int.TryParse(found[0][3], out var sequence) || rows.Count(r => r[0].Equals(found[0][0], StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidDataException($"The package doesn't list {Path.GetFileName(path)} exactly once.");
        var cab = _db.Query("SELECT `LastSequence`,`Cabinet` FROM `Media`")
            .Where(r => r[1].Length > 0 && int.TryParse(r[0], out var last) && last >= sequence)
            .OrderBy(r => int.Parse(r[0]))
            .FirstOrDefault() ?? throw new InvalidDataException($"The package has no cabinet for {Path.GetFileName(path)}.");
        var temps = new List<string>();
        try
        {
            var cabPath = cab[1].StartsWith('#') ? _db.ExtractStream(cab[1][1..], temps) : Path.Combine(Path.GetDirectoryName(_db.Location)!, cab[1]);
            var key = found[0][0];
            CabExtractor.Extract(cabPath, name => name.Equals(key, StringComparison.OrdinalIgnoreCase) ? to : null, null, ct);
        }
        finally { foreach (var t in temps) FileDelete.Try(t); }
        if (!File.Exists(to)) throw new InvalidDataException($"{Path.GetFileName(path)} was not in {cab[1]}.");
    }

    private static bool Condition(string condition, IReadOnlyDictionary<string, string> props)
    {
        if (condition.Length == 0) return true;
        var m = ConditionRx.Match(condition);
        return m.Success && props.TryGetValue(m.Groups[1].Value, out var v) && v == m.Groups[2].Value;
    }

    private Dictionary<string, string> ResolveDirectories(Dictionary<string, string> props, Sandbox? sandbox)
    {
        var rows = _db.Query("SELECT `Directory`,`Directory_Parent`,`DefaultDir` FROM `Directory`").ToDictionary(r => r[0], r => r);
        static string Sys(Environment.SpecialFolder f) => AppPaths.Special(f).TrimEnd('\\') + "\\";
        var known = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ProgramFilesFolder"] = Sys(Environment.SpecialFolder.ProgramFilesX86),
            ["CommonFilesFolder"] = Sys(Environment.SpecialFolder.CommonProgramFilesX86),
            ["ProgramMenuFolder"] = Sys(Environment.SpecialFolder.CommonPrograms),
            ["StartMenuFolder"] = Sys(Environment.SpecialFolder.CommonStartMenu),
            ["DesktopFolder"] = Sys(Environment.SpecialFolder.CommonDesktopDirectory),
            ["SystemFolder"] = Sys(Environment.SpecialFolder.SystemX86),
            ["WindowsFolder"] = Sys(Environment.SpecialFolder.Windows),
            ["TARGETDIR"] = Path.GetPathRoot(props["INSTALLDIR"])!,
        };
        if (sandbox != null)
            foreach (var f in new[] { "ProgramMenuFolder", "StartMenuFolder", "DesktopFolder" })
                known[f] = Path.Combine(sandbox.Folder, f) + "\\";
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string Resolve(string key, int depth)
        {
            if (result.TryGetValue(key, out var done)) return done;
            if (depth > 64) throw new InvalidDataException("The package's folder table loops.");
            string path;
            if (props.TryGetValue(key, out var p) && key == "INSTALLDIR") path = p;
            else if (known.TryGetValue(key, out var k)) path = k;
            else if (rows.TryGetValue(key, out var r) && r[1].Length > 0)
            {
                var name = MsiDatabase.LongName(r[2]);
                var parent = Resolve(r[1], depth + 1);
                path = name is "." or "" ? parent : Path.Combine(parent, name) + "\\";
            }
            else throw new InvalidDataException($"The package names an unknown folder: {key}");
            return result[key] = path;
        }
        foreach (var key in rows.Keys) Resolve(key, 0);
        return result;
    }

    private string FormatValue(string s, Dictionary<string, string> props, Dictionary<string, string> dirs,
                               Dictionary<string, (string Path, long Size)> files) =>
        TokenRx.Replace(s, m =>
        {
            var t = m.Groups[1].Value;
            if (t == "~") return "\0";
            if (t.StartsWith('\\') && t.Length == 2) return t[1..];
            if (t.StartsWith('#') || t.StartsWith('!')) return files.TryGetValue(t[1..], out var f) ? f.Path : "";
            if (t.StartsWith('$')) return ComponentDir(t[1..], dirs) ?? "";
            if (t.StartsWith('%')) return Environment.GetEnvironmentVariable(t[1..]) ?? "";
            if (dirs.TryGetValue(t, out var d)) return d;
            return props.TryGetValue(t, out var v) ? v : "";
        });

    private string? ComponentDir(string component, Dictionary<string, string> dirs) =>
        _db.Query($"SELECT `Directory_` FROM `Component` WHERE `Component`='{component.Replace("'", "")}'").FirstOrDefault() is { } r &&
        dirs.TryGetValue(r[0], out var d) ? d : null;

    // root 0 = classes (per machine here), 1 = current user, 2 = machine
    private static int WriteRegistry(int root, string key, string name, string value, Func<string, string> format, Sandbox? sandbox,
                                     bool keepExisting = false, Keep how = Keep.Kind)
    {
        if (root is not (0 or 1 or 2 or -1)) throw new InvalidDataException($"Registry root {root} is not supported.");
        using var hive = MsiRegistry.Hive(sandbox, user: root == 1);
        var path = root == 0 ? @"SOFTWARE\Classes\" + key : key;
        // "+" or "*" with no value creates the key, "-" is only a removal rule for uninstall
        if (value.Length == 0 && name is "+" or "*" or "-")
        {
            if (name != "-") hive.CreateSubKey(path, true)?.Dispose();
            return 0;
        }
        if (value.Length == 0 && name.Length == 0)
        {
            hive.CreateSubKey(path, true)?.Dispose();
            return 0;
        }
        var (kind, data) = RegistryData(value, format);
        if (keepExisting)
        {
            using var had = hive.OpenSubKey(path);
            if (had?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } old && had.GetValueKind(name) == kind
                && how switch { Keep.Path => SameData(old, data, paths: true), Keep.Exact => SameData(old, data, paths: false), _ => true })
                return 0;
        }
        using var k = hive.CreateSubKey(path, true);
        k.SetValue(name, data, kind);
        return 1;
    }

    private static (RegistryValueKind Kind, object Data) RegistryData(string value, Func<string, string> format)
    {
        if (value.StartsWith("#x", StringComparison.OrdinalIgnoreCase)) return (RegistryValueKind.Binary, Convert.FromHexString(value[2..]));
        if (value.StartsWith("#%")) return (RegistryValueKind.ExpandString, format(value[2..]));
        if (value.StartsWith("##")) return (RegistryValueKind.String, format(value[1..]));
        if (value.StartsWith('#') && long.TryParse(value[1..], out var dw)) return (RegistryValueKind.DWord, unchecked((int)(uint)dw));
        var text = format(value);
        return text.Contains('\0')
            ? (RegistryValueKind.MultiString, text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            : (RegistryValueKind.String, text);
    }

    private static bool SameData(object old, object want, bool paths) => (old, want) switch
    {
        (string a, string b) => paths ? SameText(a, b) : a == b,
        (string[] a, string[] b) => a.Length == b.Length && a.Zip(b).All(x => paths ? SameText(x.First, x.Second) : x.First == x.Second),
        (int a, int b) => a == b,
        (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
        _ => false,
    };

    private static bool SameText(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
