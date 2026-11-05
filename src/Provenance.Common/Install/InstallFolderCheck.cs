using Provenance.Common.PolFormat;

namespace Provenance.Common.Install;

public static class InstallFolderCheck
{
    // another region's viewer or game in the same folders would get written over, with both regions' keys pointing
    // there. This region's own install is fine, the installer carries on with it
    internal static string? OtherVersionIn(string installFolder, PolRegion region, Func<PolRegion, string, string?> registered,
                                           Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var root = Path.Combine(installFolder, "SquareEnix");
        var products = new[] { (PolPaths.ViewerProduct, "PlayOnlineViewer", "pol.exe", "PlayOnline"), (PolPaths.FfxiProduct, "FINAL FANTASY XI", "FFXiMain.dll", "FINAL FANTASY XI") };
        foreach (var other in new[] { PolRegion.UnitedStates, PolRegion.Europe, PolRegion.Japan }.Where(r => r != region))
            foreach (var (code, folder, keyFile, name) in products)
                // a switched PC keeps the old region's keys, so it only counts with the files really there
                if (registered(other, code) is { } there && SameFolder(there, Path.Combine(root, folder)) && fileExists(Path.Combine(there, keyFile)))
                    return $"The {PolPaths.VersionName(other)} version of {name} is already installed in {there}. Choose another folder for the {PolPaths.VersionName(region)} version.";
        return null;
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                      StringComparison.OrdinalIgnoreCase);
}
