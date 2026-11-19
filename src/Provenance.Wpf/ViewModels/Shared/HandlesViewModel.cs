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
    public bool ShowMoves { get; init; }
    public bool CanMoveUp { get; init; }
    public bool CanMoveDown { get; init; }
}

public sealed record HandleRow(HandleGroup Group, ImageSource? Picture, IReadOnlyList<HandleCharacterRow> Characters,
                               bool Editable)
{
    public PolHandle Handle => Group.Handle;
    public string Name => Handle.Name;
    public bool Active => Handle.Active;
    public bool CanSwitch => Editable && !Handle.Active;
    public bool CanDelete { get; init; }
    public bool Full => Group.Full;
    public bool NoCharacters => Characters.Count == 0;
}

public partial class HandlesViewModel : ObservableObject
{
    private readonly IDialogs _dialogs;
    private readonly Func<int?> _pid;
    private readonly Func<int, ImageSource?>? _picture;
    private readonly SessionState? _session;
    private readonly Func<PolSocialSnapshot>? _snapshot;
    private readonly TimeProvider _time;
    private readonly Func<int, string, string>? _text;
    private PolSocialSnapshot _snap = PolSocialSnapshot.Empty;
    private readonly HookJobWatch _watch = new();
    private DateTime Now => _time.GetUtcNow().UtcDateTime;
    private HandleChange _op;
    private string _doneText = "";
    private string _signature = "";
    private bool _wasReadOnly;
    private Action<string?>? _after;
    private List<string>? _draft;
    private string _draftFrom = "";

