using Serilog;
using static Provenance.Common.Game.ThirdParty.ThirdPartyHook;

namespace Provenance.Common.Game.Windower;

public static class WindowerInstall
{
    // Hook.dll is the one that matters now, but a folder with only Windower.exe can still fetch it
    public static bool IsWindowerFolder(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && (File.Exists(Path.Combine(dir, "Windower.exe")) || File.Exists(Path.Combine(dir, "Hook.dll")));

    public static string? DetectWindower(string? configured) =>
        Candidates(configured, @"C:\Windower", "Windower", "Windower4", "Windower 4").FirstOrDefault(IsWindowerFolder);

    public const string LinkedWindowerText =
        "Provenance can't use a Windower folder that's reached through a link. Choose the real folder the link points to in Settings, under Windower/Ashita.";

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
}
