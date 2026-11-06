using Provenance.Common.PolFormat;
using Provenance.Common.Security;

namespace Provenance.Common.Install;

public static class InstallFolderCheck
{
    // another region's viewer or game in the same folders would get written over, with both regions' keys pointing
    // there. This region's own install is fine, the installer carries on with it
    public static string? OtherVersionIn(string installFolder, PolRegion region, Func<PolRegion, GameProduct, string?> registered,
                                           Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var root = Path.Combine(installFolder, "SquareEnix");
        foreach (var other in new[] { PolRegion.UnitedStates, PolRegion.Europe, PolRegion.Japan }.Where(r => r != region))
            foreach (var product in GameProduct.All)
                // a switched PC keeps the old region's keys, so it only counts with the files really there
                if (registered(other, product) is { } there && PathInside.Same(there, Path.Combine(root, product.FolderName)) && fileExists(Path.Combine(there, product.KeyFile)))
                    return $"The {PolPaths.VersionName(other)} version of {product.ShortName} is already installed in {there}. Choose another folder for the {PolPaths.VersionName(region)} version.";
        return null;
    }
}
