using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Provenance.Common.IO;

namespace Provenance.Common.Game.Ashita;

public static class AshitaBootFiles
{
    private const string CopyPrefix = "provenance";
    private const string PlainCommand = "/game eAZcFcB";

    // tracked in Ashita's git, so changing them makes the user's next git pull fight them
    private static readonly string[] Examples = ["example-retail.ini", "example.ini", "example-privateserver.ini"];

    // polplugins that would fight Provenance over the login or the accounts, so they're always off in the launch copy
    private static readonly string[] Takeovers = ["autopol", "quicky", "extraslots"];

    public static string Folder(string ashitaDir) => Path.Combine(ashitaDir, "config", "boot");

    // one copy per source config, so two accounts on different configs never overwrite one Ashita's still reading
    public static string LaunchCopyName(string? source) =>
        string.IsNullOrWhiteSpace(source) ? CopyPrefix + ".ini" : $"{CopyPrefix}.{Path.GetFileNameWithoutExtension(source)}.ini";

    public static bool IsLaunchCopy(string fileName) =>
        fileName.Equals(CopyPrefix + ".ini", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith(CopyPrefix + ".", StringComparison.OrdinalIgnoreCase);

    public static bool IsShippedExample(string fileName) => Examples.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    public static bool IsTakeover(string polPlugin) => Takeovers.Contains(polPlugin, StringComparer.OrdinalIgnoreCase);

    public static string? ExampleFor(string ashitaDir) =>
        Examples.Take(2).FirstOrDefault(e => File.Exists(Path.Combine(Folder(ashitaDir), e)));

    public static IReadOnlyList<string> List(string? ashitaDir)
    {
        if (ashitaDir is null) return [];
        var dir = Folder(ashitaDir);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.ini")
            .Select(Path.GetFileName).OfType<string>()
            .Where(n => Path.GetExtension(n).Equals(".ini", StringComparison.OrdinalIgnoreCase) && !IsLaunchCopy(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // each one as Ashita's launcher lists it, its [ashita.launcher] name or else the file name. Two that would read the
    // same get their file names after them
    public static IReadOnlyList<(string File, string Shown)> Names(string? ashitaDir)
    {
        var named = List(ashitaDir).Select(f => (File: f, Name: LauncherName(ashitaDir!, f) ?? f)).ToList();
        return named.Select(n => (n.File, n.Name != n.File && named.Count(o => o.Name.Equals(n.Name, StringComparison.OrdinalIgnoreCase)) > 1
            ? $"{n.Name} ({n.File})" : n.Name)).ToList();
    }

    public static string ShownName(string? ashitaDir, string fileName) =>
        Names(ashitaDir).FirstOrDefault(n => n.File.Equals(fileName, StringComparison.OrdinalIgnoreCase)).Shown ?? fileName;

    private static string? LauncherName(string ashitaDir, string fileName)
    {
        try { return AshitaIniFile.Load(Path.Combine(Folder(ashitaDir), fileName)).Get("ashita.launcher", "name")?.Trim() is { Length: > 0 } n ? n : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static bool IniFileName(string name) =>
        name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && Path.GetExtension(name).Equals(".ini", StringComparison.OrdinalIgnoreCase)
        && !Regex.IsMatch(name, @"^(con|prn|aux|nul|com\d|lpt\d)(\.|$)", RegexOptions.IgnoreCase);

    // for a new name. One that's already there only has to be an .ini file, so a bad name can still be renamed
    public static string? NameProblem(string? fileName, string? ashitaDir = null)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "Give the boot configuration a name.";
        if (fileName != Path.GetFileName(fileName) || fileName.Contains("..") || fileName.Contains('/'))
            return "A boot configuration's name can't have a folder in it.";
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains(';'))
            return "That name has characters Windows or Ashita don't allow in a file name.";
        if (fileName.StartsWith('.')) return "A boot configuration's name can't start with a dot.";
        if (!Path.GetExtension(fileName).Equals(".ini", StringComparison.OrdinalIgnoreCase))
            return "A boot configuration's name has to end in .ini.";
        if (!IniFileName(fileName)) return "Windows doesn't allow that name.";
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (stem.Length >= 2 && stem[0] == '\'' && stem[^1] == '\'') return "A boot configuration's name can't start and end with a quote.";
        // Ashita-cli gets it on its command line as ANSI
        if (!AshitaIniFile.FitsCodePage(fileName))
            return "That name has characters Ashita can't read with this PC's language for non-Unicode programs.";
        if (IsLaunchCopy(fileName)) return "Provenance uses that name for its own copies, choose another one.";
        if (IsShippedExample(fileName)) return "That's one of Ashita's example files, choose another name.";
        // provenance.<name>.ini.<guid>.tmp has to fit in 255, and Ashita-cli builds the copy's whole path in 260
        if (Path.GetFileNameWithoutExtension(fileName).Length > 203) return "That name is too long.";
        if (ashitaDir is not null && AshitaIniFile.AnsiLength(Path.GetFullPath(Path.Combine(Folder(ashitaDir), LaunchCopyName(fileName)))) > 259)
            return "That name is too long for Ashita in this folder.";
        return null;
    }

    private static string BootFolder(string ashitaDir)
    {
        var config = Path.GetFullPath(Path.Combine(ashitaDir, "config"));
        var boot = Path.Combine(config, "boot");
        AshitaIniFile.RefuseLinks(config, boot);
        Directory.CreateDirectory(boot);
        AshitaIniFile.RefuseLinks(config, boot);
        return boot;
    }

    private static string NewFile(string ashitaDir, string name)
    {
        if (NameProblem(name, ashitaDir) is { } problem) throw Shown.Mark(new ArgumentException(problem));
        var path = Path.Combine(BootFolder(ashitaDir), name);
        if (File.Exists(path)) throw Shown.Mark(new IOException($"There's already a boot configuration called {name}."));
        AshitaIniFile.RefuseLinks(path, path + ".bak");
        return path;
    }

    private static string Existing(string ashitaDir, string name, bool change)
    {
        if (string.IsNullOrWhiteSpace(name) || !IniFileName(name) || IsLaunchCopy(name))
            throw Shown.Mark(new ArgumentException($"{name} isn't a boot configuration Provenance can change."));
        if (change && IsShippedExample(name))
            throw Shown.Mark(new ArgumentException($"{name} is one of Ashita's example files. Duplicate it and change the copy instead."));
        var config = Path.GetFullPath(Path.Combine(ashitaDir, "config"));
        var path = Path.Combine(config, "boot", name);
        if (!File.Exists(path)) throw Shown.Mark(new FileNotFoundException($"{name} isn't in Ashita's config\\boot folder any more.", path));
        if (change) AshitaIniFile.RefuseLinks(config, Path.Combine(config, "boot"), path, path + ".bak");
        return path;
    }

    // config and boot stay held while files are copied, moved or deleted there, and it's all done where they really are
    private static FolderPins Hold(string ashitaDir)
    {
        var config = Path.GetFullPath(Path.Combine(ashitaDir, "config"));
        return LinkCheck.Hold(config, Path.Combine(config, "boot"));
    }

    // a copy of the install's own example with the game settings left to Windows, or a short one of mine. Either way
    // the hardware mouse starts on
    public static string Create(string ashitaDir, string name, bool fromExample, int playOnline = 2)
    {
        var path = NewFile(ashitaDir, name);
        var stem = Path.GetFileNameWithoutExtension(name).Trim();
        AshitaIniFile ini;
        if (fromExample && ExampleFor(ashitaDir) is { } example)
        {
            ini = AshitaIniFile.Load(Path.Combine(Folder(ashitaDir), example));
            ini.Set("ashita.launcher", "name", stem);
            foreach (var key in ini.Entries("ffxi.registry").Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                ini.Set("ffxi.registry", key, "-1");
        }
        else ini = AshitaIniFile.FromText(Template(stem, playOnline));
        ini.Set("ffxi.registry", "0021", "1");
        ini.Save(path, backup: false);
        return path;
    }

    public static string Duplicate(string ashitaDir, string source, string name)
    {
        var from = Existing(ashitaDir, source, change: false);
        var path = NewFile(ashitaDir, name);
        using (var pins = Hold(ashitaDir)) File.Copy(pins.Real(from), pins.Real(path));
        FollowName(path, source);
        return path;
    }

    public static string Rename(string ashitaDir, string name, string newName)
    {
        var from = Existing(ashitaDir, name, change: true);
        string path;
        if (name.Equals(newName, StringComparison.OrdinalIgnoreCase))
        {
            if (NameProblem(newName, ashitaDir) is { } problem) throw Shown.Mark(new ArgumentException(problem));
            path = Path.Combine(Path.GetDirectoryName(from)!, newName);
        }
        else path = NewFile(ashitaDir, newName);
        using (var pins = Hold(ashitaDir))
        {
            var (src, dst) = (pins.Real(from), pins.Real(path));
            File.Move(src, dst);
            if (File.Exists(src + ".bak")) File.Move(src + ".bak", dst + ".bak", overwrite: true);
            DropLaunchCopy(pins, ashitaDir, name);
        }
        FollowName(path, name);
        return path;
    }

    // a launcher name that's only the old file's name (as Create sets it) follows the rename. One that can't be read
    // or written keeps its name, the file's moved either way
    private static void FollowName(string path, string oldFileName)
    {
        try
        {
            var ini = AshitaIniFile.Load(path);
            var old = Path.GetFileNameWithoutExtension(oldFileName).Trim();
            if (!string.Equals(ini.Get("ashita.launcher", "name")?.Trim(), old, StringComparison.OrdinalIgnoreCase)) return;
            ini.Set("ashita.launcher", "name", Path.GetFileNameWithoutExtension(path).Trim());
            ini.Save(path, backup: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public static void Delete(string ashitaDir, string name)
    {
        var path = Existing(ashitaDir, name, change: true);
        using var pins = Hold(ashitaDir);
        File.Delete(pins.Real(path));
        File.Delete(pins.Real(path + ".bak"));
        DropLaunchCopy(pins, ashitaDir, name);
    }

    private static void DropLaunchCopy(FolderPins pins, string ashitaDir, string name)
    {
        try { File.Delete(pins.Real(Path.Combine(Folder(ashitaDir), LaunchCopyName(name)))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static string Template(string name, int playOnline) =>
        $"""
        [ashita.launcher]
        autoclose = 1
        name = {name}

        [ashita.boot]
        file =
        command = {PlainCommand}
        gamemodule = ffximain.dll
        script = default.txt
        args =

        [ashita.language]
        playonline = {playOnline}
        ashita = 2

        [ashita.polplugins]
        sandbox = 0

        [ffxi.registry]
        0042 = -1
        padexsin000 = -1
        padguid000 = -1
        padmode000 = -1
        padsin000 = -1

        """.ReplaceLineEndings("\r\n");

    // the user's boot config with the real pol.exe, one plain command line, and the takeover plugins off with their
    // arguments gone
    public static string WriteLaunchCopy(string ashitaDir, string? source, string polExe, int playOnline)
    {
        if (source is not null && source != Path.GetFileName(source))
            throw new ArgumentException("The boot configuration has to be a file in Ashita's config\\boot folder.", nameof(source));
        var viewer = Path.GetDirectoryName(polExe);
        if (!AshitaIniFile.FitsCodePage(polExe))
            throw Shown.Mark(new InvalidOperationException(
                $"Ashita can't start PlayOnline from {viewer}, the folder's name has characters Ashita can't read. Install PlayOnline in a folder with a plain name to play through Ashita."));
        if (AshitaIniFile.FromText("").ValueProblem("ashita.boot", "file", polExe) is not null)
            throw Shown.Mark(new InvalidOperationException($"Ashita can't start PlayOnline from {viewer}, that path is too long for Ashita or has characters it can't take."));
        var dir = BootFolder(ashitaDir);
        var target = Path.Combine(dir, LaunchCopyName(source));
        if (!AshitaIniFile.FitsCodePage(target))
            throw Shown.Mark(new InvalidOperationException(source is not null && !AshitaIniFile.FitsCodePage(source)
                ? $"Ashita can't read the name of the boot configuration {source}. Give it a name with plain letters and numbers."
                : $"Ashita can't run from {ashitaDir}, the folder's name has characters it can't read."));
        if (AshitaIniFile.AnsiLength(target) > 259)
            throw Shown.Mark(new InvalidOperationException($"The path to Provenance's copy of {source ?? "the boot configuration"} is too long for Ashita. A shorter boot configuration name fixes it."));
        var src = string.IsNullOrWhiteSpace(source) ? null : Path.Combine(dir, source);
        if (src is not null && !File.Exists(src))
            throw Shown.Mark(new FileNotFoundException($"The Ashita boot configuration {source} isn't in Ashita's config\\boot folder any more. Choose another one in Settings.", src));

        var ini = src is not null ? AshitaIniFile.Load(src) : AshitaIniFile.FromText(Template("provenance", playOnline));
        ini.Set("ashita.boot", "file", polExe);
        // commented-out ones too, Ashita.dll writes a # line into its logs like any other key
        ini.Scrub(null, "command");
        ini.Set("ashita.boot", "command", PlainCommand);
        foreach (var plugin in Takeovers)
        {
            ini.Set("ashita.polplugins", plugin, "0");
            ini.Scrub("ashita.polplugins.args", plugin);
        }
        using var pins = Hold(ashitaDir);
        ini.Save(target, backup: false);

        if (!ReadsRight(target, polExe))
        {
            try { File.Delete(pins.Real(target)); } catch (IOException) { }
            throw Shown.Mark(new InvalidOperationException(
                $"Provenance couldn't make a copy of {source ?? "its boot configuration"} that Ashita reads the way it should, so it won't start Ashita with it. Check the file for odd line breaks or characters."));
        }
        return target;
    }

    // read back by Windows itself, the way Ashita-cli and Ashita.dll will, in case a line fooled the editor
    private static bool ReadsRight(string path, string polExe)
    {
        var ansi = AshitaIniFile.Ansi(0);
        string Get(string key)
        {
            var buf = new byte[4096];
            return ansi.GetString(buf, 0, GetPrivateProfileStringA("ashita.boot", key, "", buf, buf.Length, path));
        }
        var section = new byte[65536];
        int n = GetPrivateProfileSectionA("ashita.polplugins", section, section.Length, path);
        bool takeover = ansi.GetString(section, 0, n).Split('\0').Select(e => e.Split('=', 2))
            .Any(kv => kv.Length == 2 && Takeovers.Contains(kv[0].Trim(), StringComparer.OrdinalIgnoreCase) && kv[1].Trim() == "1");
        return Get("file") == polExe && Get("command") == PlainCommand && !takeover;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int GetPrivateProfileStringA(string section, string key, string fallback, byte[] buffer, int size, string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int GetPrivateProfileSectionA(string section, byte[] buffer, int size, string path);
}
