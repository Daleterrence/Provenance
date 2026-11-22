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

    // a snapshot with no characters in it leaves what's known alone. POL has no slots, so ones the game's list gave are kept
    public static bool Learn(Account account, PolSocialSnapshot snap)
    {
        var listed = HandleOrder.Listed(snap.ContentIds)
            .Where(c => c.Type == ContentIdType.Ffxi && IsName(c.Name?.Trim()))
            .Select(c => new AccountCharacter(c.Name.Trim(), World(c.Key)))
            .DistinctBy(c => (c.Name.ToLowerInvariant(), c.World?.ToLowerInvariant()))
            .ToList();
        var found = listed.Select(c => Known(account, c, listed) is { } k ? c with { World = c.World ?? k.World, Slot = k.Slot } : c)
            .OrderBy(c => c.Slot ?? int.MaxValue)
            .ToList();
        return found.Count > 0 && Take(account, found);
    }

    // the game's own character list, which is where the slots come from
    public static bool Listed(Account account, IReadOnlyList<AccountCharacter> list)
    {
        var found = list.Select(c => c.World is null && Known(account, c, list) is { } k ? c with { World = k.World } : c).OrderBy(c => c.Slot).ToList();
        return found.Count > 0 && Take(account, found);
    }

    private static bool Named(AccountCharacter c, string name) => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase);

    // by name and world, or by name alone when one of the two has no world and nobody on either list shares the name
    private static AccountCharacter? Known(Account account, AccountCharacter c, IReadOnlyList<AccountCharacter> incoming)
    {
        var named = account.Characters.Where(k => Named(k, c.Name)).ToList();
        return named.FirstOrDefault(k => string.Equals(k.World, c.World, StringComparison.OrdinalIgnoreCase))
               ?? (named.Count == 1 && (c.World is null || named[0].World is null) && incoming.Count(i => Named(i, c.Name)) == 1 ? named[0] : null);
    }

    private static bool Take(Account account, List<AccountCharacter> found)
    {
        var before = (account.AutoLoginCharacter, account.AutoLoginWorld, account.AutoLoginSlot);
        bool same = found.SequenceEqual(account.Characters);
        if (!same) account.Characters = found;
        if (account.AutoLoginCharacter is { } chosen && !found.Any(c => Chosen(account, c)))
        {
            // the only character by that name is still the one, whatever world it was saved with
            var named = found.Where(c => Named(c, chosen)).ToList();
            (account.AutoLoginCharacter, account.AutoLoginWorld) = named.Count == 1 ? (named[0].Name, named[0].World) : (null, null);
        }
        // only the game's list has slots, POL's order isn't the game's
        if (account.AutoLoginCharacter is null && IsSlot(account.AutoLoginSlot) && found.FirstOrDefault(c => c.Slot == account.AutoLoginSlot) is { } there)
            (account.AutoLoginCharacter, account.AutoLoginWorld, account.AutoLoginSlot) = (there.Name, there.World, null);
        return !same || before != (account.AutoLoginCharacter, account.AutoLoginWorld, account.AutoLoginSlot);
    }

    // the game pads a world's name with spaces. The app's own spelling of it is the one kept
    public static string? WorldNamed(string? text)
    {
        var plain = text?.Replace(" ", "") ?? "";
        return FfxiWorlds.All.Select(w => w.Name).FirstOrDefault(n => string.Equals(n, plain, StringComparison.OrdinalIgnoreCase))
               ?? (IsWorld(plain) ? plain : null);
    }

    // the key is the content ID's two numbers in hex, and the world is the second byte of the first
    private static string? World(string key) =>
        key.Length >= 8 && uint.TryParse(key.AsSpan(0, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id)
            ? FfxiWorlds.Name((int)(id >> 16 & 0xff)) : null;

    public static bool Chosen(Account account, AccountCharacter c) =>
        string.Equals(c.Name, account.AutoLoginCharacter, StringComparison.OrdinalIgnoreCase)
        && string.Equals(c.World, account.AutoLoginWorld, StringComparison.OrdinalIgnoreCase);
}
