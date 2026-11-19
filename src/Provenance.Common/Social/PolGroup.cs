namespace Provenance.Common.Social;

public sealed record PolGroupMember(int Index, string Name, string Key, int ProfilePictureId, GroupRole Role,
                                    MemberStatus Status, bool Me, bool Ignored, string Text, string Comment)
{
    public bool Online => Status is MemberStatus.Online or MemberStatus.Away or MemberStatus.Busy;
    public int Slot { get; init; } = -1;
    public string Hid { get; init; } = "";
    public bool Mine { get; init; }
    public bool InChat { get; init; }
}

public sealed record PolGroup(string Id, string Name, GroupRole MyRole, MemberStatus MyStatus,
                              IReadOnlyList<PolGroupMember> Members);
