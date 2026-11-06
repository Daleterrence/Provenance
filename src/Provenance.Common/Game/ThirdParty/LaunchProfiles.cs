using Provenance.Common.Settings;

namespace Provenance.Common.Game.ThirdParty;

public interface ILaunchProfiles
{
    IReadOnlyList<string> List();
    string? NameProblem(string name, string? renaming = null);
    string Add(string name);
    string Duplicate(string source, string name);
    string Rename(string name, string newName);
    void Delete(string name);
}

public static class LaunchProfiles
{
    // a launch finds a profile by its name in any case and trimmed, so these do too
    internal static bool Same(string? a, string b) => a is not null && a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);

    // what used the old name goes to the new one. With no new name (a delete) the chosen one goes back to the default and an
    // account to whatever the Windower/Ashita page says
    internal static void Repoint(ISettingsService settings, ThirdPartyHook platform, string old, string? replacement)
    {
        var s = settings.Current;
        if (platform == ThirdPartyHook.Windower)
        {
            if (Same(s.WindowerProfile, old)) s.WindowerProfile = replacement ?? "";
        }
        else if (Same(s.AshitaBootConfig, old)) s.AshitaBootConfig = replacement ?? "";
        if (s.ThirdPartyHookPlatform == platform)
            foreach (var e in s.MultiAccounts.Where(e => Same(e.ThirdPartyHookProfile, old))) e.ThirdPartyHookProfile = replacement;
        settings.Save(s);
    }
}
