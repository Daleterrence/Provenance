using System.IO;
using System.Text.RegularExpressions;
using Provenance.Common.Game;
using Provenance.Common.Game.Ashita;
using Provenance.Common.PolFormat;
using Serilog;

namespace Provenance.Wpf.Services;

// Windower 4 and Ashita v4 both start pol.exe and inject their own DLL, then ThirdPartyHookLauncher loads pol_hook
// into it. Nothing of ours goes in the viewer folder
public sealed class ThirdPartyHookService
{
    public const string None = "none", Ashita = "ashita", Windower = "windower";

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

    public static IReadOnlyList<string> AshitaBootConfigs(string? ashitaDir) => AshitaBootFiles.List(ashitaDir);

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

    private static PolRegion? RegionOfPol(string polExe) =>
        Path.GetDirectoryName(polExe) is { Length: > 0 } dir ? PolEnv.Region(dir) : null;

    // Ashita's [ashita.language] playonline: 1 Japanese, 2 English (North America), 3 European, for the region pol.exe's
    // env.dat says
    internal static int AshitaLanguage(string polExe) => RegionOfPol(polExe) switch
    {
        PolRegion.Japan => 1,
        PolRegion.Europe => 3,
        _ => 2,
    };

    // Windower's -r JP for a JP pol.exe. US and EU stay on Windower's own Auto
    internal static string? WindowerRegion(string polExe) => RegionOfPol(polExe) == PolRegion.Japan ? "JP" : null;

    public static string WriteAshitaBoot(string ashitaDir, string? sourceName, string polExe) =>
        AshitaBootFiles.WriteLaunchCopy(ashitaDir, sourceName, polExe, AshitaLanguage(polExe));
}
