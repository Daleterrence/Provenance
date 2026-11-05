namespace Provenance.Common.Social;

// Class: 2 invited, 3 member, 4 sub-master, 5 master
public sealed record PolGroupMember(int Index, string Name, string Key, int ProfilePictureId, int Class, int Status,
                                    bool Me, bool Ignored, string Text, string Comment)
{
    // 1 online, 2 away, 3 busy, 0 and 4 offline
    public bool Online => Status is 1 or 2 or 3;
    // the member's handle slot on its own account (-1 when the hook didn't say) and Handle ID, in the handles' key
    // format. Mine is my POL ID's row, which the viewer goes by
    public int Slot { get; init; } = -1;
    public string Hid { get; init; } = "";
    public bool Mine { get; init; }
    // in the group's chat, from the channel's own member list. Only means anything while I'm in it
    public bool InChat { get; init; }
}

public sealed record PolGroup(string Id, string Name, int MyClass, int MyStatus, IReadOnlyList<PolGroupMember> Members);
