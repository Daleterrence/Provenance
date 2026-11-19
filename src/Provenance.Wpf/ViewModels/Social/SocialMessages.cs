using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.PolFormat;
using Provenance.Common.Social;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Social;

public sealed partial class SocialMessages : ObservableObject
{
    private readonly SocialViewModel _social;
    private readonly Func<FriendItem?> _friend;
    private bool _conversationVisible;
    private DateTime _conversationDay;
    private string? _listedFor;
    private string? _draftFor;
    private FriendItem? _draftPerson;

    public SocialMessages(SocialViewModel social, Func<FriendItem?> friend)
    {
        _social = social;
        _friend = friend;
    }

    public SocialViewModel Social => _social;
    public FriendItem? Friend => _friend();

    public ObservableCollection<ChatEntry> Conversation { get; } = new();
    internal MailSendQueue Sends => _social.Login.Sends;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(ComposeBytes), nameof(ComposeCount), nameof(ComposeCountShown), nameof(ComposeNote))]
    private string _compose = "";

    public ComposeRules ComposeRules { get; } = new() { MaxBytes = () => PolMessage.MaxTextBytes };
    public int ComposeBytes => ShiftJis.ByteCount(Compose.Trim());
    public string ComposeCount => $"{ComposeBytes}/{PolMessage.MaxTextBytes}";
    public bool ComposeCountShown => ComposeBytes >= PolMessage.MaxTextBytes * 9 / 10;
    private bool ComposeHasWide => ComposeBytes > Compose.Trim().Replace("\r", "").Length;

    public bool CanCompose => Friend is { CanMessage: true } f && _social.Friends.Any(i => i.Is(f)) && !_social.GameRunning && !_social.HandleSwitching;

    private Dictionary<string, string> Drafts => _social.Login.Drafts;
    private static string DraftKey(FriendItem f) =>
        f.Key.Length > 0 ? "k:" + f.Key : $"n:{f.Name.ToUpperInvariant()}:{f.Row}";

    public string ComposeNote => Friend switch
    {
        null => "",
        _ when _social.GameRunning => "While FINAL FANTASY XI is running, replies go through the game.",
        _ when _social.HandleSwitching => SocialViewModel.SwitchingNote,
        { CanMessage: true } when ComposeCountShown && ComposeHasWide =>
            ShiftJis.HasDoubleByte(Compose) ? "Japanese characters count as two." : "Some characters count as two.",
        { CanMessage: true, Online: true } => "This sends a PlayOnline message.",
        { CanMessage: true } f => $"{f.Name}'s offline, so they'll get this next time they log in.",
        { Pending: true } f => $"You can message {f.Name} once they accept your friend request.",
        { IsRequest: true } f => $"Accept {f.Name}'s friend request to send them messages.",
        { IsGroupInvite: true } => "You can't reply to a group invitation, only accept or decline it.",
        var f => $"PlayOnline messages only go to friends. Add {f.Name} as a friend to reply.",
    };

    public bool ConversationVisible
    {
        get => _conversationVisible;
        set
        {
            if (_conversationVisible == value) return;
            _conversationVisible = value;
            OnPropertyChanged();
            if (value && Friend is { } f && _social.Chat is { } chat && chat.UnreadFrom(f.Name, f.Key) > 0)
                ReadConversation(f, rebuild: true);
        }
    }

    internal bool DayChanged => _conversationDay != DateTime.Now.Date;

    internal void Reset()
    {
        _draftFor = null;
        _draftPerson = null;
        Compose = "";
        _listedFor = null;
        Conversation.Clear();
    }

    internal void StateChanged()
    {
        OnPropertyChanged(nameof(CanCompose));
        OnPropertyChanged(nameof(ComposeNote));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void ReadConversation(FriendItem f, bool rebuild)
    {
        _social.Login.Sync.NoteRead(_social.Chat!.MarkRead(f.Name, f.Key));
        _social.FlushReads();
        _social.UnreadCount = _social.Chat.Unread;
        _social.MarkFriendsDirty();
        if (rebuild) _social.RebuildFriends();
    }

    internal void Release()
    {
        if (_draftFor is not null)
        {
            if (string.IsNullOrWhiteSpace(Compose)) Drafts.Remove(_draftFor);
            else Drafts[_draftFor] = Compose;
        }
        _draftFor = null;
        _draftPerson = null;
    }

    internal void SelectionChanged(FriendItem? value)
    {
        OnPropertyChanged(nameof(Friend));
        OnPropertyChanged(nameof(ComposeNote));
        var key = value is null ? null : DraftKey(value);
        bool samePerson = value != null && _draftPerson != null && value.Is(_draftPerson) &&
                          (value.Key.Length > 0 || _draftPerson.Key.Length > 0 || key == _draftFor);
        if (samePerson)
        {
            if (_draftFor != null && key != _draftFor && Drafts.Remove(_draftFor, out var moved))
                Drafts[key!] = moved;
        }
        else if (_draftFor != null)
        {
            if (string.IsNullOrWhiteSpace(Compose)) Drafts.Remove(_draftFor);
            else Drafts[_draftFor] = Compose;
        }
        _draftPerson = value;
        _draftFor = key;
        if (!samePerson) Compose = key != null && Drafts.TryGetValue(key, out var draft) ? draft : "";
        if (value != null && _social.Chat != null && value.Unread > 0 && ConversationVisible)
            ReadConversation(value, rebuild: !_social.ApplyingSnapshot);
        Refresh();
        OnPropertyChanged(nameof(CanCompose));
        SendCommand.NotifyCanExecuteChanged();
    }

    internal void Refresh()
    {
        _conversationDay = DateTime.Now.Date;
        if (Friend is not { } f || _social.Chat is not { } chat)
        {
            _listedFor = null;
            Conversation.Clear();
            return;
        }
        var who = f.Key.Length > 0 ? "k:" + f.Key : "n:" + f.Name.ToUpperInvariant();
        if (who != _listedFor)
        {
            _listedFor = who;
            Conversation.Clear();
        }
        var next = new List<ChatEntry>();
        DateTime? day = null;
        foreach (var line in chat.With(f.Name, f.Key))
        {
            var e = line.Outgoing ? line
                  : line with { Subject = _social.Viewer.Filtered(line.Subject), Text = _social.Viewer.Filtered(line.Text) };
            var d = e.When.Date;
            next.Add(d == day ? e : e with { DayBreak = DayLabel(d) });
            day = d;
        }
        Conversation.Patch(next, (a, b) => a.Key == b.Key, (a, b) => a == b);
    }

    private static string DayLabel(DateTime d)
    {
        var today = DateTime.Now.Date;
        if (d == today) return "Today";
        if (d == today.AddDays(-1)) return "Yesterday";
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        return d.Year == today.Year ? d.ToString("dddd d MMMM", culture) : d.ToString("d MMMM yyyy", culture);
    }

    private bool CanSend() =>
        CanCompose && !string.IsNullOrWhiteSpace(Compose) && ComposeBytes <= PolMessage.MaxTextBytes && _social.Session.Pid != null;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        if (!CanSend() || Friend is not { } f || _social.Chat == null) return;
        var text = Compose.Trim();
        if (text.Length == 0) return;
        Queue(f, text);
        Compose = "";
        if (_social.Chat.UnreadFrom(f.Name, f.Key) > 0) ReadConversation(f, rebuild: false);
        Refresh();
    }

    public void RemoveFailedLine(ChatEntry e)
    {
        if (e.Failed && _social.Chat is { } chat && chat.RemoveFailed(e.Key)) Refresh();
    }

    public void DeleteLine(ChatEntry e)
    {
        if (_social.Chat is not { } chat || !ChatStore.CanDelete(e) || !chat.Delete(e.Key)) return;
        chat.Flush();
        _social.DeleteStoreFiles([e]);
        AfterDelete();
    }

    public void ClearConversation(IReadOnlyCollection<string> keys)
    {
        if (_social.Chat is not { } chat) return;
        var set = keys.ToHashSet(StringComparer.Ordinal);
        var gone = chat.Entries.Where(e => set.Contains(e.Key) && ChatStore.CanDelete(e)).ToList();
        if (chat.DeleteKeys(keys) == 0) return;
        chat.Flush();
        _social.DeleteStoreFiles(gone);
        AfterDelete();
    }

    private void AfterDelete()
    {
        _social.RefreshConversations();
        _social.UnreadCount = _social.Chat?.Unread ?? 0;
        _social.MarkFriendsDirty();
        _social.RebuildFriends();
    }

    public void Resend(ChatEntry e)
    {
        if (!e.Failed || _social.Chat is not { } chat || !CanCompose || Friend is not { } f || _social.Session.Pid is null) return;
        if (!chat.RemoveFailed(e.Key)) return;
        Queue(f, e.Text);
        Refresh();
    }

    private void Queue(FriendItem f, string text)
    {
        if (Sends.Add(_social.Chat!, f.Row, f.Name, f.Key, text, _social.Session.Pid, _social.GameRunning || _social.HandleSwitching))
            Refresh();
    }

    public void SettleSends(SessionEnd end)
    {
        Sends.Settle(_social.Chat, end != SessionEnd.Dropped, end == SessionEnd.Play ? "failed:game" : "failed:session");
        _social.SaveChat();
        _social.RefreshConversations();
    }

    public static string ClearConversationQuestion(string name, bool gameRunning = false) => gameRunning
        ? $"Delete every message with {name}? FINAL FANTASY XI and PlayOnline Viewer keep their copies while the game's running. This can't be undone."
        : $"Delete every message with {name}? They're deleted from FINAL FANTASY XI and PlayOnline Viewer as well. This can't be undone.";

    public IReadOnlyList<MenuAction> LineMenu(ChatEntry entry) =>
        [new("Delete message", ChatStore.CanDelete(entry), () => DeleteLine(entry))];

    public IReadOnlyList<MenuAction> ConversationMenu(FriendItem f, Action<FriendItem>? openConversation = null)
    {
        var menu = _social.FriendMenu(f, openConversation).ToList();
        if (menu.Count > 0) menu.Add(MenuAction.Separator);
        menu.Add(new("Clear conversation...", Conversation.Any(ChatStore.CanDelete), () =>
        {
            var shown = Conversation.Select(e => e.Key).ToList();
            if (_social.ConfirmDestructive("Clear conversation", ClearConversationQuestion(f.Name, _social.GameRunning), "CLEAR")) ClearConversation(shown);
        }));
        return menu;
    }
}
