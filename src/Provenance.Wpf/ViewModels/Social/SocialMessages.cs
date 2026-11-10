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
    private bool _conversationVisible;
    private DateTime _conversationDay;   // the day the "Today" headings were made on

    public SocialMessages(SocialViewModel social) => _social = social;

    public ObservableCollection<ChatEntry> Conversation { get; } = new();
    internal MailSendQueue Sends => _social.Login.Sends;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(ComposeBytes), nameof(ComposeCount), nameof(ComposeCountShown), nameof(ComposeNote))]
    private string _compose = "";

    // the box counts in the bytes FFXI goes by, a Japanese character's two
    public ComposeRules ComposeRules { get; } = new() { MaxBytes = () => PolMessage.MaxTextBytes };
    public int ComposeBytes => ShiftJis.ByteCount(Compose.Trim());
    public string ComposeCount => $"{ComposeBytes}/{PolMessage.MaxTextBytes}";
    public bool ComposeCountShown => ComposeBytes >= PolMessage.MaxTextBytes * 9 / 10;
    private bool ComposeHasWide => ComposeBytes > Compose.Trim().Replace("\r", "").Length;

    public bool CanCompose => _social.SelectedFriend is { CanMessage: true } && !_social.GameRunning && !_social.HandleSwitching;

    // a draft per conversation, so it never goes to the next person picked (POL mail can't be called back)
    private sealed class PerLogin
    {
        public readonly Dictionary<string, string> Drafts = new();
        public string? DraftFor;
        public FriendItem? DraftPerson;
    }

    private PerLogin _login = new();
    // keyless people can share a name, so the row's in there too
    private static string DraftKey(FriendItem f) =>
        f.Key.Length > 0 ? "k:" + f.Key : $"n:{f.Name.ToUpperInvariant()}:{f.Row}";

    public string ComposeNote => _social.SelectedFriend switch
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
            if (value && _social.SelectedFriend is { } f && _social.Chat is { } chat && chat.UnreadFrom(f.Name, f.Key) > 0)
                ReadConversation(f, rebuild: true);
        }
    }

    internal bool DayChanged => _conversationDay != DateTime.Now.Date;

    internal void Reset()
    {
        _login = new();
        Compose = "";
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
        _social.Chat!.MarkRead(f.Name, f.Key);
        _social.UnreadCount = _social.Chat.Unread;
        _social.MarkFriendsDirty();
        if (rebuild) _social.RebuildFriends();
    }

    internal void SelectionChanged(FriendItem? value)
    {
        OnPropertyChanged(nameof(ComposeNote));
        var key = value is null ? null : DraftKey(value);
        // a presence update gives the same person a new row and the text goes with them. Keyless needs the same row too
        bool samePerson = value != null && _login.DraftPerson != null && value.Is(_login.DraftPerson) &&
                          (value.Key.Length > 0 || _login.DraftPerson.Key.Length > 0 || key == _login.DraftFor);
        if (samePerson)
        {
            if (_login.DraftFor != null && key != _login.DraftFor && _login.Drafts.Remove(_login.DraftFor, out var moved))
                _login.Drafts[key!] = moved;
        }
        else if (_login.DraftFor != null)
        {
            if (string.IsNullOrWhiteSpace(Compose)) _login.Drafts.Remove(_login.DraftFor);
            else _login.Drafts[_login.DraftFor] = Compose;
        }
        _login.DraftPerson = value;
        _login.DraftFor = key;
        if (!samePerson) Compose = key != null && _login.Drafts.TryGetValue(key, out var draft) ? draft : "";
        // picking someone in the main window shows nothing, so it stays unread until the friends window shows it
        if (value != null && _social.Chat != null && value.Unread > 0 && ConversationVisible)
            ReadConversation(value, rebuild: !_social.ApplyingSnapshot);
        Refresh();
        OnPropertyChanged(nameof(CanCompose));
        SendCommand.NotifyCanExecuteChanged();
    }

    internal void Refresh()
    {
        _conversationDay = DateTime.Now.Date;
        Conversation.Clear();
        if (_social.SelectedFriend is not { } f || _social.Chat is not { } chat) return;
        DateTime? day = null;
        foreach (var line in chat.With(f.Name, f.Key))
        {
            var e = line.Outgoing || line.State == "notice" ? line
                  : line with { Subject = _social.Viewer.Filtered(line.Subject), Text = _social.Viewer.Filtered(line.Text) };
            var d = e.When.Date;
            Conversation.Add(d == day ? e : e with { DayBreak = DayLabel(d) });
            day = d;
        }
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

    // POL mail (the viewer's stored send), so it reaches them offline too
    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        if (!CanSend() || _social.SelectedFriend is not { } f || _social.Chat == null) return;
        var text = Compose.Trim();
        if (text.Length == 0) return;
        Queue(f, text);
        Compose = "";
        Refresh();
    }

    public void RemoveFailedLine(ChatEntry e)
    {
        if (e.Failed && _social.Chat is { } chat && chat.RemoveFailed(e.Key)) Refresh();
    }

    // this PC's copy only. It stays gone, the next login's read of the game's store won't bring it back
    public void DeleteLine(ChatEntry e)
    {
        if (_social.Chat is not { } chat || !ChatStore.CanDelete(e) || !chat.Delete(e.Key)) return;
        AfterDelete();
    }

    // only the lines that were on screen when Clear was picked, not ones that landed while it asked
    public void ClearConversation(IReadOnlyCollection<string> keys)
    {
        if (_social.Chat is not { } chat || chat.DeleteKeys(keys) == 0) return;
        AfterDelete();
    }

    private void AfterDelete()
    {
        Refresh();
        _social.UnreadCount = _social.Chat?.Unread ?? 0;
        _social.MarkFriendsDirty();
        _social.RebuildFriends();
    }

    public void Resend(ChatEntry e)
    {
        if (!e.Failed || _social.Chat is not { } chat || !CanCompose || _social.SelectedFriend is not { } f || _social.Session.Pid is null) return;
        if (!chat.RemoveFailed(e.Key)) return;
        Queue(f, e.Text);
        Refresh();
    }

    private void Queue(FriendItem f, string text)
    {
        if (Sends.Add(_social.Chat!, f.Row, f.Name, f.Key, text, _social.Session.Pid, _social.GameRunning || _social.HandleSwitching))
            Refresh();
    }

    // the session's ending and its results won't get here
    public void SettleSends(SessionEnd end)
    {
        Sends.Settle(_social.Chat, end != SessionEnd.Dropped, end == SessionEnd.Play ? "failed:game" : "failed:session");
        Refresh();
    }

    public static string ClearConversationQuestion(string name) =>
        $"Delete every message with {name} that Provenance has kept on this PC? This can't be undone.";

    public IReadOnlyList<MenuAction> LineMenu(ChatEntry entry) =>
        [new("Delete message", ChatStore.CanDelete(entry), () => DeleteLine(entry))];

    public IReadOnlyList<MenuAction> ConversationMenu(FriendItem f, Action<FriendItem>? openConversation = null)
    {
        var menu = _social.FriendMenu(f, openConversation).ToList();
        if (menu.Count > 0) menu.Add(MenuAction.Separator);
        menu.Add(new("Clear conversation...", Conversation.Any(ChatStore.CanDelete), () =>
        {
            var shown = Conversation.Select(e => e.Key).ToList();
            if (_social.ConfirmDestructive("Clear conversation", ClearConversationQuestion(f.Name), "CLEAR")) ClearConversation(shown);
        }));
        return menu;
    }
}
