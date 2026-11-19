namespace Provenance.Common.Social;

public sealed record PolFriend(string Name, string Comment, PolStatus Status,
                               bool Online, bool Pending, int Row = -1, int ProfilePictureId = -1, string Key = "")
{
    public bool HasComment => !string.IsNullOrWhiteSpace(Comment);
    public string StatusLabel => PolStatusText.Label(Status);
    public bool IsMe { get; init; }
    public PolFriendGame? Game { get; init; }
}

public sealed record PolFriendGame(int World, CharacterId Character);

public sealed record PolCharacterLookup(CharacterId Character, string Name, ProfileFieldState NameState, PrivacyLevel Level,
                                        string WorldName, int ZoneId = 0, ProfileFieldState ZoneState = ProfileFieldState.NotSet);

public sealed record PolSearchHit(int Index, string Name, int ProfilePictureId, string Key, bool IsFriend)
{
    public bool IsMe { get; init; }
    public string Handle { get; init; } = "";
    public string World { get; init; } = "";
}

public sealed record SearchResult(FriendSearchKind Kind, int Total, bool TooMany, IReadOnlyList<PolSearchHit> Hits)
{
    public static readonly SearchResult None = new(FriendSearchKind.Handle, 0, false, []);
}

public sealed record PolBlocked(int Index, string Name, string Key, bool AllHandles);
