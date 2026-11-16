using Provenance.Common.PolFormat;

namespace Provenance.Common.Mirror;

public static class MirrorLayout
{
    public const string BaseUrl = "https://patch.provenancexi.app/";

    private static readonly (PolRegion Region, string Folder)[] Regions =
        [(PolRegion.UnitedStates, "us"), (PolRegion.Europe, "eu"), (PolRegion.Japan, "jp")];

    public static string RegionFolder(PolRegion region) => Regions.First(r => r.Region == region).Folder;

    public static PolRegion RegionOf(string folder) => Regions.FirstOrDefault(r => r.Folder == folder) is { Folder: not null } r
        ? r.Region
        : throw new ArgumentException($"Unknown mirror region \"{folder}\".", nameof(folder));

    public static string ObjectKey(string region, string sha256) => $"{region}/objects/{sha256}";

    public static string IndexKey(string region, string product, string version) => $"{region}/{product}/index/{version}";

    public static string PackKey(string region, string sha256) => $"{region}/packs/{sha256}";

    public static string PackedIndexKey(string region, string product, string version) => $"{region}/{product}/pindex/{version}";

    public static string PointerKey(string region) => $"{region}/current";

    // one file for every region, outside the region folders so a prune never looks at it
    public const string BuildsKey = "viewer-builds";
}
