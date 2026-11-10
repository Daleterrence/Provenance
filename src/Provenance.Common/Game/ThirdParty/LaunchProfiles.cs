using Provenance.Common.Settings;

namespace Provenance.Common.Game.ThirdParty;

public interface ILaunchProfiles
{
    IReadOnlyList<string> List();
    string? NameProblem(string name, string? renaming = null);
    string NewName();
    string CopyName(string name, string display);
    string Canonical(string typed);
    string Add(string name);
    string Duplicate(string source, string name);
    string Rename(string name, string newName);
    void Delete(string name);
}

public static class LaunchProfiles
{
    // a launch finds a profile by its name in any case and trimmed, so these do too
    internal static bool Same(string? a, string b) => a is not null && a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);

    // what used the old name goes to the new one, or back to the Windower/Ashita page's pick on a delete.
    // The platform's own chosen profile is its own to change
    internal static void Repoint(ISettingsService settings, ThirdPartyHook platform, string old, string? replacement,
                                 Action<LauncherSettings> repointChosen)
    {
        settings.SaveChange(s =>
        {
            repointChosen(s);
            if (s.ThirdPartyHookPlatform == platform)
                foreach (var e in s.MultiAccounts.Where(e => Same(e.ThirdPartyHookProfile, old))) e.ThirdPartyHookProfile = replacement;
        });
    }
}
