namespace Provenance.Common.Game.Windower;

internal sealed record WindowerLaunchScan(WindowerLaunchLists Lists, string LauncherVersion, string HookVersion)
{
    public static WindowerLaunchScan Run(string windowerDir, WindowerProfile profile, WindowerAddonList effective, WindowerScanCache cache,
                                         IReadOnlyCollection<string>? leaveOut = null)
    {
        var plugins = WindowerPlugins.Scan(windowerDir, cache);
        var addons = WindowerAddons.ScanForLaunch(windowerDir, cache);
        var lists = WindowerAddonList.ForLaunch(windowerDir, profile, effective, plugins, addons, leaveOut);
        var scan = new WindowerLaunchScan(lists, WindowerHookBlock.LauncherVersion(windowerDir, cache), WindowerHookBlock.HookVersion(windowerDir, cache));
        cache.Save();
        return scan;
    }
}
