using System.IO;
using System.Text.RegularExpressions;
using Provenance.Common.Game;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.Windower;
using Provenance.Common.IO;
using Provenance.Common.PolFormat;
using Serilog;

namespace Provenance.Wpf.Services;

// Ashita v4 starts pol.exe and injects its own DLL, then ThirdPartyHookLauncher loads pol_hook into it. Windower's
// Hook.dll goes in through the watcher instead (WindowerLaunch). Nothing of ours goes in the viewer folder
public sealed class ThirdPartyHookService
{
    public const string None = "none", Ashita = "ashita", Windower = "windower";

    public static string NativeDir => Path.Combine(AppContext.BaseDirectory, "native");
    public const string WatcherExe = "Provenance Watcher.exe";

    public static bool IsAshitaFolder(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "Ashita-cli.exe")) && File.Exists(Path.Combine(dir, "Ashita.dll"));

    // Hook.dll is the one that matters now, but a folder with only Windower.exe can still fetch it
    public static bool IsWindowerFolder(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && (File.Exists(Path.Combine(dir, "Windower.exe")) || File.Exists(Path.Combine(dir, "Hook.dll")));

    public static string? DetectAshita(string? configured) =>
        Candidates(configured, @"C:\Ashita", "Ashita", "Ashita v4", "Ashita4").FirstOrDefault(IsAshitaFolder);

    public static string? DetectWindower(string? configured) =>
        Candidates(configured, @"C:\Windower", "Windower", "Windower4", "Windower 4").FirstOrDefault(IsWindowerFolder);

    // the updater's own root test, so a folder it would refuse is flagged up front. OneDrive's reparse points are fine
    public static bool IsLinkedFolder(string dir)
    {
        try { return FolderPins.ThroughLink(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    public const string LinkedWindowerText =
        "Provenance can't use a Windower folder that's reached through a link. Choose the real folder the link points to in Settings, under Windower/Ashita.";

    private static IEnumerable<string> Candidates(string? configured, string root, params string[] names)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured!;
        yield return root;
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                   Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            foreach (var n in names)
                if (!string.IsNullOrEmpty(pf)) yield return Path.Combine(pf, n);
    }

    public static IReadOnlyList<string> AshitaBootConfigs(string? ashitaDir) => AshitaBootFiles.List(ashitaDir);

    public const string IncompatibleText = "Incompatible with Provenance";
    public const string IncompatibleNote = "The ones marked incompatible stay off whenever Provenance starts the game. Your Ashita settings aren't changed.";

    public static string IncompatibleReason(string polPlugin) => polPlugin.ToLowerInvariant() switch
    {
        "autopol" => "Provenance logs you in itself.",
        "quicky" => "Provenance takes you into the game itself.",
        "extraslots" => "Provenance manages your accounts itself.",
        _ => "Provenance does this itself.",
    };

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
                // one that's been renamed or deleted since would only fail the launch
                if (m.Success && !AshitaBootFiles.IsLaunchCopy(m.Groups["n"].Value)
                    && File.Exists(Path.Combine(AshitaBootFiles.Folder(ashitaDir), m.Groups["n"].Value)))
                    return m.Groups["n"].Value;
            }
        }
        catch { }
        return null;
    }

    // the user's own profiles in file order, "" for the unnamed one
    public static IReadOnlyList<string> WindowerProfiles(string? windowerDir)
    {
        if (windowerDir is null) return [];
        try
        {
            var f = WindowerSettingsFile.Load(windowerDir);
            return f.LoadError is null ? f.Profiles.Select(p => p.Name).Where(n => !WindowerSettingsFile.IsOldCopy(n)).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not read Windower profiles");
            return [];
        }
    }

    internal static PolRegion? RegionOfPol(string polExe) =>
        Path.GetDirectoryName(polExe) is { Length: > 0 } dir ? PolEnv.Region(dir) : null;

    // Ashita's [ashita.language] playonline: 1 Japanese, 2 English (North America), 3 European, for the region pol.exe's
    // env.dat says
    internal static int AshitaLanguage(string polExe) => RegionOfPol(polExe) switch
    {
        PolRegion.Japan => 1,
        PolRegion.Europe => 3,
        _ => 2,
    };

    public static string WriteAshitaBoot(string ashitaDir, string? sourceName, string polExe) =>
        AshitaBootFiles.WriteLaunchCopy(ashitaDir, sourceName, polExe, AshitaLanguage(polExe));
}
