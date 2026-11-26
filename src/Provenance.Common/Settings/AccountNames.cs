namespace Provenance.Common.Settings;

public static class AccountNames
{
    public static string Of(IReadOnlyList<Account> profiles, Account account)
    {
        if (string.IsNullOrWhiteSpace(account.HandleName)) return account.DisplayName;
        int n = 0;
        foreach (var p in profiles)
        {
            if (!string.Equals(p.HandleName, account.HandleName, StringComparison.OrdinalIgnoreCase)) continue;
            n++;
            if (p.Id == account.Id) return n == 1 ? account.HandleName : $"{account.HandleName} ({n})";
        }
        return account.HandleName;
    }

    public static Account? Find(IReadOnlyList<Account> profiles, string name)
    {
        var want = name.Trim();
        return profiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.HandleName)
                                            && Of(profiles, p).Equals(want, StringComparison.OrdinalIgnoreCase));
    }
}
