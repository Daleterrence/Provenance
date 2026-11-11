using Provenance.Common.Hook;

namespace Provenance.Common.Social;

// Orphan: linked to a slot with no handle in it, which polcore can neither unlink nor link again
public sealed record HandleCharacter(PolContentId Record, string Label)
{
    public bool Orphan { get; init; }
    public bool Named => !string.IsNullOrWhiteSpace(Record.Name);
}

public sealed record HandleGroup(PolHandle Handle, IReadOnlyList<HandleCharacter> Characters)
{
    // every Content ID on it counts, not only FFXI's
    public bool Full => Handle.ContentIds.Count >= HandleList.MaxContentIds;
}

// one of the viewer's own lines: where it is in its string table, and the US words for when that can't be read
public readonly record struct ViewerLine(int Id, string Us);

// the Handles page's lists and wording. The questions are the viewer's own
public static class HandleList
{
    public const int MaxContentIds = 8;
    public const int MaxHandles = 64, MaxContentIdRecords = 64;

    public static readonly ViewerLine CreateTitle = new(0x3b4f, "Create New Handle");
    public static readonly ViewerLine EnterName = new(0x3ba9, "Enter new handle.");
    public static readonly ViewerLine RegisterText = new(0x3b9f, "Register %s?");
    public static readonly ViewerLine UpdatingText = new(0x3b95, "Updating handle list...");
    public static readonly ViewerLine NameRefused = new(0x3bab, "Cannot register selected handle. Please try another.");
    public static readonly ViewerLine NameFailed = new(0x3ba4, "Handle registration failed. Please re-enter.");
    public static readonly ViewerLine NoWordList = new(0x3bb6, "Cannot register handle. Data may be corrupted.");
    public static readonly ViewerLine HandlesFull = new(0x3ba5, "Cannot register any more handles.");
    public static readonly ViewerLine DeleteActive = new(0x3ba8, "Cannot delete active handle.");
    public static readonly ViewerLine DeleteText = new(0x3baa, "Delete specified handle?");
    public static readonly ViewerLine DeleteInGroupText = new(0x6f32, "Leaving group. Delete handle?");

    public const string CreateHelpText =
        "A handle is a name on PlayOnline that other players can see. Use 1 to 15 English letters, numbers or symbols.";
    public const string DeleteTitle = "Delete Handle";
    public const string MoveFirstText = "Move this handle's characters to another handle before you delete it.";
    public const string OtherContentText =
        "This handle has a Content ID from another PlayOnline service linked to it, so Provenance can't delete it.";
    public const string WrongNameText = "That isn't the handle's name.";
    public const string OrderChangedText = "Character order changed. Save it to keep it.";

    // the viewer's 0x6f32 ends in a space
    public static string Say(ViewerLine line, Func<int, string, string>? text = null) =>
        (text?.Invoke(line.Id, line.Us) ?? line.Us).Trim();

    public const string LimitText = HookFailures.Handles.LimitText;
    public const string LoggedOutText = "You're not logged in. Log in to see your handles and the characters linked to them.";
    public const string ReadOnlyText = "Handles can't be changed while FINAL FANTASY XI is starting or running.";
    public const string MultiRunText = "Handles can't be changed while Provenance is starting your accounts.";
    public const string NoTargetText = "There's no other handle to link it to.";
    public const string GameStartedText = "Provenance couldn't confirm the change before the game started.";
    public const string AutoSwitchText =
        "When you log in to FINAL FANTASY XI with a character that's linked to another handle, that handle becomes your active handle.";
    public const string NoCharactersText = "No characters linked to this handle.";
    public const string NotLoggedInText = "You're not logged in to PlayOnline.";
    public const string PageBusyText = "Another handle change is still going. Try again in a moment.";
    public const string SwitchMenuTitle = "Active handle";

    // the viewer's own fallback for a record with no name
    public static string Label(PolContentId c) => string.IsNullOrWhiteSpace(c.Name) ? "Content ID" : c.Name.Trim();

    public static (IReadOnlyList<HandleGroup> Handles, IReadOnlyList<HandleCharacter> Unlinked) Build(PolSocialSnapshot snap,
                                                                                                     IReadOnlyList<string>? order = null)
    {
        var ffxi = HandleOrder.Listed(snap.ContentIds).Where(c => c.Type == ContentIdType.Ffxi).ToList();
        if (order is not null)
        {
            var place = order.Select((key, at) => (key, at)).ToDictionary(p => p.key, p => p.at);
            ffxi = [.. ffxi.OrderBy(c => place.GetValueOrDefault(c.Key, int.MaxValue))];
        }
        var handles = snap.Handles.OrderBy(h => h.Order).ThenBy(h => h.Slot).ToList();
        var slots = handles.Select(h => h.Slot).ToHashSet();
        var groups = handles.Select(h => new HandleGroup(h,
            ffxi.Where(c => c.Handle == h.Slot).Select(c => new HandleCharacter(c, Label(c))).ToList())).ToList();
        var unlinked = ffxi.Where(c => !slots.Contains(c.Handle))
                           .Select(c => new HandleCharacter(c, Label(c)) { Orphan = c.Handle >= 0 }).ToList();
        return (groups, unlinked);
    }

