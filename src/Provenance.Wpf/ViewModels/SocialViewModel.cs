using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.PolFormat;
using Provenance.Common.Social;
using Provenance.Wpf.Services;
using FriendEdit = Provenance.Common.Social.SocialSnapshot.FriendEdit;
using RequestAnswer = Provenance.Common.Social.SocialSnapshot.RequestAnswer;

namespace Provenance.Wpf.ViewModels;

// a friend, or someone who wrote but isn't on the list (Row -1)
// how a session ends for its lines in flight: Play and log out (or exit) let the hook finish them, a drop doesn't
public enum SessionEnd { Play, LogOut, Dropped }

// Playing is the FFXI line next to their name, PlayingShort the same without the world for when that doesn't fit
public sealed record FriendItem(string Name, string Playing, string Comment, string StatusLabel,
                                bool Online, int Row, ImageSource? Avatar, int Unread, string Section)
{
    public string PlayingShort { get; init; } = "";
    public string Key { get; init; } = "";   // the hook's hashed POL ID + handle, empty if it couldn't make one
    public bool Pending { get; init; }
    public int RequestMid { get; init; }
    public bool IsGroupInvite { get; init; }
    public bool IsMe { get; init; }           // one of my own handles on my list, it can only be removed
    public bool IsRequest => RequestMid > 0 && !IsGroupInvite;
    public bool HasUnread => Unread > 0;
    public bool IsFriend => Row >= 0;
    public bool CanMessage => Row >= 0 && !Pending && !IsMe;

    public bool Is(FriendItem other) =>
        Key.Length > 0 && other.Key.Length > 0 ? Key == other.Key
                                               : string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
}

public partial class SocialViewModel : ObservableObject
{
    private readonly PolSessionService _pol;
    private readonly AvatarService _avatars;
    private readonly ISettingsService _settings;
    private ChatStore? _chat;
    private PolSocialSnapshot _last = PolSocialSnapshot.Empty;
    private string _friendsSignature = "";

