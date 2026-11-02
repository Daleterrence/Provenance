using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Social;
using HandleChange = Provenance.Common.Social.SocialSnapshot.HandleChange;

namespace Provenance.Wpf.ViewModels;

public sealed record HandleCharacterRow(HandleCharacter Character, PolHandle? Handle, bool Editable)
{
    public string Label => Character.Label;
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

// Settings > Handles. Everything shown comes from the hook's snapshot, and a change is one request the hook checks again
public partial class HandlesViewModel : ObservableObject
{
    private readonly Func<int?> _pid;
    private readonly Func<int, ImageSource?>? _picture;
    private PolSocialSnapshot _snap = PolSocialSnapshot.Empty;
    private string? _token;
    private DateTime _at;
    private HandleChange _op;
    private string _doneText = "";
    private string _signature = "";

    public HandlesViewModel(Func<int?> pid, Func<int, ImageSource?>? picture = null)
    {
        _pid = pid;
        _picture = picture;
    }

    public ObservableCollection<HandleRow> Rows { get; } = new();
    public ObservableCollection<HandleCharacterRow> Unlinked { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowList))] private bool _loggedIn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Editable))] private bool _readOnly;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Editable))] private bool _busy;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private bool _noteIsError;
    [ObservableProperty] private bool _hasUnlinked;

    public bool ShowList => LoggedIn;
    public bool Editable => LoggedIn && !ReadOnly && !Busy;
    public string LoggedOutText => HandleList.LoggedOutText;
    public string ReadOnlyText => HandleList.ReadOnlyText;
    public string AutoSwitchText => HandleList.AutoSwitchText;
    public string LimitText => HandleList.LimitText;
    public string NoCharactersText => HandleList.NoCharactersText;

    // title, question, OK label -> yes
    public Func<string, string, string, bool>? Confirm { get; set; }

    public void Update(PolSocialSnapshot? snap, bool loggedIn, bool readOnly)
    {
        LoggedIn = loggedIn && snap != null;
        ReadOnly = readOnly;
        if (snap != null) _snap = snap;
        if (!LoggedIn) _token = null;
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
            if (DateTime.UtcNow - _at < TimeSpan.FromSeconds(110)) return;
            if (_pid() is { } pid) SocialSnapshot.CancelRequest(pid, "handle");
            state = "timeout";
        }
        _token = null;
        NoteIsError = state != "saved";
        Note = state == "saved" ? _doneText : HandleList.Failed(_op, state);
    }

    private void Rebuild()
    {
        var (groups, unlinked) = HandleList.Build(_snap);
        bool edit = Editable;
        var sig = $"{edit}|" + string.Join("|", groups.Select(g =>
                      $"{g.Handle.Slot}/{g.Handle.Name}/{g.Handle.Key}/{g.Handle.ProfilePictureId}/{g.Handle.Active}/{g.Full}/" +
                      string.Join(",", g.Characters.Select(c => c.Record.Key + ":" + c.Label)))) +
                  "|u:" + string.Join(",", unlinked.Select(c => c.Record.Key + ":" + c.Label));
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
        Rows.Where(r => r.Handle.Slot != c.Handle?.Slot).ToList();

    public void Switch(HandleRow row)
    {
        if (!Editable || row.Active) return;
        if (Confirm?.Invoke("Switch Handle", HandleList.SwitchQuestion(row.Handle), "SWITCH") != true) return;
        Send(HandleChange.Switch, row.Handle, null, HandleList.Done(HandleChange.Switch, row.Name, ""));
    }

    public void Link(HandleCharacterRow c, HandleRow to)
    {
        if (!Editable || c.Handle?.Slot == to.Handle.Slot) return;
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
        if (_pid() is not { } pid) return;
        var token = Guid.NewGuid().ToString("N")[..12];
        if (!SocialSnapshot.ChangeHandle(pid, op, token, handle, content))
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
