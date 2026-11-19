namespace Provenance.Common.Social;

// the viewer only opens a group's chat under the handle I joined with
public sealed record GroupHandleCheck(PolHandle? To, string MemberHandle, string Question)
{
    public const string Title = "Switch Handle";
    public const string QuestionText =
        "The active handle \"{0}\" is not registered with the selected group.\nSwitch to the registered handle \"{1}\"?";
    public const string MissingText = "Provenance can't find the handle \"{0}\" on this PlayOnline account, so it can't switch to it.";

    public string? Problem => To is null ? string.Format(MissingText, MemberHandle) : null;

    public static GroupHandleCheck? For(PolSocialSnapshot snap, PolGroup group)
    {
        if (group.Members.FirstOrDefault(m => m.Mine) is not { Slot: >= 0 } mine) return null;
        if (snap.Handles.FirstOrDefault(h => h.Active) is not { } active || active.Slot == mine.Slot) return null;
        return new GroupHandleCheck(snap.Handles.FirstOrDefault(h => h.Slot == mine.Slot && h.Key == mine.Hid), mine.Name,
                                    string.Format(QuestionText, active.Name, mine.Name));
    }
}
