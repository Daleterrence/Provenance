using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Hook;
using Provenance.Common.PolFormat;
using Provenance.Common.Social;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Shared;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.ViewModels.Chat;

public sealed record GroupChatLineItem(string Id, string Name, string Text, bool Me, DateTimeOffset When)
{
    public string Display => $"{Name}: {Text}";
    public string TimeText => When.ToString("HH:mm");
}

public sealed record GroupChatMemberItem(string Name, bool Me, string Note);

public sealed partial class GroupChatViewModel : ObservableObject
{
    public enum Phase { Out, Joining, In }

    public const string DialogTitle = "Group Chat";
    public const string ExitQuestion = "Exit Group Chat?";
    public const string FilteredNote = "Group chat can't send some of those characters, so they were left out.";

    private readonly ISocialSession _social;
    private readonly IDialogs _dialogs;
    private readonly Func<bool> _timesWanted, _flashWanted;
    private readonly ChatSendGate _gate = new(HookLimits.GroupChatLine);
    private GroupChatLog? _log;
    private readonly HookJobWatch _join = new();
    private long _seenSeq;
    private string? _sentLine;
    private string? _quietlyEnded, _goneQuietly;
    // a chat that's ended or been switched away from: its last lines can still land in the hook's file after that
    private string? _tailGroup;
    private long _tailSeen;
    private DateTime _tailUntil;
    internal static readonly TimeSpan TailFor = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, int> _unread = new(StringComparer.OrdinalIgnoreCase);

    internal GroupChatViewModel(ISocialSession social, IDialogs dialogs, Func<bool> showTimes, Func<bool> flash)
    {
        _social = social;
        _dialogs = dialogs;
        _timesWanted = showTimes;
        _flashWanted = flash;
        _social.SnapshotApplied += OnSnapshot;
        _social.PropertyChanged += OnSocialChanged;
    }

    public ObservableCollection<GroupChatLineItem> Lines { get; } = new();
    public ObservableCollection<GroupChatMemberItem> Members { get; } = new();

