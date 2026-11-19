using Microsoft.Win32;
using Provenance.Common.Install;

namespace Provenance.Common.PolFormat;

public enum PolRegion { Japan, UnitedStates, Europe }

public static class PolPaths
{
    private static readonly PolRegion[] Order = [PolRegion.UnitedStates, PolRegion.Europe, PolRegion.Japan];

    public static bool Supported(PolRegion? region) => region is PolRegion.UnitedStates or PolRegion.Europe or PolRegion.Japan;

    public static string VendorKey(PolRegion region) => Hive(region) + @"\" + (region == PolRegion.Japan ? "SQUARE" : "SquareEnix");

    public static string ViewerKey(PolRegion region) => VendorKey(region) + @"\PlayOnlineViewer";

    public static string FfxiKey(PolRegion region) => VendorKey(region) + @"\FinalFantasyXI";

    public static string Hive(PolRegion region) => region switch
    {
        PolRegion.Japan => @"SOFTWARE\PlayOnline",
        PolRegion.UnitedStates => @"SOFTWARE\PlayOnlineUS",
        PolRegion.Europe => @"SOFTWARE\PlayOnlineEU",
        _ => throw new ArgumentOutOfRangeException(nameof(region)),
    };

    public static string VersionName(PolRegion region) => region switch
    {
        PolRegion.Europe => "European",
        PolRegion.Japan => "Japanese",
        _ => "North American",
    };

    public static string RegionName(PolRegion region) => region switch
    {
        PolRegion.Europe => "Europe",
        PolRegion.Japan => "\u65E5\u672C",
        _ => "North America",
    };

    public static PolRegion? TryAutoDetectRegion() => DetectRegion(r => TryGetInstallFolder(r, GameProduct.Viewer));

    // a PC that's switched regions keeps the old keys, so a hive only counts when its viewer's env.dat agrees
    internal static PolRegion? DetectRegion(Func<PolRegion, string?> viewerFolder)
    {
        var found = new List<(PolRegion Hive, string Dir)>();
        foreach (var region in Order)
            if (viewerFolder(region) is { } dir && Directory.Exists(dir))
                found.Add((region, dir));
        foreach (var (hive, dir) in found)
            if (PolEnv.Region(dir) == hive) return hive;
        return found.Count == 0 ? null : PolEnv.Region(found[0].Dir) ?? found[0].Hive;
    }

    public static string? TryGetInstallFolder(PolRegion region, GameProduct product)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = hklm.OpenSubKey(Hive(region) + @"\InstallFolder");
            var value = key?.GetValue(product.InstallValue) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('\\');
        }
        catch { return null; }
    }
}
