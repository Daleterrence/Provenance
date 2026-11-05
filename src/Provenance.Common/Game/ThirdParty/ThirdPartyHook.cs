using Provenance.Common.PolFormat;

namespace Provenance.Common.Game.ThirdParty;

// what Windower and Ashita share. Nothing of mine goes in the viewer folder
public static class ThirdPartyHook
{
    public const string None = "none", Ashita = "ashita", Windower = "windower";

    internal static IEnumerable<string> Candidates(string? configured, string root, params string[] names)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured!;
        yield return root;
        foreach (var pf in new[] { AppPaths.ProgramFilesX86, AppPaths.ProgramFiles })
            foreach (var n in names)
                if (!string.IsNullOrEmpty(pf)) yield return Path.Combine(pf, n);
    }

    internal static PolRegion? RegionOfPol(string polExe) =>
        Path.GetDirectoryName(polExe) is { Length: > 0 } dir ? PolEnv.Region(dir) : null;
}