    [ObservableProperty] private string _groupId = "";
    [ObservableProperty] private string _groupName = "";
    [ObservableProperty] private string _myHandle = "";
    [ObservableProperty] private string _count = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InChat), nameof(Joining), nameof(Open)), NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private Phase _current = Phase.Out;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _compose = "";
    [ObservableProperty] private string _composeNote = "";
    [ObservableProperty] private bool _showTimes;

    public bool InChat => Current == Phase.In;
    public bool Joining => Current == Phase.Joining;
    public bool Open => Current != Phase.Out;

    public bool WindowActive { get; set; }

    public void RefreshTimes() => ShowTimes = _timesWanted();

    public bool FlashWanted => _flashWanted();

    public void WindowActivated()
    {
        WindowActive = true;
        ShowTimes = _timesWanted();
        Seen();
    }

    public event Action<string, string, string, int>? LineArrived;
    public event Action<string?>? Ended;
    public event Action? UnreadChanged;
    public event Action? TickNeeded;

    public bool NeedsTick => Joining || !_gate.CanSend || _log is { Dirty: true };

    private void WakeTick()
    {
        if (NeedsTick) TickNeeded?.Invoke();
    }

    public int UnreadFor(string groupId) => _unread.GetValueOrDefault(groupId);
    public int UnreadTotal => _unread.Values.Sum();

    public void SetLog(GroupChatLog? log)
    {
        _log = log;
        ClearUnread();
        Lines.Clear();
        GroupId = GroupName = MyHandle = "";
        _tailGroup = null;
        _quietlyEnded = _goneQuietly = null;
    }

    private void ClearUnread()
    {
        if (_unread.Count == 0) return;
        _unread.Clear();
        UnreadChanged?.Invoke();
    }

    public bool Join(PolGroup group)
    {
        if (_social.Session.Pid is not { } pid) return false;
        if (Joining || (Open && SameGroup(group.Id))) return true;
        var mine = group.Members.FirstOrDefault(m => m.Mine);
        var token = HookFiles.NewToken();
        if (!GroupChatFiles.Join(pid, token, group.Id, mine?.Slot is >= 0 and var s ? s : 0, mine?.Hid ?? "")) return false;
        if (Open) KeepTail();
        ReadTail();
        long seen = _social.Last.GroupChat?.LineSeq ?? 0;
        if (_tailGroup is { } t && string.Equals(t, group.Id, StringComparison.OrdinalIgnoreCase))
        {
            seen = Math.Max(seen, _tailSeen);
            _tailGroup = null;
        }
        _log?.Flush();
        _seenSeq = seen;
        _join.Begin(token, HookLimits.GroupChatJoin, _social.Now);
        _quietlyEnded = _goneQuietly = null;
        _gate.Reset();
        GroupId = group.Id;
        GroupName = group.Name;
        MyHandle = mine?.Name ?? _social.AccountName;
        ShowTimes = _timesWanted();
        Current = Phase.Joining;
        ComposeNote = "";
        LoadHistory();
        RefreshMembers(group);
        WakeTick();
        return true;
    }

    private GroupChatLineItem Item(GroupChatEntry e) =>
        new(e.Id, e.Name, e.Me ? e.Text : _social.Viewer.Filtered(e.Text), e.Me, e.When);

    private void LoadHistory()
    {
        Lines.Clear();
        foreach (var e in _log?.Lines(GroupId) ?? []) Lines.Add(Item(e));
        TrimLines();
    }

    private void TrimLines()
    {
        while (Lines.Count > 500) Lines.RemoveAt(0);
    }

    internal void Tell(string text) => _dialogs.Info(DialogTitle, text);

    internal bool ConfirmExit() => _dialogs.YesNo(DialogTitle, ExitQuestion);

    public void Exit()
    {
        if (!Open) return;
        CallOff();
        KeepTail();
        ReadTail();
        End(null);
    }

    private void KeepTail()
    {
        _tailGroup = GroupId;
        _tailSeen = _seenSeq;
        _tailUntil = _social.Now + TailFor;
    }

    private void ReadTail()
    {
        if (_tailGroup is not { } group || _social.Session.Pid is not { } pid) return;
        if (_social.Now > _tailUntil)
        {
            _tailGroup = null;
            return;
        }
        if (GroupChatFiles.TryReadLines(pid) is not { } file || !string.Equals(file.Group, group, StringComparison.OrdinalIgnoreCase)) return;
        var fresh = file.After(_tailSeen);
        if (fresh.Count == 0) return;
        _tailSeen = fresh[^1].Seq;
        _log?.Add(group, fresh.Select(GroupChatEntry.From));
        _log?.Flush();
        WakeTick();
    }

    private void CallOff()
    {
        if (_social.Session.Pid is not { } pid) return;
        bool withdrawn = Current == Phase.Joining &&
                         HookFiles.Withdraw(pid, HookContract.Requests.GroupChatJoin) == Withdrawal.Withdrawn;
        if (!withdrawn) GroupChatFiles.Leave(pid, HookFiles.NewToken(), Current == Phase.Joining ? _join.Token ?? "" : "");
    }

    public void CloseQuietly()
    {
        if (Open) End(null);
        ClearUnread();
    }

    public bool GroupLeft(string groupId, string message)
    {
        _log?.Clear(groupId);
        WakeTick();
        if (_unread.Remove(groupId)) UnreadChanged?.Invoke();
        if (_tailGroup is { } t && string.Equals(t, groupId, StringComparison.OrdinalIgnoreCase)) _tailGroup = null;
        bool endedForIt = _quietlyEnded is { } q && string.Equals(q, groupId, StringComparison.OrdinalIgnoreCase);
        _quietlyEnded = null;
        if (endedForIt)
        {
            Lines.Clear();
            Ended?.Invoke(message);
            return true;
        }
        if (!Open || !SameGroup(groupId)) return false;
        Lines.Clear();
        End(message);
        return true;
    }

    public bool LeaveFailed(string groupId, PolSocialSnapshot snap)
    {
        if (_quietlyEnded is { } q && string.Equals(q, groupId, StringComparison.OrdinalIgnoreCase))
        {
            _quietlyEnded = null;
            return true;
        }
        if (!Open || !SameGroup(groupId) || snap.GroupChat is not { Job.State: "gone" } gc || gc.Job.Token != _join.Token) return false;
        _goneQuietly = groupId;
        return true;
    }

    public void FlushLog() => _log?.Flush();

    internal void DiscardLog(string profileId)
    {
        if (_log?.ProfileId == profileId) _log.Discard();
    }

    private void End(string? message)
    {
        _log?.Flush();
        _join.Forget();
        _gate.Reset();
        _sentLine = null;
        Current = Phase.Out;
        Compose = "";
        ComposeNote = "";
        Members.Clear();
        Count = "";
        Ended?.Invoke(message);
    }

    public void Seen()
    {
        if (GroupId.Length == 0 || !_unread.Remove(GroupId)) return;
        UnreadChanged?.Invoke();
    }

    private bool CanSend => Current == Phase.In && Compose.Trim().Length > 0 && _gate.CanSend &&
                            _social.WriteBlockedNote is null;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = PolCharacters.Keep(Compose.Trim().Replace("\r", "").Replace('\n', ' '), newlines: false).Trim();
        if (text.Length == 0 || _social.Session.Pid is not { } pid) return;
        if (text.Length > GroupChatFiles.MaxText) text = text[..GroupChatFiles.MaxText];
        var token = HookFiles.NewToken();
        if (!GroupChatFiles.Send(pid, token, text))
        {
            ComposeNote = HookFailures.GroupChat.SendFailed;
            return;
        }
        _gate.Sent(token, Compose, _social.Now);
        WakeTick();
        _sentLine = text;
        Compose = "";
        ComposeNote = "";
        SendCommand.NotifyCanExecuteChanged();
    }

    public ComposeRules ComposeRules => _rules ??= new() { SingleLine = true, MaxChars = GroupChatFiles.MaxText, Filter = FilterInput };
    private ComposeRules? _rules;

    public string FilterInput(string text)
    {
        var kept = PolCharacters.Keep(text, newlines: false, out bool dropped);
        if (dropped) ComposeNote = FilteredNote;
        return kept;
    }

    public void Tick()
    {
        var now = _social.Now;
        SendCommand.NotifyCanExecuteChanged();
        _log?.SaveIfDue(now);
        if (_gate.Expire(now) is { } done) FinishSend(done.State, done.Text);
        if (Current != Phase.Joining || !_join.Reached(now)) return;
        CallOff();
        End(_social.Viewer.Text(0x3eb8, HookFailures.GroupChat.Busy));
    }

    public static string ClearQuestion(string group) =>
        $"Delete every line of the \"{group}\" group chat that Provenance has kept on this PC? This can't be undone.";

    public IReadOnlyList<MenuAction> MoreMenu() =>
        [new("Clear chat...", Lines.Count > 0, () =>
        {
            var shown = Lines.Select(l => l.Id).ToList();
            if (_dialogs.Confirm("Clear chat", ClearQuestion(GroupName), "CLEAR", ConfirmStyle.Destructive)) ClearLines(shown);
        })];

    public IReadOnlyList<MenuAction> LineMenu(GroupChatLineItem line) => [new("Delete message", true, () => DeleteLine(line))];

    public bool DeleteLine(GroupChatLineItem line)
    {
        if (_log is null || !_log.Delete(GroupId, line.Id)) return false;
        WakeTick();
        Lines.Remove(line);
        return true;
    }

    public void ClearLines(IReadOnlyCollection<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        _log?.Delete(GroupId, set);
        WakeTick();
        foreach (var l in Lines.Where(l => set.Contains(l.Id)).ToList()) Lines.Remove(l);
    }

    private void OnSocialChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ISocialSession.HandleSwitching)) SendCommand.NotifyCanExecuteChanged();
        if (e.PropertyName == nameof(ISocialSession.GameRunning) && _social.GameRunning && Open) End(null);
    }

    private void OnSnapshot(PolSocialSnapshot snap)
    {
        ReadTail();
        if (snap.GroupChat is not { } gc || !_join.Running || gc.Job.Token != _join.Token)
        {
            if (Open && snap.GroupList.FirstOrDefault(g => SameGroup(g.Id)) is { } still) RefreshMembers(still);
            return;
        }
        switch (gc.Job.State)
        {
            case "joining":
                break;
            case "in":
                if (Current != Phase.In) Current = Phase.In;
                ReadLines(gc);
                ApplySend(gc);
                break;
            case "left" or "left:handle" or "lost":
                ReadLines(gc);
                End(null);
                return;
            case "gone" or "removed":
                ReadLines(gc);
                if (_social.LeavingGroupId is { } leaving && SameGroup(leaving))
                {
                    _quietlyEnded = GroupId;
                    End(null);
                    return;
                }
                if (_goneQuietly is { } g && SameGroup(g))
                {
                    _goneQuietly = null;
                    End(null);
                    return;
                }
                End(gc.Job.State == "gone"
                    ? _social.Viewer.Text(0x3ebb, "You are not a member of the group \"%s.\" Exiting Group Chat.").Replace("%s", GroupName)
                    : _social.Viewer.Text(0x3ebc, "You have been removed from the group \"%s.\" Exiting Group Chat.").Replace("%s", GroupName));
                return;
            default:
                if (!gc.Job.State.StartsWith("failed", StringComparison.Ordinal)) break;
                End(gc.Job.State switch
                {
                    "failed:busy" => _social.Viewer.Text(0x3eb8, HookFailures.GroupChat.Busy),
                    "failed:not a member" or "failed:no such group" => _social.Viewer.Text(0x3eb7, HookFailures.GroupChat.NotMember),
                    "failed:handle changing" => HookFailures.GroupChat.JoinHandleChanging,
                    "failed:handle" or "failed:list changed" => HookFailures.GroupChat.HandlesChanged,
                    _ => _social.Viewer.Text(0x3eb9, HookFailures.GroupChat.JoinFailed),
                });
                return;
        }
        if (snap.GroupList.FirstOrDefault(g => SameGroup(g.Id)) is { } group) RefreshMembers(group);
    }

    private bool SameGroup(string id) => string.Equals(id, GroupId, StringComparison.OrdinalIgnoreCase);

    private void ReadLines(GroupChatSnapshot gc)
    {
        if (gc.LineSeq <= _seenSeq || _social.Session.Pid is not { } pid) return;
        if (GroupChatFiles.TryReadLines(pid) is not { } file || !SameGroup(file.Group)) return;
        var fresh = file.After(_seenSeq);
        if (fresh.Count == 0) return;
        _seenSeq = fresh[^1].Seq;
        var entries = fresh.Select(GroupChatEntry.From).ToList();
        _log?.Add(GroupId, entries);
        WakeTick();
        var shown = entries.Select(Item).ToList();
        foreach (var e in shown) Lines.Add(e);
        TrimLines();
        var theirs = shown.Where(e => !e.Me).ToList();
        if (theirs.Count == 0 || WindowActive) return;
        _unread[GroupId] = _unread.GetValueOrDefault(GroupId) + theirs.Count;
        UnreadChanged?.Invoke();
        LineArrived?.Invoke(GroupName, theirs[^1].Name, theirs[^1].Text, theirs.Count);
    }

    private void ApplySend(GroupChatSnapshot gc)
    {
        if (_gate.Settle(gc.SendJob.Token, gc.SendJob.State, _social.Now) is { } done) FinishSend(done.State, done.Text);
    }

    private void FinishSend(string done, string text)
    {
        if (done == "sent:unechoed" && _sentLine is { } line)
        {
            var e = GroupChatEntry.Mine(MyHandle, line);
            _log?.Add(GroupId, [e]);
            WakeTick();
            Lines.Add(Item(e));
            TrimLines();
        }
        else if (done != "sent")
        {
            ComposeNote = done == "failed:characters" ? HookFailures.GroupChat.Characters
                : HookFailures.ForChatLine(done) ?? _social.Viewer.Text(0x3eba, HookFailures.GroupChat.SendFailed);
            if (Compose.Length == 0) Compose = text;
        }
        _sentLine = null;
        SendCommand.NotifyCanExecuteChanged();
    }

    private void RefreshMembers(PolGroup group)
    {
        if (!Open) return;
        var me = group.Members.FirstOrDefault(m => m.Mine) ?? group.Members.FirstOrDefault(m => m.Me);
        var now = new List<GroupChatMemberItem>();
        if (me != null) now.Add(new GroupChatMemberItem(me.Name, true, "You"));
        if (InChat)
            foreach (var m in group.Members.Where(m => m.InChat && !ReferenceEquals(m, me))
                                           .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
                now.Add(new GroupChatMemberItem(m.Name, false, ""));
        if (!Members.SequenceEqual(now))
        {
            Members.Clear();
            foreach (var m in now) Members.Add(m);
        }
        Count = $"{now.Count:00}/{group.Members.Count:00}";
        GroupName = group.Name;
    }
}
