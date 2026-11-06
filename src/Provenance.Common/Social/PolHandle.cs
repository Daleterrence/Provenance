namespace Provenance.Common.Social;

// one of the account's handles. ContentIds are the indexes of the Content IDs linked to it, 8 at most
public sealed record PolHandle(int Slot, string Name, string Key, int ProfilePictureId, int Order, bool Active,
                               IReadOnlyList<int> ContentIds);

// a Content ID record. Handle is the slot it's linked to or -1
public sealed record PolContentId(int Index, ContentIdType Type, string Name, string Text, int Order, int Handle, string Key);
