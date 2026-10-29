using Microsoft.Win32;

namespace Provenance.Common.PolFormat;

public enum PolRegion { Japan, UnitedStates, Europe }

// the InstallFolder keys (32-bit view) name values by product code: "1000" is the viewer folder (where pol.exe
// lives), "0001" the FFXI one
public static class PolPaths
{
    public const string ViewerProduct = "1000";
    public const string FfxiProduct = "0001";

    public static PolRegion? TryAutoDetectRegion()
    {
        foreach (var region in new[] { PolRegion.UnitedStates, PolRegion.Europe, PolRegion.Japan })
            if (TryGetInstallFolder(region, ViewerProduct) is { } viewer && Directory.Exists(viewer))
                return region;
        return null;
    }

    public static string? TryGetInstallFolder(PolRegion region, string productCode)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = hklm.OpenSubKey(region switch
            {
                PolRegion.Japan => @"SOFTWARE\PlayOnline\InstallFolder",
                PolRegion.UnitedStates => @"SOFTWARE\PlayOnlineUS\InstallFolder",
                PolRegion.Europe => @"SOFTWARE\PlayOnlineEU\InstallFolder",
                _ => throw new ArgumentOutOfRangeException(nameof(region)),
            });
            var value = key?.GetValue(productCode) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('\\');
        }
        catch { return null; }
    }
}
