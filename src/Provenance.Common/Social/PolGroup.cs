namespace Provenance.Common.Social;

public sealed record PolGroupMember(int Index, string Name, string Key, int ProfilePictureId, GroupRole Role,
                                    MemberStatus Status, bool Me, bool Ignored, string Text, string Comment)
{
    public bool Online => Status is MemberStatus.Online or MemberStatus.Away or MemberStatus.Busy;
    // the member's handle slot on its own account (-1 when the hook didn't say) and Handle ID, in the handles' key
    // format. Mine is my POL ID's row, which the viewer goes by
    public int Slot { get; init; } = -1;
    public string Hid { get; init; } = "";
    public bool Mine { get; init; }
    // in the group's chat, from the channel's own member list. Only means anything while I'm in it
    public bool InChat { get; init; }
}

public sealed record PolGroup(string Id, string Name, GroupRole MyRole, MemberStatus MyStatus,
                              IReadOnlyList<PolGroupMember> Members);
