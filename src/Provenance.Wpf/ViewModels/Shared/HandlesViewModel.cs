using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Hook;
using Provenance.Common.Social;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Chat;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf.ViewModels.Shared;

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
    private readonly IDialogs _dialogs;
    private readonly Func<int?> _pid;
    private readonly Func<int, ImageSource?>? _picture;
    private readonly SessionState? _session;
    private readonly Func<PolSocialSnapshot>? _snapshot;
    private PolSocialSnapshot _snap = PolSocialSnapshot.Empty;
    private string? _token;
    internal DateTime _at;
    private HandleChange _op;
    private string _doneText = "";
    private string _signature = "";
    private bool _wasReadOnly;
    private Action<string?>? _after;

    public HandlesViewModel(IDialogs dialogs, Func<int?> pid, Func<int, ImageSource?>? picture = null, SessionState? session = null,
                            Func<PolSocialSnapshot>? snapshot = null)
    {
        _dialogs = dialogs;
        _pid = pid;
        _picture = picture;
        _session = session;
        _snapshot = snapshot;
        if (session is not null) session.PhaseChanged += Refresh;
        Note.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StatusLine.Text)) _noteSeen = _open; };
    }

    public ObservableCollection<HandleRow> Rows { get; } = new();
    public ObservableCollection<HandleCharacterRow> Unlinked { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Editable))] private bool _loggedIn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ReadOnly), nameof(Editable))] private string? _readOnlyText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Editable))] private bool _busy;
    public StatusLine Note { get; } = new();
    private bool _open, _noteSeen;
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
                             (LoggedIn && _snap is { HandleOp: "switch", HandleJob.State: "working" });

    // why a switch can't go right now (something open that's bound to the active handle), or null
    public Func<string?>? SwitchBlocked { get; set; }

    // a multi-account run's lean sessions take no requests
    public void Refresh()
    {
        if (_session is not { } session || _snapshot is null) return;
        bool on = session.LoggedIn && session.Pid is not null;
        Update(on ? _snapshot() : null, on,
               session.GameRunning ? HandleList.ReadOnlyText : session.InMultiRun ? HandleList.MultiRunText : null);
    }

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
            Note.Fail(_op == HandleChange.Switch ? "The PlayOnline session ended before Provenance could confirm the switch."
                                                 : "The PlayOnline session ended before Provenance could confirm the change.");
            After(Note.Text);
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
        var state = _snap.HandleJob.Token == _token ? _snap.HandleJob.State : "";
        if (state != "saved" && !state.StartsWith("failed", StringComparison.Ordinal))
        {
            // once the hook's taken it, it's the hook's answer that ends it
            if (state == "working" || DateTime.UtcNow - _at < TimeSpan.FromSeconds(110)) return;
            if (_pid() is { } pid) HookFiles.Discard(pid, HookContract.Requests.Handle);
            state = "timeout";
        }
        _token = null;
        Note.Set(state == "saved" ? _doneText : HandleList.Failed(_op, state), state != "saved");
        After(state == "saved" ? null : Note.Text);
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

    // the handles a character could go to, a full one greyed out with the limit under its name
    public IReadOnlyList<MenuAction> LinkMenu(HandleCharacterRow c) =>
        LinkTargets(c).Select(to => new MenuAction(to.Name, !to.Full, () => Link(c, to)) { Detail = to.Full ? HandleList.LimitText : "" })
                      .ToList();

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
        Note.Fail(HandleList.GameStartedText);
        After(Note.Text);
        Rebuild();
    }

    // Link to... with nowhere to go says so rather than opening nothing
    public void NoTarget()
    {
        Note.Show(HandleList.NoTargetText);
    }

    [RelayCommand]
    public void Switch(HandleRow row)
    {
        if (!Editable || row.Active) return;
        if (SwitchBlocked?.Invoke() is { } why)
        {
            Note.Fail(why);
            return;
        }
        if (!_dialogs.Confirm("Switch Handle", HandleList.SwitchQuestion(row.Handle, InChatRoom), "SWITCH")) return;
        Send(HandleChange.Switch, (pid, token) => SocialRequests.SwitchHandle(pid, token, row.Handle),
             HandleList.Done(HandleChange.Switch, row.Name, ""));
    }

    // Settings is opening: an answer that came in while it was shut shows this once, one already seen there goes
    public void Opened()
    {
        if (!Busy && _noteSeen)
        {
            Note.Clear();
        }
        _open = true;
        _noteSeen = true;
    }

    public void Closed() => _open = false;

    // a group's chat only opens under the handle I joined it with
    public void OpenGroup(PolGroup group, Action open)
    {
        void tell(string text) => _dialogs.Info(GroupChatViewModel.DialogTitle, text);
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
        if (!_dialogs.YesNo(GroupHandleCheck.Title, check.Question)) return;
        Send(HandleChange.Switch, (pid, token) => SocialRequests.SwitchHandle(pid, token, check.To!),
             HandleList.Done(HandleChange.Switch, check.To!.Name, ""));
        if (_token is null) tell(Note.Text);
        else _after = failed => { if (failed is null) open(); else tell(failed); };
    }

    public void Link(HandleCharacterRow c, HandleRow to)
    {
        if (!c.CanLink || !Editable || c.Handle?.Slot == to.Handle.Slot) return;
        if (to.Full)
        {
            Note.Fail(HandleList.LimitText);
            return;
        }
        var question = HandleList.LinkQuestion(c.Character, to.Handle, _snap.Handles);
        if (!_dialogs.Confirm("Link", question, "LINK")) return;
        Send(HandleChange.Link, (pid, token) => SocialRequests.LinkCharacter(pid, token, to.Handle, c.Character.Record),
             HandleList.Done(HandleChange.Link, to.Name, c.Label));
    }

    [RelayCommand]
    public void Unlink(HandleCharacterRow c)
    {
        if (!Editable || c.Handle is not { } from) return;
        if (!_dialogs.Confirm("Unlink", HandleList.UnlinkQuestion(c.Character, from), "UNLINK")) return;
        Send(HandleChange.Unlink, (pid, token) => SocialRequests.UnlinkCharacter(pid, token, from, c.Character.Record),
             HandleList.Done(HandleChange.Unlink, from.Name, c.Label));
    }

    private void Send(HandleChange op, Func<int, string, bool> write, string done)
    {
        _after = null;
        var token = HookFiles.NewToken();
        if (_pid() is not { } pid || !write(pid, token))
        {
            Note.Fail("Provenance couldn't pass the change to PlayOnline.");
            return;
        }
        _token = token;
        _at = DateTime.UtcNow;
        _op = op;
        _doneText = done;
        Note.Show(op == HandleChange.Switch ? "Updating online status..." : "Updating Content ID list...");
        Busy = true;
        Rebuild();
    }
}
