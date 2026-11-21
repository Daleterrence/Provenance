using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Login;

namespace Provenance.Common.Settings;

public static class MultiAccountRules
{
    // a refused password never counts even when turned on, three refusals lock an SE account
    public static List<MultiAccountEntry> RunEntries(IEnumerable<MultiAccountEntry> stored, IEnumerable<string> profileIds)
    {
        var known = profileIds.ToHashSet();
        return stored.Where(e => e.Enabled && !e.CredentialsRefused && known.Contains(e.ProfileId))
                     .DistinctBy(e => e.ProfileId)
                     .ToList();
    }

    public static bool StartsAtOpen(LauncherSettings s, bool suppressed) =>
        s.MultiStartAtOpen && !suppressed && s.ThirdPartyHookPlatform is ThirdPartyHook.Windower or ThirdPartyHook.Ashita
        && RunEntries(s.MultiAccounts, s.Profiles.Select(p => p.Id)).Count > 0;

    public static readonly TimeSpan GameUpLimit = TimeSpan.FromSeconds(15);

    // a Windower profile's game settings go in the one registry key every game reads as it starts, so the next
    // account's can't go in until the game in front has read its own
    public static bool NextWaitsForGame(ThirdPartyHook platform, IReadOnlyList<MultiRunAccount> run, string profileId,
                                        IReadOnlyDictionary<string, string?> profiles, Func<string, bool> inGame)
    {
        if (platform != ThirdPartyHook.Windower) return false;
        int at = run.ToList().FindIndex(a => a.ProfileId == profileId);
        var next = at < 0 ? null : run.Skip(at + 1).FirstOrDefault(a => !inGame(a.ProfileId));
        return next is not null && (profiles.GetValueOrDefault(profileId) ?? "") != (profiles.GetValueOrDefault(next.ProfileId) ?? "");
    }

    public static List<MultiAccountEntry> WithoutProfile(IEnumerable<MultiAccountEntry> runEntries) =>
        runEntries.Where(e => e.ThirdPartyHookProfile is null).ToList();

    public static string NoProfileText(ThirdPartyHook platform, IReadOnlyList<string> names)
    {
        var kind = platform == ThirdPartyHook.Ashita ? "an Ashita boot configuration" : "a Windower profile";
        return names.Count == 1
            ? $"{names[0]} doesn't have {kind} picked yet. Choose one in Settings, under Multi-account, then start all accounts again."
            : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]} don't have {kind} picked yet. "
              + "Choose one for each in Settings, under Multi-account, then start all accounts again.";
    }
}