    // the viewer's and polcore's rule, plain ASCII and not just spaces
    public static string? NameProblem(string name) =>
        name.Any(c => c < 0x20 || c > 0x7e) ? "Use English letters, numbers and symbols only."
        : string.IsNullOrWhiteSpace(name) ? "Type a handle name."
        : name.Length > PolLimits.HandleName ? HookFailures.Friends.BadName : null;

    public static string RegisterQuestion(string name, Func<int, string, string>? text = null) =>
        Say(RegisterText, text).Replace("%s", name);

    public static string DeleteWarning(string name) => $"{name} will be deleted from this PlayOnline account. This can't be undone.";

    public static string DeleteQuestion(PolHandle handle, bool inGroup, Func<int, string, string>? text = null) =>
        Say(inGroup ? DeleteInGroupText : DeleteText, text) + "\n\n" + DeleteWarning(handle.Name);

    public static string TypeNamePrompt(string name) => $"Type {name} to delete this handle.";

    // the viewer goes by the group header's slot bits. This is one of my rows having the slot and Handle ID,
    // the same answer unless the groups didn't load
    public static bool InGroup(PolSocialSnapshot snap, PolHandle handle) =>
        snap.GroupList.Any(g => g.Members.Any(m => m.Mine && m.Slot == handle.Slot && m.Hid == handle.Key));

    public static string SwitchQuestion(PolHandle h, bool inChatRoom = false) =>
        inChatRoom ? $"Make {h.Name} active? You'll leave the chat room you're in." : $"Make {h.Name} active?";

    public static string LinkQuestion(HandleCharacter c, PolHandle to, IReadOnlyList<PolHandle> handles) =>
        handles.FirstOrDefault(h => h.Slot == c.Record.Handle && h.Slot != to.Slot) is { } from
            ? $"{c.Label} is linked to the handle {from.Name}. Link to the handle {to.Name} instead?"
            : $"Link {c.Label} to the handle {to.Name}?";

    public static string Done(HandleChange op, string handle, string character) => op switch
    {
        HandleChange.Switch => $"{handle} is now your active handle.",
        HandleChange.Create => $"The handle {handle} was created.",
        HandleChange.Delete => $"The handle {handle} was deleted.",
        HandleChange.Reorder => "Character order saved.",
        _ => $"{character} is now linked to the handle {handle}.",
    };

    // one the server never answered may have gone through all the same, so neither says "not saved". -261 is only
    // a refused name on a create
    private static string? ListEditFailed(HandleChange op, string state, Func<int, string, string>? text) => (op, state) switch
    {
        (_, "failed:unconfirmed") => HookFailures.Handles.Unconfirmed,
        (_, "failed:reloaded") => HookFailures.Handles.HandleListReloaded,
        (HandleChange.Create, "failed:word" or "failed:-261") => Say(NameRefused, text),
        (HandleChange.Create, "failed:bad name") => Say(NameFailed, text),
        (HandleChange.Create, "failed:no dictionary") => Say(NoWordList, text),
        (HandleChange.Create, "failed:handles full" or "failed:-7184") => Say(HandlesFull, text),
        (HandleChange.Delete, "failed:active") => Say(DeleteActive, text),
        (_, "failed:has content ids") => HookFailures.Handles.ListChanged,
        (_, "failed:busy" or "failed:bad request" or "failed:list changed" or "failed:-7172" or "failed:draining" or "failed:not in hold"
            or "failed:cancelled" or "timeout") => null,
        _ => op == HandleChange.Create ? HookFailures.Handles.CreateUnconfirmed : HookFailures.Handles.DeleteUnconfirmed,
    };

    // "timeout" is the app's own, the rest are the hook's. Codes are polcore's
    public static string Failed(HandleChange op, string state, Func<int, string, string>? text = null) =>
        op is HandleChange.Create or HandleChange.Delete && ListEditFailed(op, state, text) is { } words ? words
        : op == HandleChange.Reorder && state == "failed:bad request" ? HookFailures.Handles.ListChanged
        : OtherFailed(op, state);

    private static string OtherFailed(HandleChange op, string state) => state switch
    {
        "failed:busy" => HookFailures.Busy,
        "failed:already active" => HookFailures.Handles.AlreadyActive,
        "failed:handle full" or "failed:-7169" => LimitText,
        "failed:list changed" or "failed:not linked" or "failed:already linked" or "failed:not active" or "failed:-7172"
            or "failed:-7173" or "failed:-7174" => HookFailures.Handles.ListChanged,
        "failed:-7168" => HookFailures.Handles.AlreadyLinked,
        "failed:reloaded" => HookFailures.Handles.Reloaded,
        "failed:not saved" => HookFailures.Handles.NotSaved,
        "failed:orphan" => HookFailures.Handles.Orphan,
        "failed:draining" or "failed:not in hold" =>
            HookFailures.Handles.Closing,
        "failed:cancelled" => HookFailures.Handles.Cancelled,
        "timeout" => op == HandleChange.Switch ? HookFailures.Handles.SwitchNoAnswer
                                                              : HookFailures.Handles.ChangeNoAnswer,
        var s => op == HandleChange.Switch
            ? HookFailures.Handles.SwitchRejected(SocialRequests.FailReason(s))
            : HookFailures.Handles.ChangeRejected(SocialRequests.FailReason(s)),
    };
}
