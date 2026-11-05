using System.IO;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.Services;

// what Windower and Ashita share, and where the watcher sits. Nothing of mine goes in the viewer folder
public static class ThirdPartyHook
{
    public const string None = "none", Ashita = "ashita", Windower = "windower";

    public static string NativeDir => Path.Combine(AppContext.BaseDirectory, "native");
    public const string WatcherExe = "Provenance Watcher.exe";

    internal static IEnumerable<string> Candidates(string? configured, string root, params string[] names)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured!;
        yield return root;
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                   Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            foreach (var n in names)
                if (!string.IsNullOrEmpty(pf)) yield return Path.Combine(pf, n);
    }

    internal static PolRegion? RegionOfPol(string polExe) =>
        Path.GetDirectoryName(polExe) is { Length: > 0 } dir ? PolEnv.Region(dir) : null;
}
