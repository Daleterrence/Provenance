namespace Provenance.Common.Social;

public sealed record PolFriend(string Name, string Comment, PolStatus Status,
                               bool Online, bool Pending, int Row = -1, int ProfilePictureId = -1, string Key = "")
{
    public bool HasComment => !string.IsNullOrWhiteSpace(Comment);
    public string StatusLabel => PolStatusText.Label(Status);
    // another of my own handles (same POL ID), can't be ignored or invited
    public bool IsMe { get; init; }
    // null unless they're online and in FFXI right now
    public PolFriendGame? Game { get; init; }
}

// the FFXI character a friend's on, from their presence: World is the game's world id, Ctsid and Ctid pick out the
// character (the FFXI profile's z_ctsid and z_ctid)
public sealed record PolFriendGame(int World, uint Ctsid, ulong Ctid)
{
    public string CharacterKey => $"{Ctsid}:{Ctid}";
}

// the hook's answer for one character's FFXI profile. NameState is PolProfile's Shown, Private or NotSet
public sealed record PolCharacterLookup(uint Ctsid, ulong Ctid, string Name, int NameState, int Level, string WorldName)
{
    public string CharacterKey => $"{Ctsid}:{Ctid}";
}

// Name is the character's for a character search, Handle is always the handle's
public sealed record PolSearchHit(int Index, string Name, int ProfilePictureId, string Key, bool IsFriend)
{
    public bool IsMe { get; init; }
    public string Handle { get; init; } = "";
    public string World { get; init; } = "";
}

public sealed record PolBlocked(int Index, string Name, string Key, bool AllHandles);
