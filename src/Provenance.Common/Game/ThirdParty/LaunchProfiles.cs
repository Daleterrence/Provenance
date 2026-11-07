using Provenance.Common.Settings;

namespace Provenance.Common.Game.ThirdParty;

public interface ILaunchProfiles
{
    IReadOnlyList<string> List();
    string? NameProblem(string name, string? renaming = null);
    // a name for a new one that's free, and the one to offer for a copy of name (shown as display)
    string NewName();
    string CopyName(string name, string display);
    // what a typed name is called once it's made, to tell a rename that changes nothing
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

    // what used the old name goes to the new one. With no new name (a delete) an account goes back to whatever the
    // Windower/Ashita page says. The platform's own chosen profile is its own to change
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
