using Provenance.Common.Settings;
using Provenance.Common.Social;

namespace Provenance.Common.Login;

public static class AutoLogin
{
    public const string WindowerAddon = "autoenterkey";
    public const string AshitaAddon = "autologin";

    public static bool IsName(string? name) => name is { Length: >= 1 and <= 15 } && name.All(char.IsAsciiLetter);

    public static bool IsWorld(string? world) => world is { Length: >= 1 and <= 15 } && world.All(char.IsAsciiLetterOrDigit);

    public static SessionOptions Apply(SessionOptions options, Account? account, LauncherSettings settings)
    {
        bool picks = IsName(account?.AutoLoginCharacter);
        return options with
        {
            AcceptLicence = settings.AcceptLicenceAgreement,
            Character = picks ? account!.AutoLoginCharacter : null,
            CharacterWorld = picks && IsWorld(account!.AutoLoginWorld) ? account.AutoLoginWorld : null,
        };
    }

    // a snapshot with no characters in it leaves what's known alone
    public static bool Learn(Account account, PolSocialSnapshot snap)
    {
        var found = HandleOrder.Listed(snap.ContentIds)
            .Where(c => c.Type == ContentIdType.Ffxi && IsName(c.Name?.Trim()))
            .Select(c => new AccountCharacter(c.Name.Trim(), c.Text?.Trim() is { } world && IsWorld(world) ? world : null))
            .DistinctBy(c => (c.Name.ToLowerInvariant(), c.World?.ToLowerInvariant()))
            .ToList();
        if (found.Count == 0 || found.SequenceEqual(account.Characters)) return false;
        account.Characters = found;
        if (account.AutoLoginCharacter is not null && !found.Any(c => Chosen(account, c))) (account.AutoLoginCharacter, account.AutoLoginWorld) = (null, null);
        return true;
    }

    public static bool Chosen(Account account, AccountCharacter c) =>
        string.Equals(c.Name, account.AutoLoginCharacter, StringComparison.OrdinalIgnoreCase)
        && string.Equals(c.World, account.AutoLoginWorld, StringComparison.OrdinalIgnoreCase);
}
