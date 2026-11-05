namespace Provenance.Common.Social;

// Location has 3 lines, Interest 2, the rest 1, and only Portrait has a pic
public sealed record PolProfileRow(string Label, string Field, int State, IReadOnlyList<string> Lines,
                                   int ProfilePictureId);

public sealed record PolProfile(string Name, string Mail, int MailState, string Hid, long Updated, int Level,
                                IReadOnlyList<PolProfileRow> Rows, string Away, string AwayFrom)
{
    public const int Shown = 0, Private = 1, NotSet = 2, UnderTen = 3;

    public int NameState { get; init; }
    // the handle's FFXI characters, one tab each
    public IReadOnlyList<PolFfxiCharacter> Characters { get; init; } = [];
}

// one of the handle's game slots with an FFXI character in it. World is the game's world id
public sealed record PolFfxiCharacter(int Slot, uint Ctsid, ulong Ctid, int World)
{
    public string CharacterKey => $"{Ctsid}:{Ctid}";
}

// a character's FFXI profile as the hook reads it, raw numbers with PolProfile's Shown, Private or NotSet each
public sealed record PolFfxiProfile(int Slot, int Level, string Name, int NameState, string WorldName, int WorldNameState)
{
    public (int Value, int State) Nation { get; init; }
    public (int Value, int State) Area { get; init; }
    public (int Value, int State) Job { get; init; }
    public (int Value, int State) JobLevel { get; init; }
    public (int Value, int State) Race { get; init; }
    // the character the hook read, from the friend's slot
    public uint Ctsid { get; init; }
    public ulong Ctid { get; init; }
    public string CharacterKey => $"{Ctsid}:{Ctid}";
}
