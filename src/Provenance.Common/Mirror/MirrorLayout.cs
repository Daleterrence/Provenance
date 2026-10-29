namespace Provenance.Common.Mirror;

public static class MirrorLayout
{
    public const string BaseUrl = "https://patch.provenancexi.app/";

    public static string ObjectKey(string region, string sha256) => $"{region}/objects/{sha256}";

    public static string IndexKey(string region, string product, string version) => $"{region}/{product}/index/{version}";

    public static string PackKey(string region, string sha256) => $"{region}/packs/{sha256}";

    public static string PackedIndexKey(string region, string product, string version) => $"{region}/{product}/pindex/{version}";

    public static string PointerKey(string region) => $"{region}/current";
}
