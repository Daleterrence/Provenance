using System.Globalization;
using Provenance.Common.Settings;
using Provenance.Common.Social;

namespace Provenance.Common.Login;

public static class AutoLogin
{
    public const string WindowerAddon = "autoenterkey";
    public const string AshitaAddon = "autologin";

    public static bool IsName(string? name) => name is { Length: >= 1 and <= 15 } && name.All(char.IsAsciiLetter);

    public const int Slots = 16;

    public static bool IsSlot(int? slot) => slot is >= 1 and <= Slots;

    public static bool IsWorld(string? world) => world is { Length: >= 1 and <= 15 } && world.All(char.IsAsciiLetterOrDigit);

    public static SessionOptions Apply(SessionOptions options, Account? account, LauncherSettings settings)
    {
        bool picks = IsName(account?.AutoLoginCharacter);
        return options with
        {
            AcceptLicence = settings.AcceptLicenceAgreement,
            Character = picks ? account!.AutoLoginCharacter : null,
            CharacterWorld = picks && IsWorld(account!.AutoLoginWorld) ? account.AutoLoginWorld : null,
            CharacterSlot = !picks && IsSlot(account?.AutoLoginSlot) ? account!.AutoLoginSlot : null,
        };
    }

    // a snapshot with no characters in it leaves what's known alone
    public static bool Learn(Account account, PolSocialSnapshot snap)
    {
        var found = HandleOrder.Listed(snap.ContentIds)
            .Where(c => c.Type == ContentIdType.Ffxi && IsName(c.Name?.Trim()))
            .Select(c => new AccountCharacter(c.Name.Trim(), World(c.Key)))
            .DistinctBy(c => (c.Name.ToLowerInvariant(), c.World?.ToLowerInvariant()))
            .ToList();
        if (found.Count == 0 || found.SequenceEqual(account.Characters)) return false;
        account.Characters = found;
        if (account.AutoLoginCharacter is { } chosen && !found.Any(c => Chosen(account, c)))
        {
            // one saved with no world takes the world of the only character by that name
            var named = account.AutoLoginWorld is null ? found.Where(c => string.Equals(c.Name, chosen, StringComparison.OrdinalIgnoreCase)).ToList() : [];
            (account.AutoLoginCharacter, account.AutoLoginWorld) = named.Count == 1 ? (named[0].Name, named[0].World) : (null, null);
        }
        // the game's list isn't in the account's order, so only the first slot of a lone character is known for sure
        if (account.AutoLoginCharacter is null && account.AutoLoginSlot == 1 && found.Count == 1)
            (account.AutoLoginCharacter, account.AutoLoginWorld, account.AutoLoginSlot) = (found[0].Name, found[0].World, null);
        return true;
    }

    // the key is the content ID's two numbers in hex, and the world is the second byte of the first
    private static string? World(string key) =>
        key.Length >= 8 && uint.TryParse(key.AsSpan(0, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id)
            ? FfxiWorlds.Name((int)(id >> 16 & 0xff)) : null;

    public static bool Chosen(Account account, AccountCharacter c) =>
        string.Equals(c.Name, account.AutoLoginCharacter, StringComparison.OrdinalIgnoreCase)
        && string.Equals(c.World, account.AutoLoginWorld, StringComparison.OrdinalIgnoreCase);
}