    public SocialViewModel(PolSessionService pol, AvatarService avatars, ISettingsService settings)
    {
        _pol = pol;
        _avatars = avatars;
        _settings = settings;
        FriendsView = CollectionViewSource.GetDefaultView(Friends);
        FriendsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FriendItem.Section)));
    }

    public ObservableCollection<FriendItem> Friends { get; } = new();
    public ICollectionView FriendsView { get; }
    public ObservableCollection<ChatEntry> Conversation { get; } = new();
    public ObservableCollection<PolBlocked> Blocked { get; } = new();

    [ObservableProperty] private string _accountName = "PlayOnline";
    [ObservableProperty] private ImageSource? _avatar;
    [ObservableProperty] private int _onlineCount;
    [ObservableProperty] private string _myComment = "";
    [ObservableProperty] private bool _hasBlocked;
    [ObservableProperty] private string? _accountNotice;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))] private bool _connected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))] private bool _friendsUnavailable;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RefreshMailCommand))] private bool _messagesUnavailable;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPlay))] private string? _playBlockedReason;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnread), nameof(AttentionCount), nameof(NeedsAttention))]
    private int _unreadCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AttentionCount), nameof(NeedsAttention))]
    private int _pendingAnswers;
    // unread, plus requests no friend list has had on screen yet. The taskbar stays lit while it's above 0
    [ObservableProperty] private int _unseenCount;
    public int MyProfilePictureId { get; private set; }

    public bool HasUnread => UnreadCount > 0;
    // requests go in the badge too, so one's never just an 8 sec banner
    public int AttentionCount => UnreadCount + PendingAnswers;
    public bool NeedsAttention => AttentionCount > 0;
    public bool CanPlay => PolAccountNotices.CanPlay(PlayBlockedReason);
    public string Summary => OnlineCount switch { 0 => "No friends online", 1 => "1 friend online", var n => $"{n} friends online" };
    public bool NoFriends => Connected && !FriendsUnavailable && Friends.Count == 0;
    public string NothingSelected => NoFriends ? NoFriendsText
                                               : "Select a friend to see your messages.";
    public const string NoFriendsText = "You don't have any friends on this PlayOnline account yet.";
    public int? SessionPid => _pol.Pid;

    public event Action? ChangeProfilePictureRequested;
    public event Action<string, string>? MessageReceived;
    public event Action<string, string>? NoticeReceived;
    public event Action<string, int>? FriendRequestReceived;
    public event Action<string, int>? GroupInviteReceived;
    public event Action<string>? FriendCameOnline;          // never for the presence that lands at sign-in
    public event Action<PolSocialSnapshot>? SnapshotApplied;

    [RelayCommand]
    private void ChangeProfilePicture()
    {
        if (GameRunning) EditNote = GameRunningNote;
        else if (!ProfilePictureSaving) ChangeProfilePictureRequested?.Invoke();
    }

    private static string NewToken() => Guid.NewGuid().ToString("N")[..12];

    internal static string FailReason(string state) =>
        state.StartsWith("failed:", StringComparison.Ordinal) ? state[7..] : state;

    // a job's last word (ok or "failed:..."), "timeout" once it's run past secs, or null while it's still going
    private static string? Settled(string? state, string ok, DateTime since, int secs) =>
        state == ok || state?.StartsWith("failed", StringComparison.Ordinal) == true ? state
        : DateTime.UtcNow - since > TimeSpan.FromSeconds(secs) ? "timeout" : null;

    private string? ViewerFolder() =>
        _settings.DetectedRegion is { } r ? PolPaths.TryGetInstallFolder(r, PolPaths.ViewerProduct) : null;

    // the viewer's status menu has these three, only the viewer itself ever sets Busy
    private static readonly PolStatus[] StatusChoices = [PolStatus.Online, PolStatus.Away, PolStatus.AppearOffline];
    public IReadOnlyList<string> StatusLabels { get; } = StatusChoices.Select(PolStatusText.Label).ToList();
    [ObservableProperty] private int _myStatusIndex;
    [ObservableProperty] private string _statusNote = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusEditable))] private bool _statusPending;
    public bool StatusEditable => !StatusPending && !GameRunning;
    private PolStatus? _requestedStatus;
    private DateTime _requestedAt;
    private bool _applyingSnapshot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(NoSelection), nameof(CanCompose))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private FriendItem? _selectedFriend;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(ComposeBytes), nameof(ComposeCount), nameof(ComposeCountShown), nameof(ComposeNote))]
    private string _compose = "";
    public bool HasSelection => SelectedFriend != null;
    public bool NoSelection => SelectedFriend == null;

    // from the Play press till the game closes nothing new goes to the hook, answers already asked for still land
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompose), nameof(ComposeNote), nameof(CanEdit), nameof(StatusEditable))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(RefreshMailCommand))]
    private bool _gameRunning;
    public const string GameRunningNote = "That has to wait till FINAL FANTASY XI is closed.";

    partial void OnGameRunningChanged(bool value) => StatusNote = value ? GameRunningNote : "";
    public bool CanCompose => SelectedFriend is { CanMessage: true } && !GameRunning;

    // the box counts in the bytes FFXI goes by, a Japanese character's two
    public int ComposeBytes => PolMessage.TextBytes(Compose.Trim());
    public string ComposeCount => $"{ComposeBytes}/{PolMessage.MaxTextBytes}";
    public bool ComposeCountShown => ComposeBytes >= PolMessage.MaxTextBytes * 9 / 10;
    private bool ComposeHasWide => ComposeBytes > Compose.Trim().Replace("\r", "").Length;

    // a draft per conversation, so it never goes to the next person picked (POL mail can't be called back)
    private readonly Dictionary<string, string> _drafts = new();
    private string? _draftFor;
    private FriendItem? _draftPerson;
    // keyless people can share a name, so the row's in there too
    private static string DraftKey(FriendItem f) =>
        f.Key.Length > 0 ? "k:" + f.Key : $"n:{f.Name.ToUpperInvariant()}:{f.Row}";

    public string ComposeNote => SelectedFriend switch
    {
        null => "",
        _ when GameRunning => "While FINAL FANTASY XI is running, replies go through the game.",
        { CanMessage: true } when ComposeCountShown && ComposeHasWide =>
            PolMessage.HasDoubleByte(Compose) ? "Japanese characters count as two." : "Some characters count as two.",
        { CanMessage: true, Online: true } => "This sends a PlayOnline message.",
        { CanMessage: true } f => $"{f.Name}'s offline, so they'll get this next time they log in.",
        { Pending: true } f => $"You can message {f.Name} once they accept your friend request.",
        { IsRequest: true } f => $"Accept {f.Name}'s friend request to send them messages.",
        { IsGroupInvite: true } => "You can't reply to a group invitation, only accept or decline it.",
        var f => $"PlayOnline messages only go to friends. Add {f.Name} as a friend to reply.",
    };

    private readonly HashSet<int> _acked = new();
    // answered requests the hook may delete now, kept until the ack file's written so they can't come back as pending
    private readonly HashSet<int> _ackPending = new();
    private readonly HashSet<int> _answered = new();
    private readonly HashSet<int> _seenRequests = new();
    private readonly HashSet<int> _shownRequests = new();
    private bool _friendsListVisible, _mainWindowActive;
    private HashSet<string> _onlineFriends = new();
    private DateTime? _presenceQuietUntil;

    public bool IsWaiting(int mid) => mid > 0 && Friends.Any(f => f.RequestMid == mid);

    // the windows say when a friend list is on screen, like ConversationVisible
    public bool FriendsListVisible
    {
        get => _friendsListVisible;
        set
        {
            _friendsListVisible = value;
            RefreshUnseen();
        }
    }

    public bool MainWindowActive
    {
        get => _mainWindowActive;
        set
        {
            _mainWindowActive = value;
            RefreshUnseen();
        }
    }

    private void RefreshUnseen()
    {
        var waiting = Friends.Where(f => f.RequestMid > 0).Select(f => f.RequestMid).ToList();
        if (_friendsListVisible || _mainWindowActive) _shownRequests.UnionWith(waiting);
        UnseenCount = UnreadCount + waiting.Count(m => !_shownRequests.Contains(m));
    }

    partial void OnUnreadCountChanged(int value) => RefreshUnseen();

    private void FlushAcks()
    {
        if (_ackPending.Count == 0 || _pol.Pid is not { } pid) return;
        _ackPending.ExceptWith(_acked);
        if (_ackPending.Count == 0 || !SocialSnapshot.Ack(pid, _ackPending.ToList())) return;
        _acked.UnionWith(_ackPending);
        _ackPending.Clear();
    }

    private void Answered(int mid, bool ack)
    {
        _answered.Add(mid);
        _friendsSignature = "";
        if (!ack) return;
        _ackPending.Add(mid);
        FlushAcks();
    }

    private string? NoticeText(PolMessage m) => m.Kind switch
    {
        PolMessage.KindFriendAccepted => "Accepted your friend request.",
        PolMessage.KindFriendDeclined => "Declined your friend request.",
        PolMessage.KindGroupAccepted => "Accepted your group invitation.",
        PolMessage.KindGroupDeclined => "Declined your group invitation.",
        PolMessage.KindGroupRemoved => m.Group.Length == 0 ? "You have been removed from a friend group."
            : OneLine(ViewerText(0x46dd, "You have been removed from the group \"%s.\"")).Replace("%s", m.Group),
        PolMessage.KindGroupDisbanded => m.Group.Length == 0 ? "A friend group you were in has been disbanded."
            : OneLine(ViewerText(0x46db, "The group \"%s\" has been disbanded.")).Replace("%s", m.Group),
        _ => null,
    };

    // SE's group lines already say what happened, so they don't get the sender's name in front
    private static bool SaysWho(int kind) => kind is not (PolMessage.KindGroupRemoved or PolMessage.KindGroupDisbanded);

    private static string OneLine(string s) => s.Replace("\r\n", " ").Replace('\n', ' ');

    private void NoticeComingOnline(PolSocialSnapshot snap)
    {
        static string Id(PolFriend f) => f.Key.Length > 0 ? f.Key : "n:" + f.Name;
        if (!snap.FriendsLoaded) return;   // an empty list isn't "everyone went offline"
        var now = snap.Friends.Where(f => f.Online && !f.Pending).ToList();
        // everyone already online turns up in the first few secs, only later changes are news
        _presenceQuietUntil ??= DateTime.UtcNow + TimeSpan.FromSeconds(30);
        if (DateTime.UtcNow >= _presenceQuietUntil)
            foreach (var f in now)
                if (!_onlineFriends.Contains(Id(f))) FriendCameOnline?.Invoke(f.Name);
        _onlineFriends = now.Select(Id).ToHashSet();
    }

    public void Start(string profileId, string? storeRoot = null)
    {
        _chat = new ChatStore(profileId, storeRoot) { Log = line => Serilog.Log.Warning("Chat log: {Line}", line) };
        _chat.ExpireSending(TimeSpan.FromMinutes(3));   // lines a closed session never answered
        _friendsSignature = "";
        _acked.Clear();
        _ackPending.Clear();
        _drafts.Clear();
        _requestedStatus = null;
        StatusPending = false;
        StatusNote = "";
        _draftFor = null;
        _draftPerson = null;
        Compose = "";
        _answered.Clear();
        _seenRequests.Clear();
        _shownRequests.Clear();
        _onlineFriends.Clear();
        _presenceQuietUntil = null;
        _lookups?.Reset();
        _answerToken = _editToken = _picToken = _searchToken = _requestToken = _profileToken = _groupToken = _ffxiToken = null;
        _answerFor = null;
        _sendQueue.Clear();   // unsent lines stay in the log and expire as "not sent"
        _sendInFlight.Clear();
        _sendsClosed = false;
        _storeTaken = false;
        GameRunning = false;
        _groupsSignature = "";
        Groups.Clear();
        HasGroups = false;
        EditBusy = false;
        ProfilePictureSaving = false;
        SearchBusy = false;
        EditNote = "";
        SearchNote = "";
        SearchHits.Clear();
        Conversation.Clear();
        SelectedFriend = null;
    }

    public void Apply(PolSocialSnapshot snap)
    {
        _applyingSnapshot = true;
        try { ApplyCore(snap); }
        finally { _applyingSnapshot = false; }
    }

    private void ApplyCore(PolSocialSnapshot snap)
    {
        _last = snap;
        bool chatChanged = _chat != null && MergeChat(_chat, snap);
        if (FilterOn != _filterWasOn)
        {
            _filterWasOn = FilterOn;
            _friendsSignature = _groupsSignature = "";
            chatChanged = true;
        }
        FlushAcks();
        // my own jobs first, so a failure never leaves a lock behind and an answered request's gone before the redraw
        ApplyEditState(snap);
        ApplyAnswerState(snap);
        ApplySearchState(snap);
        ApplyProfilePictureState(snap);
        ApplyProfileState(snap);
        ApplyFfxiProfileState(snap);
        ApplyGroupState(snap);
        ApplyCharacters(snap);
        RebuildGroups(snap);
        FriendsUnavailable = !snap.FriendsLoaded;
        MessagesUnavailable = snap.Mail == "failed";
        RefreshMailCommand.NotifyCanExecuteChanged();
        RebuildFriends(snap);
        NoticeComingOnline(snap);
        OnlineCount = snap.Friends.Count(f => f.Online && !f.Pending);
        UnreadCount = _chat?.Unread ?? 0;
        if (SelectedFriend != null && _conversationDay != DateTime.Now.Date) RefreshConversation();   // past midnight
        PendingAnswers = Friends.Count(f => f.IsRequest || f.IsGroupInvite);
        RefreshUnseen();

        if (_requestedStatus is { } want)
        {
            if (!snap.StatusBusy && snap.MyStatus == want)
            {
                _requestedStatus = null;
                StatusNote = "";
            }
            else if (!snap.StatusBusy && DateTime.UtcNow - _requestedAt > TimeSpan.FromSeconds(5))
            {
                _requestedStatus = null;
                StatusNote = "PlayOnline didn't accept the status change.";
            }
            else if (DateTime.UtcNow - _requestedAt > TimeSpan.FromSeconds(110))
            {
                _requestedStatus = null;   // polcore gives up after 100 secs
                StatusNote = "PlayOnline didn't answer the status change.";
            }
        }
        StatusPending = _requestedStatus != null || snap.StatusBusy;
        if (_requestedStatus is null)
        {
            var status = snap.MyStatus == PolStatus.Busy ? PolStatus.Online : snap.MyStatus;
            MyStatusIndex = Array.IndexOf(StatusChoices, status);
            StatusNote = snap.Presence switch
            {
                "starting" => "Going online...",
                "no-handle" => "This account doesn't have a PlayOnline handle, so friends can't see you and won't show as online.",
                var p when p.StartsWith("failed", StringComparison.Ordinal) =>
                    "PlayOnline didn't let you go online, so your friends show as offline. Log out and back in again.",
                _ => StatusNote is "Going online..." ? "" : StatusNote,
            };
        }
        if (!string.IsNullOrWhiteSpace(snap.MyHandle)) AccountName = snap.MyHandle;
        Avatar = _avatars.ProfilePicture(snap.MyProfilePictureId);
        MyProfilePictureId = snap.MyProfilePictureId;
        var region = _settings.DetectedRegion ?? PolRegion.UnitedStates;
        var notice = PolAccountNotices.For(snap.Notice, region, snap.NoticeDays);
        PlayBlockedReason = PolAccountNotices.PlayBlockedReason(snap, region);
        AccountNotice = snap.BadPassword ? PolAccountNotices.BadPassword
                      : notice is { Blocks: false } ? $"{notice.Text} ({notice.Code})" : null;
        Connected = true;
        MyComment = snap.MyComment;
        static string BlockedId(PolBlocked x) => $"{x.Index}/{x.Key}/{x.Name}";
        if (!Blocked.Select(BlockedId).SequenceEqual(snap.Blocked.Select(BlockedId)))
        {
            Blocked.Clear();
            foreach (var x in snap.Blocked) Blocked.Add(x);
        }
        HasBlocked = Blocked.Count > 0;
        OnPropertyChanged(nameof(CanEdit));
        if (chatChanged) RefreshConversation();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(NoFriends));
        OnPropertyChanged(nameof(NothingSelected));
        SnapshotApplied?.Invoke(snap);
    }

    // new mail and send results into the log. POL's copy of a message only goes once ours is saved, like the viewer
    private bool MergeChat(ChatStore chat, PolSocialSnapshot snap)
    {
        bool changed = false;
        var saved = new List<int>();
        foreach (var f in snap.Friends)
            if (f.Key.Length > 0 &&
                snap.Friends.Count(x => string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase)) == 1)
                changed |= chat.Adopt(f.Name, f.Key);   // lines saved while the hook had no key for them
        bool takeStore = snap.Stored != null && !_storeTaken;
        if (takeStore) changed |= MergeStored(chat, snap);
        foreach (var m in snap.Messages)
        {
            var who = snap.Friends.FirstOrDefault(f => f.Key.Length > 0 && f.Key == m.FromKey)?.Name ?? m.From;
            if (m.HasBody && NoticeText(m) is { } line)
            {
                // one about my own action (say I disbanded the group) isn't news, but it still goes from the server
                if (!Mine(snap, m) && chat.MergeNotice(m, line))
                {
                    changed = true;
                    NoticeReceived?.Invoke(who, SaysWho(m.Kind) ? $"{who} {char.ToLowerInvariant(line[0])}{line[1..]}" : line);
                }
                if (m.Mid > 0 && _acked.Add(m.Mid)) saved.Add(m.Mid);
                continue;
            }
            if (m.HasBody && m.Kind == PolMessage.KindGroupInvite && m.Mid > 0 && _seenRequests.Add(m.Mid))
                GroupInviteReceived?.Invoke(m.From, m.Mid);
            if (m.HasBody && m.Kind == PolMessage.KindFriendRequest && m.Mid > 0 && _seenRequests.Add(m.Mid))
                FriendRequestReceived?.Invoke(m.From, m.Mid);
            // chat from someone I ignore gets binned unread. The hook only catches listed mail, not a push
            if (m.IsChat && m.FromKey.Length > 0 && snap.Blocked.Any(x => x.Key == m.FromKey))
            {
                if (m.Mid > 0 && _acked.Add(m.Mid)) saved.Add(m.Mid);
                continue;
            }
            if (chat.MergeIncoming(m))
            {
                changed = true;
                MessageReceived?.Invoke(who, Filtered(m.Text.Length > 0 ? m.Text : m.Subject));
            }
            // chat only, a request stays on the server for the viewer and is never deleted unseen
            if (m.HasBody && m.IsChat && m.Mid > 0 && _acked.Add(m.Mid)) saved.Add(m.Mid);
        }
        // the recent results too, an answer or invitation can take the send slot before we see a line's result
        foreach (var (token, state, time, seq) in snap.SendResults.Append((snap.SendToken, snap.SendState, 0L, 0L)))
            if (token.Length > 0 && (state == "sent" || state.StartsWith("failed", StringComparison.Ordinal)))
            {
                changed |= chat.SetSendState(token, state, time, seq);
                _sendInFlight.RemoveAll(x => x.Token == token);
            }
        foreach (var (token, state) in snap.SendQueue)
            if (_sendInFlight.FindIndex(x => x.Token == token) is var i and >= 0)
                _sendInFlight[i] = _sendInFlight[i] with { HookState = state, HookSaid = DateTime.UtcNow };
        PumpSends();
        // a stored send finishes well inside 2 mins, so nothing says "Sending..." for ever
        changed |= chat.ExpireSending(ChatStore.SendExpiry(_sendQueue.Count), SendsStillPending());
        if (changed && ConversationVisible && SelectedFriend is { } open && chat.UnreadFrom(open.Name, open.Key) > 0)
            chat.MarkRead(open.Name, open.Key);
        // Dirty rather than changed, a log whose last save failed isn't on disk either
        bool kept = !chat.Dirty || chat.Save();
        if (takeStore && kept && _pol.Pid is { } storePid)
        {
            SocialSnapshot.DeleteStore(storePid);
            _storeTaken = true;
        }
        if (!kept || (saved.Count > 0 && _pol.Pid is { } pid && !SocialSnapshot.Ack(pid, saved)))
            foreach (var mid in saved) _acked.Remove(mid);   // again next snapshot
        return changed;
    }

    private bool _storeTaken;

    // sent by this handle, or another handle on my own POL ID
    private static bool Mine(PolSocialSnapshot snap, PolMessage m) =>
        m.IsMe || (snap.MyKey.Length > 0 && m.FromKey == snap.MyKey);

    // the game's message store, read by the hook at login: it's history, so no notices, and nothing to ack since none of
    // it's on the server any more
    private bool MergeStored(ChatStore chat, PolSocialSnapshot snap)
    {
        bool changed = false;
        foreach (var m in snap.Stored!.Received)
        {
            if (m.IsChat && m.FromKey.Length > 0 && snap.Blocked.Any(x => x.Key == m.FromKey)) continue;
            if (NoticeText(m) is { } line)
            {
                if (!Mine(snap, m)) changed |= chat.MergeNotice(m, line);
            }
            else changed |= chat.MergeIncoming(m);
        }
        foreach (var s in snap.Stored.Sent)
        {
            var to = snap.Friends.FirstOrDefault(f => f.Key.Length > 0 && f.Key == s.ToKey)?.Name ??
                     (s.To.Length > 0 ? s.To : chat.NameFor(s.ToKey));
            if (to.Length > 0 || s.ToKey.Length > 0) changed |= chat.AddGameSent(to, s);
        }
        return changed;
    }

    private static readonly string[] Sections =
        ["FRIEND REQUESTS", "GROUP INVITATIONS", "ONLINE", "OFFLINE", "REQUESTS SENT", "OTHERS"];

    private void RebuildFriends(PolSocialSnapshot snap)
    {
        var items = snap.Friends.Select(f =>
        {
            var (playing, playingShort) = Playing(f);
            return new FriendItem(
                f.Name, playing, f.Pending ? "" : Filtered(f.Comment),
                f.Pending ? "Friend request sent" : f.Online ? f.StatusLabel : "Offline", f.Online && !f.Pending, f.Row,
                _avatars.ProfilePicture(f.ProfilePictureId), _chat?.UnreadFrom(f.Name, f.Key) ?? 0,
                f.Pending ? "REQUESTS SENT" : f.Online ? "ONLINE" : "OFFLINE")
            { Key = f.Key, Pending = f.Pending, IsMe = f.IsMe, PlayingShort = playingShort };
        }).ToList();
        // invitations (kind 0xe) and requests (kind 1) stay on the server until they're answered
        foreach (var m in snap.Messages.Where(m => m.HasBody && m.Mid > 0 && !_answered.Contains(m.Mid)))
            if (m.Kind == PolMessage.KindGroupInvite)
                items.Add(new FriendItem(m.From, "", m.Text.Length > 0 ? Filtered(m.Text) : "Group invitation", "", false, -1,
                                         _avatars.NoProfilePicture, 0, "GROUP INVITATIONS")
                          { Key = m.FromKey, RequestMid = m.Mid, IsGroupInvite = true });
            else if (m.Kind == PolMessage.KindFriendRequest)
                items.Add(new FriendItem(m.From, "", "Wants to be your friend", "", false, -1,
                                         _avatars.NoProfilePicture, 0, "FRIEND REQUESTS")
                          { Key = m.FromKey, RequestMid = m.Mid, IsMe = m.IsMe });
        if (_chat != null)
        {
            var keys = items.Where(i => i.Key.Length > 0).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
            var names = items.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keyless = items.Where(i => i.Key.Length == 0).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, last, used) in _chat.Writers)
            {
                bool listed = last.PeerKey.Length > 0 ? keys.Contains(last.PeerKey) || used.Any(keyless.Contains)
                                                      : names.Contains(last.Peer);
                if (listed) continue;
                items.Add(new FriendItem(last.Peer, "", "Not on your friend list", "", false, -1,
                                         _avatars.NoProfilePicture, _chat.UnreadFrom(last.Peer, last.PeerKey),
                                         "OTHERS") { Key = last.PeerKey });
            }
        }
        items = items.OrderBy(i => Array.IndexOf(Sections, i.Section))
                     .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var sig = string.Join("|", items.Select(i => $"{i.Name}/{i.Key}/{i.StatusLabel}/{i.Playing}/{i.PlayingShort}/{i.Comment}/" +
                                                     $"{i.Unread}/{i.Avatar != null}/{i.Section}/{i.Row}/" +
                                                     $"{i.Pending}/{i.RequestMid}"));
        if (sig == _friendsSignature) return;
        _friendsSignature = sig;
        // patched in place rather than cleared, so the list keeps its scroll position and selection and only the rows
        // that changed get redrawn
        var keep = SelectedFriend;
        for (int i = 0; i < items.Count; i++)
        {
            int j = -1;
            for (int k = i; k < Friends.Count && j < 0; k++)
                if (SameRow(Friends[k], items[i])) j = k;
            if (j < 0)
            {
                Friends.Insert(i, items[i]);
                continue;
            }
            if (j != i) Friends.Move(j, i);
            if (Friends[i] == items[i]) continue;
            bool selected = ReferenceEquals(SelectedFriend, Friends[i]);
            Friends[i] = items[i];
            if (selected) SelectedFriend = items[i];
        }
        while (Friends.Count > items.Count) Friends.RemoveAt(Friends.Count - 1);
        if (keep != null && !Friends.Contains(SelectedFriend!))
            SelectedFriend = Friends.FirstOrDefault(i => i.Is(keep));
    }

    // online and offline count as one section (a presence change just moves the row), a request and a friend row don't
    private static bool SameRow(FriendItem a, FriendItem b) =>
        a.Is(b) && (a.Section == b.Section ||
                    (a.Section is "ONLINE" or "OFFLINE" && b.Section is "ONLINE" or "OFFLINE"));

    private bool _conversationVisible;
    private DateTime _conversationDay;   // the day the "Today" headings were made on

    public bool ConversationVisible
    {
        get => _conversationVisible;
        set
        {
            if (_conversationVisible == value) return;
            _conversationVisible = value;
            if (value && SelectedFriend is { } f && _chat != null && _chat.UnreadFrom(f.Name, f.Key) > 0)
                ReadConversation(f, rebuild: true);
        }
    }

    private void ReadConversation(FriendItem f, bool rebuild)
    {
        _chat!.MarkRead(f.Name, f.Key);
        UnreadCount = _chat.Unread;
        _friendsSignature = "";
        if (rebuild) RebuildFriends(_last);
    }

    partial void OnSelectedFriendChanged(FriendItem? value)
    {
        OnPropertyChanged(nameof(ComposeNote));
        var key = value is null ? null : DraftKey(value);
        // a presence update gives the same person a new row and the text goes with them. Keyless needs the same row too
        bool samePerson = value != null && _draftPerson != null && value.Is(_draftPerson) &&
                          (value.Key.Length > 0 || _draftPerson.Key.Length > 0 || key == _draftFor);
        if (samePerson)
        {
            if (_draftFor != null && key != _draftFor && _drafts.Remove(_draftFor, out var moved))
                _drafts[key!] = moved;
        }
        else if (_draftFor != null)
        {
            if (string.IsNullOrWhiteSpace(Compose)) _drafts.Remove(_draftFor);
            else _drafts[_draftFor] = Compose;
        }
        _draftPerson = value;
        _draftFor = key;
        if (!samePerson) Compose = key != null && _drafts.TryGetValue(key, out var draft) ? draft : "";
        // picking someone in the main window shows nothing, so it stays unread until the friends window shows it
        if (value != null && _chat != null && value.Unread > 0 && ConversationVisible)
            ReadConversation(value, rebuild: !_applyingSnapshot);
        RefreshConversation();
    }

    private void RefreshConversation()
    {
        _conversationDay = DateTime.Now.Date;
        Conversation.Clear();
        if (SelectedFriend is not { } f || _chat == null) return;
        DateTime? day = null;
        foreach (var line in _chat.With(f.Name, f.Key))
        {
            var e = line.Outgoing || line.State == "notice" ? line
                  : line with { Subject = Filtered(line.Subject), Text = Filtered(line.Text) };
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(EditNoteShown), nameof(Saving))]
    [NotifyPropertyChangedFor(nameof(EditStatus), nameof(EditStatusShown))]
    private bool _editBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EditNoteShown), nameof(EditStatus), nameof(EditStatusShown))]
    private string _editNote = "";
    public bool EditNoteShown => EditNote.Length > 0 && !EditBusy && !ProfilePictureSaving;
    public bool Saving => EditBusy || ProfilePictureSaving;
    public string EditStatus => EditBusy ? "Saving the change to PlayOnline..."
                              : ProfilePictureSaving ? "Saving your profile picture to PlayOnline..." : EditNote;
    public bool EditStatusShown => Saving || !string.IsNullOrWhiteSpace(EditNote);
    public bool CanEdit => Connected && !EditBusy && !FriendsUnavailable && _pol.Pid != null && !GameRunning;

    private DispatcherTimer? _noteTimer;
    // success notes go by themselves, failures stay until dismissed. A note's a failure unless the setter says so
    private bool _noteIsSuccess;
    private string? _editToken;
    private DateTime _editAt;
    private const string RequestWriteFailed = "Provenance couldn't send the request to PlayOnline. Try again.";

    partial void OnEditNoteChanged(string value)
    {
        bool success = _noteIsSuccess;
        _noteIsSuccess = false;
        if (_noteTimer is null)
        {
            _noteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            _noteTimer.Tick += (_, _) =>
            {
                _noteTimer.Stop();
                EditNote = "";
            };
        }
        _noteTimer.Stop();
        if (value.Length > 0 && success) _noteTimer.Start();
    }

    [RelayCommand]
    private void DismissEditNote() => EditNote = "";

    private void WriteOrFail(bool written)
    {
        if (written) return;
        _editToken = null;
        _answerToken = null;
        _answerFor = null;
        EditBusy = false;
        EditNote = RequestWriteFailed;
    }

    private bool BeginEdit(out int pid, out string token)
    {
        token = NewToken();
        pid = _pol.Pid ?? 0;
        if (!CanEdit || pid == 0) return false;
        _editToken = token;
        _editAt = DateTime.UtcNow;
        EditBusy = true;
        EditNote = "";
        return true;
    }

    private void EditFriend(FriendEdit op, int row, string key, string arg = "")
    {
        if (BeginEdit(out var pid, out var token))
            WriteOrFail(SocialSnapshot.EditFriend(pid, op, token, row, key, arg));
    }

    public void RenameFriend(FriendItem f, string name)
    {
        if (f.IsFriend && !f.Pending && f.Key.Length > 0) EditFriend(FriendEdit.Rename, f.Row, f.Key, name.Trim());
    }

    public void RemoveFriend(FriendItem f)
    {
        if (f.IsFriend && f.Key.Length > 0) EditFriend(FriendEdit.Delete, f.Row, f.Key);
    }

    // POL drops their messages and takes them off the list. allHandles covers every handle on their POL ID
    public void IgnoreFriend(FriendItem f, bool allHandles)
    {
        if (f.IsFriend && !f.Pending && f.Key.Length > 0)
            EditFriend(FriendEdit.Ignore, f.Row, f.Key, allHandles ? "1" : "0");
    }

    public void Unignore(PolBlocked b) => EditFriend(FriendEdit.Unignore, b.Index, b.Key);

    public void SetComment(string text)
    {
        if (BeginEdit(out var pid, out var token)) WriteOrFail(SocialSnapshot.SetComment(pid, token, text.Trim()));
    }

    private void ApplyEditState(PolSocialSnapshot snap)
    {
        if (_editToken is null) return;
        // polcore gives up after 100 secs
        if (Settled(snap.EditToken == _editToken ? snap.EditState : null, "saved", _editAt, 110) is not { } st) return;
        _editToken = null;
        EditBusy = false;
        if (st != "saved") EditNote = EditFailureText(st);
    }

    private static string EditFailureText(string state) => state switch
    {
        "timeout" => "PlayOnline didn't answer the change.",
        "failed:pending" => "That person hasn't accepted your friend request yet, so you can only delete it.",
        "failed:self" => "You can't ignore yourself.",
        "failed:friend list changed" or "failed:no such friend" => "Your friend list changed. Try again.",
        "failed:list changed" or "failed:no such entry" => "Your ignore list changed. Try again.",
        "failed:bad name" => "Use 1 to 15 English letters, numbers or symbols.",
        "failed:friends not loaded" => "Your friend list didn't load, so this can't be changed right now. Log out and back in again.",
        "failed:account notice" => "This PlayOnline account can't change its friends right now.",
        "failed:reloaded" => "PlayOnline had a newer friend list, so Provenance loaded it instead. Try again.",
        "failed:no handle" => "This account doesn't have a PlayOnline handle.",
        "failed:-7187" => "That person is already on your ignore list.",
        "failed:-7188" or "failed:-516" => "Your ignore list is full (100 people).",
        _ => $"PlayOnline didn't accept the change ({FailReason(state)}).",
    };

    private string? _answerToken;
    private FriendItem? _answerFor;
    private RequestAnswer _answerKind;
    private DateTime _answerAt;
    private IReadOnlyDictionary<int, string>? _viewerStrings;
    private bool _viewerStringsTried;

    private PolLanguageFilter? _wordFilter;
    private bool _wordFilterTried, _filterWasOn;

    private bool FilterOn => PolOptions.ChatFilter(_settings.Current);

    // the chat language filter where the viewer runs it: what other people wrote, never my own lines
    internal string Filtered(string text)
    {
        if (text.Length == 0 || !FilterOn) return text;
        if (!_wordFilterTried && _settings.DetectedRegion != null)
        {
            _wordFilterTried = true;
            var viewer = ViewerFolder();
            _wordFilter = viewer is null ? null : PolLanguageFilter.Load(viewer);
        }
        return _wordFilter?.Apply(text) ?? text;
    }

    internal string ViewerText(int id, string fallback)
    {
        if (!_viewerStringsTried)
        {
            _viewerStringsTried = true;
            var viewer = ViewerFolder();
            _viewerStrings = viewer is null ? null : PolStringTable.Load(viewer);
        }
        return _viewerStrings != null && _viewerStrings.TryGetValue(id, out var s) && s.Length > 0 ? s : fallback;
    }

    // Accept and Decline reply with the viewer's fixed subject, Ignore ignores them and declines
    public void AnswerRequest(FriendItem f, RequestAnswer answer)
    {
        if (!f.IsRequest) return;
        if (f.IsMe && answer != RequestAnswer.Decline)
        {
            EditNote = "That request is from one of your own handles. You can only decline it.";
            return;
        }
        if (answer == RequestAnswer.Accept && Friends.Count(x => x.IsFriend) >= 200)
        {
            EditNote = "Your friend list is full (200 friends).";
            return;
        }
        if (answer == RequestAnswer.Ignore && f.Key.Length > 0 && Blocked.Any(x => x.Key == f.Key))
        {
            EditNote = $"{f.Name} is already on your ignore list.";
            return;
        }
        if (!BeginEdit(out var pid, out var token)) return;
        _editToken = null;   // followed through the send result instead
        _answerToken = token;
        _answerFor = f;
        _answerKind = answer;
        _answerAt = DateTime.UtcNow;
        var subject = answer == RequestAnswer.Accept
            ? ViewerText(0x46d0, "Friend registration accepted")
            : ViewerText(0x46d1, "Friend registration declined");
        // ignoring from a request only ever covers this handle, same as the viewer
        WriteOrFail(SocialSnapshot.AnswerRequest(pid, answer, token, f.RequestMid, subject, allHandles: false));
    }

    private void ApplyAnswerState(PolSocialSnapshot snap)
    {
        if (_answerToken is null || _answerFor is not { } f) return;
        // a list upload and a send, each up to 100 secs
        if (Settled(snap.SendStateFor(_answerToken), "sent", _answerAt, 250) is not { } st) return;
        _answerToken = null;
        _answerFor = null;
        EditBusy = false;
        if (st == "sent")
        {
            Answered(f.RequestMid, ack: true);
            _chat?.AddNote(f.Name, f.Key, _answerKind switch
            {
                RequestAnswer.Accept => "You accepted the friend request.",
                RequestAnswer.Decline => "You declined the friend request.",
                _ => "You declined the friend request and ignored this person.",
            });
            return;
        }
        // it may still have gone out, so it leaves the list for this session rather than invite a second answer
        if (st == "timeout") Answered(f.RequestMid, ack: false);
        EditNote = st switch
        {
            "timeout" => "PlayOnline didn't reply, so it's not certain your answer got through. If the request's still open, it'll show again next time you log in.",
            "failed:friends not loaded" => "Your friend list didn't load, so this can't be changed right now. Log out and back in again.",
            "failed:account notice" => "This PlayOnline account can't answer friend requests right now.",
            "failed:no such request" => "That friend request isn't there any more.",
            "failed:self" => "That request is from one of your own handles. You can only decline it.",
            "failed:already ignored" => "That person is already on your ignore list.",
            "failed:-7188" => "Your friend list is full (200 friends).",
            "failed:no such friend" => "That person isn't on your friend list any more, so nothing was sent.",
            "failed:stopped" => "PlayOnline didn't take your recent messages, so answers can't be sent until you log in again.",
            "failed:hourly limit" => "You've sent 600 messages in the last hour, so your answer wasn't sent. Try again later.",
            var s when s.StartsWith("failed:list", StringComparison.Ordinal) =>
                "PlayOnline didn't save the friend list, so your answer wasn't sent. Try again.",
            var s => $"PlayOnline didn't accept your answer ({FailReason(s)}).",
        };
    }

    // Name is the character's for a character hit, which shows like a friend in game does
    public sealed record SearchHitItem(int Index, string Name, ImageSource? Avatar, bool IsFriend, string Key,
                                       bool RequestSent = false, bool IsMe = false)
    {
        public bool IsCharacter { get; init; }
        public string World { get; init; } = "";
        public string Label => IsCharacter && World.Length > 0 ? PlayingText.For(Name, World).Long : Name;
        public string Note =>
            IsMe ? "This is you" : IsFriend ? "Already on your friend list" : RequestSent ? "Friend request sent" : "";
    }

    public ObservableCollection<SearchHitItem> SearchHits { get; } = new();
    [ObservableProperty] private bool _searchBusy;
    [ObservableProperty] private string _searchNote = "";
    [ObservableProperty] private bool _searchNoteIsError;
    partial void OnSearchNoteChanged(string value) => SearchNoteIsError = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchByHandle), nameof(SearchByHid), nameof(SearchByCharacter), nameof(SearchHint),
                              nameof(SearchIntro), nameof(SearchMaxLength))]
    private FriendSearchKind _searchKind;
    // the picker's off while a search runs, so the hits always match the kind showing
    partial void OnSearchKindChanged(FriendSearchKind value)
    {
        if (SearchBusy) return;
        SearchHits.Clear();
        SearchNote = "";
    }
    public bool SearchByHandle { get => SearchKind == FriendSearchKind.Handle; set { if (value) SearchKind = FriendSearchKind.Handle; } }
    public bool SearchByHid { get => SearchKind == FriendSearchKind.Hid; set { if (value) SearchKind = FriendSearchKind.Hid; } }
    public bool SearchByCharacter { get => SearchKind == FriendSearchKind.Character; set { if (value) SearchKind = FriendSearchKind.Character; } }
    public string SearchHint => SearchKind switch
    {
        FriendSearchKind.Hid => "H-ID, like 00-1234567",
        FriendSearchKind.Character => "Character name",
        _ => "Handle name",
    };
    public string SearchIntro => SearchKind switch
    {
        FriendSearchKind.Hid => "Search PlayOnline for a handle by its H-ID. Every handle has its own, shown in its profile.",
        FriendSearchKind.Character => "Search for a FINAL FANTASY XI character by name. The same name can be on more than one world, so check the world.",
        _ => "Search PlayOnline for a handle name. Many people can share one name, so check the picture.",
    };
    public int SearchMaxLength => SearchKind == FriendSearchKind.Hid ? 20 : 15;

    private string? _searchToken;
    private DateTime _searchAt;
    private FriendSearchKind _searchedKind;
    private string _searchedValue = "";
    private string? _requestToken;
    private string _requestName = "";
    private string _requestKey = "";
    private bool _requestCharacter;
    private DateTime _requestAt;
    private const string BadHandleName = "Type a handle name of up to 15 English letters, numbers or symbols.";
    private const string QuoteInName = "Provenance can't search for handle names with an apostrophe (') in them.";
    private const string BadHid = "Type an H-ID in numbers, like 00-1234567.";
    private const string BadCharacterName = "Type a character name of up to 15 English letters, with no spaces, numbers or symbols.";
    private const string TooManyResults = "Too many results. Please narrow your search.";

    private void SearchFailed(string note)
    {
        SearchNote = note;
        SearchNoteIsError = true;
    }

    public void Search(string text)
    {
        var value = text.Trim();
        var kind = SearchKind;
        if (SearchBusy || !Connected || _pol.Pid is not { } pid) return;
        if (GameRunning)
        {
            SearchFailed(GameRunningNote);
            return;
        }
        if (!FriendSearch.Ok(kind, value))
        {
            SearchFailed(kind switch
            {
                FriendSearchKind.Hid => BadHid,
                FriendSearchKind.Character => BadCharacterName,
                _ => BadHandleName,
            });
            return;
        }
        if (kind == FriendSearchKind.Handle && value.Contains('\''))
        {
            SearchFailed(QuoteInName);
            return;
        }
        _searchToken = NewToken();
        _searchAt = DateTime.UtcNow;
        _searchedKind = kind;
        _searchedValue = value;
        SearchHits.Clear();
        SearchBusy = true;
        SearchNote = "Searching PlayOnline...";
        if (SocialSnapshot.Search(pid, _searchToken, kind, value)) return;
        _searchToken = null;
        SearchBusy = false;
        SearchFailed(RequestWriteFailed);
    }

    internal string? FriendRequestBlocked =>
        CanEdit ? null
        : GameRunning ? GameRunningNote
        : EditBusy ? "Wait for your last change to finish first."
        : FriendsUnavailable ? "Your friend list didn't load, so friend requests can't be sent right now. Log out and back in again."
        : "Log in to send friend requests.";

    public void SendFriendRequest(SearchHitItem hit)
    {
        if (hit.IsMe)
        {
            SearchFailed("You can't add yourself as a friend.");
            return;
        }
        if (hit.IsFriend || hit.RequestSent || _requestToken != null || !BeginEdit(out var pid, out var token)) return;
        _editToken = null;
        _requestToken = token;
        _requestName = hit.Name;
        _requestKey = hit.Key;
        _requestCharacter = hit.IsCharacter;
        _requestAt = DateTime.UtcNow;
        SearchNote = $"Sending a friend request to {hit.Name}...";
        if (SocialSnapshot.SendFriendRequest(pid, token, hit.Index, hit.Key, ViewerText(0x46c9, "Let's be friends!"),
                                             ViewerText(0x46e6, "Would you like to be friends?"))) return;
        _requestToken = null;
        EditBusy = false;
        SearchFailed(RequestWriteFailed);
    }

    private void ApplySearchState(PolSocialSnapshot snap)
    {
        // two profile-server jobs, each up to 100 secs
        if (_searchToken != null &&
            Settled(snap.SearchToken == _searchToken ? snap.SearchState : null, "done", _searchAt, 220) is { } st)
        {
            _searchToken = null;
            SearchBusy = false;
            var characters = _searchedKind == FriendSearchKind.Character;
            if (st == "done" && snap.SearchTooMany) SearchFailed(TooManyResults);
            else if (st == "done")
            {
                SearchHits.Clear();
                foreach (var h in snap.SearchResults)
                {
                    // the search matched INITCAP of what was typed, so a hit with no name in it is still called that
                    var name = characters && h.Name.Length == 0 ? FriendSearch.InitCap(_searchedValue) : h.Name;
                    SearchHits.Add(new SearchHitItem(h.Index, name, _avatars.ProfilePicture(characters ? 0 : h.ProfilePictureId),
                                                     h.IsFriend, h.Key, IsMe: h.IsMe)
                                   { IsCharacter = characters, World = characters ? h.World : "" });
                }
                var what = characters ? "character" : "handle";
                var total = Math.Max(snap.SearchTotal, SearchHits.Count);
                SearchNote = SearchHits.Count == 0 ? _searchedKind switch
                             {
                                 FriendSearchKind.Hid => "There's no PlayOnline handle with that H-ID.",
                                 FriendSearchKind.Character => "There's no FINAL FANTASY XI character with that name.",
                                 _ => "There's no PlayOnline handle with that name.",
                             }
                           : total > SearchHits.Count ? $"Found {total} {what}s. Showing {SearchHits.Count} of them."
                           : SearchHits.Count == 1 ? $"Found 1 {what}." : $"Found {SearchHits.Count} {what}s.";
            }
            else SearchFailed(st switch
            {
                "timeout" or "failed:timeout" => "PlayOnline didn't answer the search.",
                "failed:bad name" => BadHandleName,
                "failed:quote" => QuoteInName,
                "failed:bad hid" => BadHid,
                "failed:bad character" => BadCharacterName,
                "failed:no schema" when characters =>
                    "Provenance couldn't read the FINAL FANTASY XI profile layout from your PlayOnline install, so it can't search for characters.",
                "failed:no schema" => "Provenance couldn't read the profile layout from your PlayOnline install, so it can't search for handles.",
                _ => $"PlayOnline couldn't run the search ({FailReason(st)}).",
            });
        }
        if (_requestToken is null) return;
        if (Settled(snap.SendStateFor(_requestToken), "sent", _requestAt, 250) is not { } rst) return;
        _requestToken = null;
        EditBusy = false;
        if (rst != "sent")
        {
            SearchFailed(rst switch
            {
                "timeout" => "PlayOnline didn't answer the friend request.",
                "failed:already a friend" => $"{_requestName} is already on your friend list.",
                "failed:self" => "You can't add yourself as a friend.",
                "failed:-7181" => $"{_requestName} is on your ignore list. Stop ignoring them first.",
                "failed:-7188" or "failed:-516" => "Your friend list is full (200 friends).",
                "failed:no such result" => "Search again, then send the request.",
                "failed:no handle" => "This account doesn't have a PlayOnline handle.",
                "failed:no such friend" => $"Your friend list changed before the request to {_requestName} went, so it wasn't sent. Try again.",
                "failed:stopped" => "PlayOnline didn't take your recent messages, so friend requests can't be sent until you log in again.",
                "failed:hourly limit" => "You've sent 600 messages in the last hour, so the request wasn't sent. Try again later.",
                var s when s.StartsWith("failed:list", StringComparison.Ordinal) =>
                    "PlayOnline didn't save the friend list, so your request wasn't sent. Try again.",
                var s => $"PlayOnline didn't accept the request ({FailReason(s)}).",
            });
            return;
        }
        SearchNote = $"Friend request sent to {_requestName}. They'll show on your list{(_requestCharacter ? " under their PlayOnline handle," : "")} as \"Friend request sent\" until they accept.";
        for (int i = 0; i < SearchHits.Count; i++)
            if (SearchHits[i].Name == _requestName && SearchHits[i].Key == _requestKey)
                SearchHits[i] = SearchHits[i] with { RequestSent = true };
    }

    // a pic save is two requests, in the viewer's order
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditNoteShown), nameof(Saving), nameof(EditStatus), nameof(EditStatusShown))]
    private bool _profilePictureSaving;
    private string? _picToken;
    private DateTime _picAt;

    public void SaveProfilePicture(int picId)
    {
        if (ProfilePictureSaving || !Connected || _pol.Pid is not { } pid) return;
        if (GameRunning)
        {
            EditNote = GameRunningNote;
            return;
        }
        _picToken = NewToken();
        _picAt = DateTime.UtcNow;
        ProfilePictureSaving = true;
        EditNote = "";
        if (SocialSnapshot.SetProfilePicture(pid, picId, _picToken)) return;
        _picToken = null;
        ProfilePictureSaving = false;
        EditNote = RequestWriteFailed;
    }

    private void ApplyProfilePictureState(PolSocialSnapshot snap)
    {
        if (_picToken is null) return;
        // two jobs, each up to 100 secs in polcore
        if (Settled(snap.ProfilePictureToken == _picToken ? snap.ProfilePictureState : null, "saved", _picAt, 250)
            is not { } st) return;
        _picToken = null;
        ProfilePictureSaving = false;
        if (st == "saved") return;
        EditNote = st switch
        {
            "timeout" => "PlayOnline didn't answer the profile picture change.",
            "failed:account notice" => "This PlayOnline account can't change its profile picture right now.",
            "failed:no handle" => "This account doesn't have a PlayOnline handle.",
            _ => $"PlayOnline didn't save the profile picture ({FailReason(st)}).",
        };
    }

    private bool CanSend() =>
        CanCompose && !string.IsNullOrWhiteSpace(Compose) && ComposeBytes <= PolMessage.MaxTextBytes && _pol.Pid != null;

    // POL mail (the viewer's stored send), so it reaches them offline too
    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        if (!CanSend() || SelectedFriend is not { } f || _chat == null) return;
        var text = Compose.Trim();
        if (text.Length == 0) return;
        Queue(f, text);
        Compose = "";
        RefreshConversation();
    }

    public void RemoveFailedLine(ChatEntry e)
    {
        if (e.Failed && _chat != null && _chat.RemoveFailed(e.Key)) RefreshConversation();
    }

    public void Resend(ChatEntry e)
    {
        if (!e.Failed || _chat == null || !CanCompose || SelectedFriend is not { } f || _pol.Pid is null) return;
        if (!_chat.RemoveFailed(e.Key)) return;
        Queue(f, e.Text);
        RefreshConversation();
    }

    // the hook runs 2 sends at once but reads one request file at a time, and a new file replaces an untaken one, so
    // lines queue here
    private const int SendsAtOnce = 2;
    private readonly Queue<(int Row, string Name, string Token, string Text)> _sendQueue = new();
    // the pol.exe each went to, and what the hook last said about it and when ("sending" once its PUT's started)
    private readonly record struct SendOut(string Token, DateTime At, int Pid, string HookState = "",
                                           DateTime HookSaid = default);
    private readonly List<SendOut> _sendInFlight = new();
    private bool _sendsClosed;   // the session's ending, nothing more goes to its hook

    // lines that mustn't expire yet. With nothing in flight, queued lines age from when they were typed
    private HashSet<string> SendsStillPending()
    {
        var keep = new HashSet<string>();
        var live = _sendInFlight.Where(x => DateTime.UtcNow - x.At < TimeSpan.FromMinutes(3)).ToList();
        if (live.Count == 0) return keep;
        foreach (var x in live) keep.Add("out:" + x.Token);
        foreach (var q in _sendQueue) keep.Add("out:" + q.Token);
        return keep;
    }

    private void Queue(FriendItem f, string text)
    {
        var token = NewToken();
        _chat!.AddOutgoing(f.Name, f.Key, text, token);
        _sendQueue.Enqueue((f.Row, f.Name, token, text));
        PumpSends();
    }

    private void PumpSends()
    {
        int newest = _sendInFlight.Count - 1;
        for (int i = newest; i >= 0; i--)
        {
            var x = _sendInFlight[i];
            if (DateTime.UtcNow - x.At <= TimeSpan.FromMinutes(3)) continue;
            // never answered. Untaken it's withdrawn (only the newest can be), but once the hook has it, it might still
            // go, so it's not re-sent
            bool withdrawn = i == newest &&
                             SocialSnapshot.WithdrawRequest(x.Pid, "send") == SocialSnapshot.Withdrawal.Withdrawn;
            if (!withdrawn && _chat?.SetSendState(x.Token, "unconfirmed") == true) RefreshConversation();
            _sendInFlight.RemoveAt(i);
        }
        while (!_sendsClosed && _sendInFlight.Count < SendsAtOnce && !GameRunning && _sendQueue.Count > 0 &&
               _pol.Pid is { } pid && !SocialSnapshot.SendPending(pid))
        {
            var (row, name, token, text) = _sendQueue.Dequeue();
            // one that waited long enough to show "not sent" stays that way, they might've sent it again already
            var line = _chat?.Entries.FirstOrDefault(e => e.Key == "out:" + token);
            if (line is not { State: "sending" or "queued" }) continue;
            // no subject, like the viewer's own chat window
            if (SocialSnapshot.Send(pid, row, name, token, "", text))
                _sendInFlight.Add(new(token, DateTime.UtcNow, pid));
            else _chat?.SetSendState(token, "failed:write");
        }
    }

    // the session's ending and its results won't get here. One the hook started, or will in a drain, is unconfirmed,
    // the rest are not sent
    public void SettleSends(SessionEnd end)
    {
        bool drained = end != SessionEnd.Dropped;
        string notSent = end == SessionEnd.Play ? "failed:game" : "failed:session";
        var states = new List<(string, string)>();
        int newest = _sendInFlight.Count - 1;
        for (int i = newest; i >= 0; i--)
        {
            var x = _sendInFlight[i];
            // only the newest can still be untaken, the next file's only written once the hook's taken the last
            var w = i == newest ? SocialSnapshot.WithdrawRequest(x.Pid, "send") : SocialSnapshot.Withdrawal.Taken;
            // held a second ago it could've started since, the snapshot that says so takes about 300 ms to get here
            bool held = x.HookState == "queued" && DateTime.UtcNow - x.HookSaid >= TimeSpan.FromSeconds(1);
            bool went = w == SocialSnapshot.Withdrawal.Taken ? drained || !held
                      : w == SocialSnapshot.Withdrawal.Unknown && drained;   // the hook might take it before the drain
            states.Add((x.Token, went ? "unconfirmed" : notSent));
        }
        foreach (var q in _sendQueue) states.Add((q.Token, notSent));
        _chat?.SetSendStates(states);
        _sendInFlight.Clear();
        _sendQueue.Clear();
        _sendsClosed = true;
        RefreshConversation();
    }

    private DateTime _mailAskedAt = DateTime.MinValue;

    // new mail comes by push, so this is only offered after the inbox failed. 30 secs apart, the hook's own gap
    [RelayCommand(CanExecute = nameof(CanRefreshMail))]
    private void RefreshMail()
    {
        if (_pol.Pid is not { } pid) return;
        SocialSnapshot.RefreshMail(pid);
        _mailAskedAt = DateTime.UtcNow;
        RefreshMailCommand.NotifyCanExecuteChanged();
        // unlocks after the gap even if no snapshot turns up
        _ = Task.Delay(TimeSpan.FromSeconds(31)).ContinueWith(_ => RefreshMailCommand.NotifyCanExecuteChanged(),
                                                             TaskScheduler.FromCurrentSynchronizationContext());
    }

    private bool CanRefreshMail() => _pol.Pid != null && !GameRunning && _last.Mail != "pending" &&
                                     DateTime.UtcNow - _mailAskedAt >= TimeSpan.FromSeconds(30);

    partial void OnMyStatusIndexChanged(int value)
    {
        if (_applyingSnapshot || GameRunning || value < 0 || value >= StatusChoices.Length) return;
        if (StatusPending) return;
        var want = StatusChoices[value];
        _requestedStatus = want;
        _requestedAt = DateTime.UtcNow;
        StatusPending = true;
        StatusNote = "";
        _settings.Current.PlayOnlineStatus = PolStatusText.Code(want);
        _settings.Save(_settings.Current);
        if (_pol.Pid is { } pid) SocialSnapshot.SetStatus(pid, want);
    }

    public enum LayoutAction { Split, Merge, OpenMessages, ShowMessages }

    public event Action<LayoutAction>? LayoutRequested;
    public void RequestLayout(LayoutAction action) => LayoutRequested?.Invoke(action);
}
