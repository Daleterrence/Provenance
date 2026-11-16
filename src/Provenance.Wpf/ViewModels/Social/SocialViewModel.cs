using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Game;
using Provenance.Common.Hook;
using Provenance.Common.Install;
using Provenance.Common.Login;
using Provenance.Common.Logging;
using Provenance.Common.PolFormat;
using Provenance.Common.Settings;
using Provenance.Common.Social;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Chat;
using Provenance.Wpf.ViewModels.Main;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Social;

public partial class SocialViewModel : ObservableObject, ISocialSession
{
    private readonly IDialogs _dialogs;
    private readonly IWindowManager _windows;
    private readonly PolSession _pol;
    private readonly AvatarService _avatars;
    private readonly ISettingsService _settings;
    private readonly SessionState _session;
    private readonly TimeProvider _time;
    private readonly ViewerResources _viewer;
    private SocialSession _login = new();

    public SocialViewModel(IDialogs dialogs, IWindowManager windows, PolSession pol, AvatarService avatars, ISettingsService settings,
                           SessionState session, ViewerResources viewer, TimeProvider? time = null)
    {
        _session = session;
        _viewer = viewer;
        _time = time ?? TimeProvider.System;
        _dialogs = dialogs;
        _windows = windows;
        _pol = pol;
        _avatars = avatars;
        _settings = settings;
        Search = new(this);
        Messages = new(this);
        Profile = new(this);
        Groups = new(this);
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
            Groups.MarkDirty();
            Groups.Rebuild(_login.Last);
            OnPropertyChanged(nameof(GroupChatUnread));
        };
        session.PhaseChanged += OnPhaseChanged;
    }

    public SocialSearch Search { get; }
    public SocialMessages Messages { get; }
    public FriendProfileViewModel Profile { get; }
    public GroupsViewModel Groups { get; }
    public GroupChatViewModel GroupChat { get; }
    public int GroupChatUnread => GroupChat.UnreadTotal;

    public ObservableCollection<FriendItem> Friends { get; } = new();
    public ICollectionView FriendsView { get; }
    public ObservableCollection<PolBlocked> Blocked { get; } = new();

    [ObservableProperty] private string _accountName = "PlayOnline";
    [ObservableProperty] private ImageSource? _avatar;
    [ObservableProperty] private int _onlineCount;
    [ObservableProperty] private string _myComment = "";
    [ObservableProperty] private bool _hasBlocked;
    [ObservableProperty] private string? _accountNotice;
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
    // requests get a badge too (on Friends), so one's never just an 8s banner
    public bool HasPendingAnswers => PendingAnswers > 0;
    public bool CanPlay => PlayBlockedReason is null;
    public string Summary => OnlineCount switch { 0 => "No friends online", 1 => "1 friend online", var n => $"{n} friends online" };
    public bool NoFriends => LoggedIn && Friends.Count == 0;
    public bool NoFriendsInList => NoFriends && !FriendsUnavailable;

    private bool _goingOnline;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FriendsUnavailable), nameof(NoFriendsInList))] private bool _onlineFailed;
    public bool PopShown => PopText.Length > 0 && LoggedIn && !GameRunning;
    public bool FriendsUnavailable => OnlineFailed && LoggedIn;
    public bool LoggedIn => _session.LoggedIn;
    public string NothingSelected => NoFriends ? NoFriendsText
                                               : "Select a friend to see your messages.";
    private const string NoFriendsText = "You don't have any friends on this PlayOnline account yet.";
    public SessionState Session => _session;

    public event Action<FriendItem>? OpenChatRequested;
    public event Action<string, string>? MessageReceived;
    public event Action<string, string>? NoticeReceived;
    public event Action<string, int>? FriendRequestReceived;
    public event Action<string, int>? GroupInviteReceived;
    public event Action<string>? FriendCameOnline;          // never for the presence that lands at login
    public event Action<PolSocialSnapshot>? SnapshotApplied;

    public void RequestChat(FriendItem friend) => OpenChatRequested?.Invoke(friend);

    [RelayCommand]
    private void ChangeProfilePicture()
    {
        if (WriteBlockedNote is { } blocked) EditNote.Fail(blocked);
        else if (!ProfilePictureSaving) PickProfilePicture();
    }

    private void PickProfilePicture()
    {
        var catalog = _viewer.Pictures;
        if (catalog is null)
        {
            _dialogs.Error("Profile picture", "Provenance couldn't find the PlayOnline profile pictures in the PlayOnline Viewer folder.");
            return;
        }
        if (_windows.PickProfilePicture(catalog, _avatars, MyProfilePictureId) is { } pic && pic != MyProfilePictureId)
            SaveProfilePicture(pic);
    }

    public DateTime Now => _time.GetUtcNow().UtcDateTime;

    internal IDialogs Dialogs => _dialogs;
    internal AvatarService Avatars => _avatars;
    internal ChatStore? Chat => _login.Chat;
    internal SocialSession Login => _login;

    // nobody's waiting on it any more, so a request the hook hasn't taken yet doesn't run
    internal void DiscardRequest(string ext)
    {
        if (_pol.Pid is { } pid) HookFiles.Discard(pid, ext);
    }

    public ViewerResources Viewer => _viewer;

    public IDictionary<string, PolFfxiProfile> OwnCharacterProfiles => _login.OwnCharacterProfiles;

    // the viewer's status menu has these three, only the viewer itself ever sets Busy
    private static readonly PolStatus[] StatusChoices = [PolStatus.Online, PolStatus.Away, PolStatus.AppearOffline];
    public sealed record StatusItem(PolStatus Status, string Label)
    {
        public override string ToString() => Label;
    }
    public IReadOnlyList<StatusItem> StatusItems { get; } = StatusChoices.Select(s => new StatusItem(s, PolStatusText.Label(s))).ToList();
    [ObservableProperty] private int _myStatusIndex;
    // one card above the bottom bar, the newest message wins and an error stays up for PopErrorTime
    internal static readonly TimeSpan PopErrorTime = TimeSpan.FromSeconds(6);
    private const string StatusWaiting = "Changing your online status...";
    public static string StatusChanging(PolStatus status) => $"Changing your online status to {PolStatusText.Label(status)}...";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PopShown))] private string _popText = "";
    [ObservableProperty] private bool _popIsError;
    [ObservableProperty] private string _statusPendingText = StatusWaiting;

    private void RefreshPop()
    {
        string text = "";
        bool error = false;
        if (_login.PopError is { } failed && Now < _login.PopErrorUntil) (text, error) = (failed, true);
        else
        {
            _login.PopError = null;
            if (_login.RequestedStatus is { } want) text = StatusChanging(want);
            else if (_goingOnline) text = "Going online...";
        }
        PopText = text;
        if (text.Length > 0) PopIsError = error;
        StatusPendingText = _login.RequestedStatus is { } asked ? StatusChanging(asked) : StatusWaiting;
    }

    private void PopStatusError(string text)
    {
        _login.PopError = text;
        _login.PopErrorUntil = Now + PopErrorTime;
    }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusEditable))] private bool _statusPending;
    public bool StatusEditable => !StatusPending && WriteBlockedNote is null;
    private bool _applyingSnapshot;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection), nameof(NoSelection))]
    private FriendItem? _selectedFriend;
    public bool HasSelection => SelectedFriend != null;
    public bool NoSelection => SelectedFriend == null;

    // from the Play press till the game closes nothing new goes to the hook, answers already asked for still land
    public bool GameRunning => _session.GameRunning;
    public const string GameRunningNote = "That has to wait till FINAL FANTASY XI is closed.";

    private LastRaised _gameRunningShown;

    private void OnPhaseChanged()
    {
        OnPropertyChanged(nameof(LoggedIn));
        OnPropertyChanged(nameof(NoFriends));
        OnPropertyChanged(nameof(NoFriendsInList));
        OnPropertyChanged(nameof(FriendsUnavailable));
        OnPropertyChanged(nameof(PopShown));
        OnPropertyChanged(nameof(NothingSelected));
        bool running = _session.GameRunning;
        if (_gameRunningShown.Changed(running))
        {
            Messages.StateChanged();
            OnPropertyChanged(nameof(GameRunning));
            OnPropertyChanged(nameof(StatusEditable));
        }
        OnPropertyChanged(nameof(CanGroupChat));
        OnPropertyChanged(nameof(CanEdit));
    }

    public bool FriendsTabVisible => FriendsListVisible;

    // more than one account running means lean sessions, which have no chat at all
    public bool CanGroupChat => LoggedIn && WriteBlockedNote is null && !_session.InMultiRun;

    public event Action<GroupsViewModel.GroupItem>? GroupChatRequested;

    [RelayCommand]
    private void OpenGroupChat(GroupsViewModel.GroupItem? g)
    {
        if (g is { CanChat: true } && CanGroupChat) GroupChatRequested?.Invoke(g);
    }

    // a handle switch going: nothing writes till it's done, it'd go from the new handle
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(StatusEditable))]
    private bool _handleSwitching;
    public const string SwitchingNote = "Wait for the handle switch to finish.";

    string? ISocialSession.LeavingGroupId => Groups.LeavingGroupId;
    System.Windows.Input.ICommand ISocialSession.ChangeProfilePictureCommand => ChangeProfilePictureCommand;

    public string? WriteBlockedNote => GameRunning ? GameRunningNote : HandleSwitching ? SwitchingNote : null;

    partial void OnHandleSwitchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGroupChat));
        Messages.StateChanged();
        if (!value && EditNote.Text == SwitchingNote) EditNote.Clear();
    }

    private bool _friendsListVisible, _groupsWindowVisible, _mainWindowActive;

    public bool IsWaiting(int mid) => mid > 0 && Friends.Any(f => f.RequestMid == mid);

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
            }
            RefreshUnseen();
        }
    }

    public bool GroupsWindowVisible
    {
        get => _groupsWindowVisible;
        set
        {
            bool changed = _groupsWindowVisible != value;
            _groupsWindowVisible = value;
            if (changed) OnPropertyChanged();
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
        if (_friendsListVisible || _mainWindowActive) _login.ShownRequests.UnionWith(waiting);
        // the groups window only lists group invitations
        else if (_groupsWindowVisible) _login.ShownRequests.UnionWith(Friends.Where(f => f.IsGroupInvite).Select(f => f.RequestMid));
        UnseenCount = UnreadCount + waiting.Count(m => !_login.ShownRequests.Contains(m));
    }

    partial void OnUnreadCountChanged(int value) => RefreshUnseen();

    private void FlushAcks()
    {
        if (_login.AckPending.Count == 0 || _pol.Pid is not { } pid) return;
        _login.AckPending.RemoveWhere(_login.Sync.IsAcked);
        if (_login.AckPending.Count == 0 || !SocialRequests.Ack(pid, _login.AckPending.ToList())) return;
        _login.Sync.MarkAcked(_login.AckPending);
        _login.AckPending.Clear();
    }

    internal void Answered(int mid, bool ack)
    {
        _login.Answered.Add(mid);
        _login.FriendsDirty = true;
        if (!ack) return;
        _login.AckPending.Add(mid);
        FlushAcks();
    }

    private string? NoticeText(PolMessage m) => m.Kind switch
    {
        PolMessage.KindFriendAccepted => "Accepted your friend request.",
        PolMessage.KindFriendDeclined => "Declined your friend request.",
        PolMessage.KindGroupAccepted => "Accepted your group invitation.",
        PolMessage.KindGroupDeclined => "Declined your group invitation.",
        PolMessage.KindGroupRemoved => m.Group.Length == 0 ? "You have been removed from a friend group."
            : TextBits.OneLine(_viewer.Text(0x46dd, "You have been removed from the group \"%s.\"")).Replace("%s", m.Group),
        PolMessage.KindGroupDisbanded => m.Group.Length == 0 ? "A friend group you were in has been disbanded."
            : TextBits.OneLine(_viewer.Text(0x46db, "The group \"%s\" has been disbanded.")).Replace("%s", m.Group),
        _ => null,
    };

    private void NoticeComingOnline(PolSocialSnapshot snap)
    {
        static string Id(PolFriend f) => f.Key.Length > 0 ? f.Key : "n:" + f.Name;
        if (!snap.FriendsLoaded) return;   // an empty list isn't "everyone went offline"
        var now = snap.Friends.Where(f => f.Online && !f.Pending).ToList();
        // everyone already online turns up in the first 30s, only later changes are news
        _login.PresenceQuietUntil ??= Now + TimeSpan.FromSeconds(30);
        if (Now >= _login.PresenceQuietUntil)
            foreach (var f in now)
                if (!_login.OnlineFriends.Contains(Id(f))) FriendCameOnline?.Invoke(f.Name);
        _login.OnlineFriends = now.Select(Id).ToHashSet();
    }

    // a new login, maybe on another account, so nothing from the last one can show under this one
    public void Start(string profileId, string? storeRoot = null)
    {
        _login.Chat?.Flush();
        var chat = new ChatStore(profileId, storeRoot);
        _login = new SocialSession { Chat = chat };
        GroupChat.SetLog(new GroupChatLog(profileId, storeRoot));
        chat.ExpireSending(TimeSpan.FromMinutes(3));   // lines a closed session never answered
        StatusPending = false;
        _lookups?.Reset();
        Profile.Reset();
        Search.Reset();
        Messages.Reset();
        Groups.Reset();
        EditBusy = false;
        ProfilePictureSaving = false;
        EditNote.Clear();
        SelectedFriend = null;
        Friends.Clear();
        Blocked.Clear();
        HasBlocked = false;
        OnlineCount = UnreadCount = PendingAnswers = 0;
        MyComment = "";
        AccountName = "PlayOnline";
        Avatar = null;
        AccountNotice = PlayBlockedReason = null;
        MessagesUnavailable = false;
        _goingOnline = false;
        OnlineFailed = false;
        RefreshPop();
        RefreshUnseen();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(NoFriends));
        OnPropertyChanged(nameof(NoFriendsInList));
    }

    public void Apply(PolSocialSnapshot snap)
    {
        _applyingSnapshot = true;
        try { ApplyCore(snap); }
        finally { _applyingSnapshot = false; }
        Profile.RaisePopup();
    }

    internal bool ApplyingSnapshot => _applyingSnapshot;

    public PolSocialSnapshot Last => _login.Last;

    private void ApplyCore(PolSocialSnapshot snap)
    {
        var timer = ReferenceEquals(_login.Last, PolSocialSnapshot.Empty) ? new StepTimer("First friends and messages apply") : null;
        // a friend's profile shows what they let this handle see, so ones read under another handle go
        if (_login.Last.MyKey.Length > 0 && snap.MyKey.Length > 0 && _login.Last.MyKey != snap.MyKey) Profile.Forget();
        _login.Last = snap;
        bool chatChanged = _login.Chat != null && MergeChat(_login.Chat, snap);
        timer?.Lap("merge chat");
        if (_viewer.FilterOn != _filterWasOn)
        {
            _filterWasOn = _viewer.FilterOn;
            _login.FriendsDirty = true;
            Groups.MarkDirty();
            chatChanged = true;
        }
        FlushAcks();
        // my own jobs first, so a failure never leaves a lock behind and an answered request's gone before the redraw
        ApplyEditState(snap);
        Search.Apply(snap);
        ApplyProfilePictureState(snap);
        Profile.Apply(snap);
        Groups.Apply(snap);
        ApplyCharacters(snap);
        timer?.Lap("jobs, profile and characters");
        Groups.Rebuild(snap);
        timer?.Lap("groups");
        MessagesUnavailable = snap.Mail == "failed";
        RebuildFriends(snap);
        timer?.Lap("friends");
        NoticeComingOnline(snap);
        OnlineCount = snap.Friends.Count(f => f.Online && !f.Pending);
        UnreadCount = _login.Chat?.Unread ?? 0;
        if (SelectedFriend != null && Messages.DayChanged) Messages.Refresh();   // past midnight
        PendingAnswers = Friends.Count(f => f.IsRequest || f.IsGroupInvite);
        RefreshUnseen();

        if (_login.RequestedStatus is { } want)
        {
            if (!snap.StatusBusy && snap.MyStatus == want)
            {
                _login.RequestedStatus = null;
            }
            else if (!snap.StatusBusy && Now - _login.RequestedAt > HookLimits.StatusRefused)
            {
                _login.RequestedStatus = null;
                PopStatusError(HookFailures.Friends.StatusRefused(PolStatusText.Label(want)));
            }
            else if (Now - _login.RequestedAt > HookLimits.Status)
            {
                _login.RequestedStatus = null;   // polcore gives up after 100s
                PopStatusError(HookFailures.Friends.StatusNoAnswer(PolStatusText.Label(want)));
            }
        }
        StatusPending = _login.RequestedStatus != null || snap.StatusBusy;
        if (_login.RequestedStatus is null)
        {
            var status = snap.MyStatus == PolStatus.Busy ? PolStatus.Online : snap.MyStatus;
            MyStatusIndex = Array.IndexOf(StatusChoices, status);
            _goingOnline = snap.Presence == "starting";
            OnlineFailed = snap.Presence.StartsWith("failed", StringComparison.Ordinal);
        }
        RefreshPop();
        if (!string.IsNullOrWhiteSpace(snap.MyHandle)) AccountName = snap.MyHandle;
        timer?.Lap("status");
        Avatar = _avatars.ProfilePicture(snap.MyProfilePictureId);
        timer?.Lap("own picture");
        MyProfilePictureId = snap.MyProfilePictureId;
        var region = _settings.RegionOrDefault;
        var notice = PolAccountNotices.For(snap.Notice, region, snap.NoticeDays);
        PlayBlockedReason = PolAccountNotices.PlayBlockedReason(snap);
        AccountNotice = snap.BadPassword ? PolAccountNotices.BadPassword
                      : notice is { Blocks: false } ? $"{notice.Text} ({notice.Code})" : null;
        MyComment = snap.MyComment;
        static string BlockedId(PolBlocked x) => $"{x.Index}/{x.Key}/{x.Name}";
        if (!Blocked.Select(BlockedId).SequenceEqual(snap.Blocked.Select(BlockedId)))
        {
            Blocked.Clear();
            foreach (var x in snap.Blocked) Blocked.Add(x);
        }
        HasBlocked = Blocked.Count > 0;
        OnPropertyChanged(nameof(CanEdit));
        if (chatChanged) Messages.Refresh();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(NoFriends));
        OnPropertyChanged(nameof(NoFriendsInList));
        OnPropertyChanged(nameof(NothingSelected));
        SnapshotApplied?.Invoke(snap);
    }

    private bool MergeChat(ChatStore chat, PolSocialSnapshot snap)
    {
        (string, string)? open = Messages.ConversationVisible && SelectedFriend is { } f ? (f.Name, f.Key) : null;
        var r = _login.Sync.Merge(chat, snap, _pol.Pid, WriteBlockedNote is not null, open, NoticeText, _viewer.Filtered);
        if (r.SendsChanged) Messages.Refresh();
        foreach (var n in r.News)
            switch (n.Kind)
            {
                case ChatNewsKind.Notice: NoticeReceived?.Invoke(n.From, n.Text); break;
                case ChatNewsKind.GroupInvite: GroupInviteReceived?.Invoke(n.From, n.Mid); break;
                case ChatNewsKind.FriendRequest: FriendRequestReceived?.Invoke(n.From, n.Mid); break;
                default: MessageReceived?.Invoke(n.From, n.Text); break;
            }
        if (!r.Kept || (r.ToAck.Count > 0 && _pol.Pid is { } pid && !SocialRequests.Ack(pid, r.ToAck.ToList())))
            _login.Sync.Unack(r.ToAck);
        return r.Changed;
    }

    // what's still on its way under the active handle. A switch waits for it, it'd go from the new one otherwise
    internal string? PendingWrite() =>
        Messages.Sends.Going ? "Wait for your messages to finish sending first."
        : _login.Edit.Running ? "Wait for your friend list change to finish first."
        : Search.Writing ? "Wait for your friend request to finish first."
        : Groups.Writing ? "Wait for your friend group change to finish first."
        : _login.Picture.Running ? "Wait for your profile picture to finish saving first."
        : StatusPending ? "Wait for your status change to finish first."
        : null;

    private void RebuildFriends(PolSocialSnapshot snap)
    {
        var items = snap.Friends.Select(f =>
        {
            var (playing, playingShort) = Playing(f);
            return new FriendItem(
                f.Name, playing, f.Pending ? "" : _viewer.Filtered(f.Comment),
                f.Pending ? "Friend request sent" : f.Online ? f.StatusLabel : "Offline", f.Online && !f.Pending, f.Row,
                _avatars.ProfilePicture(f.ProfilePictureId), _login.Chat?.UnreadFrom(f.Name, f.Key) ?? 0,
                f.Pending ? FriendSection.RequestsSent : f.Online ? FriendSection.Online : FriendSection.Offline)
            { Key = f.Key, Status = f.Status, Pending = f.Pending, IsMe = f.IsMe, PlayingShort = playingShort, InGame = f.Online && !f.Pending && f.Game is not null };
        }).ToList();
        // invitations (kind 0xe) and requests (kind 1) stay on the server until they're answered
        foreach (var m in snap.Messages.Where(m => m.HasBody && m.Mid > 0 && !_login.Answered.Contains(m.Mid)))
            if (m.Kind == PolMessage.KindGroupInvite)
                items.Add(new FriendItem(m.From, "", m.Text.Length > 0 ? _viewer.Filtered(m.Text) : "Group invitation", "", false, -1,
                                         _avatars.NoProfilePicture, 0, FriendSection.GroupInvitations)
                          { Key = m.FromKey, RequestMid = m.Mid, IsGroupInvite = true });
            else if (m.Kind == PolMessage.KindFriendRequest)
                items.Add(new FriendItem(m.From, "", "Wants to be your friend", "", false, -1,
                                         _avatars.NoProfilePicture, 0, FriendSection.FriendRequests)
                          { Key = m.FromKey, RequestMid = m.Mid, IsMe = m.IsMe });
        if (_login.Chat != null)
        {
            var keys = items.Where(i => i.Key.Length > 0).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
            var names = items.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keyless = items.Where(i => i.Key.Length == 0).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, last, used) in _login.Chat.Writers)
            {
                bool listed = last.PeerKey.Length > 0 ? keys.Contains(last.PeerKey) || used.Any(keyless.Contains)
                                                      : names.Contains(last.Peer);
                if (listed) continue;
                items.Add(new FriendItem(last.Peer, "", "Not on your friend list", "", false, -1,
                                         _avatars.NoProfilePicture, _login.Chat.UnreadFrom(last.Peer, last.PeerKey),
                                         FriendSection.Others) { Key = last.PeerKey });
            }
        }
        items = items.OrderBy(i => i.Section)
                     .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!_login.FriendsDirty && items.SequenceEqual(Friends)) return;
        _login.FriendsDirty = false;
        var keep = SelectedFriend;
        Friends.Patch(items, SameRow, (a, b) => a == b, (i, item) =>
        {
            bool selected = ReferenceEquals(SelectedFriend, Friends[i]);
            Friends[i] = item;
            if (selected) SelectedFriend = item;
        });
        if (keep != null && !Friends.Contains(SelectedFriend!))
            SelectedFriend = Friends.FirstOrDefault(i => i.Is(keep));
        Groups.SyncInvites(Friends);
    }

    // online and offline count as one section (a presence change just moves the row), a request and a friend row don't
    private static bool SameRow(FriendItem a, FriendItem b) =>
        a.Is(b) && (a.Section == b.Section ||
                    (a.Section is FriendSection.Online or FriendSection.Offline && b.Section is FriendSection.Online or FriendSection.Offline));

    partial void OnSelectedFriendChanged(FriendItem? value) => Messages.SelectionChanged(value);

    internal void MarkFriendsDirty() => _login.FriendsDirty = true;

    internal void RebuildFriends() => RebuildFriends(_login.Last);

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
    public bool CanEdit => LoggedIn && !EditBusy && _pol.Pid != null && WriteBlockedNote is null;

    [RelayCommand]
    private void DismissEditNote() => EditNote.Clear();

    internal void WriteOrFail(bool written)
    {
        if (written) return;
        _login.Edit.Forget();
        EditBusy = false;
        EditNote.Fail(HookFailures.RequestWriteFailed);
    }

    // followSend: the job's answer comes back as a send result, so the edit job isn't tracked
    internal bool BeginEdit(out int pid, out string token, bool followSend = false)
    {
        token = HookFiles.NewToken();
        pid = _pol.Pid ?? 0;
        if (!CanEdit || pid == 0) return false;
        if (!followSend) _login.Edit.Begin(token, HookLimits.FriendEdit, Now);
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
        if (!_login.Edit.Running) return;
        if (_login.Edit.Settle(snap.EditJob.StateFor(_login.Edit.Token), "saved", Now) is not { } st) return;
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

    private bool _filterWasOn;

    // a pic save is two requests, in the viewer's order
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditNoteShown), nameof(Saving), nameof(EditStatus), nameof(EditStatusShown))]
    private bool _profilePictureSaving;

    public void SaveProfilePicture(int picId)
    {
        if (ProfilePictureSaving || !LoggedIn || _pol.Pid is not { } pid) return;
        if (WriteBlockedNote is { } blocked)
        {
            EditNote.Fail(blocked);
            return;
        }
        var token = _login.Picture.Begin(HookLimits.Picture, Now);
        ProfilePictureSaving = true;
        EditNote.Clear();
        if (SocialRequests.SetProfilePicture(pid, picId, token)) return;
        _login.Picture.Forget();
        ProfilePictureSaving = false;
        EditNote.Fail(HookFailures.RequestWriteFailed);
    }

    private void ApplyProfilePictureState(PolSocialSnapshot snap)
    {
        if (!_login.Picture.Running) return;
        _login.Picture.WaitingTurn(snap.PictureJob.Token, snap.ProfileServerBusy, Now);
        if (_login.Picture.Settle(snap.PictureJob.StateFor(_login.Picture.Token), "saved", Now) is not { } st) return;
        if (st == "timeout") DiscardRequest(HookContract.Requests.Pic);
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

    partial void OnMyStatusIndexChanged(int value)
    {
        if (_applyingSnapshot || WriteBlockedNote is not null || value < 0 || value >= StatusChoices.Length) return;
        if (StatusPending) return;
        var want = StatusChoices[value];
        _login.RequestedStatus = want;
        _login.RequestedAt = Now;
        StatusPending = true;
        _login.PopError = null;
        RefreshPop();
        _settings.SaveChange(s => s.PlayOnlineStatus = PolStatusText.Code(want));
        if (_pol.Pid is not { } pid || SocialRequests.SetStatus(pid, want)) return;
        _login.RequestedStatus = null;
        StatusPending = false;
        PopStatusError(HookFailures.RequestWriteFailed);
        RefreshPop();
        var shown = _login.Last.MyStatus == PolStatus.Busy ? PolStatus.Online : _login.Last.MyStatus;
        _applyingSnapshot = true;
        try { MyStatusIndex = Array.IndexOf(StatusChoices, shown); }
        finally { _applyingSnapshot = false; }
    }

    private CharacterNames? _characterNames;
    private CharacterLookups? _lookups;

    internal CharacterNames CharacterNames
    {
        get => _characterNames ??= new CharacterNames();
        set
        {
            _characterNames = value;
            _lookups = null;
            _login.FriendsDirty = true;
        }
    }

    private CharacterLookups Lookups => _lookups ??= new CharacterLookups(CharacterNames);

    private void ApplyCharacters(PolSocialSnapshot snap)
    {
        var lookups = Lookups;
        lookups.Answer(snap.CharacterJob.Token, snap.CharacterJob.State, snap.Character);
        lookups.Waiting(snap.CharacterJob.Token, snap.ProfileServerBusy, Now);
        if (snap.FriendsLoaded) lookups.Seen(snap.Friends);
        if (GameRunning || _pol.Pid is not { } pid || lookups.Next(Now) is not { } game) return;
        if (!SocialRequests.RequestCharacter(pid, lookups.Token, game.Character)) lookups.Failed();
    }

    private (string Long, string Short) Playing(PolFriend f)
    {
        if (!f.Online || f.Pending || f.Game is not { } game) return ("", "");
        var name = CharacterNames.Get(game.Character.Key)?.Name ?? "";
        return PlayingText.For(name.Length > 0 ? _viewer.Filtered(name) : "", FfxiWorlds.Name(game.World));
    }

    public void AddFriend() => _windows.AddFriend(this);

    internal void ShowProfile(FriendItem f) => _windows.ShowProfile(this, f);

    [RelayCommand]
    public void ToggleProfileEditor()
    {
        if (_session.Phase == SessionPhase.InGame) return;
        if (!_windows.CloseIfShown(SecondWindow.ProfileEditor)) _windows.EditProfile(this);
    }

    internal void ShowMessagesWindow() => _windows.ShowMessages(this);

    public void ShowMessagesAlongside(bool takeFocus) => _windows.ShowMessagesAlongside(this, takeFocus);

    public void SetWindowsSplit(bool split)
    {
        if (split) _windows.Split(this);
        else _windows.Merge(this);
    }

    // a request or invitation gets the viewer's answer window (Ignore lives on the right-click menu)
    public void AnswerRow(FriendItem f)
    {
        if (!CanEdit) return;
        if (f.IsGroupInvite)
        {
            if (_dialogs.Choose("Group invitation from " + f.Name, f.Comment, "ANSWER",
                                "Accept: join the group", "Decline") is { } answer)
                Groups.AnswerInvite(f, answer == 0);
            return;
        }
        if (_dialogs.Choose("Friend request from " + f.Name,
                $"{f.Name} would like to be your friend on PlayOnline.", "ANSWER",
                "Accept: add " + f.Name + " to your friend list", "Decline", ConfirmStyle.Destructive) is { } pick)
            Search.AnswerRequest(f, pick == 0 ? RequestAnswer.Accept : RequestAnswer.Decline);
    }

    public static string IgnoreQuestion(string name) =>
        $"PlayOnline will take {name} off your friend list, and their messages won't reach you any more. " +
        "You can stop ignoring them later, but that won't put them back on your friend list. " +
        "You'd have to send them a new friend request.";

    public static string IgnoreRequestQuestion(string name, bool onMyList) => onMyList
        ? $"Add {name} to your ignore list? This will remove {name} off your friends list, and decline any pending requests."
        : $"Add {name} to your ignore list? This will decline any pending requests.";

    public static string StopIgnoringQuestion(string name) =>
        $"Take {name} off your ignore list? Their messages will reach you again. " +
        "This doesn't add them to your friend list, you'd have to send them a friend request for that.";

    public void AskUnignore(PolBlocked b)
    {
        if (_dialogs.Confirm("Stop ignoring " + b.Name, StopIgnoringQuestion(b.Name), "STOP IGNORING"))
            Unignore(b);
    }

    internal bool ConfirmDestructive(string title, string body, string ok) =>
        _dialogs.Confirm(title, body, ok, ConfirmStyle.Destructive);

    public IReadOnlyList<MenuAction> FriendMenu(FriendItem f, Action<FriendItem>? openConversation = null)
    {
        bool edit = CanEdit && f.IsFriend && f.Key.Length > 0;
        if (f.IsGroupInvite)
            return [new("Accept group invitation", CanEdit, () => Groups.AnswerInvite(f, true)),
                    new("Decline group invitation", CanEdit, () => Groups.AnswerInvite(f, false))];
        if (f.IsRequest)
            return [new("Accept friend request", CanEdit && !f.IsMe, () => Search.AnswerRequest(f, RequestAnswer.Accept)),
                    new("Decline friend request...", CanEdit, () =>
                    {
                        if (ConfirmDestructive("Decline friend request", $"Decline the friend request from {f.Name}?", "DECLINE"))
                            Search.AnswerRequest(f, RequestAnswer.Decline);
                    }),
                    MenuAction.Separator,
                    // the viewer's request window only ignores this one handle
                    new("Ignore...", CanEdit && !f.IsMe, () =>
                    {
                        if (ConfirmDestructive("Ignore " + f.Name,
                                IgnoreRequestQuestion(f.Name, f.Key.Length > 0 && Friends.Any(x => x.IsFriend && !x.Pending && x.Key == f.Key)),
                                "IGNORE"))
                            Search.AnswerRequest(f, RequestAnswer.Ignore);
                    })];
        if (!f.IsFriend && !f.Pending)
            return [new($"Add {f.Name} as a friend...", CanEdit, () => _windows.AddFriend(this, f.Name, f.Key))];
        var menu = new List<MenuAction>
        {
            new("Send message", f.CanMessage, () =>
            {
                SelectedFriend = f;
                openConversation?.Invoke(f);
            }),
            new("View profile", f.IsFriend && !f.Pending, () => _windows.ShowProfile(this, f)),
        };
        // the viewer's Invite into Group: only groups I'm master or sub-master of, and never myself
        var inviteTo = Groups.Items.Where(g => g.CanInvite && !g.Members.Any(m => m.Key.Length > 0 && m.Key == f.Key)).ToList();
        if (inviteTo.Count > 0 && f.IsFriend && !f.Pending && !f.IsMe)
            menu.Add(new("Invite into group", CanEdit)
            {
                Items = inviteTo.Select(g => new MenuAction(g.Name, CanEdit, () => Groups.Invite(g, f))).ToList(),
            });
        menu.Add(MenuAction.Separator);
        menu.Add(new("Rename...", edit && !f.Pending, () =>
        {
            var name = _dialogs.Ask("Rename friend",
                $"Choose a new name for {f.Name} on your friend list (up to 15 English letters, numbers or symbols). " +
                "PlayOnline saves it with your friend list.", f.Name, maxLength: PolLimits.HandleName);
            // the same name has nothing to save, the viewer skips it too
            if (name is not null && name.Trim() != f.Name) RenameFriend(f, name);
        }));
        var removeBody = f.Pending
            ? $"Delete your friend request to {f.Name}? This takes it off your own friend list."
            : $"Remove {f.Name} from your PlayOnline friend list? Your saved messages with {f.Name} stay in Provenance.";
        menu.Add(new(f.Pending ? "Delete friend request..." : "Remove from friends...", edit, () =>
        {
            if (ConfirmDestructive(f.Pending ? "Delete friend request" : "Remove friend", removeBody, f.Pending ? "DELETE" : "REMOVE"))
                RemoveFriend(f);
        }));
        menu.Add(new("Ignore...", edit && !f.Pending && !f.IsMe, () =>
        {
            var pick = _dialogs.Choose("Ignore " + f.Name, IgnoreQuestion(f.Name), "IGNORE",
                "Ignore this handle only", "Ignore every handle of this PlayOnline ID", ConfirmStyle.Destructive);
            if (pick is { } which) IgnoreFriend(f, which == 1);
        }));
        return menu;
    }
}
