namespace Provenance.Common.Social;

public sealed record PolHandle(int Slot, string Name, string Key, int ProfilePictureId, int Order, bool Active,
                               IReadOnlyList<int> ContentIds);

public sealed record PolContentId(int Index, ContentIdType Type, string Name, string Text, int Order, int Handle, string Key);
