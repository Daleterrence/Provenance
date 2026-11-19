using Provenance.Common.PolFormat;
using Provenance.Common.Security;

namespace Provenance.Common.Install;

public static class InstallFolderCheck
{
    public static string? OtherVersionIn(string installFolder, PolRegion region, Func<PolRegion, GameProduct, string?> registered,
                                           Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var root = Path.Combine(installFolder, "SquareEnix");
        foreach (var other in new[] { PolRegion.UnitedStates, PolRegion.Europe, PolRegion.Japan }.Where(r => r != region))
            foreach (var product in GameProduct.All)
                if (registered(other, product) is { } there && PathInside.Same(there, Path.Combine(root, product.FolderName)) && fileExists(Path.Combine(there, product.KeyFile)))
                    return $"The {PolPaths.VersionName(other)} version of {product.ShortName} is already installed in {there}. Choose another folder for the {PolPaths.VersionName(region)} version.";
        return null;
    }
}
