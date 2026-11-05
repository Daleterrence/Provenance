using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.PolFormat;
using Provenance.Common.Social;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.ViewModels.Chat;

// Text is what's shown (filtered, unless it's mine), the log keeps what came in
public sealed record GroupChatLineItem(string Id, string Name, string Text, bool Me, DateTimeOffset When)
{
    // the viewer's own log line, "<handle>: <text>"
    public string Display => $"{Name}: {Text}";
    public string TimeText => When.ToString("t", CultureInfo.CurrentCulture);
}

public sealed record GroupChatMemberItem(string Name, bool Me, string Note);

// a friend group's chat, one at a time like the viewer. Close only hides it, it stays in till Exit, a handle switch,
// the game, logging out or the group going
public sealed partial class GroupChatViewModel : ObservableObject
{
    public enum Phase { Out, Joining, In }

    public const string ExitQuestion = "Exit Group Chat?";
    public const string BusyText = "Unable to join Group Chat. The server is busy.";
    public const string JoinFailedText = "Unable to join Group Chat.";
    public const string NotMemberText = "You are not a member of the selected group.";
    public const string SendFailedText = "Unable to send chat message.";
    public const string FilteredNote = "Group chat can't send some of those characters, so they were left out.";
    public const string CharactersText = "Group chat can't send some of those characters.";
    // a little past the hook's own 30 s, for a join or a line it never answered
    internal static readonly TimeSpan JoinGiveUp = TimeSpan.FromSeconds(35);
    internal static readonly TimeSpan SendGiveUp = TimeSpan.FromSeconds(35);

    private readonly SocialViewModel _social;
    private readonly Func<bool> _timesWanted;
    private readonly ChatSendGate _gate = new(SendGiveUp);
    private GroupChatLog? _log;
    private string? _token;
    private long _seenSeq;
    private DateTime _joinAt;
    private string? _sentLine;
    private string? _quietlyEnded, _goneQuietly;
    // a chat that's ended or been switched away from: its last lines can still land in the hook's file after that
    private string? _tailGroup;
    private long _tailSeen;
    private DateTime _tailUntil;
    // the hook writes its file at most once a second, so a few seconds covers the last of it
    internal static readonly TimeSpan TailFor = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, int> _unread = new(StringComparer.OrdinalIgnoreCase);

    public GroupChatViewModel(SocialViewModel social, Func<bool> showTimes)
    {
        _social = social;
        _timesWanted = showTimes;
        _social.SnapshotApplied += OnSnapshot;
        _social.PropertyChanged += OnSocialChanged;
    }

    public ObservableCollection<GroupChatLineItem> Lines { get; } = new();
    public ObservableCollection<GroupChatMemberItem> Members { get; } = new();

    // the group's id as the snapshot has it, its name and the handle I'm in it with
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

    // the window's on screen and in front, so new lines are read as they land
    public bool WindowActive { get; set; }

    // lines from someone else while the window's hidden or behind, once a batch: the group's name, the last one's
    // sender and text, and how many came
    public event Action<string, string, string, int>? LineArrived;
    // the chat's over: null when there's nothing to say about it, otherwise the viewer's own words for why
    public event Action<string?>? Ended;
    public event Action? UnreadChanged;

    public int UnreadFor(string groupId) => _unread.GetValueOrDefault(groupId);
    public int UnreadTotal => _unread.Values.Sum();

    // a new login's log, and nothing unread carried over from the last
    public void SetLog(GroupChatLog? log)
    {
        _log = log;
        ClearUnread();
    }

    private void ClearUnread()
    {
        if (_unread.Count == 0) return;
        _unread.Clear();
        UnreadChanged?.Invoke();
    }

    private static string NewToken() => Guid.NewGuid().ToString("N")[..12];

    // the handle question's already been asked by the time this runs (HandlesViewModel.OpenGroup). One still joining
    // just has its window shown again, like the viewer
    public bool Join(PolGroup group)
    {
        if (_social.SessionPid is not { } pid) return false;
        if (Joining || (Open && SameGroup(group.Id))) return true;
        var mine = group.Members.FirstOrDefault(m => m.Mine);
        var token = NewToken();
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
        _token = token;
        _seenSeq = seen;
        _joinAt = DateTime.UtcNow;
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
        return true;
    }

    private GroupChatLineItem Item(GroupChatEntry e) =>
        new(e.Id, e.Name, e.Me ? e.Text : _social.Filtered(e.Text), e.Me, e.When);

    private void LoadHistory()
    {
        Lines.Clear();
        foreach (var e in _log?.Lines(GroupId) ?? []) Lines.Add(Item(e));
    }

