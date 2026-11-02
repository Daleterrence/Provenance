using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels;

public sealed record GroupChatLineItem(string Id, string Name, string Text, bool Me, DateTimeOffset When)
{
    // the viewer's own log line, "<handle>: <text>"
    public string Display => $"{Name}: {Text}";
    public string TimeText => When.ToString("HH:mm");
}

public sealed record GroupChatMemberItem(string Name, bool Me, string Note);

// a friend group's chat, one at a time like the viewer. It stays in with its window hidden (Close) and lines keep
// coming in, till Exit, a handle switch, the game, logging out or the group going. Every line's kept on this PC
public sealed partial class GroupChatViewModel : ObservableObject
{
    public enum Phase { Out, Joining, In }

    public const string ExitQuestion = "Exit Group Chat?";
    public const string BusyText = "Unable to join Group Chat. The server is busy.";
    public const string JoinFailedText = "Unable to join Group Chat.";
    public const string NotMemberText = "You are not a member of the selected group.";
    public const string SendFailedText = "Unable to send chat message.";
    public const string FilteredNote = "Group chat can't send some of those characters, so they were left out.";

    private readonly SocialViewModel _social;
    private readonly Func<bool> _timesWanted;
    private readonly ChatSendGate _gate = new();
    private GroupChatLog? _log;
    private string? _token;
    private long _seenSeq;
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

    // a line from someone else while the window's hidden or behind: the group's name, who and what
    public event Action<string, string, string>? LineArrived;
    // the chat's over: null when there's nothing to say about it, otherwise the viewer's own words for why
    public event Action<string?>? Ended;
    public event Action? UnreadChanged;

    public int UnreadFor(string groupId) => _unread.GetValueOrDefault(groupId);
    public int UnreadTotal => _unread.Values.Sum();

    public void SetLog(GroupChatLog? log) => _log = log;

    private static string NewToken() => Guid.NewGuid().ToString("N")[..12];

    // the handle question's already been asked by the time this runs (HandlesViewModel.OpenGroup)
    public bool Join(PolGroup group)
    {
        if (_social.SessionPid is not { } pid) return false;
        if (Open && string.Equals(GroupId, group.Id, StringComparison.OrdinalIgnoreCase)) return true;
        var mine = group.Members.FirstOrDefault(m => m.Mine);
        var token = NewToken();
        if (!GroupChatFiles.Join(pid, token, group.Id, mine?.Slot is >= 0 and var s ? s : 0, mine?.Hid ?? "")) return false;
        _token = token;
        _seenSeq = _social.Last.GroupChat?.LineSeq ?? 0;
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

    private void LoadHistory()
    {
        Lines.Clear();
        foreach (var e in _log?.Lines(GroupId) ?? []) Lines.Add(new GroupChatLineItem(e.Id, e.Name, e.Text, e.Me, e.When));
    }

    // the viewer's Exit, after its question
    public void Exit()
    {
        if (!Open) return;
        if (_social.SessionPid is { } pid)
        {
            bool withdrawn = Current == Phase.Joining &&
                             SocialSnapshot.WithdrawRequest(pid, "groupchatjoin") == SocialSnapshot.Withdrawal.Withdrawn;
            if (!withdrawn) GroupChatFiles.Leave(pid, NewToken(), Current == Phase.Joining ? _token ?? "" : "");
        }
        End(null);
    }

    // the session's over (logged out, PlayOnline closed), nothing to send
    public void CloseQuietly() => End(null);

    // leaving the group from the Groups tab: its chat goes with it, and so do the lines kept for it
    public bool GroupLeft(string groupId, string message)
    {
        _log?.Clear(groupId);
        if (_unread.Remove(groupId)) UnreadChanged?.Invoke();
        if (!Open || !string.Equals(GroupId, groupId, StringComparison.OrdinalIgnoreCase)) return false;
        Lines.Clear();
        End(message);
        return true;
    }

    private void End(string? message)
    {
        _token = null;
        _gate.Reset();
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

    private bool CanSend => Current == Phase.In && Compose.Trim().Length > 0 && _gate.CanSend(DateTime.UtcNow) &&
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
        _gate.Sent(token, DateTime.UtcNow);
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

    public void SendTick() => SendCommand.NotifyCanExecuteChanged();

    public bool DeleteLine(GroupChatLineItem line)
    {
        if (_log is null || !_log.Delete(GroupId, line.Id)) return false;
        Lines.Remove(line);
        return true;
    }

    public void ClearLines()
    {
        _log?.Clear(GroupId);
        Lines.Clear();
        if (_unread.Remove(GroupId)) UnreadChanged?.Invoke();
    }

    private void OnSocialChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SocialViewModel.HandleSwitching)) SendCommand.NotifyCanExecuteChanged();
        // the hook leaves for the game, the window goes with it
        if (e.PropertyName == nameof(SocialViewModel.GameRunning) && _social.GameRunning && Open) End(null);
    }

    private void OnSnapshot(PolSocialSnapshot snap)
    {
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
            case "gone":
                ReadLines(gc);
                End(_social.ViewerText(0x3ebb, "You are not a member of the group \"%s.\" Exiting Group Chat.")
                                           .Replace("%s", GroupName));
                return;
            case "removed":
                ReadLines(gc);
                End(_social.ViewerText(0x3ebc, "You have been removed from the group \"%s.\" Exiting Group Chat.").Replace("%s", GroupName));
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
        var entries = fresh.Select(l => new GroupChatEntry(Guid.NewGuid().ToString("N")[..12], l.Time > 0 ? l.Time : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                                           l.Name, l.Me ? l.Text : _social.Filtered(l.Text), l.Me)).ToList();
        _log?.Add(GroupId, entries);
        foreach (var e in entries) Lines.Add(new GroupChatLineItem(e.Id, e.Name, e.Text, e.Me, e.When));
        var theirs = entries.Where(e => !e.Me).ToList();
        if (theirs.Count == 0 || WindowActive) return;
        _unread[GroupId] = _unread.GetValueOrDefault(GroupId) + theirs.Count;
        UnreadChanged?.Invoke();
        foreach (var e in theirs) LineArrived?.Invoke(GroupName, e.Name, e.Text);
    }

    private void ApplySend(GroupChatSnapshot gc)
    {
        var done = _gate.Settle(gc.SendToken, gc.SendState, DateTime.UtcNow);
        if (done is null) return;
        if (done != "sent")
            ComposeNote = done switch
            {
                "failed:too long" => "That's too long for one line.",
                "failed:characters" => FilteredNote,
                "failed:handle changing" => "Not sent: your handle was changing.",
                _ => _social.ViewerText(0x3eba, SendFailedText),
            };
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
