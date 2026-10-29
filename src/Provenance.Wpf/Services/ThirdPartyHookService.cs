using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Provenance.Common.Game;
using Provenance.Common.PolFormat;
using Serilog;

namespace Provenance.Wpf.Services;

// Windower 4 and Ashita v4 both start pol.exe and inject their own DLL, then ThirdPartyHookLauncher loads pol_hook
// into it. Nothing of ours goes in the viewer folder
public sealed class ThirdPartyHookService
{
    public const string None = "none", Ashita = "ashita", Windower = "windower";
    private const string AshitaBootPrefix = "provenance";

    // one copy per source config, so two accounts on different configs never overwrite one Ashita's still reading
    public static string AshitaBootNameFor(string? source) =>
        string.IsNullOrWhiteSpace(source) ? AshitaBootPrefix + ".ini"
                                          : $"{AshitaBootPrefix}.{Path.GetFileNameWithoutExtension(source)}.ini";

    public static bool IsOwnAshitaBoot(string fileName) =>
        fileName.Equals(AshitaBootPrefix + ".ini", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith(AshitaBootPrefix + ".", StringComparison.OrdinalIgnoreCase);

    public static string NativeDir => Path.Combine(AppContext.BaseDirectory, "native");

    public static string? DetectAshita(string? configured) =>
        Candidates(configured, @"C:\Ashita", "Ashita", "Ashita v4", "Ashita4")
            .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "Ashita-cli.exe")) && File.Exists(Path.Combine(dir, "Ashita.dll")));

    public static string? DetectWindower(string? configured) =>
        Candidates(configured, @"C:\Windower", "Windower", "Windower4", "Windower 4")
            .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "Windower.exe")));

    private static IEnumerable<string> Candidates(string? configured, string root, params string[] names)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured!;
        yield return root;
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                   Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            foreach (var n in names)
                if (!string.IsNullOrEmpty(pf)) yield return Path.Combine(pf, n);
    }

    public static IReadOnlyList<string> AshitaBootConfigs(string? ashitaDir)
    {
        if (ashitaDir is null) return Array.Empty<string>();
        var dir = Path.Combine(ashitaDir, "config", "boot");
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.ini")
            .Select(Path.GetFileName).OfType<string>()
            .Where(n => !IsOwnAshitaBoot(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Ashita writes the boot config into each log ("Ashita Config Script: \boot\config.ini"), so the newest log that
    // isn't from my own launch wins. Bit cheeky, but it works
    public static string? LastUsedAshitaBootConfig(string? ashitaDir)
    {
        try
        {
            if (ashitaDir is null) return null;
            var logs = Path.Combine(ashitaDir, "logs");
            if (!Directory.Exists(logs)) return null;
            // a plain file name only, no slashes of either kind (so no .. tricks either)
            var re = new Regex(@"Config Script:\s*\\boot\\(?<n>[^\\/:\r\n]+\.ini)", RegexOptions.IgnoreCase);
            foreach (var f in new DirectoryInfo(logs).GetFiles("*.txt").OrderByDescending(f => f.LastWriteTimeUtc).Take(20))
            {
                using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                var buf = new char[16384];   // the header's at the top
                var head = new string(buf, 0, sr.Read(buf, 0, buf.Length));
                var m = re.Match(head);
                if (m.Success && !IsOwnAshitaBoot(m.Groups["n"].Value))
                    return m.Groups["n"].Value;
            }
        }
        catch { }
        return null;
    }

    public static IReadOnlyList<string> WindowerProfiles(string? windowerDir)
    {
        try
        {
            var file = windowerDir is null ? null : Path.Combine(windowerDir, "settings.xml");
            return file is null ? Array.Empty<string>() : WindowerSettings.Profiles(file);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read Windower profiles");
            return Array.Empty<string>();
        }
    }

    public static string PrepareWindowerProfile(string windowerDir, string? profile)
    {
        var r = WindowerSettings.PrepareCopy(Path.Combine(windowerDir, "settings.xml"), profile);
        if (!r.SourceFound && r.Source.Length > 0)
            Log.Warning("Windower profile {Profile} is not in settings.xml; its copy uses Windower's defaults", r.Source);
        if (r.Wrote)
            Log.Information("Wrote Windower profile {Name}: a copy of {Source} with fast login, the account limit and background throttling off",
                            r.Name, r.Source.Length == 0 ? "the default profile" : r.Source);
        return r.Name;
    }

    private static void EnsureKey(List<string> lines, string section, string key, string value)
    {
        int sec = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (sec < 0) { lines.Add($"[{section}]"); lines.Add($"{key} = {value}"); return; }
        int end = lines.FindIndex(sec + 1, l => l.TrimStart().StartsWith("["));
        if (end < 0) end = lines.Count;
        for (int i = sec + 1; i < end; i++)
            if (lines[i].Split('=')[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) { lines[i] = $"{key} = {value}"; return; }
        lines.Insert(end, $"{key} = {value}");
    }

    // Ashita's [ashita.language] playonline: 2 English (North America), 3 European, for the region pol.exe's env.dat says
    internal static int AshitaLanguage(string polExe) =>
        Path.GetDirectoryName(polExe) is { Length: > 0 } dir && PolEnv.Region(dir) == PolRegion.Europe ? 3 : 2;

    // the user's boot config minus anything that'd fight the app: no command line (it can carry a credential blob), no
    // sign-in plugin or menu skip, and file = the real pol.exe
    public static string WriteAshitaBoot(string ashitaDir, string? sourceName, string polExe)
    {
        var bootDir = Path.Combine(ashitaDir, "config", "boot");
        Directory.CreateDirectory(bootDir);
        var src = string.IsNullOrWhiteSpace(sourceName) ? null : Path.Combine(bootDir, sourceName!);
        var lines = src != null && File.Exists(src) ? File.ReadAllLines(src) : new[]
        {
            "[ashita.launcher]", "autoclose = 1", "name = provenance",
            "[ashita.boot]", "file = ", "command = ", "gamemodule = ffximain.dll", "script = default.txt", "args = ",
            "[ashita.language]", $"playonline = {AshitaLanguage(polExe)}", "ashita = 2",
            "[ashita.polplugins]", "sandbox = 0", "autopol = 0", "quicky = 0",
        };
        // the sign-in plugin's switched off quietly
        var outLines = lines.Select(line => line.Split('=')[0].Trim().ToLowerInvariant() switch
        {
            "command" => "command = /game eAZcFcB",
            "autopol" => "autopol = 0",
            "quicky" => "quicky = 0",
            "name" when line.TrimStart().StartsWith("name", StringComparison.OrdinalIgnoreCase) => "name = provenance",
            "file" => $"file = {polExe}",
            _ => line,
        }).ToList();
        EnsureKey(outLines, "ashita.boot", "command", "/game eAZcFcB");
        EnsureKey(outLines, "ashita.boot", "file", polExe);
        EnsureKey(outLines, "ashita.boot", "args", "");
        EnsureKey(outLines, "ashita.polplugins", "autopol", "0");
        EnsureKey(outLines, "ashita.polplugins", "quicky", "0");
        var target = Path.Combine(bootDir, AshitaBootNameFor(sourceName));
        File.WriteAllLines(target, outLines, new UTF8Encoding(false));
        return target;
    }
}
