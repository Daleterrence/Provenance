using Serilog;
using static Provenance.Common.Game.ThirdParty.ThirdPartyHooks;

namespace Provenance.Common.Game.Windower;

public static class WindowerInstall
{
    public static bool IsFolder(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && Usable(dir) is { } d && (File.Exists(Path.Combine(d, "Windower.exe")) || File.Exists(Path.Combine(d, "Hook.dll")));

    // a lone Windower.exe is a download nobody's run yet, and a lone Hook.dll could be some other program's
    public static bool IsSetUp(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && Usable(dir) is { } d && File.Exists(Path.Combine(d, "Windower.exe")) && File.Exists(Path.Combine(d, "Hook.dll"));

    internal static readonly string[] ProgramFilesNames = ["Windower", "Windower4", "Windower 4"];

    public static string? Detect(string? configured) =>
        Candidates(configured, @"C:\Windower", ProgramFilesNames).Select(d => Usable(d)!).FirstOrDefault(IsFolder);

    public const string BrowseTitle = "Choose your Windower 4 folder";
    public const string NotFoundText = "Couldn't find Windower 4 in that folder.";
    public const string LinkedText =
        "Provenance can't use a Windower folder that's reached through a link. Choose the real folder the link points to in Settings, under Windower/Ashita.";

    public static IReadOnlyList<string> Profiles(string? windowerDir)
    {
        if (windowerDir is null) return [];
        try
        {
            var f = WindowerSettingsFile.Load(windowerDir);
            return f.LoadError is null ? f.Profiles.Select(p => p.Name).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Could not read Windower profiles");
            return [];
        }
    }
    public static string DisplayName(string name, IReadOnlyCollection<string> all) =>
        name.Length > 0 ? name : all.Any(n => n.Equals("Default", StringComparison.OrdinalIgnoreCase)) ? "Default (no name)" : "Default";
}
