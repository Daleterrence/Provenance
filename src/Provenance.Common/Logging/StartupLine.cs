using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Install;
using Provenance.Common.PolFormat;
using Provenance.Common.Settings;

namespace Provenance.Common.Logging;

public static class StartupLine
{
    public static string For(string version, Version windows, ThirdPartyHook platform, PolRegion? region)
    {
        string platformName = platform switch { ThirdPartyHook.Windower => "Windower", ThirdPartyHook.Ashita => "Ashita", _ => "neither" };
        string client = region switch { PolRegion.Europe => ", European client", PolRegion.Japan => ", Japanese client", PolRegion.UnitedStates => ", North American client", _ => "" };
        return $"Provenance {version} starting on Windows {windows.Major}.{windows.Minor}.{windows.Build}, using {platformName}{client}";
    }

    public static IReadOnlyList<string> Details(LauncherSettings settings, PolRegion? region)
    {
        string? Folder(GameProduct product) => region is { } r ? PolPaths.TryGetInstallFolder(r, product) : null;
        return [SettingsSummary.Line(settings), .. GraphicsWrappers.Lines(Folder(GameProduct.Viewer), Folder(GameProduct.Game))];
    }
}
