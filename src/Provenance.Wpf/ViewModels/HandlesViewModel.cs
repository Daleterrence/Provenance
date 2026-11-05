using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Social;
using HandleChange = Provenance.Common.Social.SocialSnapshot.HandleChange;

namespace Provenance.Wpf.ViewModels;

public sealed record HandleCharacterRow(HandleCharacter Character, PolHandle? Handle, bool Editable)
{
    public string Label => Character.Label;
    public bool CanLink => Editable && !Character.Orphan;
    public bool CanUnlink => Editable && Handle != null;
}

public sealed record HandleRow(HandleGroup Group, ImageSource? Picture, IReadOnlyList<HandleCharacterRow> Characters,
                               bool Editable)
{
    public PolHandle Handle => Group.Handle;
    public string Name => Handle.Name;
    public bool Active => Handle.Active;
    public bool CanSwitch => Editable && !Handle.Active;
    public bool Full => Group.Full;
    public bool NoCharacters => Characters.Count == 0;
}

// Settings > Handles, from the hook's snapshot. It lives as long as the app, so a change still going when Settings
// closes says how it went next time
public partial class HandlesViewModel : ObservableObject
{
    private readonly Func<int?> _pid;
    private readonly Func<int, ImageSource?>? _picture;
    private PolSocialSnapshot _snap = PolSocialSnapshot.Empty;
    private string? _token;
    internal DateTime _at;
    private HandleChange _op;
    private string _doneText = "";
    private string _signature = "";
    private bool _wasReadOnly;
    private Action<string?>? _after;

    public HandlesViewModel(Func<int?> pid, Func<int, ImageSource?>? picture = null)
    {
        _pid = pid;
        _picture = picture;
    }

