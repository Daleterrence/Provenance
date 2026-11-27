namespace Provenance.Common.Settings;

public static class AccountNames
{
    public static string Of(IReadOnlyList<Account> profiles, Account account)
    {
        if (string.IsNullOrWhiteSpace(account.HandleName)) return account.DisplayName;
        var handle = account.HandleName;
        int seen = 0, number = 1;
        foreach (var p in profiles)
        {
            if (!string.Equals(p.HandleName, handle, StringComparison.OrdinalIgnoreCase)) continue;
            if (++seen > 1)
            {
                do number++;
                while (Taken(profiles, $"{handle} ({number})"));
            }
            if (p.Id == account.Id) return seen == 1 ? handle : $"{handle} ({number})";
        }
        return handle;
    }

    // a real handle can have brackets in it too
    private static bool Taken(IReadOnlyList<Account> profiles, string name) =>
        profiles.Any(p => string.Equals(p.HandleName, name, StringComparison.OrdinalIgnoreCase));

    public static Account? Find(IReadOnlyList<Account> profiles, string name)
    {
        var want = name.Trim();
        return profiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.HandleName)
                                            && Of(profiles, p).Equals(want, StringComparison.OrdinalIgnoreCase));
    }
}
