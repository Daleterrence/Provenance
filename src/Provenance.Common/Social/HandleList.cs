namespace Provenance.Common.Social;

// Orphan: linked to a slot with no handle in it, which polcore can neither unlink nor link again
public sealed record HandleCharacter(PolContentId Record, string Label)
{
    public bool Orphan { get; init; }
}

public sealed record HandleGroup(PolHandle Handle, IReadOnlyList<HandleCharacter> Characters)
{
    // every Content ID on it counts, not only FFXI's
    public bool Full => Handle.ContentIds.Count >= HandleList.MaxContentIds;
}

// the Handles page's lists and wording. The questions are the viewer's own
public static class HandleList
{
    public const int MaxContentIds = 8;
    public const int Ffxi = 1;

    public const string LimitText = "Content ID limit reached. Cannot link any more Content IDs.";
    public const string LoggedOutText = "You're not logged in. Log in to see your handles and the characters linked to them.";
    public const string ReadOnlyText = "Handles can't be changed while FINAL FANTASY XI is starting or running.";
    public const string MultiRunText = "Handles can't be changed while Provenance is starting your accounts.";
    public const string NoHandlesText = "This PlayOnline account doesn't have a handle yet.";
    public const string NoTargetText = "There's no other handle to link it to.";
    public const string GameStartedText = "Provenance couldn't confirm the change before the game started.";
    public const string AutoSwitchText =
        "When you log in to FINAL FANTASY XI with a character that's linked to another handle, that handle becomes your active handle.";
    public const string NoCharactersText = "No characters linked to this handle.";

    // the viewer's own fallback for a record with no name
    public static string Label(PolContentId c) => string.IsNullOrWhiteSpace(c.Name) ? "Content ID" : c.Name.Trim();

    public static (IReadOnlyList<HandleGroup> Handles, IReadOnlyList<HandleCharacter> Unlinked) Build(PolSocialSnapshot snap)
    {
        var ffxi = snap.ContentIds.Where(c => c.Type == Ffxi).OrderBy(c => c.Order).ThenBy(c => c.Index).ToList();
        var handles = snap.Handles.OrderBy(h => h.Order).ThenBy(h => h.Slot).ToList();
        var slots = handles.Select(h => h.Slot).ToHashSet();
        var groups = handles.Select(h => new HandleGroup(h,
            ffxi.Where(c => c.Handle == h.Slot).Select(c => new HandleCharacter(c, Label(c))).ToList())).ToList();
        var unlinked = ffxi.Where(c => !slots.Contains(c.Handle))
                           .Select(c => new HandleCharacter(c, Label(c)) { Orphan = c.Handle >= 0 }).ToList();
        return (groups, unlinked);
    }

    public static string SwitchQuestion(PolHandle h, bool inChatRoom = false) =>
        inChatRoom ? $"Make {h.Name} active? You'll leave the chat room you're in." : $"Make {h.Name} active?";

    public static string LinkQuestion(HandleCharacter c, PolHandle to, IReadOnlyList<PolHandle> handles) =>
        handles.FirstOrDefault(h => h.Slot == c.Record.Handle && h.Slot != to.Slot) is { } from
            ? $"{c.Label} is linked to the handle {from.Name}. Link to the handle {to.Name} instead?"
            : $"Link {c.Label} to the handle {to.Name}?";

    public static string UnlinkQuestion(HandleCharacter c, PolHandle from) => $"Unlink {c.Label} from the handle {from.Name}?";

    public static string Done(SocialSnapshot.HandleChange op, string handle, string character) => op switch
    {
        SocialSnapshot.HandleChange.Switch => $"{handle} is now your active handle.",
        SocialSnapshot.HandleChange.Link => $"{character} is now linked to the handle {handle}.",
        _ => $"{character} is no longer linked to the handle {handle}.",
    };

    // "timeout" is the app's own, the rest are the hook's. Codes are polcore's
    public static string Failed(SocialSnapshot.HandleChange op, string state) => state switch
    {
        "failed:busy" => "PlayOnline is busy with something else. Try again in a moment.",
        "failed:already active" => "That handle is already active.",
        "failed:handle full" or "failed:-7169" => LimitText,
        "failed:list changed" or "failed:not linked" or "failed:already linked" or "failed:-7172" or "failed:-7173"
            or "failed:-7174" => "Your handles or characters changed, so nothing was saved. Try again.",
        "failed:-7168" => "Not saved: that character is already linked to a handle.",
        "failed:reloaded" => "Not saved: PlayOnline had a newer Content ID list, so Provenance loaded that instead. Try again.",
        "failed:not saved" => "Not saved: PlayOnline didn't keep the change. Try again.",
        "failed:orphan" => "That character is linked to a handle this account doesn't have any more, so it can't be changed.",
        "failed:account notice" => "This PlayOnline account can't change its handles right now.",
        "failed:draining" or "failed:not in hold" =>
            "That couldn't be done while PlayOnline was closing or FINAL FANTASY XI was starting.",
        "failed:cancelled" => "FINAL FANTASY XI started before PlayOnline answered, so Provenance couldn't confirm the change.",
        "timeout" => op == SocialSnapshot.HandleChange.Switch ? "PlayOnline didn't answer, so your active handle may not have changed."
                                                              : "PlayOnline didn't answer, so the change may not have been saved.",
        var s => op == SocialSnapshot.HandleChange.Switch
            ? $"Provenance couldn't confirm the switch ({SocialSnapshot.FailReason(s)})."
            : $"Not saved: PlayOnline didn't accept the change ({SocialSnapshot.FailReason(s)}).",
    };
}
