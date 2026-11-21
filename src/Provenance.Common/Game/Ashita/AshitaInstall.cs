using System.Text.RegularExpressions;
using static Provenance.Common.Game.ThirdParty.ThirdPartyHooks;
using Provenance.Common.IO;

namespace Provenance.Common.Game.Ashita;

public static class AshitaInstall
{
    public static bool IsFolder(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && Usable(dir) is { } d && File.Exists(Path.Combine(d, "Ashita-cli.exe")) && File.Exists(Path.Combine(d, "Ashita.dll"));

    internal static readonly string[] ProgramFilesNames = ["Ashita", "Ashita v4", "Ashita4"];

    public static string? Detect(string? configured) =>
        Candidates(configured, @"C:\Ashita", ProgramFilesNames).Select(d => Usable(d)!).FirstOrDefault(IsFolder);

    public const string BrowseTitle = "Choose your Ashita v4 folder";
    public const string NotFoundText = "Couldn't find Ashita v4 in that folder.";
    public const string IncompatibleText = "Incompatible with Provenance";
    public const string IncompatibleNote = "The ones marked incompatible stay off whenever Provenance starts the game. Your Ashita settings aren't changed.";

    // Ashita writes the boot config into each log, so the newest log that isn't from my own launch wins. Bit cheeky, but it works
    public static string? LastUsedBootConfig(string? ashitaDir)
    {
        try
        {
            if (ashitaDir is null) return null;
            var logs = Path.Combine(ashitaDir, "logs");
            if (!Directory.Exists(logs)) return null;
            var re = new Regex(@"Config Script:\s*\\boot\\(?<n>[^\\/:\r\n]+\.ini)", RegexOptions.IgnoreCase);
            foreach (var f in new DirectoryInfo(logs).GetFiles("*.txt").OrderByDescending(f => f.LastWriteTimeUtc).Take(20))
            {
                using var fs = SharedFile.OpenRead(f.FullName);
                using var sr = new StreamReader(fs);
                var buf = new char[16384];
                var head = new string(buf, 0, sr.Read(buf, 0, buf.Length));
                var m = re.Match(head);
                if (m.Success && !AshitaBootFiles.IsLaunchCopy(m.Groups["n"].Value)
                    && File.Exists(Path.Combine(AshitaBootFiles.Folder(ashitaDir), m.Groups["n"].Value)))
                    return m.Groups["n"].Value;
            }
        }
        catch { }
        return null;
    }

    public static string WriteBoot(string ashitaDir, string? sourceName, string polExe, IReadOnlyList<string>? leaveOut = null) =>
        AshitaBootFiles.WriteLaunchCopy(ashitaDir, sourceName, polExe, AshitaBootFiles.LanguageCodeFor(polExe), leaveOut);
}