    // the viewer's Exit, after its question
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
        _tailUntil = DateTime.UtcNow + TailFor;
    }

    // what the hook still has of a chat that's over goes in that group's kept lines, nothing on screen
    private void ReadTail()
    {
        if (_tailGroup is not { } group || _social.SessionPid is not { } pid) return;
        if (DateTime.UtcNow > _tailUntil)
        {
            _tailGroup = null;
            return;
        }
        if (GroupChatFiles.TryReadLines(pid) is not { } file || !string.Equals(file.Group, group, StringComparison.OrdinalIgnoreCase)) return;
        var fresh = file.Lines.Where(l => l.Seq > _tailSeen).OrderBy(l => l.Seq).ToList();
        if (fresh.Count == 0) return;
        _tailSeen = fresh[^1].Seq;
        _log?.Add(group, fresh.Select(l => new GroupChatEntry(Guid.NewGuid().ToString("N")[..12],
                                                              l.Time > 0 ? l.Time : DateTimeOffset.UtcNow.ToUnixTimeSeconds(), l.Name, l.Text, l.Me)));
        _log?.Flush();
    }

    private void CallOff()
    {
        if (_social.SessionPid is not { } pid) return;
        bool withdrawn = Current == Phase.Joining &&
                         SocialRequests.WithdrawRequest(pid, "groupchatjoin") == Withdrawal.Withdrawn;
        if (!withdrawn) GroupChatFiles.Leave(pid, NewToken(), Current == Phase.Joining ? _token ?? "" : "");
    }

    // the session's over (logged out, POL closed), nothing to send
    public void CloseQuietly()
    {
        if (Open) End(null);
        ClearUnread();
    }

    // leaving or disbanding the group from the Groups tab: its chat goes with it, and so do the lines kept for it
    public bool GroupLeft(string groupId, string message)
    {
        _log?.Clear(groupId);
        if (_unread.Remove(groupId)) UnreadChanged?.Invoke();
        // and the hook's last lines for it don't go back in a log that's just been cleared
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

    // a leave or disband that didn't go: true when its chat's already closed, or closing in this same snapshot
    public bool LeaveFailed(string groupId, PolSocialSnapshot snap)
    {
        if (_quietlyEnded is { } q && string.Equals(q, groupId, StringComparison.OrdinalIgnoreCase))
        {
            _quietlyEnded = null;
            return true;
        }
        if (!Open || !SameGroup(groupId) || snap.GroupChat is not { State: "gone" } gc || gc.Token != _token) return false;
        _goneQuietly = groupId;
        return true;
    }

    private void End(string? message)
    {
        _log?.Flush();
        _token = null;
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
                            !_social.GameRunning && !_social.HandleSwitching;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = ShiftJis.Filter(Compose.Trim().Replace("\r", "").Replace('\n', ' '), out _);
        if (text.Length == 0 || _social.SessionPid is not { } pid) return;
        if (text.Length > GroupChatFiles.MaxText) text = text[..GroupChatFiles.MaxText];
        var token = NewToken();
        if (!GroupChatFiles.Send(pid, token, text))
        {
            ComposeNote = SendFailedText;
            return;
        }
        _gate.Sent(token, Compose, DateTime.UtcNow);
        _sentLine = text;
        Compose = "";
        ComposeNote = "";
        SendCommand.NotifyCanExecuteChanged();
    }

    // typing or pasting: what Shift-JIS can't carry is dropped, with a word about it
    public string FilterInput(string text)
    {
        var kept = ShiftJis.Filter(text, out bool dropped);
        if (dropped) ComposeNote = FilteredNote;
        return kept;
    }

    // the window's half-second tick: Send's button, a join or a line that never got an answer, and the log's save
    public void Tick(DateTime now)
    {
        SendCommand.NotifyCanExecuteChanged();
        _log?.SaveIfDue(now);
        if (_gate.Expire(now) is { } done) Settled(done.State, done.Text);
        if (Current != Phase.Joining || now - _joinAt < JoinGiveUp) return;
        CallOff();
        End(_social.ViewerText(0x3eb8, BusyText));
    }

    public bool DeleteLine(GroupChatLineItem line)
    {
        if (_log is null || !_log.Delete(GroupId, line.Id)) return false;
        Lines.Remove(line);
        return true;
    }

    // only the lines that were on screen when Clear was picked, not ones that came in while it asked
    public void ClearLines(IReadOnlyCollection<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        _log?.Delete(GroupId, set);
        foreach (var l in Lines.Where(l => set.Contains(l.Id)).ToList()) Lines.Remove(l);
    }

    private void OnSocialChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SocialViewModel.HandleSwitching)) SendCommand.NotifyCanExecuteChanged();
        // the hook leaves for the game, the window goes with it
        if (e.PropertyName == nameof(SocialViewModel.GameRunning) && _social.GameRunning && Open) End(null);
    }

    private void OnSnapshot(PolSocialSnapshot snap)
    {
        ReadTail();
        if (snap.GroupChat is not { } gc || _token is null || gc.Token != _token)
        {
            if (Open && snap.GroupList.FirstOrDefault(g => SameGroup(g.Id)) is { } still) RefreshMembers(still);
            return;
        }
        switch (gc.State)
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
                // my own leave or disband from the Groups tab, which comes before its answer, and that one says it
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
                End(gc.State == "gone"
                    ? _social.ViewerText(0x3ebb, "You are not a member of the group \"%s.\" Exiting Group Chat.").Replace("%s", GroupName)
                    : _social.ViewerText(0x3ebc, "You have been removed from the group \"%s.\" Exiting Group Chat.").Replace("%s", GroupName));
                return;
            default:
                if (!gc.State.StartsWith("failed", StringComparison.Ordinal)) break;
                End(gc.State switch
                {
                    "failed:busy" => _social.ViewerText(0x3eb8, BusyText),
                    "failed:not a member" or "failed:no such group" => _social.ViewerText(0x3eb7, NotMemberText),
                    "failed:handle changing" => "Your handle was changing, so the group chat wasn't joined.",
                    "failed:handle" or "failed:list changed" => "Your handles changed, so the group chat wasn't joined. Try again.",
                    _ => _social.ViewerText(0x3eb9, JoinFailedText),
                });
                return;
        }
        if (snap.GroupList.FirstOrDefault(g => SameGroup(g.Id)) is { } group) RefreshMembers(group);
    }

    private bool SameGroup(string id) => string.Equals(id, GroupId, StringComparison.OrdinalIgnoreCase);

    private void ReadLines(GroupChatSnapshot gc)
    {
        if (gc.LineSeq <= _seenSeq || _social.SessionPid is not { } pid) return;
        if (GroupChatFiles.TryReadLines(pid) is not { } file || !SameGroup(file.Group)) return;
        var fresh = file.Lines.Where(l => l.Seq > _seenSeq).OrderBy(l => l.Seq).ToList();
        if (fresh.Count == 0) return;
        _seenSeq = fresh[^1].Seq;
        var entries = fresh.Select(l => new GroupChatEntry(Guid.NewGuid().ToString("N")[..12],
                                                           l.Time > 0 ? l.Time : DateTimeOffset.UtcNow.ToUnixTimeSeconds(), l.Name, l.Text, l.Me))
                           .ToList();
        _log?.Add(GroupId, entries);
        var shown = entries.Select(Item).ToList();
        foreach (var e in shown) Lines.Add(e);
        var theirs = shown.Where(e => !e.Me).ToList();
        if (theirs.Count == 0 || WindowActive) return;
        _unread[GroupId] = _unread.GetValueOrDefault(GroupId) + theirs.Count;
        UnreadChanged?.Invoke();
        LineArrived?.Invoke(GroupName, theirs[^1].Name, theirs[^1].Text, theirs.Count);
    }

    private void ApplySend(GroupChatSnapshot gc)
    {
        if (_gate.Settle(gc.SendToken, gc.SendState, DateTime.UtcNow) is { } done) Settled(done.State, done.Text);
    }

    private void Settled(string done, string text)
    {
        // it went, but with no row of mine in the group polcore never hands my copy back, so it's shown from here
        if (done == "sent:unechoed" && _sentLine is { } line)
        {
            var e = new GroupChatEntry(Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow.ToUnixTimeSeconds(), MyHandle, line, true);
            _log?.Add(GroupId, [e]);
            Lines.Add(Item(e));
        }
        else if (done != "sent")
        {
            ComposeNote = done switch
            {
                "failed:too long" => "That's too long for one line.",
                "failed:characters" => CharactersText,
                "failed:handle changing" => "Not sent: your handle was changing.",
                "failed:timeout" => "PlayOnline didn't answer, so that line may not have gone.",
                _ => _social.ViewerText(0x3eba, SendFailedText),
            };
            // the line goes back in the box, like the viewer keeps it till it's gone
            if (Compose.Length == 0) Compose = text;
        }
        _sentLine = null;
        SendCommand.NotifyCanExecuteChanged();
    }

    // me first, then whoever's in the chat, the viewer's "in chat / members" count
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