    public HandlesViewModel(IDialogs dialogs, Func<int?> pid, Func<int, ImageSource?>? picture = null, SessionState? session = null,
                            Func<PolSocialSnapshot>? snapshot = null, TimeProvider? time = null,
                            Func<int, string, string>? text = null)
    {
        _time = time ?? TimeProvider.System;
        _text = text;
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

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Editable), nameof(CanCreate))] private bool _loggedIn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ReadOnly), nameof(Editable), nameof(CanCreate))] private string? _readOnlyText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Editable), nameof(CanCreate), nameof(Ordering))] private bool _busy;
    public StatusLine Note { get; } = new();
    private bool _open, _noteSeen;
    [ObservableProperty] private bool _hasUnlinked;

    public bool ReadOnly => ReadOnlyText != null;
    public bool Editable => LoggedIn && !ReadOnly && !Busy;
    private bool CanChange => Editable && _draft is null;
    public bool CanCreate => CanChange;
    public bool Ordering => _draft is not null && !(Busy && _op == HandleChange.Reorder);
    public string OrderChangedText => HandleList.OrderChangedText;
    public string LoggedOutText => HandleList.LoggedOutText;
    public string AutoSwitchText => HandleList.AutoSwitchText;
    public string LimitText => HandleList.LimitText;
    public string NoCharactersText => HandleList.NoCharactersText;
    public bool InChatRoom => _snap.Chat is { RoomJob.State: "in" or "entering" };
    public bool Switching => (Busy && _op == HandleChange.Switch) ||
                             (LoggedIn && _snap is { HandleOp: "switch", HandleJob.State: "working" });

    public bool ListEditGoing => (Busy && _op is HandleChange.Create or HandleChange.Delete) ||
                                 (LoggedIn && _snap is { HandleOp: "create" or "delete", HandleJob.State: "working" });

    public Func<string?>? SwitchBlocked { get; set; }

    public void Refresh()
    {
        if (_session is not { } session || _snapshot is null) return;
        bool on = session.LoggedIn && session.Pid is not null;
        Update(on ? _snapshot() : null, on,
               session.GameRunning ? HandleList.ReadOnlyText : session.InMultiRun ? HandleList.MultiRunText : null);
    }

    public void Update(PolSocialSnapshot? snap, bool loggedIn, string? readOnly)
    {
        LoggedIn = loggedIn && snap != null;
        ReadOnlyText = readOnly;
        _snap = LoggedIn ? snap! : PolSocialSnapshot.Empty;
        if (!LoggedIn && _watch.Running)
        {
            _watch.Forget();
            Note.Fail(_op == HandleChange.Switch ? "The PlayOnline session ended before Provenance could confirm the switch."
                                                 : "The PlayOnline session ended before Provenance could confirm the change.");
            After(Note.Text);
        }
        if (_draft is not null && (!LoggedIn || readOnly != null)) ClearDraft();
        else if (Ordering && Characters(_snap) != _draftFrom)
        {
            ClearDraft();
            Note.Fail(HookFailures.Handles.ListChanged);
        }
        if (readOnly != null || _wasReadOnly) _watch.Restart(Now);
        _wasReadOnly = readOnly != null;
        Settle();
        Busy = _watch.Running;
        Rebuild();
    }

    // polcore gives up on a job after 100s
    private void Settle()
    {
        if (!_watch.Running) return;
        var state = _snap.HandleJob.StateFor(_watch.Token) ?? "";
        if (state != "saved" && !state.StartsWith("failed", StringComparison.Ordinal))
        {
            if (state == "working" || !_watch.Reached(Now)) return;
            if (_pid() is { } pid) HookFiles.Discard(pid, HookContract.Requests.Handle);
            state = "timeout";
        }
        _watch.Forget();
        if (_op == HandleChange.Reorder) ClearDraft();
        Note.Set(state == "saved" ? _doneText : HandleList.Failed(_op, state, _text), state != "saved");
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
        var (groups, unlinked) = HandleList.Build(_snap, _draft);
        bool edit = CanChange, move = Editable;
        bool delete = edit && groups.Any(g => g.Handle.Active);
        var sig = $"{edit}/{move}|" + string.Join("|", groups.Select(g =>
                      $"{g.Handle.Slot}/{g.Handle.Name}/{g.Handle.Key}/{g.Handle.ProfilePictureId}/{g.Handle.Active}/{g.Full}/" +
                      string.Join(",", g.Characters.Select(c => c.Record.Key + ":" + c.Label)))) +
                  "|u:" + string.Join(",", unlinked.Select(c => c.Record.Key + ":" + c.Label + ":" + c.Orphan));
        if (sig == _signature) return;
        _signature = sig;
        Rows.Clear();
        foreach (var g in groups)
        {
            var named = g.Characters.Where(c => c.Named).ToList();
            HandleCharacterRow Character(HandleCharacter c)
            {
                int at = named.Count > 1 ? named.IndexOf(c) : -1;
                return new(c, g.Handle, edit) { ShowMoves = at >= 0, CanMoveUp = move && at > 0, CanMoveDown = move && at >= 0 && at < named.Count - 1 };
            }
            Rows.Add(new HandleRow(g, _picture?.Invoke(g.Handle.ProfilePictureId), g.Characters.Select(Character).ToList(), edit) { CanDelete = delete });
        }
        Unlinked.Clear();
        foreach (var c in unlinked) Unlinked.Add(new HandleCharacterRow(c, null, edit));
        HasUnlinked = Unlinked.Count > 0;
    }

    public IReadOnlyList<MenuAction> LinkMenu(HandleCharacterRow c) =>
        LinkTargets(c).Select(to => new MenuAction(to.Name, !to.Full, () => Link(c, to)) { Detail = to.Full ? HandleList.LimitText : "" })
                      .ToList();

    public IReadOnlyList<HandleRow> LinkTargets(HandleCharacterRow c) =>
        c.Character.Orphan ? [] : Rows.Where(r => r.Handle.Slot != c.Handle?.Slot).ToList();

    public void GameStarted()
    {
        if (!_watch.Running) return;
        _watch.Forget();
        if (_draft is not null) ClearDraft();
        Busy = false;
        Note.Fail(HandleList.GameStartedText);
        After(Note.Text);
        Rebuild();
    }

    public void NoTarget()
    {
        Note.Show(HandleList.NoTargetText);
    }

    private static IEnumerable<PolContentId> Ffxi(PolSocialSnapshot snap) =>
        HandleOrder.Listed(snap.ContentIds).Where(c => c.Type == ContentIdType.Ffxi);

    private static string Characters(PolSocialSnapshot snap) => string.Join(",", Ffxi(snap).Select(c => $"{c.Key}:{c.Handle}:{c.Order}"));

    private void ClearDraft()
    {
        _draft = null;
        OnPropertyChanged(nameof(Ordering));
        OnPropertyChanged(nameof(CanCreate));
    }

    [RelayCommand]
    public void MoveUp(HandleCharacterRow c) => Move(c, -1);

    [RelayCommand]
    public void MoveDown(HandleCharacterRow c) => Move(c, 1);

    private void Move(HandleCharacterRow c, int by)
    {
        if (!Editable || Rows.FirstOrDefault(r => r.Handle.Slot == c.Handle?.Slot) is not { } row) return;
        var named = row.Characters.Where(x => x.ShowMoves).Select(x => x.Character.Record.Key).ToList();
        int at = named.IndexOf(c.Character.Record.Key);
        if (at < 0 || at + by < 0 || at + by >= named.Count) return;
        var saved = Ffxi(_snap).Select(x => x.Key).ToList();
        var keys = new List<string>(_draft ?? saved);
        int from = keys.IndexOf(named[at]), to = keys.IndexOf(named[at + by]);
        if (from < 0 || to < 0) return;
        (keys[from], keys[to]) = (keys[to], keys[from]);
        if (_draft is null) _draftFrom = Characters(_snap);
        _draft = keys.SequenceEqual(saved) ? null : keys;
        OnPropertyChanged(nameof(Ordering));
        OnPropertyChanged(nameof(CanCreate));
        Rebuild();
    }

    [RelayCommand]
    public void SaveOrder()
    {
        if (!Editable || _draft is not { } draft) return;
        var changes = HandleOrder.Changes(_snap.ContentIds, draft);
        if (changes.Count > 0)
        {
            Send(HandleChange.Reorder, (pid, token) => SocialRequests.ReorderContentIds(pid, token, changes),
                 HandleList.Done(HandleChange.Reorder, "", ""));
            OnPropertyChanged(nameof(Ordering));
            return;
        }
        ClearDraft();
        Rebuild();
    }

    [RelayCommand]
    public void CancelOrder()
    {
        if (_draft is null || !Ordering) return;
        ClearDraft();
        Rebuild();
    }

    [RelayCommand]
    public void Switch(HandleRow row)
    {
        if (!CanChange || row.Active) return;
        if (SwitchBlocked?.Invoke() is { } why)
        {
            Note.Fail(why);
            return;
        }
        StartSwitch(row);
    }

    private bool StartSwitch(HandleRow row)
    {
        if (!_dialogs.Confirm("Switch Handle", HandleList.SwitchQuestion(row.Handle, InChatRoom), "SWITCH")) return false;
        Send(HandleChange.Switch, (pid, token) => SocialRequests.SwitchHandle(pid, token, row.Handle),
             HandleList.Done(HandleChange.Switch, row.Name, ""));
        return true;
    }

    private string? SwitchRefusal() =>
        ReadOnlyText ?? (Busy || Switching ? HandleList.PageBusyText : null) ?? SwitchBlocked?.Invoke();

    public IReadOnlyList<MenuAction> SwitchMenu(Action<string> failed, Action<string>? refused = null)
    {
        if (!LoggedIn || Rows.Count == 0) return [];
        var why = SwitchRefusal();
        var menu = new List<MenuAction> { new(HandleList.SwitchMenuTitle, false) { Detail = why ?? "" }, MenuAction.Separator };
        foreach (var row in Rows)
        {
            int slot = row.Handle.Slot;
            // Detail makes the name a text block, a plain header would lose an underscore to the access keys
            menu.Add(new(row.Name, why is null, row.Active ? null : () => SwitchFromMenu(slot, failed, refused ?? failed)) { Checked = row.Active, Detail = "" });
        }
        return menu;
    }

    private void SwitchFromMenu(int slot, Action<string> failed, Action<string> refused)
    {
        if (!LoggedIn || Rows.FirstOrDefault(r => r.Handle.Slot == slot) is not { Active: false } row) return;
        if (SwitchRefusal() is { } why)
        {
            refused(why);
            return;
        }
        if (!StartSwitch(row)) return;
        if (!_watch.Running) failed(Note.Text);
        else _after = text => { if (text is not null) failed(text); };
    }

    public void NewLogin()
    {
        Note.Clear();
        _noteSeen = false;
    }

    public void Opened()
    {
        if (!Busy && _noteSeen)
        {
            Note.Clear();
        }
        _open = true;
        _noteSeen = true;
    }

    public void Closed()
    {
        _open = false;
        if (!Ordering) return;
        ClearDraft();
        Rebuild();
    }

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
        if (!_watch.Running) tell(Note.Text);
        else _after = failed => { if (failed is null) open(); else tell(failed); };
    }

    public void Link(HandleCharacterRow c, HandleRow to)
    {
        if (!c.CanLink || !CanChange || c.Handle?.Slot == to.Handle.Slot) return;
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
    public void Create()
    {
        if (!CanCreate) return;
        if (_snap.Handles.Count >= HandleList.MaxHandles)
        {
            Note.Fail(HandleList.Say(HandleList.HandlesFull, _text));
            return;
        }
        var title = HandleList.Say(HandleList.CreateTitle, _text);
        var name = _dialogs.Ask(title, HandleList.Say(HandleList.EnterName, _text) + "\n\n" + HandleList.CreateHelpText,
                                maxLength: PolLimits.HandleName, validate: HandleList.NameProblem);
        if (name is null || HandleList.NameProblem(name) is not null) return;
        if (!_dialogs.Confirm(title, HandleList.CreateQuestion(name), "CREATE") || !CanCreate) return;
        Send(HandleChange.Create, (pid, token) => SocialRequests.CreateHandle(pid, token, name), HandleList.Done(HandleChange.Create, name, ""));
    }

    [RelayCommand]
    public void Delete(HandleRow row)
    {
        HandleRow? Current() => Rows.FirstOrDefault(r => r.Handle.Slot == row.Handle.Slot && r.Handle.Key == row.Handle.Key);
        if (!CanChange || Current() is not { CanDelete: true } now) return;
        if (now.Active)
        {
            Note.Fail(HandleList.Say(HandleList.DeleteActive, _text));
            return;
        }
        if (!_dialogs.Confirm(HandleList.DeleteTitle, HandleList.DeleteQuestion, "DELETE", ConfirmStyle.Destructive) || !CanChange) return;
        if (Current() is not { CanDelete: true, Active: false } still || still.Name != now.Name)
        {
            Note.Fail(HookFailures.Handles.ListChanged);
            return;
        }
        Send(HandleChange.Delete, (pid, token) => SocialRequests.DeleteHandle(pid, token, now.Handle),
             HandleList.Done(HandleChange.Delete, now.Name.Trim(), ""));
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
        _watch.Begin(token, HookLimits.HandleUntaken, Now);
        _op = op;
        _doneText = done;
        Note.Show(op == HandleChange.Switch ? "Updating online status..."
                  : op is HandleChange.Create or HandleChange.Delete ? HandleList.Say(HandleList.UpdatingText, _text)
                  : "Updating Content ID list...");
        Busy = true;
        Rebuild();
    }
}
