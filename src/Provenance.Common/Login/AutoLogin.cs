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
        var found = HandleOrder.Listed(snap.ContentIds)
            .Where(c => c.Type == ContentIdType.Ffxi && IsName(c.Name?.Trim()))
            .Select(c => new AccountCharacter(c.Name.Trim(), World(c.Key)))
            .DistinctBy(c => (c.Name.ToLowerInvariant(), c.World?.ToLowerInvariant()))
            .Select(c => Known(account, c) is { } k ? c with { World = c.World ?? k.World, Slot = k.Slot } : c)
            .OrderBy(c => c.Slot ?? int.MaxValue)
            .ToList();
        return found.Count > 0 && Take(account, found);
    }

    // the game's own character list, which is where the slots come from
    public static bool Listed(Account account, IReadOnlyList<AccountCharacter> list)
    {
        var found = list.Select(c => c.World is null && Known(account, c) is { } k ? c with { World = k.World } : c).OrderBy(c => c.Slot).ToList();
        return found.Count > 0 && Take(account, found);
    }

    // by name and world, or by name alone when one of the two has no world and nobody shares the name
    private static AccountCharacter? Known(Account account, AccountCharacter c)
    {
        var named = account.Characters.Where(k => string.Equals(k.Name, c.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        return named.FirstOrDefault(k => string.Equals(k.World, c.World, StringComparison.OrdinalIgnoreCase))
               ?? (named.Count == 1 && (c.World is null || named[0].World is null) ? named[0] : null);
    }

    private static bool Take(Account account, List<AccountCharacter> found)
    {
        var before = (account.AutoLoginCharacter, account.AutoLoginWorld, account.AutoLoginSlot);
        bool same = found.SequenceEqual(account.Characters);
        if (!same) account.Characters = found;
        if (account.AutoLoginCharacter is { } chosen && !found.Any(c => Chosen(account, c)))
        {
            // one saved with no world takes the world of the only character by that name
            var named = account.AutoLoginWorld is null ? found.Where(c => string.Equals(c.Name, chosen, StringComparison.OrdinalIgnoreCase)).ToList() : [];
            (account.AutoLoginCharacter, account.AutoLoginWorld) = named.Count == 1 ? (named[0].Name, named[0].World) : (null, null);
        }
        if (account.AutoLoginCharacter is null && IsSlot(account.AutoLoginSlot))
        {
            // POL's order isn't the game's, so until the game's list is in only the first slot of a lone character is known for sure
            var there = found.FirstOrDefault(c => c.Slot == account.AutoLoginSlot)
                        ?? (account.AutoLoginSlot == 1 && found is [{ Slot: null } only] ? only : null);
            if (there is not null) (account.AutoLoginCharacter, account.AutoLoginWorld, account.AutoLoginSlot) = (there.Name, there.World, null);
        }
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
