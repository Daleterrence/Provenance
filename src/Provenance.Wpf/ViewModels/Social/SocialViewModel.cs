using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Game;
using Provenance.Common.Hook;
using Provenance.Common.Install;
using Provenance.Common.Login;
using Provenance.Common.PolFormat;
using Provenance.Common.Settings;
using Provenance.Common.Social;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Chat;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Social;

public partial class SocialViewModel : ObservableObject
{
    private readonly PolSession _pol;
    private readonly AvatarService _avatars;
    private readonly ISettingsService _settings;
    private ChatStore? _chat;
    private PolSocialSnapshot _last = PolSocialSnapshot.Empty;
    private string _friendsSignature = "";

    public SocialViewModel(IDialogs dialogs, IWindowManager windows, PolSession pol, AvatarService avatars, ISettingsService settings)
    {
        Dialogs = dialogs;
        Windows = windows;
        _pol = pol;
        _avatars = avatars;
        _settings = settings;
        _sync = new(_sends);
        EditNote.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(StatusLine.Text)) return;
            OnPropertyChanged(nameof(EditNoteShown));
            OnPropertyChanged(nameof(EditStatus));
            OnPropertyChanged(nameof(EditStatusShown));
        };
        FriendsView = CollectionViewSource.GetDefaultView(Friends);
        FriendsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FriendItem.SectionTitle)));
        GroupChat = new GroupChatViewModel(this, dialogs, () => _settings.Current.GroupChatTimes, () => _settings.Current.GroupChatFlash);
        GroupChat.UnreadChanged += () =>
        {
            _groupsSignature = "";
            RebuildGroups(_last);
            OnPropertyChanged(nameof(GroupChatUnread));
        };
    }

    public IDialogs Dialogs { get; }
    public IWindowManager Windows { get; }
    public GroupChatViewModel GroupChat { get; }
    public int GroupChatUnread => GroupChat.UnreadTotal;

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
    [ObservableProperty] private bool _messagesUnavailable;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPlay))] private string? _playBlockedReason;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPendingAnswers))]
    private int _pendingAnswers;
    // unread, plus requests no friend list has had on screen yet. The taskbar stays lit while it's above 0
    [ObservableProperty] private int _unseenCount;
    public int MyProfilePictureId { get; private set; }

    public bool HasUnread => UnreadCount > 0;
    // requests get a badge too (on Friends), so one's never just an 8 sec banner
    public bool HasPendingAnswers => PendingAnswers > 0;
    public bool CanPlay => PlayBlockedReason is null;
    public string Summary => OnlineCount switch { 0 => "No friends online", 1 => "1 friend online", var n => $"{n} friends online" };
    public bool NoFriends => Connected && Friends.Count == 0;
    public string NothingSelected => NoFriends ? NoFriendsText
                                               : "Select a friend to see your messages.";
    private const string NoFriendsText = "You don't have any friends on this PlayOnline account yet.";
    public int? SessionPid => _pol.Pid;

    public event Action<FriendItem>? OpenChatRequested;
    public event Action<string, string>? MessageReceived;
    public event Action<string, string>? NoticeReceived;
    public event Action<string, int>? FriendRequestReceived;
    public event Action<string, int>? GroupInviteReceived;
    public event Action<string>? FriendCameOnline;          // never for the presence that lands at sign-in
    public event Action<PolSocialSnapshot>? SnapshotApplied;
    // the viewer's own popups over the profile window
    public event Action<string>? ProfilePopup;

    public void RequestChat(FriendItem friend) => OpenChatRequested?.Invoke(friend);

    [RelayCommand]
    private void ChangeProfilePicture()
    {
        if (GameRunning) EditNote.Fail(GameRunningNote);
        else if (HandleSwitching) EditNote.Fail(SwitchingNote);
        else if (!ProfilePictureSaving) PickProfilePicture();
    }

    private void PickProfilePicture()
    {
        var viewer = ViewerFolder();
        var catalog = viewer is null ? null : ProfilePictureCatalog.Load(viewer);
        if (catalog is null)
        {
            Dialogs.Error("Profile picture", "Provenance couldn't find the PlayOnline profile pictures in the PlayOnline Viewer folder.");
            return;
        }
        if (Windows.PickProfilePicture(catalog, _avatars, MyProfilePictureId) is { } pic && pic != MyProfilePictureId)
            SaveProfilePicture(pic);
    }

    // a job's last word (ok or "failed:..."), "timeout" once it's run past secs, or null while it's still going
    private static string? Settled(string? state, string ok, DateTime since, int secs) =>
        state == ok || state?.StartsWith("failed", StringComparison.Ordinal) == true ? state
        : DateTime.UtcNow - since > TimeSpan.FromSeconds(secs) ? "timeout" : null;

    // a request the hook hasn't taken yet never runs for nobody
    private void DiscardRequest(string ext)
    {
        if (_pol.Pid is { } pid) HookFiles.Discard(pid, ext);
    }

    // profile server reads go one at a time in the hook, so a clock only starts once the hook's taken that read
    private static void WaitingTurn(ref DateTime since, string? token, string hookToken, PolSocialSnapshot snap)
    {
        if (token is not null && hookToken != token && snap.ProfileServerBusy) since = DateTime.UtcNow;
    }

    private string? ViewerFolder() =>
        _settings.DetectedRegion is { } r ? PolPaths.TryGetInstallFolder(r, GameProduct.Viewer) : null;

    // the viewer's status menu has these three, only the viewer itself ever sets Busy
    private static readonly PolStatus[] StatusChoices = [PolStatus.Online, PolStatus.Away, PolStatus.AppearOffline];
    public sealed record StatusItem(PolStatus Status, string Label)
    {
        public override string ToString() => Label;
    }
    public IReadOnlyList<StatusItem> StatusItems { get; } = StatusChoices.Select(s => new StatusItem(s, PolStatusText.Label(s))).ToList();
    [ObservableProperty] private int _myStatusIndex;
    [ObservableProperty] private string _statusNote = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusEditable))] private bool _statusPending;
    public bool StatusEditable => !StatusPending && !GameRunning && !HandleSwitching;
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
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _gameRunning;
    public const string GameRunningNote = "That has to wait till FINAL FANTASY XI is closed.";

    partial void OnGameRunningChanged(bool value)
    {
        StatusNote = value ? GameRunningNote : "";
        OnPropertyChanged(nameof(CanGroupChat));
    }

    // more than one account running means lean sessions, which have no chat at all
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanGroupChat))] private bool _groupChatBlocked;
    public bool CanGroupChat => Connected && !GameRunning && !HandleSwitching && !GroupChatBlocked;

    partial void OnConnectedChanged(bool value) => OnPropertyChanged(nameof(CanGroupChat));

    // the Groups tab's Group Chat button. The main window asks the handle question and opens the window
    public event Action<GroupItem>? GroupChatRequested;

    [RelayCommand]
    private void OpenGroupChat(GroupItem? g)
    {
        if (g is { CanChat: true } && CanGroupChat) GroupChatRequested?.Invoke(g);
    }
    public bool CanCompose => SelectedFriend is { CanMessage: true } && !GameRunning && !HandleSwitching;

    // a handle switch going: nothing writes till it's done, it'd go from the new handle
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompose), nameof(ComposeNote), nameof(CanEdit), nameof(StatusEditable))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _handleSwitching;
    public const string SwitchingNote = "Wait for the handle switch to finish.";

    partial void OnHandleSwitchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGroupChat));
        if (!value && EditNote.Text == SwitchingNote) EditNote.Clear();
        if (GameRunning) return;
        StatusNote = value ? SwitchingNote : StatusNote == SwitchingNote ? "" : StatusNote;
    }

    // the box counts in the bytes FFXI goes by, a Japanese character's two
    public ComposeRules ComposeRules { get; } = new() { MaxBytes = () => PolMessage.MaxTextBytes };
    public int ComposeBytes => ShiftJis.ByteCount(Compose.Trim());
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
        _ when HandleSwitching => SwitchingNote,
        { CanMessage: true } when ComposeCountShown && ComposeHasWide =>
            ShiftJis.HasDoubleByte(Compose) ? "Japanese characters count as two." : "Some characters count as two.",
        { CanMessage: true, Online: true } => "This sends a PlayOnline message.",
        { CanMessage: true } f => $"{f.Name}'s offline, so they'll get this next time they log in.",
        { Pending: true } f => $"You can message {f.Name} once they accept your friend request.",
        { IsRequest: true } f => $"Accept {f.Name}'s friend request to send them messages.",
        { IsGroupInvite: true } => "You can't reply to a group invitation, only accept or decline it.",
        var f => $"PlayOnline messages only go to friends. Add {f.Name} as a friend to reply.",
    };

    private readonly MailSendQueue _sends = new();
    private readonly ChatSync _sync;
    // answered requests the hook may delete now, kept until the ack file's written so they can't come back as pending
    private readonly HashSet<int> _ackPending = new();
    private readonly HashSet<int> _answered = new();
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
            bool changed = _friendsListVisible != value;
            _friendsListVisible = value;
            if (changed)
            {
                OnPropertyChanged();
                OnPropertyChanged(nameof(FriendsTabVisible));
                OnPropertyChanged(nameof(GroupsTabVisible));
            }
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
        _ackPending.RemoveWhere(_sync.IsAcked);
        if (_ackPending.Count == 0 || !SocialRequests.Ack(pid, _ackPending.ToList())) return;
        _sync.MarkAcked(_ackPending);
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

    // a new login, maybe on another account, so nothing from the last one can show under this one
    public void Start(string profileId, string? storeRoot = null)
    {
        _last = PolSocialSnapshot.Empty;
        _chat = new ChatStore(profileId, storeRoot);
        GroupChat.SetLog(new GroupChatLog(profileId, storeRoot));
        _chat.ExpireSending(TimeSpan.FromMinutes(3));   // lines a closed session never answered
        _friendsSignature = "";
        _sync.Reset();
        _ackPending.Clear();
        _drafts.Clear();
        _requestedStatus = null;
        StatusPending = false;
        StatusNote = "";
        _draftFor = null;
        _draftPerson = null;
        Compose = "";
        _answered.Clear();
        _shownRequests.Clear();
        _onlineFriends.Clear();
        _presenceQuietUntil = null;
        _lookups?.Reset();
        if (_profileToken is not null || _ffxiToken is not null) ProfileNote.Clear();   // nothing's coming for those
        _answerToken = _editToken = _picToken = _searchToken = _requestToken = _profileToken = _groupToken = _ffxiToken = null;
        ProfileLoading = false;
        _profileCache.Clear();   // a new hook has none of the slots the cached profiles' tabs read
        _answerFor = null;
        _sends.Reset();
        GameRunning = false;
        _groupsSignature = "";
        Groups.Clear();
        HasGroups = false;
        EditBusy = false;
        ProfilePictureSaving = false;
        SearchBusy = false;
        EditNote.Clear();
        SearchNote.Clear();
        SearchHits.Clear();
        Conversation.Clear();
        SelectedFriend = null;
        Friends.Clear();
        Blocked.Clear();
        HasBlocked = false;
        OnlineCount = UnreadCount = PendingAnswers = 0;
        MyComment = "";
        Avatar = null;
        AccountNotice = PlayBlockedReason = null;
        MessagesUnavailable = false;
        ShowGroups = false;
        ClearProfile();
        RefreshUnseen();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(NoFriends));
    }

    public void Apply(PolSocialSnapshot snap)
    {
        _applyingSnapshot = true;
        try { ApplyCore(snap); }
        finally { _applyingSnapshot = false; }
        // a popup's modal loop lets the next snapshot in, so it waits till this one's all applied
        if (_popupAfterApply is { } text)
        {
            _popupAfterApply = null;
            ProfilePopup?.Invoke(text);
        }
    }

    private string? _popupAfterApply;
    internal bool ApplyingSnapshot => _applyingSnapshot;

    public PolSocialSnapshot Last => _last;

    private void ApplyCore(PolSocialSnapshot snap)
    {
        // a friend's profile shows what they let this handle see, so ones read under another handle go
        if (_last.MyKey.Length > 0 && snap.MyKey.Length > 0 && _last.MyKey != snap.MyKey) ForgetProfiles();
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
        MessagesUnavailable = snap.Mail == "failed";
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
                StatusNote = HookFailures.Friends.StatusNoAnswer;
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
        PlayBlockedReason = PolAccountNotices.PlayBlockedReason(snap);
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

    private bool MergeChat(ChatStore chat, PolSocialSnapshot snap)
    {
        (string, string)? open = ConversationVisible && SelectedFriend is { } f ? (f.Name, f.Key) : null;
        var r = _sync.Merge(chat, snap, _pol.Pid, GameRunning || HandleSwitching, open, NoticeText, Filtered);
        if (r.SendsChanged) RefreshConversation();
        foreach (var n in r.News)
            switch (n.Kind)
            {
                case ChatNewsKind.Notice: NoticeReceived?.Invoke(n.From, n.Text); break;
                case ChatNewsKind.GroupInvite: GroupInviteReceived?.Invoke(n.From, n.Mid); break;
                case ChatNewsKind.FriendRequest: FriendRequestReceived?.Invoke(n.From, n.Mid); break;
                default: MessageReceived?.Invoke(n.From, n.Text); break;
            }
        if (!r.Kept || (r.ToAck.Count > 0 && _pol.Pid is { } pid && !SocialRequests.Ack(pid, r.ToAck.ToList())))
            _sync.Unack(r.ToAck);
        return r.Changed;
    }

    // what's still on its way under the active handle. A switch waits for it, it'd go from the new one otherwise
    internal string? PendingWrite() =>
        _sends.Going ? "Wait for your messages to finish sending first."
        : _editToken != null ? "Wait for your friend list change to finish first."
        : _answerToken != null || _requestToken != null ? "Wait for your friend request to finish first."
        : _groupToken != null ? "Wait for your friend group change to finish first."
        : _picToken != null ? "Wait for your profile picture to finish saving first."
        : StatusPending ? "Wait for your status change to finish first."
        : null;

    private void RebuildFriends(PolSocialSnapshot snap)
    {
        var items = snap.Friends.Select(f =>
        {
            var (playing, playingShort) = Playing(f);
            return new FriendItem(
                f.Name, playing, f.Pending ? "" : Filtered(f.Comment),
                f.Pending ? "Friend request sent" : f.Online ? f.StatusLabel : "Offline", f.Online && !f.Pending, f.Row,
                _avatars.ProfilePicture(f.ProfilePictureId), _chat?.UnreadFrom(f.Name, f.Key) ?? 0,
                f.Pending ? FriendSection.RequestsSent : f.Online ? FriendSection.Online : FriendSection.Offline)
            { Key = f.Key, Status = f.Status, Pending = f.Pending, IsMe = f.IsMe, PlayingShort = playingShort, InGame = f.Online && !f.Pending && f.Game is not null };
        }).ToList();
        // invitations (kind 0xe) and requests (kind 1) stay on the server until they're answered
        foreach (var m in snap.Messages.Where(m => m.HasBody && m.Mid > 0 && !_answered.Contains(m.Mid)))
            if (m.Kind == PolMessage.KindGroupInvite)
                items.Add(new FriendItem(m.From, "", m.Text.Length > 0 ? Filtered(m.Text) : "Group invitation", "", false, -1,
                                         _avatars.NoProfilePicture, 0, FriendSection.GroupInvitations)
                          { Key = m.FromKey, RequestMid = m.Mid, IsGroupInvite = true });
            else if (m.Kind == PolMessage.KindFriendRequest)
                items.Add(new FriendItem(m.From, "", "Wants to be your friend", "", false, -1,
                                         _avatars.NoProfilePicture, 0, FriendSection.FriendRequests)
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
                                         FriendSection.Others) { Key = last.PeerKey });
            }
        }
        items = items.OrderBy(i => i.Section)
                     .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        // counted, so an empty list never matches the "" that forces a rebuild and leaves the old rows up
        var sig = items.Count + ":" + string.Join("|", items.Select(i => $"{i.Name}/{i.Key}/{i.StatusLabel}/{i.Playing}/{i.PlayingShort}/{i.Comment}/" +
                                                     $"{i.Unread}/{i.Avatar != null}/{i.Section}/{i.Row}/" +
                                                     $"{i.Pending}/{i.RequestMid}/{i.InGame}"));
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
                    (a.Section is FriendSection.Online or FriendSection.Offline && b.Section is FriendSection.Online or FriendSection.Offline));

    private bool _conversationVisible;
    private DateTime _conversationDay;   // the day the "Today" headings were made on

    public bool ConversationVisible
    {
        get => _conversationVisible;
        set
        {
            if (_conversationVisible == value) return;
            _conversationVisible = value;
            OnPropertyChanged();
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
    public StatusLine EditNote { get; } = new();
    public bool EditNoteShown => EditNote.Text.Length > 0 && !EditBusy && !ProfilePictureSaving;
    public bool Saving => EditBusy || ProfilePictureSaving;
    public string EditStatus => EditBusy ? "Saving the change to PlayOnline..."
                              : ProfilePictureSaving ? "Saving your profile picture to PlayOnline..." : EditNote.Text;
    public bool EditStatusShown => Saving || !string.IsNullOrWhiteSpace(EditNote.Text);
    public bool CanEdit => Connected && !EditBusy && _pol.Pid != null && !GameRunning && !HandleSwitching;

    private string? _editToken;
    private DateTime _editAt;
    private const string RequestWriteFailed = HookFailures.RequestWriteFailed;

    [RelayCommand]
    private void DismissEditNote() => EditNote.Clear();

    private void WriteOrFail(bool written)
    {
        if (written) return;
        _editToken = null;
        _answerToken = null;
        _answerFor = null;
        EditBusy = false;
        EditNote.Fail(RequestWriteFailed);
    }

    private bool BeginEdit(out int pid, out string token)
    {
        token = HookFiles.NewToken();
        pid = _pol.Pid ?? 0;
        if (!CanEdit || pid == 0) return false;
        _editToken = token;
        _editAt = DateTime.UtcNow;
        EditBusy = true;
        EditNote.Clear();
        return true;
    }

    private void EditFriend(FriendEdit op, int row, string key, string arg = "")
    {
        if (BeginEdit(out var pid, out var token))
            WriteOrFail(SocialRequests.EditFriend(pid, op, token, row, key, arg));
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
        if (BeginEdit(out var pid, out var token)) WriteOrFail(SocialRequests.SetComment(pid, token, text.Trim()));
    }

    private void ApplyEditState(PolSocialSnapshot snap)
    {
        if (_editToken is null) return;
        // polcore gives up after 100 secs
        if (Settled(snap.EditToken == _editToken ? snap.EditState : null, "saved", _editAt, 110) is not { } st) return;
        _editToken = null;
        EditBusy = false;
        if (st != "saved") EditNote.Fail(EditFailureText(st));
    }

    private static string EditFailureText(string state) => state switch
    {
        "timeout" => HookFailures.Friends.EditNoAnswer,
        "failed:busy" => HookFailures.Busy,
        "failed:pending" => HookFailures.Friends.DeleteOnly,
        "failed:self" => HookFailures.Friends.IgnoreSelf,
        "failed:friend list changed" or "failed:no such friend" => HookFailures.FriendListChanged,
        "failed:list changed" or "failed:no such entry" => HookFailures.Friends.IgnoreListChanged,
        "failed:bad name" => HookFailures.Friends.BadName,
        "failed:handle changing" => HookFailures.Friends.EditHandleChanging,
        "failed:reloaded" => HookFailures.Friends.EditReloaded,
        "failed:-7187" => HookFailures.IgnoredAlready,
        "failed:-7188" or "failed:-516" => HookFailures.Friends.IgnoreListFull,
        _ => HookFailures.ChangeRejected(SocialRequests.FailReason(state)),
    };

    internal string? _answerToken;
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
            EditNote.Fail(HookFailures.Friends.OwnRequest);
            return;
        }
        if (answer == RequestAnswer.Accept && Friends.Count(x => x.IsFriend) >= 200)
        {
            EditNote.Fail(HookFailures.FriendListFull);
            return;
        }
        if (answer == RequestAnswer.Ignore && f.Key.Length > 0 && Blocked.Any(x => x.Key == f.Key))
        {
            EditNote.Fail($"{f.Name} is already on your ignore list.");
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
        WriteOrFail(SocialRequests.AnswerRequest(pid, answer, token, f.RequestMid, subject, allHandles: false));
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
        EditNote.Fail(st switch
        {
            "timeout" => HookFailures.Friends.AnswerNoReply,
            "failed:handle changing" => HookFailures.Friends.AnswerHandleChanging,
            "failed:busy" => HookFailures.Busy,
            "failed:no handle" => HookFailures.Friends.AnswerHandleGone,
            "failed:no such request" => HookFailures.Friends.NoSuchRequest,
            "failed:self" => HookFailures.Friends.OwnRequest,
            "failed:already ignored" => HookFailures.IgnoredAlready,
            "failed:-7188" => HookFailures.FriendListFull,
            "failed:no such friend" => HookFailures.Friends.AnswerFriendGone,
            "failed:stopped" => HookFailures.Friends.AnswerStopped,
            "failed:hourly limit" => HookFailures.Friends.AnswerHourlyLimit,
            var s when s.StartsWith("failed:list", StringComparison.Ordinal) =>
                HookFailures.Friends.AnswerListNotSaved,
            var s => HookFailures.Friends.AnswerRejected(SocialRequests.FailReason(s)),
        });
    }

    // Name is the character's for a character hit, which shows like a friend in game does
    public sealed record SearchHitItem(int Index, string Name, ImageSource? Avatar, bool IsFriend, string Key,
                                       bool RequestSent = false, bool IsMe = false)
    {
        public bool IsCharacter { get; init; }
        public string World { get; init; } = "";
        public string Label => IsCharacter && World.Length > 0 ? PlayingText.For(Name, World).Long : Name;
        public string Note =>
            IsMe ? "This is you" : IsFriend ? "Already on your friend list" : RequestSent ? "Friend request sent"
            : IsCharacter && World.Length == 0 ? "World not shown" : "";
    }

    public ObservableCollection<SearchHitItem> SearchHits { get; } = new();
    [ObservableProperty] private bool _searchBusy;
    public StatusLine SearchNote { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchByHandle), nameof(SearchByHid), nameof(SearchByCharacter), nameof(SearchHint),
                              nameof(SearchIntro), nameof(SearchMaxLength))]
    private FriendSearchKind _searchKind;
    // the picker's off while a search runs, so the hits always match the kind showing
    partial void OnSearchKindChanged(FriendSearchKind value)
    {
        if (SearchBusy) return;
        SearchHits.Clear();
        SearchNote.Clear();
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
        FriendSearchKind.Character => "Search for a FINAL FANTASY XI character by name. The same name can be on more than one world, so check the world (a private profile doesn't show it).",
        _ => "Search PlayOnline for a handle name. Many people can share one name, so check the picture.",
    };
    public int SearchMaxLength => SearchKind == FriendSearchKind.Hid ? PolLimits.HidSearch : PolLimits.HandleName;

    private string? _searchToken;
    internal DateTime _searchAt;
    private FriendSearchKind _searchedKind;
    private string _searchedValue = "";
    private string? _requestToken;
    private string _requestName = "";
    private string _requestKey = "";
    private bool _requestCharacter;
    private DateTime _requestAt;
    private const string BadHandleName = HookFailures.Friends.BadHandleName;
    private const string QuoteInName = HookFailures.Friends.QuoteInName;
    private const string BadHid = HookFailures.Friends.BadHid;
    private const string BadCharacterName = HookFailures.Friends.BadCharacterName;
    private const int SearchPage = 100;   // the hook fetches the first 100, like the viewer
    private const string TooManyResults = "Too many results. Please narrow your search.";

    public void Search(string text)
    {
        var value = text.Trim();
        var kind = SearchKind;
        if (SearchBusy || !Connected || _pol.Pid is not { } pid) return;
        if (GameRunning)
        {
            SearchNote.Fail(GameRunningNote);
            return;
        }
        if (!FriendSearch.Ok(kind, value))
        {
            SearchNote.Fail(kind switch
            {
                FriendSearchKind.Hid => BadHid,
                FriendSearchKind.Character => BadCharacterName,
                _ => BadHandleName,
            });
            return;
        }
        if (kind == FriendSearchKind.Handle && value.Contains('\''))
        {
            SearchNote.Fail(QuoteInName);
            return;
        }
        _searchToken = HookFiles.NewToken();
        _searchAt = DateTime.UtcNow;
        _searchedKind = kind;
        _searchedValue = value;
        SearchHits.Clear();
        SearchBusy = true;
        SearchNote.Show("Searching PlayOnline...");
        if (SocialRequests.Search(pid, _searchToken, kind, value)) return;
        _searchToken = null;
        SearchBusy = false;
        SearchNote.Fail(RequestWriteFailed);
    }

    // a search nobody's waiting on any more: withdrawn if the hook hasn't taken it yet, and its answer's ignored
    public void CancelSearch()
    {
        if (_searchToken is null) return;
        DiscardRequest(HookContract.Requests.Search);
        _searchToken = null;
        SearchBusy = false;
        SearchNote.Clear();
        SearchHits.Clear();
    }

    public bool CanRequest(SearchHitItem? hit) => HitProblem(hit) is null;

    public string? RequestProblem(SearchHitItem? hit) => HitProblem(hit) ?? FriendRequestBlocked;

    private string? HitProblem(SearchHitItem? hit) => hit switch
    {
        null => SearchKind == FriendSearchKind.Character ? "Choose a character from the list first." : "Choose a handle from the list first.",
        { IsMe: true } => "That's one of your own handles. You can't add yourself as a friend.",
        { IsFriend: true } => HookFailures.Friends.AlreadyFriend(hit.Name),
        { RequestSent: true } => $"You've already sent {hit.Name} a friend request.",
        _ => null,
    };

    internal string? FriendRequestBlocked =>
        CanEdit ? null
        : GameRunning ? GameRunningNote
        : EditBusy ? "Wait for your last change to finish first."
        : "Log in to send friend requests.";

    public void SendFriendRequest(SearchHitItem hit)
    {
        if (hit.IsMe)
        {
            SearchNote.Fail(HookFailures.Friends.RequestSelf);
            return;
        }
        if (hit.IsFriend || hit.RequestSent || _requestToken != null || !BeginEdit(out var pid, out var token)) return;
        _editToken = null;
        _requestToken = token;
        _requestName = hit.Name;
        _requestKey = hit.Key;
        _requestCharacter = hit.IsCharacter;
        _requestAt = DateTime.UtcNow;
        SearchNote.Show($"Sending a friend request to {hit.Name}...");
        if (SocialRequests.SendFriendRequest(pid, token, hit.Index, hit.Key, ViewerText(0x46c9, "Let's be friends!"),
                                             ViewerText(0x46e6, "Would you like to be friends?"))) return;
        _requestToken = null;
        EditBusy = false;
        SearchNote.Fail(RequestWriteFailed);
    }

    private void ApplySearchState(PolSocialSnapshot snap)
    {
        // two profile-server jobs, each up to polcore's 100 secs
        WaitingTurn(ref _searchAt, _searchToken, snap.SearchToken, snap);
        if (_searchToken != null &&
            Settled(snap.SearchToken == _searchToken ? snap.SearchState : null, "done", _searchAt, 250) is { } st)
        {
            if (st == "timeout") DiscardRequest(HookContract.Requests.Search);
            _searchToken = null;
            SearchBusy = false;
            var characters = _searchedKind == FriendSearchKind.Character;
            if (st == "done" && snap.SearchTooMany) SearchNote.Fail(TooManyResults);
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
                // only past the first page were there more than came back, the hook drops a hit with nothing in it
                var total = snap.SearchTotal > SearchPage ? snap.SearchTotal : SearchHits.Count;
                SearchNote.Show(SearchHits.Count == 0 ? _searchedKind switch
                                {
                                    FriendSearchKind.Hid => HookFailures.Friends.NoHidHit,
                                    FriendSearchKind.Character => HookFailures.Friends.NoCharacterHit,
                                    _ => HookFailures.Friends.NoHandleHit,
                                }
                              : total > SearchHits.Count ? $"Found {total} {what}s. Showing {SearchHits.Count} of them."
                              : SearchHits.Count == 1 ? $"Found 1 {what}." : $"Found {SearchHits.Count} {what}s.");
            }
            else SearchNote.Fail(st switch
            {
                "timeout" => HookFailures.Friends.SearchNoAnswer,
                "failed:busy" => HookFailures.Busy,
                "failed:bad name" => BadHandleName,
                "failed:quote" => QuoteInName,
                "failed:bad hid" => BadHid,
                "failed:bad character" => BadCharacterName,
                "failed:no schema" when characters =>
                    HookFailures.Friends.NoSchemaCharacters,
                "failed:no schema" => HookFailures.Friends.NoSchemaHandles,
                _ => HookFailures.Friends.SearchRejected(SocialRequests.FailReason(st)),
            });
        }
        if (_requestToken is null) return;
        if (Settled(snap.SendStateFor(_requestToken), "sent", _requestAt, 250) is not { } rst) return;
        _requestToken = null;
        EditBusy = false;
        if (rst != "sent")
        {
            SearchNote.Fail(rst switch
            {
                "timeout" => HookFailures.Friends.RequestNoAnswer,
                "failed:busy" => HookFailures.Busy,
                "failed:already a friend" => HookFailures.Friends.AlreadyFriend(_requestName),
                "failed:self" => HookFailures.Friends.RequestSelf,
                "failed:-7181" => HookFailures.Friends.IgnoredFirst(_requestName),
                "failed:-7188" or "failed:-516" => HookFailures.FriendListFull,
                "failed:no such result" => HookFailures.Friends.NoSuchResult,
                "failed:no such friend" => HookFailures.Friends.FriendListChangedFor(_requestName),
                "failed:stopped" => HookFailures.Friends.RequestStopped,
                "failed:hourly limit" => HookFailures.Friends.RequestHourlyLimit,
                var s when s.StartsWith("failed:list", StringComparison.Ordinal) =>
                    HookFailures.Friends.RequestListNotSaved,
                var s => HookFailures.Friends.RequestRejected(SocialRequests.FailReason(s)),
            });
            return;
        }
        SearchNote.Show($"Friend request sent to {_requestName}. They'll show on your list{(_requestCharacter ? " under their PlayOnline handle," : "")} as \"Friend request sent\" until they accept.");
        for (int i = 0; i < SearchHits.Count; i++)
            if (SearchHits[i].Name == _requestName && SearchHits[i].Key == _requestKey)
                SearchHits[i] = SearchHits[i] with { RequestSent = true };
    }

    // a pic save is two requests, in the viewer's order
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditNoteShown), nameof(Saving), nameof(EditStatus), nameof(EditStatusShown))]
    private bool _profilePictureSaving;
    private string? _picToken;
    internal DateTime _picAt;

    public void SaveProfilePicture(int picId)
    {
        if (ProfilePictureSaving || !Connected || _pol.Pid is not { } pid) return;
        if (GameRunning || HandleSwitching)
        {
            EditNote.Fail(GameRunning ? GameRunningNote : SwitchingNote);
            return;
        }
        _picToken = HookFiles.NewToken();
        _picAt = DateTime.UtcNow;
        ProfilePictureSaving = true;
        EditNote.Clear();
        if (SocialRequests.SetProfilePicture(pid, picId, _picToken)) return;
        _picToken = null;
        ProfilePictureSaving = false;
        EditNote.Fail(RequestWriteFailed);
    }

    private void ApplyProfilePictureState(PolSocialSnapshot snap)
    {
        if (_picToken is null) return;
        WaitingTurn(ref _picAt, _picToken, snap.ProfilePictureToken, snap);
        // two jobs, each up to 100 secs in polcore
        if (Settled(snap.ProfilePictureToken == _picToken ? snap.ProfilePictureState : null, "saved", _picAt, 250)
            is not { } st) return;
        if (st == "timeout") DiscardRequest(HookContract.Requests.Pic);
        _picToken = null;
        ProfilePictureSaving = false;
        if (st == "saved") return;
        EditNote.Fail(st switch
        {
            "timeout" => HookFailures.Friends.PictureNoAnswer,
            "failed:handle changing" => HookFailures.Friends.PictureHandleChanging,
            "failed:busy" => HookFailures.Busy,
            _ => HookFailures.Friends.PictureRejected(SocialRequests.FailReason(st)),
        });
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

    // this PC's copy only. It stays gone, the next login's read of the game's store won't bring it back
    public void DeleteLine(ChatEntry e)
    {
        if (_chat == null || !ChatStore.CanDelete(e) || !_chat.Delete(e.Key)) return;
        AfterDelete();
    }

    // only the lines that were on screen when Clear was picked, not ones that landed while it asked
    public void ClearConversation(IReadOnlyCollection<string> keys)
    {
        if (_chat == null || _chat.DeleteKeys(keys) == 0) return;
        AfterDelete();
    }

    private void AfterDelete()
    {
        RefreshConversation();
        UnreadCount = _chat?.Unread ?? 0;
        _friendsSignature = "";
        RebuildFriends(_last);
    }

    public void Resend(ChatEntry e)
    {
        if (!e.Failed || _chat == null || !CanCompose || SelectedFriend is not { } f || _pol.Pid is null) return;
        if (!_chat.RemoveFailed(e.Key)) return;
        Queue(f, e.Text);
        RefreshConversation();
    }

    private void Queue(FriendItem f, string text)
    {
        if (_sends.Add(_chat!, f.Row, f.Name, f.Key, text, _pol.Pid, GameRunning || HandleSwitching))
            RefreshConversation();
    }

    // the session's ending and its results won't get here
    public void SettleSends(SessionEnd end)
    {
        _sends.Settle(_chat, end != SessionEnd.Dropped, end == SessionEnd.Play ? "failed:game" : "failed:session");
        RefreshConversation();
    }

    partial void OnMyStatusIndexChanged(int value)
    {
        if (_applyingSnapshot || GameRunning || HandleSwitching || value < 0 || value >= StatusChoices.Length) return;
        if (StatusPending) return;
        var want = StatusChoices[value];
        _requestedStatus = want;
        _requestedAt = DateTime.UtcNow;
        StatusPending = true;
        StatusNote = "";
        _settings.Current.PlayOnlineStatus = PolStatusText.Code(want);
        _settings.Save(_settings.Current);
        if (_pol.Pid is { } pid) SocialRequests.SetStatus(pid, want);
    }
}
