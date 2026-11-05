using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Provenance.Common.Install;

// applies an MSI straight from its tables, skipping Windows Installer and its 61,353 components (ugh). Supported()
// refuses what it can't do
public sealed class MsiPackage : IDisposable
{
    private const string SharedDlls = @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs";
    private static readonly Regex TokenRx = new(@"\[(\\.|[^\[\]]*)\]");
    private static readonly Regex ConditionRx = new(@"^\s*([A-Za-z_][A-Za-z0-9_.]*)\s*=\s*""([^""]*)""\s*$");
    private readonly IntPtr _db;
    private readonly string _path;

    public MsiPackage(string path)
    {
        _path = Path.GetFullPath(path);
        if (MsiOpenDatabaseW(_path, IntPtr.Zero /* MSIDBOPEN_READONLY */, out _db) != 0)
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a readable installer package.");
    }

    public void Dispose() => MsiCloseHandle(_db);

    public List<string[]> Query(string sql)
    {
        var rows = new List<string[]>();
        uint r = MsiDatabaseOpenViewW(_db, sql, out var view);
        if (r == 1615 /* ERROR_BAD_QUERY_SYNTAX: no such table in this package */) return rows;
        if (r != 0) throw Unreadable(r);
        try
        {
            if ((r = MsiViewExecute(view, IntPtr.Zero)) != 0) throw Unreadable(r);
            while ((r = MsiViewFetch(view, out var rec)) != 259 /* ERROR_NO_MORE_ITEMS */)
            {
                if (r != 0) throw Unreadable(r);
                try
                {
                    var row = new string[MsiRecordGetFieldCount(rec)];
                    for (int i = 0; i < row.Length; i++) row[i] = RecordString(rec, (uint)i + 1);
                    rows.Add(row);
                }
                finally { MsiCloseHandle(rec); }
            }
        }
        finally { MsiCloseHandle(view); }
        return rows;
    }

    private static InvalidDataException Unreadable(uint error) =>
        new($"The package could not be read (error {error}).");

    // Tables Apply reproduces, or that do nothing on an installed machine (the installer's own dialogs and sequences,
    // searches, hashes). Any other table with rows makes the package unsupported.
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

    public string? Supported()
    {
        foreach (var t in Query("SELECT `Name` FROM `_Tables`").Select(r => r[0]))
            if (!Known.Contains(t) && Query($"SELECT * FROM `{t}`").Count > 0) return "the " + t + " table";
        var custom = Query("SELECT `Action` FROM `CustomAction`").Select(r => r[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var a in Query("SELECT `Action` FROM `InstallExecuteSequence`").Select(r => r[0]))
            if (custom.Contains(a) && !KnownActions.Contains(a)) return "the custom action " + a;
        if (Query("SELECT `Level` FROM `Feature`").Any(r => !int.TryParse(r[0], out var l) || l < 1)) return "a feature that is not installed by default";
        foreach (var r in Query("SELECT `Directory`,`Directory_Parent` FROM `Directory`"))
            if ((r[1].Length == 0 || r[1] == "TARGETDIR") && !KnownRoots.Contains(r[0])) return "the folder " + r[0];
        var fileKeys = Query("SELECT `File` FROM `File`").Select(r => r[0]).ToHashSet(StringComparer.Ordinal);
        var keyPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in Query("SELECT `Component`,`Condition`,`Attributes`,`KeyPath` FROM `Component`"))
        {
            // only simple PROPERTY="value" conditions, and attributes 4 (registry key path) and 8 (shared DLL count)
            if (c[1].Length > 0 && !ConditionRx.IsMatch(c[1])) return "the component condition " + c[1];
            if (!int.TryParse(c[2].Length == 0 ? "0" : c[2], out var attrs) || (attrs & ~(4 | 8)) != 0) return $"component attributes {c[2]}";
            keyPaths[c[0]] = c[3];
        }
        // read-only, hidden, system, vital and the two compression flags
        if (Query("SELECT `Attributes` FROM `File`").Any(r => r[0].Length > 0 && (!int.TryParse(r[0], out var a) || (a & ~(1 | 2 | 4 | 512 | 8192 | 16384)) != 0)))
            return "file attributes";
        foreach (var r in Query("SELECT `Component_`,`AppId_`,`FileTypeMask`,`Icon_`,`DefInprocHandler`,`Argument` FROM `Class`"))
            if (!keyPaths.TryGetValue(r[0], out var kp) || !fileKeys.Contains(kp) || r.Skip(1).Any(x => x.Length > 0)) return "a COM class this does not reproduce";
        foreach (var r in Query("SELECT `Component_` FROM `TypeLib`"))
            if (!keyPaths.TryGetValue(r[0], out var kp) || !fileKeys.Contains(kp)) return "a type library this does not reproduce";
        if (Query("SELECT `Icon_` FROM `ProgId`").Any(r => r[0].Length > 0)) return "a ProgID icon";
        if (Query("SELECT `Icon_`,`Hotkey`,`ShowCmd` FROM `Shortcut`").Any(r => r.Any(x => x.Length > 0))) return "a shortcut icon, hotkey or window state";
        return null;
    }

