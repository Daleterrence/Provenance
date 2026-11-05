using Provenance.Common.PolFormat;

namespace Provenance.Common.Patcher;

public sealed record PatchProduct(string Name, int Id, string Host, string TagWire, string TagFilter,
                                  string Folder, string ViewerFolder, string RegistryHive)
{
    public string ProductId => Id.ToString("0000");
    public int Port => 53000 + Id;

    // null reads the Interface value from the registry like POL. It's the same on every PC, so the publisher pins it
    public string? PatchVerInterface { get; init; }

    public PolRegion Region { get; init; } = PolRegion.UnitedStates;

    // pt004 and pt008 are one server: the viewer's wire tag (sqpolcts.bin) is what gets JP its own stream. FFXI's is
    // W20 everywhere, so every region gets the same FFXI
    public static PatchProduct Viewer(string viewerDir, PolRegion region = PolRegion.UnitedStates) =>
        new("PlayOnline Viewer", 1000, ViewerHost(viewerDir, region), Streams(region).Tag, Streams(region).Tag, viewerDir, viewerDir, PolPaths.Hive(region))
        { Region = region };

    public static PatchProduct Ffxi(string ffxiDir, string viewerDir, PolRegion region = PolRegion.UnitedStates) =>
        new("FINAL FANTASY XI", 1, $"pc001{Streams(region).Tag}.pol.com", "W20", Streams(region).Tag, ffxiDir, viewerDir, PolPaths.Hive(region))
        { Region = region };

    // US and EU share their streams, JP has its own
    private static (string ViewerHost, string Tag) Streams(PolRegion region) =>
        region == PolRegion.Japan ? ("pt004.pol.com", "W20") : ("pt008.pol.com", "W2U");

    // the other stream's host is never taken from env.dat
    internal static string ViewerHost(string viewerDir, PolRegion region)
    {
        var own = Streams(region).ViewerHost;
        var other = Streams(region == PolRegion.Japan ? PolRegion.UnitedStates : PolRegion.Japan).ViewerHost;
        var named = PolEnv.ValueIn(viewerDir, "PATCH_SERVER_DOMAIN");
        return named is null || !PolUpdater.IsPolHost(named) || named.Equals(other, StringComparison.OrdinalIgnoreCase) ? own : named;
    }

    internal static bool IsJapan(PolRegion? region) => region == PolRegion.Japan;

    // JP and US/EU files never mix, the US env.dat turns a JP install into a broken US one. Checked before a server's
    // asked anything
    public void CheckStream()
    {
        if (PolEnv.Region(ViewerFolder) is { } installed && IsJapan(installed) != IsJapan(Region))
            throw Shown.Mark(new InvalidOperationException(
                $"This PlayOnline install is the {PolPaths.VersionName(installed)} version, so it can't take {PolPaths.VersionName(Region)} {Name} files. Nothing was changed."));
        if (PolContents.WireTag(ViewerFolder, Id) is { } own && own != TagWire)
            throw Shown.Mark(new InvalidOperationException(
                $"The {PolPaths.VersionName(Region)} {Name} update doesn't match this PlayOnline install. Nothing was changed."));
    }
}