    public ObservableCollection<HandleRow> Rows { get; } = new();
    public ObservableCollection<HandleCharacterRow> Unlinked { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Editable))] private bool _loggedIn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ReadOnly), nameof(Editable))] private string? _readOnlyText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Editable))] private bool _busy;
    [ObservableProperty] private string _note = "";
    private bool _open, _noteSeen;
    [ObservableProperty] private bool _noteIsError;
    [ObservableProperty] private bool _hasUnlinked;

    public bool ReadOnly => ReadOnlyText != null;
    public bool Editable => LoggedIn && !ReadOnly && !Busy;
    public string LoggedOutText => HandleList.LoggedOutText;
    public string AutoSwitchText => HandleList.AutoSwitchText;
    public string LimitText => HandleList.LimitText;
    public string NoCharactersText => HandleList.NoCharactersText;
    public bool InChatRoom => _snap.Chat is { RoomState: "in" or "entering" };
    // mine, or one the hook's still running (it can take mine after the page has given up on it)
    public bool Switching => (Busy && _op == HandleChange.Switch) ||
                             (LoggedIn && _snap is { HandleOp: "switch", HandleState: "working" });

    // title, question, OK label -> yes
    public Func<string, string, string, bool>? Confirm { get; set; }

    // why a switch can't go right now (something open that's bound to the active handle), or null
    public Func<string?>? SwitchBlocked { get; set; }

    // readOnly is the line saying why, null when changes can be made
    public void Update(PolSocialSnapshot? snap, bool loggedIn, string? readOnly)
    {
        LoggedIn = loggedIn && snap != null;
        ReadOnlyText = readOnly;
        // logged out, the last account's handles go with it
        _snap = LoggedIn ? snap! : PolSocialSnapshot.Empty;
        if (!LoggedIn && _token != null)
        {
            // the session that had it is gone, and so is anything that could say how it went
            _token = null;
            NoteIsError = true;
            Note = _op == HandleChange.Switch ? "The PlayOnline session ended before Provenance could confirm the switch."
                                              : "The PlayOnline session ended before Provenance could confirm the change.";
            After(Note);
        }
        // the wait doesn't count while the game's starting, nothing can be changed then
        if (readOnly != null || _wasReadOnly) _at = DateTime.UtcNow;
        _wasReadOnly = readOnly != null;
        Settle();
        Busy = _token != null;
        Rebuild();
    }

    // polcore gives up on a job after 100 secs
    private void Settle()
    {
        if (_token is null) return;
        var state = _snap.HandleToken == _token ? _snap.HandleState : "";
        if (state != "saved" && !state.StartsWith("failed", StringComparison.Ordinal))
        {
            // once the hook's taken it, it's the hook's answer that ends it
            if (state == "working" || DateTime.UtcNow - _at < TimeSpan.FromSeconds(110)) return;
            if (_pid() is { } pid) SocialSnapshot.CancelRequest(pid, "handle");
            state = "timeout";
        }
        _token = null;
        NoteIsError = state != "saved";
        Note = state == "saved" ? _doneText : HandleList.Failed(_op, state);
        After(state == "saved" ? null : Note);
    }

    private void After(string? failed)
    {
        var after = _after;
        _after = null;
        after?.Invoke(failed);
    }

    private void Rebuild()
    {
        var (groups, unlinked) = HandleList.Build(_snap);
        bool edit = Editable;
        var sig = $"{edit}|" + string.Join("|", groups.Select(g =>
                      $"{g.Handle.Slot}/{g.Handle.Name}/{g.Handle.Key}/{g.Handle.ProfilePictureId}/{g.Handle.Active}/{g.Full}/" +
                      string.Join(",", g.Characters.Select(c => c.Record.Key + ":" + c.Label)))) +
                  "|u:" + string.Join(",", unlinked.Select(c => c.Record.Key + ":" + c.Label + ":" + c.Orphan));
        if (sig == _signature) return;
        _signature = sig;
        Rows.Clear();
        foreach (var g in groups)
            Rows.Add(new HandleRow(g, _picture?.Invoke(g.Handle.ProfilePictureId),
                                   g.Characters.Select(c => new HandleCharacterRow(c, g.Handle, edit)).ToList(), edit));
        Unlinked.Clear();
        foreach (var c in unlinked) Unlinked.Add(new HandleCharacterRow(c, null, edit));
        HasUnlinked = Unlinked.Count > 0;
    }

    // the handles a character could go to, with the full ones greyed
    public IReadOnlyList<HandleRow> LinkTargets(HandleCharacterRow c) =>
        c.Character.Orphan ? [] : Rows.Where(r => r.Handle.Slot != c.Handle?.Slot).ToList();

    // the game's window is up: the hook waited for the change before it booted FFXI, but its answer went with the
    // snapshot, so all that's known is that it wasn't confirmed here
    public void GameStarted()
    {
        if (_token is null) return;
        _token = null;
        Busy = false;
        NoteIsError = true;
        Note = HandleList.GameStartedText;
        After(Note);
        Rebuild();
    }

    // Link to... with nowhere to go says so rather than opening nothing
    public void NoTarget()
    {
        NoteIsError = false;
        Note = HandleList.NoTargetText;
    }

    public void Switch(HandleRow row)
    {
        if (!Editable || row.Active) return;
        if (SwitchBlocked?.Invoke() is { } why)
        {
            NoteIsError = true;
            Note = why;
            return;
        }
        if (Confirm?.Invoke("Switch Handle", HandleList.SwitchQuestion(row.Handle, InChatRoom), "SWITCH") != true) return;
        Send(HandleChange.Switch, row.Handle, null, HandleList.Done(HandleChange.Switch, row.Name, ""));
    }

    partial void OnNoteChanged(string value) => _noteSeen = _open;

    // Settings is opening: an answer that came in while it was shut shows this once, one already seen there goes
    public void Opened()
    {
        if (!Busy && _noteSeen)
        {
            Note = "";
            NoteIsError = false;
        }
        _open = true;
        _noteSeen = true;
    }

    public void Closed() => _open = false;

    // a group's chat only opens under the handle I joined it with
    public void OpenGroup(PolGroup group, Func<string, bool> ask, Action open, Action<string> tell)
    {
        if (!LoggedIn)
        {
            tell(HandleList.NotLoggedInText);
            return;
        }
        if (GroupHandleCheck.For(_snap, group) is not { } check)
        {
            open();
            return;
        }
        var why = ReadOnlyText ?? check.Problem ?? (Busy ? HandleList.PageBusyText : null)
                  ?? SwitchBlocked?.Invoke();
        if (why != null)
        {
            tell(why);
            return;
        }
        if (!ask(check.Question)) return;
        Send(HandleChange.Switch, check.To!, null, HandleList.Done(HandleChange.Switch, check.To!.Name, ""));
        if (_token is null) tell(Note);
        else _after = failed => { if (failed is null) open(); else tell(failed); };
    }

    public void Link(HandleCharacterRow c, HandleRow to)
    {
        if (!c.CanLink || !Editable || c.Handle?.Slot == to.Handle.Slot) return;
        if (to.Full)
        {
            NoteIsError = true;
            Note = HandleList.LimitText;
            return;
        }
        var question = HandleList.LinkQuestion(c.Character, to.Handle, _snap.Handles);
        if (Confirm?.Invoke("Link", question, "LINK") != true) return;
        Send(HandleChange.Link, to.Handle, c.Character.Record, HandleList.Done(HandleChange.Link, to.Name, c.Label));
    }

    public void Unlink(HandleCharacterRow c)
    {
        if (!Editable || c.Handle is not { } from) return;
        if (Confirm?.Invoke("Unlink", HandleList.UnlinkQuestion(c.Character, from), "UNLINK") != true) return;
        Send(HandleChange.Unlink, from, c.Character.Record, HandleList.Done(HandleChange.Unlink, from.Name, c.Label));
    }

    private void Send(HandleChange op, PolHandle handle, PolContentId? content, string done)
    {
        _after = null;
        var token = Guid.NewGuid().ToString("N")[..12];
        if (_pid() is not { } pid || !SocialSnapshot.ChangeHandle(pid, op, token, handle, content))
        {
            NoteIsError = true;
            Note = "Provenance couldn't pass the change to PlayOnline.";
            return;
        }
        _token = token;
        _at = DateTime.UtcNow;
        _op = op;
        _doneText = done;
        NoteIsError = false;
        Note = op == HandleChange.Switch ? "Updating online status..." : "Updating Content ID list...";
        Busy = true;
        Rebuild();
    }
}
