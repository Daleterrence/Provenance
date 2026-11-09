using Provenance.Common.Game.ThirdParty;

namespace Provenance.Common.Settings;

public static class MultiAccountRules
{
    // a refused password never counts even when turned on, three refusals lock an SE account
    public static List<MultiAccountEntry> RunEntries(IEnumerable<MultiAccountEntry> stored, IEnumerable<string> profileIds)
    {
        var known = profileIds.ToHashSet();
        return stored.Where(e => e.Enabled && !e.CredentialsRefused && known.Contains(e.ProfileId))
                     .DistinctBy(e => e.ProfileId)   // even from a hand-edited list
                     .ToList();
    }

    // each account has to have its own picked, so nobody's left guessing which one it starts with
    // null is nothing picked, an empty name is Windower's Default profile
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
