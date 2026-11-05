using Provenance.Common.PolFormat;

namespace Provenance.Common.Mirror;

public static class MirrorLayout
{
    public const string BaseUrl = "https://patch.provenancexi.app/";

    // the regions the mirror has a copy of, by their folder in it. Japan has none (yet)
    private static readonly (PolRegion Region, string Folder)[] Regions = [(PolRegion.UnitedStates, "us"), (PolRegion.Europe, "eu")];

    public static string? RegionFolder(PolRegion region) => Regions.FirstOrDefault(r => r.Region == region).Folder;

    private static readonly HashSet<string> JpFfxiOwn = new(["mov/mov999.pmv"], StringComparer.OrdinalIgnoreCase);

    // JP's FFXI list is the US one byte for byte, so FFXI comes from us/ (bar mov999.pmv, which POL never patches) and
    // its viewer from POL
    public static string? UpdateFolder(PolRegion installed, string product) =>
        RegionFolder(installed) ?? (installed == PolRegion.Japan && product == "ffxi" ? "us" : null);

    // files an install keeps as they are when its updates come from another region's folder
    public static IReadOnlySet<string> OwnFiles(PolRegion installed, string product) =>
        installed == PolRegion.Japan && product == "ffxi" ? JpFfxiOwn : new HashSet<string>();

    public static PolRegion RegionOf(string folder) => Regions.FirstOrDefault(r => r.Folder == folder) is { Folder: not null } r
        ? r.Region
        : throw new ArgumentException($"Unknown mirror region \"{folder}\".", nameof(folder));

    public static string ObjectKey(string region, string sha256) => $"{region}/objects/{sha256}";

    public static string IndexKey(string region, string product, string version) => $"{region}/{product}/index/{version}";

    public static string PackKey(string region, string sha256) => $"{region}/packs/{sha256}";

    public static string PackedIndexKey(string region, string product, string version) => $"{region}/{product}/pindex/{version}";

    public static string PointerKey(string region) => $"{region}/current";
}
