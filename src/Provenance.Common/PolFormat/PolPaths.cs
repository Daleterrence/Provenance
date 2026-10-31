using Microsoft.Win32;

namespace Provenance.Common.PolFormat;

public enum PolRegion { Japan, UnitedStates, Europe }

// the InstallFolder keys (32-bit view) name values by product code: "1000" is the viewer folder (where pol.exe
// lives), "0001" the FFXI one
public static class PolPaths
{
    public const string ViewerProduct = "1000";
    public const string FfxiProduct = "0001";

    private static readonly PolRegion[] Order = [PolRegion.UnitedStates, PolRegion.Europe, PolRegion.Japan];

    // the regions Provenance logs in and plays on
    public static bool Supported(PolRegion? region) => region is PolRegion.UnitedStates or PolRegion.Europe or PolRegion.Japan;

    public static string Hive(PolRegion region) => region switch
    {
        PolRegion.Japan => @"SOFTWARE\PlayOnline",
        PolRegion.UnitedStates => @"SOFTWARE\PlayOnlineUS",
        PolRegion.Europe => @"SOFTWARE\PlayOnlineEU",
        _ => throw new ArgumentOutOfRangeException(nameof(region)),
    };

    // how the text on screen names each version
    public static string VersionName(PolRegion region) => region switch
    {
        PolRegion.Europe => "European",
        PolRegion.Japan => "Japanese",
        _ => "North American",
    };

    // the installer's region list names places, everywhere else names versions
    public static string RegionName(PolRegion region) => region switch
    {
        PolRegion.Europe => "Europe",
        PolRegion.Japan => "Japan",
        _ => "North America",
    };

    public static PolRegion? TryAutoDetectRegion() => DetectRegion(r => TryGetInstallFolder(r, ViewerProduct));

    // pol.exe goes by env.dat, not the registry, and a PC that's switched regions keeps the old one's keys. So a hive
    // only counts when its viewer folder's env.dat agrees, and with none agreeing the first folder's env.dat wins
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

    public static string? TryGetInstallFolder(PolRegion region, string productCode)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = hklm.OpenSubKey(Hive(region) + @"\InstallFolder");
            var value = key?.GetValue(productCode) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('\\');
        }
        catch { return null; }
    }
}
