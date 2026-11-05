using System.Text.RegularExpressions;
using Provenance.Common.PolFormat;
using static Provenance.Common.Game.ThirdParty.ThirdPartyHook;

namespace Provenance.Common.Game.Ashita;

public static class AshitaInstall
{
    public static bool IsAshitaFolder(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "Ashita-cli.exe")) && File.Exists(Path.Combine(dir, "Ashita.dll"));

    public static string? DetectAshita(string? configured) =>
        Candidates(configured, @"C:\Ashita", "Ashita", "Ashita v4", "Ashita4").FirstOrDefault(IsAshitaFolder);

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
