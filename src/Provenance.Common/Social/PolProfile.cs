namespace Provenance.Common.Social;

// Location has 3 lines, Interest 2, the rest 1, and only Portrait has a pic
public sealed record PolProfileRow(string Label, string Field, ProfileFieldState State, IReadOnlyList<string> Lines,
                                   int ProfilePictureId);

public sealed record PolProfile(string Name, string Mail, ProfileFieldState MailState, string Hid, long Updated,
                                PrivacyLevel Level,
                                IReadOnlyList<PolProfileRow> Rows, string Away, string AwayFrom)
{
    public ProfileFieldState NameState { get; init; }
    // the handle's FFXI characters, one tab each
    public IReadOnlyList<PolFfxiCharacter> Characters { get; init; } = [];
}

// one of the handle's game slots with an FFXI character in it. World is the game's world id
public sealed record PolFfxiCharacter(int Slot, CharacterId Character, int World);

// one of my own characters on the profile editor. Key is the character's
public sealed record XiProfile(string Key, PrivacyLevel Level, string Name, string WorldName)
{
    public static readonly XiProfile None = new("", PrivacyLevel.Keep, "", "");
}

// a character's FFXI profile as the hook reads it, raw numbers with a field state each
public sealed record PolFfxiProfile(int Slot, PrivacyLevel Level, string Name, ProfileFieldState NameState, string WorldName,
                                    ProfileFieldState WorldNameState)
{
    public (int Value, ProfileFieldState State) Nation { get; init; }
    public (int Value, ProfileFieldState State) Area { get; init; }
    public (int Value, ProfileFieldState State) Job { get; init; }
    public (int Value, ProfileFieldState State) JobLevel { get; init; }
    public (int Value, ProfileFieldState State) Race { get; init; }
    // the character the hook read, from the friend's slot
    public CharacterId Character { get; init; }
}
