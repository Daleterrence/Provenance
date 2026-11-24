using Provenance.Common.Game.ThirdParty;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Logging;

public static class StartupLine
{
    public static string For(string version, Version windows, ThirdPartyHook platform, PolRegion region)
    {
        string platformName = platform switch { ThirdPartyHook.Windower => "Windower", ThirdPartyHook.Ashita => "Ashita", _ => "neither" };
        string client = region switch { PolRegion.Europe => "European", PolRegion.Japan => "Japanese", _ => "North American" };
        return $"Provenance {version} starting on Windows {windows.Major}.{windows.Minor}.{windows.Build}, using {platformName}, {client} client";
    }
}
