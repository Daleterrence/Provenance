namespace Provenance.Common.Social;

public sealed record PolProfileRow(string Label, string Field, ProfileFieldState State, IReadOnlyList<string> Lines,
                                   int ProfilePictureId);

public sealed record PolProfile(string Name, string Mail, ProfileFieldState MailState, string Hid, long Updated,
                                PrivacyLevel Level,
                                IReadOnlyList<PolProfileRow> Rows, string Away, string AwayFrom)
{
    public ProfileFieldState NameState { get; init; }
    public IReadOnlyList<PolFfxiCharacter> Characters { get; init; } = [];
}

public sealed record PolFfxiCharacter(int Slot, CharacterId Character, int World);

public sealed record PolFfxiProfile(int Slot, PrivacyLevel Level, string Name, ProfileFieldState NameState, string WorldName,
                                    ProfileFieldState WorldNameState)
{
    public (int Value, ProfileFieldState State) Nation { get; init; }
    public (int Value, ProfileFieldState State) Area { get; init; }
    public (int Value, ProfileFieldState State) Job { get; init; }
    public (int Value, ProfileFieldState State) JobLevel { get; init; }
    public (int Value, ProfileFieldState State) Race { get; init; }
    public CharacterId Character { get; init; }
}
