using Provenance.Common.Game.Windower;
using Provenance.Common.Settings;

namespace Provenance.Common.Game.ThirdParty;

public static class ThirdPartyProfiles
{
    private static bool On(LauncherSettings s, ThirdPartyHook platform) => s.ThirdPartyHookPlatform == platform;

    // a stored name of one of the old "(Provenance)" copies means its source. True when anything changed
    public static bool MapOldCopies(LauncherSettings s)
    {
        bool changed = false;
        if (WindowerSettingsFile.IsOldCopy(s.WindowerProfile))
        {
            s.WindowerProfile = WindowerSettingsFile.OldCopySource(s.WindowerProfile);
            changed = true;
        }
        if (!On(s, ThirdPartyHook.Windower)) return changed;
        foreach (var e in s.MultiAccounts.Where(e => WindowerSettingsFile.IsOldCopy(e.ThirdPartyHookProfile)))
        {
            e.ThirdPartyHookProfile = WindowerSettingsFile.OldCopySource(e.ThirdPartyHookProfile);
            changed = true;
        }
        return changed;
    }
}
