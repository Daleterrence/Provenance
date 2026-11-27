using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Settings;

namespace Provenance.Common.Logging;

public static class SettingsSummary
{
    public static string Line(LauncherSettings s)
    {
        static string On(bool on) => on ? "on" : "off";
        string platform = s.ThirdPartyHookPlatform switch
        {
            ThirdPartyHook.Windower => string.IsNullOrWhiteSpace(s.WindowerProfile)
                ? "Windower with its unnamed profile" : $"Windower with profile {s.WindowerProfile.Trim()}",
            ThirdPartyHook.Ashita => string.IsNullOrWhiteSpace(s.AshitaBootConfig)
                ? "Ashita with the boot configuration it last used" : $"Ashita with boot configuration {s.AshitaBootConfig.Trim()}",
            _ => "neither Windower nor Ashita",
        };
        int multi = MultiAccountRules.RunEntries(s.MultiAccounts, s.Profiles.Select(p => p.Id)).Count;
        return $"Settings: {s.Profiles.Count} account(s), {platform}, Log In Automatically {On(s.AutoLogin)}, "
             + $"Start Game Automatically {On(s.StartGameImmediately)}, Start all accounts when Provenance opens {On(s.MultiStartAtOpen)} "
             + $"({multi} account{(multi == 1 ? "" : "s")} turned on, Close Provenance when finished {On(s.MultiCloseWhenFinished)}), "
             + $"Close Provenance when the game exits {On(s.ExitAfterGameExits)}";
    }
}
