using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Provenance.Common.Security;
using Provenance.Common.Win32;

namespace Provenance.Common.Install;

// The record's letters are read by the uninstaller, the format is documented at the top of native\uninstall.c. A letter
// changes on both sides or neither.
internal static class UninstallRecord
{
    // the entry, and both files it runs as this build's pair in an admin-only folder, so a repair replaces an older
    // pair (an entry whose uninstaller's gone can't uninstall anything)
    public static bool Intact(MsiDatabase db, MsiPackage.UninstallSetup setup, MsiPackage.Sandbox? sandbox)
    {
        var code = db.Query("SELECT `Value` FROM `Property` WHERE `Property`='ProductCode'").FirstOrDefault()?[0];
        if (string.IsNullOrEmpty(code)) return false;
        using var hklm = MsiPackage.Hive(sandbox);
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

    // The Add/Remove Programs entry as Windows Installer writes it, except uninstall and modify run my uninstaller
    // (MsiExec /X would fail, the product isn't registered with it) and NoRepair is set
    public static void Write(MsiDatabase db, MsiPackage.UninstallSetup setup, Dictionary<string, string> props,
                             Dictionary<string, string> dirs,
                             Dictionary<string, (string Dir, string Condition, string KeyPath, int Attrs)> comps,
                             Dictionary<string, (string Path, long Size)> files, Func<string, bool> on,
                             Func<string, string> format, List<string[]> registry, MsiPackage.Sandbox? sandbox)
    {
        var p = db.Query("SELECT `Property`,`Value` FROM `Property`").ToDictionary(r => r[0], r => r[1]);
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
        var lines = new List<string> { "N " + p.GetValueOrDefault("ProductName", ""), "A " + arpKey, "I " + root };
        // the product's folder, as its InstallFolder value names it. It goes whole, so nothing in it gets a line of its own
        string? tree = null;
        string folderKey = setup.RegionKey + @"\InstallFolder";
        if (setup.RegionKey != null && setup.FolderValue != null)
            foreach (var r in registry)
                if (on(r[4]) && r[0] == "2" && format(r[1]).Equals(folderKey, StringComparison.OrdinalIgnoreCase) && format(r[2]) == setup.FolderValue
                    && Path.TrimEndingDirectorySeparator(format(r[3])) is var t && Path.IsPathFullyQualified(t)
                    && PathInside.IsUnder(root, t))
                    tree = t;
        bool InTree(string path) => tree != null && PathInside.Contains(tree, path);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Folder(string dir)
        {
            for (var d = Path.TrimEndingDirectorySeparator(dir); d.Length > 3; d = Path.GetDirectoryName(d)!)
            {
                folders.Add(d);
                if (d.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
                if (!PathInside.Contains(root, d)) break;
            }
        }
        foreach (var r in db.Query("SELECT `Directory_`,`Name`,`Component_` FROM `Shortcut`"))
            if (on(r[2]) && dirs.TryGetValue(r[0], out var d))
            {
                lines.Add("S " + Path.Combine(d, MsiDatabase.LongName(r[1]) + ".lnk"));
                folders.Add(Path.TrimEndingDirectorySeparator(d));
            }
        foreach (var r in registry)
        {
            if (!on(r[4]) || r[2] == "-") continue;
            var key = format(r[1]);
            if (key.Contains(@"\Installer\UserData\", StringComparison.OrdinalIgnoreCase)) continue;
            if (r[0] == "0") key = @"SOFTWARE\Classes\" + key;
            bool keyRow = r[3].Length == 0 && (r[2] is "+" or "*" || r[2].Length == 0);
            lines.Add($"V {(r[0] == "1" ? 'U' : 'M')} {key}\t{(keyRow ? "+" : format(r[2]))}");
        }
        foreach (var c in db.Query("SELECT `Component`,`Attributes`,`KeyPath` FROM `Component`"))
            if (on(c[0]) && int.TryParse(c[1], out var ca) && (ca & 8) != 0 && files.TryGetValue(c[2], out var shared))
                lines.Add($"V M {MsiPackage.SharedDlls}\t{shared.Path}");
        foreach (var r in db.Query("SELECT `CLSID`,`Component_` FROM `Class`"))
            if (on(r[1])) lines.Add(@"C SOFTWARE\Classes\CLSID\" + r[0]);
        foreach (var r in db.Query("SELECT `ProgId` FROM `ProgId`"))
            lines.Add(@"C SOFTWARE\Classes\" + r[0]);
        foreach (var r in db.Query("SELECT `LibID`,`Component_` FROM `TypeLib`"))
        {
            if (!on(r[1]) || !files.TryGetValue(comps[r[1]].KeyPath, out var lib)) continue;
            if (ComRegistration.ReadTypeLib(lib.Path) is { } a)
                lines.Add($"T {a.guid:B} {a.wMajorVerNum} {a.wMinorVerNum} {a.lcid}");
        }
        foreach (var f in files.Values)
        {
            if (!InTree(f.Path)) lines.Add("F " + f.Path);
            Folder(Path.GetDirectoryName(f.Path)!);
        }
        foreach (var r in db.Query("SELECT `Component_`,`FileName`,`DirProperty`,`InstallMode` FROM `RemoveFile`"))
        {
            // install modes 2 and 3 run on uninstall
            if (!on(r[0]) || !int.TryParse(r[3], out var mode) || (mode & 2) == 0) continue;
            var dir = dirs.TryGetValue(r[2], out var dd) ? dd : props.GetValueOrDefault(r[2], "");
            if (dir.Length == 0) continue;
            if (r[1].Length == 0) folders.Add(Path.TrimEndingDirectorySeparator(dir));
            else if (!InTree(Path.TrimEndingDirectorySeparator(dir))) lines.Add($"P {Path.TrimEndingDirectorySeparator(dir)}\t{MsiDatabase.LongName(r[1])}");
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
            db.CopyStream($"SELECT `Data` FROM `Icon` WHERE `Name`='{iconName.Replace("'", "")}'", iconData))
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
            .Select(f => Path.TrimEndingDirectorySeparator(AppPaths.Special(f))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lines.AddRange(folders.Where(f => !special.Contains(f) && Path.GetPathRoot(f) != f + "\\" && !InTree(f))
                              .OrderByDescending(f => f.Length).Select(f => "D " + f));
        File.WriteAllText(list, string.Join("\r\n", lines) + "\r\n", Encoding.Unicode);
        UninstallFolder.Own(list);

        var version = p.GetValueOrDefault("ProductVersion", "0.0.0").Split('.').Select(x => int.TryParse(x, out var v) ? v : 0).Concat([0, 0, 0]).ToArray();
        long kb = files.Values.Sum(f => (f.Size + 4095) / 4096 * 4);
        string command = $"\"{exe}\" \"{list}\"";
        using var hklm = MsiPackage.Hive(sandbox);
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
}