    public sealed record Sandbox(string RegistryKey, string Folder);

    // with the region's key, the product's InstallFolder value and its settings key, the uninstall takes the product's whole
    // folder and settings, not just what the package wrote
    public sealed record UninstallSetup(string UninstallerExe, string Folder, string InstallSource, string? RegionKey = null,
                                        string? FolderValue = null, string? SettingsKey = null);

    public sealed record Result(int Files, long Bytes, int RegistryValues, int Classes, int TypeLibs, int Shortcuts, int ProgIds = 0,
                                bool Uninstall = false)
    {
        public int Written => RegistryValues + Classes + TypeLibs + Shortcuts + ProgIds + (Uninstall ? 1 : 0);
    }

    private enum Keep { Kind, Path, Exact }

    // files first so a stopped run never leaves values pointing at missing files, InstallFolder goes last. keepExisting
    // (a repair) only rewrites what's missing or wrong, and the counts are what it wrote
    public Result Apply(string installDir, IReadOnlyDictionary<string, string> properties,
                        IProgress<(long done, long total)>? progress, CancellationToken ct, int parallel = 4, Sandbox? sandbox = null,
                        UninstallSetup? uninstall = null, Action<string>? status = null, bool payloadInPlace = false,
                        bool keepExisting = false)
    {
        if (Supported() is { } missing) throw new NotSupportedException($"The package uses {missing}.");
        var props = new Dictionary<string, string>(properties, StringComparer.Ordinal)
        {
            ["INSTALLDIR"] = installDir.TrimEnd('\\') + "\\",
        };
        var dirs = ResolveDirectories(props, sandbox);
        // attributes come from this one query, since a query per component (61,353 for FFXI) left the install sat at 100% for ages
        var comps = Query("SELECT `Component`,`Directory_`,`Condition`,`KeyPath`,`Attributes` FROM `Component`")
            .ToDictionary(r => r[0], r => (Dir: r[1], Condition: r[2], KeyPath: r[3], Attrs: int.TryParse(r[4], out var a) ? a : 0));
        bool On(string comp) => comps.TryGetValue(comp, out var c) && Condition(c.Condition, props);
        var files = new Dictionary<string, (string Path, long Size)>(StringComparer.Ordinal);
        var fileAttrs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in Query("SELECT `File`,`Component_`,`FileName`,`FileSize`,`Attributes` FROM `File`"))
        {
            if (!On(r[1])) continue;
            var path = Path.Combine(dirs[comps[r[1]].Dir], LongName(r[2]));
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
                progress?.Report((done, total));
            }
        }
        var cabs = payloadInPlace ? [] : Query("SELECT `Cabinet` FROM `Media`").Select(r => r[0]).Where(c => c.Length > 0).ToList();
        var temps = new List<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var cabPaths = cabs.Select(c => c.StartsWith('#') ? ExtractStream(c[1..], temps) : Path.Combine(Path.GetDirectoryName(_path)!, c)).ToList();
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
        finally { foreach (var t in temps) try { File.Delete(t); } catch { } }
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
        // a value naming one of the install's folders or files has to name it, a moved install leaves the old ones
        bool NamesPath(string s) => TokenRx.Matches(s).Any(m => m.Groups[1].Value is var t
            && (t.StartsWith('#') || t.StartsWith('!') || t.StartsWith('$') || dirs.ContainsKey(t)));
        // POL folds the Interface values into the patch.ver key, and nothing under HKCR is anybody's setting
        Keep How(int root, string key, string value) => key.EndsWith(@"\Interface", StringComparison.OrdinalIgnoreCase) ? Keep.Exact
            : NamesPath(value) ? Keep.Path : root == 0 ? Keep.Exact : Keep.Kind;
        // SE's viewer package writes some values twice from two components (its patchfiles copies of app.dll and
        // polcore.dll, then the real ones). A full apply leaves the last, so that's the only one a repair checks
        IEnumerable<(int Root, string Key, string Name, string Value)> Final(List<(int Root, string Key, string Name, string Value)> list) =>
            !keepExisting ? list
            : list.Select((r, i) => (r, i))
                  .GroupBy(x => (x.r.Root == 1, (x.r.Root == 0 ? @"SOFTWARE\Classes\" + x.r.Key : x.r.Key).ToUpperInvariant(), x.r.Name.ToUpperInvariant()))
                  .Select(g => g.Last()).OrderBy(x => x.i).Select(x => x.r);

        // before anything that marks the product installed, so it's never installed without an uninstall entry
        bool wroteUninstall = false;
        if (uninstall != null && !(keepExisting && UninstallIntact(uninstall, sandbox)))
        {
            status?.Invoke("Saving the uninstall information...");
            WriteUninstall(uninstall, props, dirs, comps, files, On, Format, sandbox);
            wroteUninstall = true;
        }
        status?.Invoke("Finishing the setup in Windows...");
        int values = 0;
        var rows = new List<(int Root, string Key, string Name, string Value)>();
        var last = new List<(int Root, string Key, string Name, string Value)>();
        foreach (var r in Query("SELECT `Root`,`Key`,`Name`,`Value`,`Component_` FROM `Registry`"))
        {
            if (!On(r[4])) continue;
            var key = Format(r[1]);
            // Windows Installer's own records never get copied, the product isn't registered with it
            if (key.Contains(@"\Installer\UserData\", StringComparison.OrdinalIgnoreCase)) continue;
            (key.EndsWith(@"\InstallFolder", StringComparison.OrdinalIgnoreCase) ? last : rows).Add((int.Parse(r[0]), key, Format(r[2]), r[3]));
        }
        foreach (var (root, key, name, value) in Final(rows))
            values += WriteRegistry(root, key, name, value, Format, sandbox, keepExisting, How(root, key, value));
        foreach (var (comp, c) in comps)
            if ((c.Attrs & 8) != 0 && On(comp) && files.TryGetValue(c.KeyPath, out var shared))
            {
                using var hive = Hive(sandbox);
                using var k = hive.CreateSubKey(SharedDlls, true);
                if (keepExisting && k.GetValue(shared.Path) is not null) continue;   // a repair isn't another install
                k.SetValue(shared.Path, (k.GetValue(shared.Path) is int n ? n : 0) + 1, RegistryValueKind.DWord);
                if (keepExisting) values++;
            }

        // RegisterClassInfo and RegisterProgIdInfo, minus the descriptor value that'd send COM off to Windows Installer
        var progIds = Query("SELECT `ProgId`,`ProgId_Parent`,`Class_`,`Description` FROM `ProgId`");
        int classes = 0, progIdsWritten = 0;
        using (var hklm = Hive(sandbox))
        {
            foreach (var r in Query("SELECT `CLSID`,`Context`,`Component_`,`ProgId_Default`,`Description` FROM `Class`"))
            {
                if (!On(r[2])) continue;
                var server = files[comps[r[2]].KeyPath];   // Supported() checked it's a file
                if (keepExisting && ClassIntact(hklm, r[0], r[1], server.Path)) continue;
                using var cls = hklm.CreateSubKey($@"SOFTWARE\Classes\CLSID\{r[0]}", true);
                if (r[4].Length > 0) cls.SetValue("", r[4]);
                using (var ctx = cls.CreateSubKey(r[1], true)) ctx.SetValue("", server.Path);
                if (r[3].Length > 0)
                {
                    using (var p = cls.CreateSubKey("ProgID", true)) p.SetValue("", r[3]);
                    if (progIds.FirstOrDefault(x => x[1] == r[3]) is { } vi)
                        using (var v = cls.CreateSubKey("VersionIndependentProgID", true)) v.SetValue("", vi[0]);
                }
                classes++;
            }
            foreach (var p in progIds)
            {
                var clsid = p[2].Length > 0 ? p[2] : progIds.FirstOrDefault(x => x[0] == p[1])?[2] ?? "";
                if (keepExisting)
                {
                    using var had = hklm.OpenSubKey($@"SOFTWARE\Classes\{p[0]}");
                    using var hadClsid = had?.OpenSubKey("CLSID");
                    if (had is not null && (clsid.Length == 0 || string.Equals(hadClsid?.GetValue("") as string, clsid, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }
                using var k = hklm.CreateSubKey($@"SOFTWARE\Classes\{p[0]}", true);
                if (p[3].Length > 0) k.SetValue("", p[3]);
                if (clsid.Length > 0) using (var c = k.CreateSubKey("CLSID", true)) c.SetValue("", clsid);
                if (p[1].Length > 0) using (var c = k.CreateSubKey("CurVer", true)) c.SetValue("", p[1]);
                progIdsWritten++;
            }
        }

        int typeLibs = 0;
        foreach (var r in Query("SELECT `LibID`,`Component_` FROM `TypeLib`"))
        {
            if (!On(r[1])) continue;
            var lib = files[comps[r[1]].KeyPath];
            if (sandbox != null) { if (!keepExisting) typeLibs++; continue; }   // a sandbox can't register one, or check it
            if (keepExisting && TypeLibIntact(lib.Path)) continue;
            Marshal.ThrowExceptionForHR(LoadTypeLibEx(lib.Path, 1 /* REGKIND_REGISTER */, out var tl));
            if (tl != null) Marshal.ReleaseComObject(tl);
            // REGKIND_REGISTER says S_OK even when the registry write was refused, so a repair looks for itself
            if (keepExisting && !TypeLibIntact(lib.Path))
                throw Shown.Mark(new InvalidOperationException(
                    $"Windows didn't register {Path.GetFileName(lib.Path)}, which the game needs. Run the repair again."));
            typeLibs++;
        }

        // a repair leaves shortcuts be, a missing one doesn't stop the game and it's usually gone on purpose
        int shortcuts = 0;
        if (!keepExisting)
        {
            status?.Invoke("Creating the shortcuts...");
            foreach (var r in Query("SELECT `Directory_`,`Name`,`Component_`,`Target`,`Arguments`,`Description`,`WkDir` FROM `Shortcut`"))
            {
                if (!On(r[2]) || !dirs.TryGetValue(r[0], out var dir)) continue;
                var wk = r[6].Length > 0 && dirs.TryGetValue(r[6], out var w) ? w.TrimEnd('\\') : "";
                CreateShortcut(Path.Combine(dir, LongName(r[1]) + ".lnk"), Format(r[3]), Format(r[4]), Format(r[5]), wk);
                shortcuts++;
            }
        }

        foreach (var (root, key, name, value) in Final(last))
            values += WriteRegistry(root, key, name, value, Format, sandbox, keepExisting, How(root, key, value));
        return new Result(files.Count, total, values, classes, typeLibs, shortcuts, progIdsWritten, wroteUninstall);
    }

    // the entry, and both files it runs as this build's pair in an admin-only folder, so a repair replaces an older
    // pair (an entry whose uninstaller's gone can't uninstall anything)
    private bool UninstallIntact(UninstallSetup setup, Sandbox? sandbox)
    {
        var code = Query("SELECT `Value` FROM `Property` WHERE `Property`='ProductCode'").FirstOrDefault()?[0];
        if (string.IsNullOrEmpty(code)) return false;
        using var hklm = Hive(sandbox);
        using var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + code);
        if (k?.GetValue("UninstallString", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command) return false;
        var parts = Regex.Matches(command, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
        if (parts.Count != 2 || !parts.All(File.Exists)) return false;
        if (!UninstallFolder.Protected(Path.GetDirectoryName(parts[1])!, parts[1])) return false;
        if (!Path.GetFileName(parts[0]).Equals(Path.GetFileName(setup.UninstallerExe), StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var record = File.ReadAllLines(parts[1], Encoding.Unicode);
            return record.Any(l => l.StartsWith("I ", StringComparison.Ordinal))
                   && (setup.RegionKey is null || record.Any(l => l.StartsWith("L ", StringComparison.Ordinal)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool ClassIntact(RegistryKey hklm, string clsid, string context, string server)
    {
        using var k = hklm.OpenSubKey($@"SOFTWARE\Classes\CLSID\{clsid}\{context}");
        return k?.GetValue("") is string named && SamePath(Unquote(named), server) && File.Exists(Unquote(named));
    }

    // registered, and at this file
    private static bool TypeLibIntact(string path)
    {
        if (LoadTypeLibEx(path, 2 /* REGKIND_NONE */, out var tl) != 0 || tl is not ITypeLib typeLib) return false;
        try
        {
            typeLib.GetLibAttr(out var pa);
            var a = Marshal.PtrToStructure<TYPELIBATTR>(pa);
            typeLib.ReleaseTLibAttr(pa);
            using var hklm = Hive(null);
            using var k = hklm.OpenSubKey($@"SOFTWARE\Classes\TypeLib\{a.guid:B}\{a.wMajorVerNum:x}.{a.wMinorVerNum:x}\{a.lcid:x}\win32");
            return k?.GetValue("") is string named && SamePath(Unquote(named), path) && File.Exists(Unquote(named));
        }
        finally { Marshal.ReleaseComObject(typeLib); }
    }

    private static string Unquote(string s) => s.Trim().Trim('"');

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public List<(string Path, byte[]? Md5)> Targets(string installDir)
    {
        var props = new Dictionary<string, string> { ["INSTALLDIR"] = installDir.TrimEnd('\\') + "\\" };
        var dirs = ResolveDirectories(props, null);
        var comps = Query("SELECT `Component`,`Directory_` FROM `Component`").ToDictionary(r => r[0], r => r[1]);
        var hashes = Query("SELECT `File_`,`HashPart1`,`HashPart2`,`HashPart3`,`HashPart4` FROM `MsiFileHash`")
            .ToDictionary(r => r[0], r => r.Skip(1).SelectMany(h => BitConverter.GetBytes(int.Parse(h))).ToArray());
        return Query("SELECT `File`,`Component_`,`FileName` FROM `File`")
            .Select(r => (Path.Combine(dirs[comps[r[1]]], LongName(r[2])), hashes.TryGetValue(r[0], out var h) ? h : null))
            .ToList();
    }

    // The Add/Remove Programs entry as Windows Installer writes it, except uninstall and modify run my uninstaller
    // (MsiExec /X would fail, the product isn't registered with it) and NoRepair is set
    private void WriteUninstall(UninstallSetup setup, Dictionary<string, string> props, Dictionary<string, string> dirs,
                                Dictionary<string, (string Dir, string Condition, string KeyPath, int Attrs)> comps,
                                Dictionary<string, (string Path, long Size)> files, Func<string, bool> on,
                                Func<string, string> format, Sandbox? sandbox)
    {
        var p = Query("SELECT `Property`,`Value` FROM `Property`").ToDictionary(r => r[0], r => r[1]);
        string code = p["ProductCode"];
        string arpKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + code;
        using var held = UninstallFolder.Hold(setup.Folder);
        string exe = Path.Combine(setup.Folder, Path.GetFileName(setup.UninstallerExe));
        string list = Path.Combine(setup.Folder, code + ".txt");
        // made fresh, so they take the folder's permissions rather than keeping whatever they had
        File.Delete(exe);
        File.Delete(list);
        File.Copy(setup.UninstallerExe, exe);
        UninstallFolder.Own(exe);

        var root = Path.TrimEndingDirectorySeparator(props["INSTALLDIR"]);
        // the record format's at the top of native\uninstall.c, so a letter changes on both sides or neither
        var lines = new List<string> { "N " + p.GetValueOrDefault("ProductName", ""), "A " + arpKey, "I " + root };
        // the product's folder, as its InstallFolder value names it. It goes whole, so nothing in it gets a line of its own
        string? tree = null;
        string folderKey = setup.RegionKey + @"\InstallFolder";
        if (setup.RegionKey != null && setup.FolderValue != null)
            foreach (var r in Query("SELECT `Root`,`Key`,`Name`,`Value`,`Component_` FROM `Registry`"))
                if (on(r[4]) && r[0] == "2" && format(r[1]).Equals(folderKey, StringComparison.OrdinalIgnoreCase) && format(r[2]) == setup.FolderValue
                    && Path.TrimEndingDirectorySeparator(format(r[3])) is var t && Path.IsPathFullyQualified(t)
                    && t.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                    tree = t;
        bool InTree(string path) => tree != null && (path.Equals(tree, StringComparison.OrdinalIgnoreCase)
                                                     || path.StartsWith(tree + "\\", StringComparison.OrdinalIgnoreCase));
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Folder(string dir)
        {
            for (var d = Path.TrimEndingDirectorySeparator(dir); d.Length > 3; d = Path.GetDirectoryName(d)!)
            {
                folders.Add(d);
                if (d.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
                if (!d.StartsWith(root, StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        foreach (var r in Query("SELECT `Directory_`,`Name`,`Component_` FROM `Shortcut`"))
            if (on(r[2]) && dirs.TryGetValue(r[0], out var d))
            {
                lines.Add("S " + Path.Combine(d, LongName(r[1]) + ".lnk"));
                folders.Add(Path.TrimEndingDirectorySeparator(d));
            }
        foreach (var r in Query("SELECT `Root`,`Key`,`Name`,`Value`,`Component_` FROM `Registry`"))
        {
            if (!on(r[4]) || r[2] == "-") continue;
            var key = format(r[1]);
            if (key.Contains(@"\Installer\UserData\", StringComparison.OrdinalIgnoreCase)) continue;
            if (r[0] == "0") key = @"SOFTWARE\Classes\" + key;
            bool keyRow = r[3].Length == 0 && (r[2] is "+" or "*" || r[2].Length == 0);
            lines.Add($"V {(r[0] == "1" ? 'U' : 'M')} {key}\t{(keyRow ? "+" : format(r[2]))}");
        }
        foreach (var c in Query("SELECT `Component`,`Attributes`,`KeyPath` FROM `Component`"))
            if (on(c[0]) && int.TryParse(c[1], out var ca) && (ca & 8) != 0 && files.TryGetValue(c[2], out var shared))
                lines.Add($"V M {SharedDlls}\t{shared.Path}");
        foreach (var r in Query("SELECT `CLSID`,`Component_` FROM `Class`"))
            if (on(r[1])) lines.Add(@"C SOFTWARE\Classes\CLSID\" + r[0]);
        foreach (var r in Query("SELECT `ProgId` FROM `ProgId`"))
            lines.Add(@"C SOFTWARE\Classes\" + r[0]);
        foreach (var r in Query("SELECT `LibID`,`Component_` FROM `TypeLib`"))
        {
            if (!on(r[1]) || !files.TryGetValue(comps[r[1]].KeyPath, out var lib)) continue;
            if (LoadTypeLibEx(lib.Path, 2 /* REGKIND_NONE */, out var tl) == 0 && tl is ITypeLib typeLib)
            {
                typeLib.GetLibAttr(out var pa);
                var a = Marshal.PtrToStructure<TYPELIBATTR>(pa);
                typeLib.ReleaseTLibAttr(pa);
                lines.Add($"T {a.guid:B} {a.wMajorVerNum} {a.wMinorVerNum} {a.lcid}");
                Marshal.ReleaseComObject(typeLib);
            }
        }
        foreach (var f in files.Values)
        {
            if (!InTree(f.Path)) lines.Add("F " + f.Path);
            Folder(Path.GetDirectoryName(f.Path)!);
        }
        foreach (var r in Query("SELECT `Component_`,`FileName`,`DirProperty`,`InstallMode` FROM `RemoveFile`"))
        {
            // install modes 2 and 3 run on uninstall
            if (!on(r[0]) || !int.TryParse(r[3], out var mode) || (mode & 2) == 0) continue;
            var dir = dirs.TryGetValue(r[2], out var dd) ? dd : props.GetValueOrDefault(r[2], "");
            if (dir.Length == 0) continue;
            if (r[1].Length == 0) folders.Add(Path.TrimEndingDirectorySeparator(dir));
            else if (!InTree(Path.TrimEndingDirectorySeparator(dir))) lines.Add($"P {Path.TrimEndingDirectorySeparator(dir)}\t{LongName(r[1])}");
        }
        if (tree != null) lines.Add("R " + tree);
        if (setup.SettingsKey != null) lines.Add("K " + setup.SettingsKey);
        if (setup.RegionKey != null)
        {
            if (setup.FolderValue != null && !lines.Contains($"V M {folderKey}\t{setup.FolderValue}", StringComparer.OrdinalIgnoreCase))
                lines.Add($"V M {folderKey}\t{setup.FolderValue}");
            lines.Add("L " + setup.RegionKey);
        }

        string? icon = null;
        using var iconData = new MemoryStream();
        if (p.TryGetValue("ARPPRODUCTICON", out var iconName) && iconName.Length > 0 &&
            CopyStream($"SELECT `Data` FROM `Icon` WHERE `Name`='{iconName.Replace("'", "")}'", iconData))
        {
            var bytes = iconData.ToArray();
            bool isExe = bytes.Length > 1 && bytes[0] == 'M' && bytes[1] == 'Z';
            icon = Path.Combine(setup.Folder, code + (isExe ? ".exe" : ".ico"));
            File.WriteAllBytes(icon, bytes);
            lines.Add("F " + icon);
        }
        var special = new[] { Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.CommonPrograms,
                              Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.ProgramFilesX86,
                              Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
                              Environment.SpecialFolder.Windows, Environment.SpecialFolder.SystemX86 }
            .Select(f => Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(f))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lines.AddRange(folders.Where(f => !special.Contains(f) && Path.GetPathRoot(f) != f + "\\" && !InTree(f))
                              .OrderByDescending(f => f.Length).Select(f => "D " + f));
        File.WriteAllText(list, string.Join("\r\n", lines) + "\r\n", Encoding.Unicode);
        UninstallFolder.Own(list);

        var version = p.GetValueOrDefault("ProductVersion", "0.0.0").Split('.').Select(x => int.TryParse(x, out var v) ? v : 0).Concat([0, 0, 0]).ToArray();
        long kb = files.Values.Sum(f => (f.Size + 4095) / 4096 * 4);
        string command = $"\"{exe}\" \"{list}\"";
        using var hklm = Hive(sandbox);
        using var k = hklm.CreateSubKey(arpKey, true);
        foreach (var empty in new[] { "AuthorizedCDFPrefix", "Comments", "Contact", "HelpLink", "HelpTelephone", "InstallLocation",
                                      "Readme", "Size", "URLInfoAbout", "URLUpdateInfo" })
            k.SetValue(empty, "");
        k.SetValue("DisplayName", p.GetValueOrDefault("ProductName", ""));
        k.SetValue("DisplayVersion", p.GetValueOrDefault("ProductVersion", ""));
        k.SetValue("Publisher", p.GetValueOrDefault("Manufacturer", ""));
        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        k.SetValue("InstallSource", setup.InstallSource);
        k.SetValue("UninstallString", command, RegistryValueKind.ExpandString);
        k.SetValue("ModifyPath", command, RegistryValueKind.ExpandString);
        if (p.GetValueOrDefault("ARPNOMODIFY") == "1") k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        k.SetValue("EstimatedSize", (int)Math.Min(kb, int.MaxValue), RegistryValueKind.DWord);
        k.SetValue("VersionMajor", version[0], RegistryValueKind.DWord);
        k.SetValue("VersionMinor", version[1], RegistryValueKind.DWord);
        k.SetValue("Version", (version[0] << 24) | (version[1] << 16) | (version[2] & 0xFFFF), RegistryValueKind.DWord);
        if (int.TryParse(p.GetValueOrDefault("ProductLanguage"), out var lang)) k.SetValue("Language", lang, RegistryValueKind.DWord);
        if (icon != null) k.SetValue("DisplayIcon", icon);
    }

    private bool CopyStream(string sql, Stream dest)
    {
        if (MsiDatabaseOpenViewW(_db, sql, out var view) != 0) return false;
        try
        {
            if (MsiViewExecute(view, IntPtr.Zero) != 0 || MsiViewFetch(view, out var rec) != 0) return false;
            try
            {
                var buf = new byte[1 << 16];
                while (true)
                {
                    uint n = (uint)buf.Length;
                    if (MsiRecordReadStream(rec, 1, buf, ref n) != 0) throw Shown.Mark(new IOException("Provenance couldn't read part of an installer package. Try again."));
                    if (n == 0) return true;
                    dest.Write(buf, 0, (int)n);
                }
            }
            finally { MsiCloseHandle(rec); }
        }
        finally { MsiCloseHandle(view); }
    }

    private static bool Condition(string condition, IReadOnlyDictionary<string, string> props)
    {
        if (condition.Length == 0) return true;
        var m = ConditionRx.Match(condition);
        return m.Success && props.TryGetValue(m.Groups[1].Value, out var v) && v == m.Groups[2].Value;
    }

    private static string LongName(string s)
    {
        int colon = s.IndexOf(':');
        if (colon >= 0) s = s[..colon];
        int bar = s.IndexOf('|');
        return bar >= 0 ? s[(bar + 1)..] : s;
    }

    private Dictionary<string, string> ResolveDirectories(Dictionary<string, string> props, Sandbox? sandbox)
    {
        var rows = Query("SELECT `Directory`,`Directory_Parent`,`DefaultDir` FROM `Directory`").ToDictionary(r => r[0], r => r);
        static string Sys(Environment.SpecialFolder f) => Environment.GetFolderPath(f).TrimEnd('\\') + "\\";
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
                var name = LongName(r[2]);
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
        Query($"SELECT `Directory_` FROM `Component` WHERE `Component`='{component.Replace("'", "")}'").FirstOrDefault() is { } r &&
        dirs.TryGetValue(r[0], out var d) ? d : null;

    private static RegistryKey Hive(Sandbox? sandbox, bool user = false) => sandbox != null
        ? Registry.CurrentUser.CreateSubKey(sandbox.RegistryKey + (user ? @"\HKCU" : @"\HKLM"), true)
        : RegistryKey.OpenBaseKey(user ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry32);

    // root 0 = classes (per machine here), 1 = current user, 2 = machine
    private static int WriteRegistry(int root, string key, string name, string value, Func<string, string> format, Sandbox? sandbox,
                                     bool keepExisting = false, Keep how = Keep.Kind)
    {
        if (root is not (0 or 1 or 2 or -1)) throw new InvalidDataException($"Registry root {root} is not supported.");
        using var hive = Hive(sandbox, user: root == 1);
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
        // the game and the viewer write their own settings with these same kinds, so the right kind is somebody's setting
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

    // how Windows compares paths, case aside and a trailing \ not mattering
    private static bool SameText(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private string ExtractStream(string name, List<string> temps)
    {
        var path = Path.Combine(Path.GetDirectoryName(_path)!, name + ".provenance");
        temps.Add(path);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        if (!CopyStream($"SELECT `Data` FROM `_Streams` WHERE `Name`='{name.Replace("'", "")}'", fs))
            throw new InvalidDataException($"The package has no embedded cabinet {name}.");
        return path;
    }

    public static void WithoutEmbeddedCabinets(string sourceMsi, string targetMsi)
    {
        List<string> embedded;
        using (var src = new MsiPackage(sourceMsi))
            embedded = src.Query("SELECT `Cabinet` FROM `Media`").Select(r => r[0]).Where(c => c.StartsWith('#')).Select(c => c[1..])
                          .Distinct(StringComparer.Ordinal).ToList();
        var target = Path.GetFullPath(targetMsi);
        var tmp = target + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(sourceMsi, tmp, overwrite: true);
        try
        {
            File.SetAttributes(tmp, File.GetAttributes(tmp) & ~FileAttributes.ReadOnly);
            Check(MsiOpenDatabaseW(tmp, (IntPtr)1 /* MSIDBOPEN_TRANSACT */, out var db), "open");
            try
            {
                foreach (var name in embedded)
                {
                    Check(MsiDatabaseOpenViewW(db, "SELECT `Name`,`Data` FROM `_Streams` WHERE `Name`=?", out var view), "view");
                    var arg = MsiCreateRecord(1);
                    try
                    {
                        Check(MsiRecordSetStringW(arg, 1, name), "name");
                        Check(MsiViewExecute(view, arg), "query");
                        uint r = MsiViewFetch(view, out var rec);
                        if (r == 259 /* ERROR_NO_MORE_ITEMS */) throw new InvalidDataException($"The package has no embedded cabinet {name}.");
                        Check(r, "fetch");
                        try { Check(MsiViewModify(view, 6 /* MSIMODIFY_DELETE */, rec), "delete " + name); }
                        finally { MsiCloseHandle(rec); }
                    }
                    finally { MsiCloseHandle(arg); MsiCloseHandle(view); }
                }
                Check(MsiDatabaseCommit(db), "commit");
            }
            finally { MsiCloseHandle(db); }
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }

        static void Check(uint r, string step)
        {
            if (r != 0) throw new InvalidDataException($"The package's cabinets could not be removed ({step}: error {r}).");
        }
    }

    private static void CreateShortcut(string lnk, string target, string args, string description, string workDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            if (args.Length > 0) link.SetArguments(args);
            if (description.Length > 0) link.SetDescription(description);
            if (workDir.Length > 0) link.SetWorkingDirectory(workDir);
            ((IPersistFile)link).Save(lnk, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    internal static string RecordString(IntPtr rec, uint field)
    {
        uint len = 0;
        var sb = new StringBuilder(1);
        uint r = MsiRecordGetStringW(rec, field, sb, ref len);
        if (r == 234 /* ERROR_MORE_DATA */)
        {
            sb = new StringBuilder((int)++len);
            r = MsiRecordGetStringW(rec, field, sb, ref len);
        }
        return r == 0 ? sb.ToString() : "";
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string rel, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadTypeLibEx(string file, int regKind, [MarshalAs(UnmanagedType.Interface)] out object? typeLib);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr db);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiDatabaseOpenViewW(IntPtr db, string query, out IntPtr view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(IntPtr view, IntPtr record);
    [DllImport("msi.dll")] private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);
    [DllImport("msi.dll")] private static extern uint MsiRecordGetFieldCount(IntPtr record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordGetStringW(IntPtr record, uint field, StringBuilder value, ref uint len);
    [DllImport("msi.dll")] private static extern uint MsiRecordReadStream(IntPtr record, uint field, byte[] buf, ref uint len);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(IntPtr handle);
    [DllImport("msi.dll")] private static extern IntPtr MsiCreateRecord(uint fields);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordSetStringW(IntPtr record, uint field, string value);
    [DllImport("msi.dll")] private static extern uint MsiViewModify(IntPtr view, int mode, IntPtr record);
    [DllImport("msi.dll")] private static extern uint MsiDatabaseCommit(IntPtr db);
}
