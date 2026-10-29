namespace Provenance.Wpf.Services;

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
}
