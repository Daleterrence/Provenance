using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Provenance.Common.Feeds;
using Provenance.Common.Game;
using Provenance.Common.Game.Windower;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Install;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Common.Social;
using Provenance.Wpf.Services;
using Provenance.Wpf.Views;

namespace Provenance.Wpf.ViewModels;

// pick an account, log in, play
public partial class MainViewModel : ObservableObject
{
    private readonly ICredentialService _credentials;
    private readonly ISettingsService _settings;
    private readonly FeedService _feeds;
    private readonly PolSessionService _pol;
    private readonly AvatarService _avatars;
    private readonly DispatcherTimer _slideTimer, _socialPoll, _worldsTimer, _elapsedTimer;

    public event Action? RequestHide;
    public event Action? RequestShow;
    public event Action? RequestRestart;
    public event Action? RequestExit;
    // true when there's a notice to show as a tray tip, false for the taskbar alone
    public event Action<bool>? RequestAttention;
    public event Action? AttentionCleared;

    public MainViewModel(ICredentialService credentials, ISettingsService settings, FeedService feeds, PolSessionService pol,
                         AvatarService avatars)
    {
        _credentials = credentials;
        _settings = settings;
        _settings.SaveFailed += why => Application.Current?.Dispatcher.InvokeAsync(() =>
            MessageDialog.ShowError(Application.Current.MainWindow, "Settings not saved",
                "Provenance couldn't save your settings, so your changes will only last until Provenance closes.", offerLogs: true));
        if (_settings.LoadProblem is { } loadProblem)
            Application.Current?.Dispatcher.InvokeAsync(() =>
                MessageDialog.ShowInfo(Application.Current.MainWindow, "Settings reset", loadProblem));
        _feeds = feeds;
        _pol = pol;
        _avatars = avatars;
        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _slideTimer.Tick += (_, _) => AdvanceNews(+1);
        // POL only rewrites world status every 5 mins anyway
        _worldsTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _worldsTimer.Tick += async (_, _) => { if (_windowShown) await RefreshIfStaleAsync(); };
        _feeds.RegionChanged += (_, _) => Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            await LoadFeedsAsync(again: true);
            await LoadWorldsAsync();
        });
        _socialPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _socialPoll.Tick += (_, _) => _pol.Poll();
        _pol.SnapshotUpdated += OnSnapshot;
        _pol.SessionEnded += why => Application.Current?.Dispatcher.Invoke(() =>
        {
            Log.Information("Session ended: {Why} (signed in here: {Connected})", why, SocialConnected);
            StartAllAccountsCommand.NotifyCanExecuteChanged();
            if (!SocialConnected) return;
            EndSession(SessionEnd.Dropped);   // no question asked, the session's gone so the editor couldn't save anyway
            RequestShow?.Invoke();
            MessageDialog.ShowInfo(Application.Current.MainWindow, "PlayOnline session ended", why);
            Log.Information("Session ended: the notice was closed");
        });
        LoadStatusIcons();
        StatusMark.IconSource = StatusIconService.Get;
        StatusMark.IconsAvailable = () => StatusIconService.Available;
        Social = new SocialViewModel(_pol, avatars, _settings);
        Social.ChangeProfilePictureRequested += OpenProfilePicturePicker;
        Social.LayoutRequested += OnSocialLayout;

        Profiles = new ObservableCollection<Profile>(_settings.Current.Profiles);
        int needDetails = Profiles.Count(p => !_credentials.HasUsableDetails(p.Id));
        if (needDetails > 0)
            Log.Information("{Count} account(s) need their sign-in details again", needDetails);
        // a fresh settings file's "Default" and a never-signed-in "New account" get a proper name
        int n = 0;
        bool changed = false;
        foreach (var p in Profiles)
        {
            n++;
            if (p.DisplayName is "New account" or "Default")
            {
                p.DisplayName = string.IsNullOrWhiteSpace(p.HandleName) ? $"Account {n}" : p.HandleName!;
                changed = true;
            }
        }
        // an account without a home slot gets one, in list order (the order they were added)
        changed |= HomeSlots.Assign(_settings.Current);
        if (changed) PersistProfileList();   // no settings write on an ordinary start
        avatars.Prewarm(Profiles.Select(p => p.ProfilePictureId));

        // --account never falls back to another account when that one's gone
        var active = LaunchArgs.ProfileId is { } forAccount
            ? Profiles.FirstOrDefault(p => p.Id == forAccount)
            : Profiles.FirstOrDefault(p => p.Id == _settings.Current.ActiveProfileId) ?? Profiles.FirstOrDefault();
        _selectedProfile = active;
        _autoLogin = _settings.Current.AutoLogin;
        if (active != null) LoadCredentialsForProfile(active);
        _startGameImmediately = _settings.Current.StartGameImmediately;
        Social.MessageReceived += (from, text) => Alert(from, $"New message from {from}: {Preview(text, 60)}");
        Social.FriendRequestReceived += (who, mid) =>
            Alert(null, $"{who} sent you a friend request. Open Friends and messages to answer it.", "AccountPlusOutline",
                  stays: true, request: mid);
        Social.GroupInviteReceived += (who, mid) =>
            Alert(who, $"{who} invited you to a friend group. Open Friends and messages to answer it.", "BellOutline",
                  request: mid);
        Social.NoticeReceived += (who, text) => Alert(who, text, "BellOutline");
        Social.GroupChatRequested += OpenGroupChat;
        Social.GroupChat.LineArrived += OnGroupChatLine;
        Social.SnapshotApplied += _ => DropAnsweredNotice();
        Social.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SocialViewModel.UnseenCount) or nameof(SocialViewModel.GroupChatUnread) && !WantsAttention)
                AttentionCleared?.Invoke();
        };
        // a quiet one, no taskbar flash, and it never covers a message notice
        Social.FriendCameOnline += who =>
        {
            if (!_settings.Current.NotifyFriendsOnline || Notice is { Length: > 0 }) return;
            _lastSender = who;
            ShowNotice($"{who} is online now.", "AccountCheckOutline");
        };
        RefreshInstallState();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _elapsedTimer.Tick += (_, _) =>
        {
            ElapsedText = Seconds(DateTime.UtcNow - _launchStarted);
            // reports stop coming in a stall, and the time left should notice
            if (_timeLeftPhase is not null)
            {
                _timeLeft.Add(Environment.TickCount64, _timeLeftDone, 1);
                TimeLeftText = _timeLeft.Text;
            }
        };
        StartRepairProbe();
    }

    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private string _elapsedText = string.Empty;
    [ObservableProperty] private string _timeLeftText = string.Empty;
    [ObservableProperty] private string _progressTitle = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelLaunchCommand))]
    private bool _canCancel;

    private DateTime _launchStarted;
    private CancellationTokenSource? _launchCts;
    private readonly TimeLeft _timeLeft = new();
    private PatchPhase? _timeLeftPhase;
    private double _timeLeftDone;
    private bool _updatedFirst;

    private static string Seconds(TimeSpan t) => t.TotalSeconds < 60
        ? $"{t.TotalSeconds:0}s"
        : $"{(int)t.TotalMinutes}m {t.Seconds:00}s";

    private void ResetProgress(string title, string text)
    {
        _launchStarted = DateTime.UtcNow;
        ElapsedText = "0s";
        ProgressTitle = title;
        ProgressText = text;
        ProgressFraction = 0.01;
        _updatedFirst = false;
        TrackTimeLeft(null);
        _elapsedTimer.Start();
    }

    private CancellationToken BeginCancellable(string title, string text)
    {
        ResetProgress(title, text);
        _launchCts = new CancellationTokenSource();
        CanCancel = true;
        return _launchCts.Token;
    }

    private void EndCancellable()
    {
        _elapsedTimer.Stop();
        TrackTimeLeft(null);
        CanCancel = false;
        _launchCts?.Dispose();
        _launchCts = null;
    }

    private void OnProgress(LaunchProgress p)
    {
        if (p.Stage == "Notice")
        {
            Log.Information("progress: notice: {Detail}", p.Detail);
            ShowNotice(p.Detail, "InformationOutline", opens: false);
            return;
        }
        if (!string.IsNullOrWhiteSpace(p.Detail)) ProgressText = p.Detail;
        if (p.Fraction >= 0)
        {
            // after a viewer update at sign-in the sign-in's own steps share what's left, so the bar only moves forward
            if (p.Patch is not null) _updatedFirst = true;
            double f = _updatedFirst && p.Patch is null
                ? LaunchProgress.SignInUpdateEnd + (1 - LaunchProgress.SignInUpdateEnd) * p.Fraction : p.Fraction;
            if (f > ProgressFraction) ProgressFraction = Math.Min(f, 1.0);
        }
        TrackTimeLeft(p.Patch);
        Log.Information("progress: {Stage}: {Detail}", p.Stage, p.Detail);
        if (p.Stage is "Failed" or "Stopped") _elapsedTimer.Stop();
    }

    // each phase gets its own, a check's pace says nothing about the download after it
    private void TrackTimeLeft(PatchProgress? patch)
    {
        if (patch is not { Fraction: >= 0 } p)
        {
            _timeLeft.Reset();
            _timeLeftPhase = null;
            TimeLeftText = "";
            return;
        }
        if (_timeLeftPhase != p.Phase)
        {
            _timeLeft.Reset();
            _timeLeftPhase = p.Phase;
        }
        _timeLeftDone = p.Fraction;
        _timeLeft.Add(Environment.TickCount64, p.Fraction, 1);
        TimeLeftText = _timeLeft.Text;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelLaunch()
    {
        if (_launchCts is not { IsCancellationRequested: false } cts) return;
        CanCancel = false;
        ProgressText = "Cancelling...";
        cts.Cancel();
    }

    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string _noticeIcon = "EmailOutline";
    [ObservableProperty] private bool _noticeOpens = true;
    private bool _noticeStays;
    private int _noticeRequest;   // the request or invitation the notice is about
    private DispatcherTimer? _noticeTimer;
    private string? _lastSender;
    private bool _gameRunning;

    public bool MinimizeGoesToTray => _settings.Current.MinimizeToTray && !_gameRunning;

    public bool KeepInTrayOnClose => SocialConnected && _settings.Current.KeepSignedInInTray;

    public string CloseTooltip => !SocialConnected ? "Close"
        : KeepInTrayOnClose ? "Close to the tray (you'll stay logged in to PlayOnline)"
        : "Close and log out of PlayOnline";
    partial void OnSocialConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CloseTooltip));
        if (value && (_settings.Current.KeepSignedInInTray || _settings.Current.MinimizeToTray)) TrayIcon.Warm();
        RefreshLargeAddressAware();
        // the hook numbers mail per session, so a request's notice can't outlive it
        if (!value && _noticeRequest != 0) Notice = null;
    }

    partial void OnIsLaunchingChanged(bool value) => RefreshLargeAddressAware();

    // an open Settings page's Large Address Aware row goes by the same sign-in and run state as Repair
    private void RefreshLargeAddressAware() => _ = _openSettings?.ReadLargeAddressAwareAsync();
    public int TraySignOutMinutes => _settings.Current.TraySignOutMinutes;
    public bool TrayTipShown => _settings.Current.TrayTipShown;

    public void MarkTrayTipShown() => SaveSetting(s => s.TrayTipShown = true);

    private void SaveSetting(Action<LauncherSettings> change)
    {
        change(_settings.Current);
        _settings.Save(_settings.Current);
    }

    private void Alert(string? sender, string text, string icon = "EmailOutline", bool stays = false, int request = 0)
    {
        _lastSender = sender;
        ShowNotice(text, icon, stays: stays);
        _noticeRequest = request;
        RequestAttention?.Invoke(true);
    }

    // false once everything's been read or seen, like a message landing in a conversation that's already on screen
    public bool WantsAttention => Social.UnseenCount > 0 || (_settings.Current.GroupChatFlash && Social.GroupChatUnread > 0);

    // answered here, the request leaves the list and the notice goes with it. Answering it anywhere else takes another
    // login of this POL ID, which ends this session, and logging out takes the notice down
    private void DropAnsweredNotice()
    {
        if (_noticeRequest != 0 && Notice != null && !Social.IsWaiting(_noticeRequest)) Notice = null;
    }

    partial void OnNoticeChanged(string? value)
    {
        if (value is null) _noticeRequest = 0;
    }

    private static string Preview(string text, int max)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf(' ', max);
        return (cut > max / 2 ? text[..cut] : text[..max]).TrimEnd() + "...";
    }

    public void HoldNotice(bool hold)
    {
        if (_noticeTimer is null || Notice is null || _noticeStays) return;
        if (hold) _noticeTimer.Stop();
        else _noticeTimer.Start();
    }

    public void HoldSlides(bool hold)
    {
        if (hold) _slideTimer.Stop();
        else if (NewsItems.Count > 1) _slideTimer.Start();
    }

    public void BannerFailed()
    {
        if (CurrentNews is not { ImageUrl: not null } item) return;
        FeedClient.ForgetImage(item.ImageUrl);
        var i = NewsItems.IndexOf(item);
        var plain = item with { ImageUrl = null };
        if (i >= 0) NewsItems[i] = plain;
        CurrentNews = plain;
    }

    private bool _noticeGroupChat;

    // lines in the group chat with its window hidden or behind, one notice a batch, as the settings say
    private void OnGroupChatLine(string group, string who, string text, int count)
    {
        bool notice = _settings.Current.GroupChatNotice;
        if (notice)
        {
            ShowNotice(count == 1 ? $"{who} in {group}: {Preview(text, 60)}" : $"{count} new lines in {group}", "Forum");
            _noticeGroupChat = true;
        }
        if (notice || _settings.Current.GroupChatFlash) RequestAttention?.Invoke(notice);
    }

    // the window side, which tests with no windows swap out
    internal Func<string, bool>? AskGroupHandle { get; set; }
    internal Action<string>? TellGroupChat { get; set; }
    internal Action? ShowGroupChat { get; set; }

    // the Groups tab's Group Chat: the viewer's handle question (0x3eb0) first when it's needed, then the window
    private void OpenGroupChat(SocialViewModel.GroupItem g)
    {
        if (MultiRunning || Social.Last.GroupList.FirstOrDefault(x => x.Id == g.Id) is not { } group) return;
        Window? Owner() => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        var show = ShowGroupChat ?? (() => GroupChatWindow.ShowFor(Social.GroupChat, _settings));
        var tell = TellGroupChat ?? (text => MessageDialog.ShowInfo(Owner(), "Group Chat", text));
        if (Social.GroupChat.Open && string.Equals(Social.GroupChat.GroupId, group.Id, StringComparison.OrdinalIgnoreCase))
        {
            show();
            return;
        }
        // the Handles page might never have been opened, and the question needs this login's handles
        ShowHandles();
        Handles.OpenGroup(group,
            ask: AskGroupHandle ?? (q => MessageDialog.Confirm(Owner(), GroupHandleCheck.Title, q, "YES", cancelLabel: "NO") == 0),
            open: () =>
            {
                if (Social.GroupChat.Join(group)) show();
                else tell(GroupChatViewModel.JoinFailedText);
            },
            tell: tell);
    }

    private void ShowNotice(string text, string icon = "EmailOutline", bool opens = true, bool stays = false)
    {
        _noticeRequest = 0;
        _noticeGroupChat = false;
        Notice = text;
        NoticeIcon = icon;
        NoticeOpens = opens;
        _noticeStays = stays;
        if (_noticeTimer is null)
        {
            _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _noticeTimer.Tick += (_, _) =>
            {
                Notice = null;
                _noticeTimer?.Stop();
            };
        }
        _noticeTimer.Stop();
        if (!stays) _noticeTimer.Start();
    }

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    public ObservableCollection<Profile> Profiles { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile), nameof(ProfileTitle), nameof(ProfilePicture))]
    private Profile? _selectedProfile;

    public bool HasProfile => SelectedProfile != null;

    public string ProfileTitle => SelectedProfile?.DisplayName ?? string.Empty;

    public System.Windows.Media.ImageSource? ProfilePicture =>
        _avatars.ProfilePicture(SelectedProfile?.ProfilePictureId ?? 0);

    private string? _sessionProfileId;   // can differ from the selection

    private void LearnIdentity(PolSocialSnapshot snap)
    {
        if (string.IsNullOrWhiteSpace(snap.MyHandle) || _sessionProfileId is null) return;
        if (Profiles.FirstOrDefault(p => p.Id == _sessionProfileId) is not { } prof) return;
        // no profile picture (-1) says nothing, comparing it anyway would rewrite settings.json every 2 s
        if (prof.DisplayName == snap.MyHandle && prof.HandleName == snap.MyHandle &&
            (snap.MyProfilePictureId < 0 || prof.ProfilePictureId == snap.MyProfilePictureId)) return;
        prof.DisplayName = snap.MyHandle;
        prof.HandleName = snap.MyHandle;
        if (snap.MyProfilePictureId >= 0) prof.ProfilePictureId = snap.MyProfilePictureId;
        PersistProfileList();
        if (prof == SelectedProfile) RefreshProfileDisplay();
        else { var i = Profiles.IndexOf(prof); if (i >= 0) Profiles[i] = prof; }
    }

    partial void OnSelectedProfileChanged(Profile? value)
    {
        if (value == null) return;
        if (LaunchArgs.ProfileId is null) SaveSetting(s => s.ActiveProfileId = value.Id);
        LoadCredentialsForProfile(value);
    }

    [ObservableProperty] private bool _useOtp;

    partial void OnUseOtpChanged(bool value)
    {
        if (SelectedProfile is { } p && p.UseOtp != value) { p.UseOtp = value; PersistProfileList(); }
    }

    public bool HasSavedCredentials { get; private set; }

    private void LoadCredentialsForProfile(Profile profile)
    {
        UseOtp = profile.UseOtp;
        HasSavedCredentials = _credentials.HasUsableDetails(profile.Id);
    }

    // with slots, the settings lock is held from reading another copy's numbers to saving, so two can't give out one
    private void PersistProfileList(bool slots = false)
    {
        using var gate = slots ? _settings.HoldLock() : null;
        SaveSetting(s =>
        {
            s.Profiles = Profiles.ToList();
            if (slots) HomeSlots.Assign(s, _settings.ReadSaved());
        });
    }

    // a login always goes with the account's own home slot, so one without gets it now
    internal int HomeSlotFor(Profile p)
    {
        if (!HomeSlots.Valid(p.HomeSlot) || Profiles.Any(o => o != p && o.HomeSlot == p.HomeSlot))
            PersistProfileList(slots: true);
        return p.HomeSlot;
    }

    // only the POL entries' user names get read, no password's decoded
    private Profile? ProfileWithPolId(string polId, Profile? except = null) =>
        Profiles.FirstOrDefault(p => p != except &&
                                     string.Equals(_credentials.ReadPolId(p.Id), polId, StringComparison.OrdinalIgnoreCase));

    private void RefreshProfileDisplay()
    {
        if (SelectedProfile is not { } p) return;
        var idx = Profiles.IndexOf(p);
        if (idx >= 0) Profiles[idx] = p;   // redraws the switcher's text after an in-place rename
        SelectedProfile = p;
        OnPropertyChanged(nameof(ProfileTitle));
        OnPropertyChanged(nameof(ProfilePicture));
    }

    [RelayCommand]
    private void AddAccount() => TryAddAccount();

    private bool TryAddAccount()
    {
        var r = AccountDialog.Show(Application.Current.MainWindow, null, false, edit: false);
        if (r == null) return false;
        // one POL ID twice would be two cards for one account, and the message names the card, never the ID
        if (ProfileWithPolId(r.PolId) is { } same)
        {
            MessageDialog.ShowInfo(Application.Current.MainWindow, "Account already added",
                $"This PlayOnline ID is already saved as {same.DisplayName}. Press Edit account if you want to change its details.");
            SelectedProfile = same;
            return false;
        }
        int n = Profiles.Count(p => p.DisplayName.StartsWith("New account", StringComparison.Ordinal));
        var profile = new Profile { DisplayName = n == 0 ? "New account" : $"New account {n + 1}", UseOtp = r.UseOtp,
                                    OtpSource = r.OtpSource, OtpItem = r.OtpItem, OtpAccount = r.OtpAccount };
        _credentials.SavePolAccount(profile.Id, new PolAccount(r.PolId, r.PolPassword));
        try { _credentials.SaveSeaAccount(profile.Id, new SeaAccount(r.SeaId, r.SeaPassword)); }
        catch
        {
            // the account isn't added, so its POL entry mustn't stay behind
            try { _credentials.DeletePolAccount(profile.Id); }
            catch (Exception ex) { Log.Warning(ex, "Could not remove the POL entry of account {Id} after a failed save", profile.Id); }
            throw;
        }
        Profiles.Add(profile);
        PersistProfileList(slots: true);
        SelectedProfile = profile;
        Log.Information("Added account {Id}", profile.Id);
        return true;
    }

    [RelayCommand]
    private void EditAccount() => EditAccount(requireAll: false);

    private bool EditAccount(bool requireAll, AccountDialogResult? typed = null)
    {
        if (SelectedProfile is not { } p) return false;
        requireAll |= NeedsAllDetails(_credentials.HasUsableDetails(p.Id),
                                      _settings.Current.MultiAccounts.FirstOrDefault(e => e.ProfileId == p.Id)?.Problem);
        _ = _credentials.CheckKeyAsync();
        var r = AccountDialog.Show(Application.Current.MainWindow, p.DisplayName, typed?.UseOtp ?? p.UseOtp, edit: true,
                                   typed?.OtpSource ?? p.OtpSource, typed?.OtpItem ?? p.OtpItem, requireAll: requireAll,
                                   polId: typed?.PolId ?? "", seaId: typed?.SeaId ?? "", otpAccount: typed?.OtpAccount ?? p.OtpAccount);
        if (r == null) return false;
        if (r.PolId.Length > 0 && ProfileWithPolId(r.PolId, except: p) is { } same)
        {
            MessageDialog.ShowInfo(Application.Current.MainWindow, "Account already added",
                $"This PlayOnline ID is already saved as {same.DisplayName}, so nothing was changed.");
            return false;
        }
        var polEdit = SideEditOf(r.PolId, r.PolPassword);
        var seaEdit = SideEditOf(r.SeaId, r.SeaPassword);
        var pol = polEdit == SideEdit.Fill ? _credentials.LoadPolAccount(p.Id) : null;
        var sea = seaEdit == SideEdit.Fill ? _credentials.LoadSeaAccount(p.Id) : null;
        if (FillFailed(polEdit, pol, seaEdit, sea))
            return EditAccount(requireAll: true, typed: WithoutPasswords(r));
        // read before anything's saved, so the typed details get compared with the saved ones
        var blocked = _settings.Current.MultiAccounts.FirstOrDefault(e => e.ProfileId == p.Id && e.CredentialsRefused);
        bool cures = blocked is not null && ChangesSignIn(r, _credentials.ReadIds(p.Id), p.UseOtp, p.OtpSource, p.OtpItem, p.OtpAccount);
        if (polEdit != SideEdit.Keep)
            _credentials.SavePolAccount(p.Id, new PolAccount(r.PolId.Length > 0 ? r.PolId : pol!.PolId,
                                                             r.PolPassword.Length > 0 ? r.PolPassword : pol!.Password));
        if (seaEdit != SideEdit.Keep)
            _credentials.SaveSeaAccount(p.Id, new SeaAccount(r.SeaId.Length > 0 ? r.SeaId : sea!.Username,
                                                             r.SeaPassword.Length > 0 ? r.SeaPassword : sea!.Password));
        p.UseOtp = r.UseOtp;
        p.OtpSource = r.OtpSource;
        p.OtpItem = r.OtpItem;
        p.OtpAccount = r.OtpAccount;
        PersistProfileList();
        OtpSources.ForgetKeePassXcIfUnused(Profiles);
        // without a change that can cure the refusal the refused details are still what'd be sent, so the block stays
        if (blocked is not null && cures)
        {
            blocked.CredentialsRefused = false;
            blocked.Problem = null;
            blocked.Enabled = true;
            _settings.Save(_settings.Current);
        }
        else if (blocked is not null)
            MessageDialog.ShowInfo(Application.Current.MainWindow, "Edit account", NothingChangedText);
        LoadCredentialsForProfile(p);
        RefreshProfileDisplay();
        return true;
    }

    internal enum SideEdit
    {
        Keep,      // ID and password both empty, the saved entry's neither read nor written
        Replace,
        Fill,
    }

    internal static SideEdit SideEditOf(string id, string password) =>
        id.Length == 0 && password.Length == 0 ? SideEdit.Keep
        : id.Length > 0 && password.Length > 0 ? SideEdit.Replace
        : SideEdit.Fill;

    // a key that's gone still passes the header test, so the run's details-again text is what tells
    internal static bool NeedsAllDetails(bool usable, string? multiProblem) =>
        !usable || multiProblem == PolSessionService.DetailsAgainText;

    internal static bool FillFailed(SideEdit polEdit, PolAccount? pol, SideEdit seaEdit, SeaAccount? sea) =>
        (polEdit == SideEdit.Fill && pol is null) || (seaEdit == SideEdit.Fill && sea is null);

    internal const string NothingChangedText =
        "You didn't change anything that affects logging in, so this account stays turned off for multi-account login.";

    internal static bool ChangesSignIn(AccountDialogResult r, (string? PolId, string? SeaId) saved,
                                       bool useOtp, string otpSource, string otpItem, string otpAccount = "") =>
        (r.PolId.Length > 0 && !string.Equals(r.PolId, saved.PolId, StringComparison.OrdinalIgnoreCase))
        || (r.SeaId.Length > 0 && !string.Equals(r.SeaId, saved.SeaId, StringComparison.OrdinalIgnoreCase))
        || r.PolPassword.Length > 0 || r.SeaPassword.Length > 0
        || r.UseOtp != useOtp || r.OtpSource != otpSource || r.OtpItem != otpItem || r.OtpAccount != otpAccount;

    internal static AccountDialogResult WithoutPasswords(AccountDialogResult typed) =>
        typed with { PolPassword = "", SeaPassword = "" };

    internal const string RemoveFailedText = "Provenance couldn't remove this account's saved details.";

    [RelayCommand]
    private void RemoveAccount(Profile? profile)
    {
        profile ??= SelectedProfile;
        if (profile == null) return;
        if (MessageDialog.Confirm(Application.Current.MainWindow, "Remove account",
                $"Remove {profile.DisplayName} and its saved passwords from this PC? Your PlayOnline account itself won't change.",
                "REMOVE", destructive: true) is null) return;
        // the card stays when an entry couldn't go, so no saved password's left without a card that names it
        try { _credentials.DeleteProfile(profile.Id); }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not remove the saved details of account {Id}", profile.Id);
            MessageDialog.ShowError(Application.Current.MainWindow, "Remove account", RemoveFailedText, offerLogs: true);
            return;
        }
        Profiles.Remove(profile);
        PersistProfileList();
        OtpSources.ForgetKeePassXcIfUnused(Profiles);
        if (profile == SelectedProfile) SelectedProfile = Profiles.FirstOrDefault();
        Log.Information("Removed account {Id}", profile.Id);
    }

    [ObservableProperty] private bool _autoLogin;

    partial void OnAutoLoginChanged(bool value) => SaveSetting(s => s.AutoLogin = value);

    [ObservableProperty] private bool _startGameImmediately;

    partial void OnStartGameImmediatelyChanged(bool value) => SaveSetting(s => s.StartGameImmediately = value);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand), nameof(StartAllAccountsCommand), nameof(RepairGameFilesCommand))]
    [NotifyPropertyChangedFor(nameof(ShowInstallCard), nameof(ShowSignInCard), nameof(ShowFriendsCard))]
    private bool _isLaunching;

    private bool CanLogin() => !IsLaunching && !MultiRunning;


    private static string? ValidateDetails(string polId, string polPassword, string seaId, string seaPassword)
    {
        if (string.IsNullOrWhiteSpace(polId)) return "Enter your PlayOnline ID.";
        if (polId.Trim().Length != 8 || !polId.Trim().All(char.IsAsciiLetterOrDigit))
            return "A PlayOnline ID is 8 letters and numbers.";
        if (string.IsNullOrEmpty(polPassword)) return "Enter your PlayOnline password.";
        if (string.IsNullOrWhiteSpace(seaId)) return "Enter your Square Enix ID.";
        if (seaId.Contains('@')) return "Use your Square Enix ID here, not your email address.";
        if (string.IsNullOrEmpty(seaPassword)) return "Enter your Square Enix password.";
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private Task SignInAsync() => _connectTask = ConnectSocialAsync();

    private Task? _connectTask;

    public bool SigningIn => _launchCts != null && !_gameRunning && _connectTask is { IsCompleted: false };

    public async Task CancelSignInAsync()
    {
        _launchCts?.Cancel();
        if (_connectTask is { } t) await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(15)));
    }


    private static string TitleFor(int code) => code switch
    {
        PolSessionService.ErrorAuthFailed => "Login not accepted",
        PolSessionService.ErrorUnfinished => "Login didn't finish",
        PolSessionService.ErrorNoPolHandle => "PlayOnline handle needed",
        PolSessionService.ErrorHandleRejected => "Handle not accepted",
        PolSessionService.ErrorStalled => "PlayOnline stopped responding",
        PolSessionService.ErrorAccountBlocked => "PlayOnline account notice",
        PolSessionService.ErrorLauncherFailed => "PlayOnline didn't start",
        PolSessionService.ErrorDetailsUnreadable => "Account details incomplete",
        _ => "Couldn't log in",
    };

    public ObservableCollection<FeedItem> NewsItems { get; } = new();
    public ObservableCollection<FeedItem> StatusItems { get; } = new();

    [ObservableProperty] private FeedItem? _currentNews;
    [ObservableProperty] private int _currentNewsIndex;
    [ObservableProperty] private bool _feedsLoaded;

    public bool ShowLoadingHint => NewsItems.Count == 0;
    partial void OnFeedsLoadedChanged(bool value) { OnPropertyChanged(nameof(ShowLoadingHint)); OnPropertyChanged(nameof(FeedsHint)); }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeedsHint))]
    private bool _feedsFailed;

    public string FeedsHint => FeedsFailed ? "PlayOnline news isn't available right now."
                             : FeedsLoaded ? "No news right now." : "Loading PlayOnline news...";

    [RelayCommand]
    private async Task RetryFeeds()
    {
        FeedsFailed = false;
        await LoadFeedsAsync();
    }

    public ObservableCollection<WorldStatus> Worlds { get; } = new();

    [ObservableProperty] private string _worldsSummary = "Checking world status...";
    [ObservableProperty] private string _worldsState = "";
    [ObservableProperty] private string _worldsCheckedText = "";

    private DateTime _worldsAt = DateTime.MinValue, _feedsAt = DateTime.MinValue;
    private bool _worldsLoading, _feedsLoading, _feedsAgain, _windowShown = true;
    private static readonly TimeSpan WorldsMaxAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FeedsMaxAge = TimeSpan.FromMinutes(30);

    public void SetWindowShown(bool shown)
    {
        bool was = _windowShown;
        _windowShown = shown;
        if (shown && !was) _ = RefreshIfStaleAsync();
    }

    private async Task RefreshIfStaleAsync()
    {
        if (DateTime.UtcNow - _worldsAt >= WorldsMaxAge) await LoadWorldsAsync();
        if (DateTime.UtcNow - _feedsAt >= FeedsMaxAge) await LoadFeedsAsync();
    }

    public async Task LoadWorldsAsync()
    {
        if (_worldsLoading) return;
        _worldsLoading = true;
        IReadOnlyList<WorldStatus> list;
        try { list = await _feeds.LoadWorldsAsync(); }
        finally { _worldsLoading = false; }
        _worldsTimer.Start();
        if (list.Count == 0)
        {
            if (Worlds.Count == 0) { WorldsSummary = "World status isn't available"; WorldsState = ""; }
            return;
        }
        _worldsAt = DateTime.UtcNow;   // only a good read counts, a failed one's tried again on the next tick
        WorldsCheckedText = "Checked at " + DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
        Worlds.Clear();
        foreach (var w in list) Worlds.Add(w);
        var notUp = list.Where(w => !w.IsUp).ToList();
        bool onlyMaintenance = notUp.All(w => w.Status is "maintenance" or "partial-maintenance");
        WorldsSummary = notUp.Count switch
        {
            0 => $"All {list.Count} worlds up",
            _ when notUp.Count == list.Count => onlyMaintenance ? "All worlds in maintenance" : "Worlds are down",
            1 => $"{notUp[0].Name}: {notUp[0].Label}",
            _ => $"{notUp.Count} worlds {(onlyMaintenance ? "in maintenance" : "not up")}",
        };
        WorldsState = notUp.Count == 0 ? "up" : onlyMaintenance ? "maintenance" : "down";
    }

    [RelayCommand]
    private void OpenServerStatus() => OpenLink(_feeds.ServicePageUrl);

    public async Task LoadFeedsAsync(bool again = false)
    {
        // a region change mid-load mustn't get lost
        if (_feedsLoading) { _feedsAgain |= again; return; }
        _feedsLoading = true;
        try
        {
            var (news, status) = await _feeds.LoadAsync();
            if (news.Items.Count + status.Items.Count > 0) _feedsAt = DateTime.UtcNow;
            var newNews = (await _feeds.WithLocalImagesAsync(news.Items.Take(5).ToList())).ToList();
            static string Sig(IEnumerable<FeedItem> items) =>
                string.Join("\n", items.Select(i => $"{i.Url}|{i.Title}|{i.Date:O}|{i.Category}|{i.Badge}"));
            if (newNews.Count + status.Items.Count == 0 && FeedsLoaded) return;
            if (Sig(newNews) != Sig(NewsItems))
            {
                var keepUrl = CurrentNews?.Url;
                NewsItems.Clear();
                foreach (var it in newNews) NewsItems.Add(it);
                var at = keepUrl is null ? 0 : Math.Max(0, newNews.FindIndex(n => n.Url == keepUrl));
                CurrentNewsIndex = at;
                CurrentNews = NewsItems.ElementAtOrDefault(at);
            }
            // four fit without a scroll bar
            var topStatus = status.Items.Take(4).ToList();
            if (Sig(topStatus) != Sig(StatusItems))
            {
                StatusItems.Clear();
                foreach (var it in topStatus) StatusItems.Add(it);
            }
            FeedsLoaded = NewsItems.Count > 0 || StatusItems.Count > 0;
            FeedsFailed = !FeedsLoaded;
            OnPropertyChanged(nameof(ShowLoadingHint));
            OnPropertyChanged(nameof(FeedsHint));
            if (NewsItems.Count > 1) _slideTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Feed load failed");
            FeedsFailed = !FeedsLoaded;
        }
        finally
        {
            _feedsLoading = false;
            if (_feedsAgain) { _feedsAgain = false; _ = LoadFeedsAsync(); }
        }
    }

    private void AdvanceNews(int delta)
    {
        if (NewsItems.Count == 0) return;
        CurrentNewsIndex = ((CurrentNewsIndex + delta) % NewsItems.Count + NewsItems.Count) % NewsItems.Count;
    }

    partial void OnCurrentNewsIndexChanged(int value)
    {
        if (value < 0 || value >= NewsItems.Count) return;   // the ListBox pushes -1 while the list's rebuilt
        CurrentNews = NewsItems[value];
        if (NewsItems.Count > 1) { _slideTimer.Stop(); _slideTimer.Start(); }
    }

    [RelayCommand]
    private void NextNews() => AdvanceNews(+1);

    [RelayCommand]
    private void PrevNews() => AdvanceNews(-1);

    [RelayCommand]
    private void OpenCurrentNews()
    {
        if (CurrentNews is { Url: { Length: > 0 } url }) OpenLink(url);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartAllAccountsCommand), nameof(RepairGameFilesCommand))]
    [NotifyPropertyChangedFor(nameof(ShowInstallCard), nameof(ShowSignInCard), nameof(ShowFriendsCard))]
    private bool _socialConnected;

    public SocialViewModel Social { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallCard), nameof(ShowSignInCard))]
    private bool _gameInstalled = true;

    public bool ShowInstallCard => !GameInstalled && !IsLaunching && !SocialConnected;
    public bool ShowSignInCard => GameInstalled && !IsLaunching && !SocialConnected;
    public bool ShowFriendsCard => SocialConnected && !IsLaunching;

    private static string? ViewerFolder(PolRegion? region) =>
        region is { } r ? PolPaths.TryGetInstallFolder(r, PolPaths.ViewerProduct) : null;

    private void RefreshInstallState()
    {
        var region = _settings.DetectedRegion;
        GameInstalled = region is { } r &&
                        PolPaths.TryGetInstallFolder(r, PolPaths.FfxiProduct) is { } f &&
                        File.Exists(Path.Combine(f, "FFXiMain.dll"));
        RefreshRepairAvailable(region);
        if (!StatusIconService.Available) LoadStatusIcons();
    }

    private void LoadStatusIcons() => StatusIconService.Load(ViewerFolder(_settings.DetectedRegion));

    [RelayCommand]
    private void LocateInstall()
    {
        if (SettingsViewModel.PickFolder("Find your FINAL FANTASY XI or PlayOnline folder") is not { } folder) return;
        var region = _settings.DetectedRegion ?? PolRegion.UnitedStates;
        var problem = GameInstaller.LocateExisting(folder, region, ex => Log.Warning(ex, "Could not record the install folder {Folder}", folder));
        if (problem != null) { MessageDialog.ShowError(Application.Current.MainWindow, "Couldn't use that folder", problem); return; }
        _settings.RedetectRegion();
        RefreshInstallState();
        if (GameInstalled) MessageDialog.ShowInfo(Application.Current.MainWindow, "Install found",
            "FINAL FANTASY XI is now set up in Provenance.");
        else MessageDialog.ShowError(Application.Current.MainWindow, "Game not found",
            "Provenance still can't find FINAL FANTASY XI in that folder. Choose the folder FINAL FANTASY XI is in, or install the game.");
    }

    [RelayCommand]
    private void DownloadInstall()
    {
        var vm = new InstallViewModel(_settings.DetectedRegion, _pol.HoldPrestarts, _pol.ReleasePrestarts);
        var w = new InstallWindow { DataContext = vm };
        if (Application.Current.MainWindow is { IsVisible: true } owner) w.Owner = owner;
        vm.Installed += () => Application.Current.Dispatcher.Invoke(() =>
        {
            _settings.RedetectRegion();
            SaveSetting(PolOptions.NewInstall);
            RefreshInstallState();
            Task.Run(() => LargeAddressAwareSwitch.Apply(_settings, "after the install", _pol.HoldPrestarts, _pol.ReleasePrestarts,
                                                          LargeAddressAwareBlocked));
        });
        w.ShowDialog();
    }

    public void Startup(bool autoLoginSuppressed)
    {
        _pol.EndOrphans();
        _pol.Prestart();
        if (LaunchArgs.Play && SelectedProfile != null && SelectedProfile.Id == (LaunchArgs.ProfileId ?? SelectedProfile.Id))
        {
            Log.Information("Signing in {Account} to start the game (--play)", SelectedProfile.Id);
            SignInCommand.Execute(null);
            return;
        }
        if (LaunchArgs.ProfileId != null || !AutoLogin || autoLoginSuppressed || !HasSavedCredentials) return;
        Log.Information("Auto sign-in for {Account}", SelectedProfile?.Id);
        SignInCommand.Execute(null);
    }

    private void OnSnapshot(PolSocialSnapshot snap)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_pol.Pid is null) return;   // read just before a sign-out
            // a run's lean session has no social side and nothing here may write a request to its hook
            if (MultiRunning) { _runSnapshot = snap; return; }
            try
            {
                Social.AccountName = SelectedProfile?.DisplayName ?? "PlayOnline";
                Social.Apply(snap);
                LearnIdentity(snap);
                if (!SocialConnected) { SocialConnected = true; _socialPoll.Start(); }
            }
            catch (Exception ex)
            {
                // this runs for the watcher's thread, where a throw would end the process
                Log.Error(ex, "Applying a social snapshot failed");
            }
        });
    }

    public void OpenChatWith(FriendItem friend)
    {
        if (MultiRunning) return;
        Social.SelectedFriend = Social.Friends.FirstOrDefault(f => f.Is(friend)) ?? friend;
        ShowSocialWindow(_settings.Current.SocialSplit ? SocialPart.Messages : SocialPart.Both, activate: true);
    }

    public void OpenNotice()
    {
        Notice = null;
        if (!NoticeOpens) return;
        if (_noticeGroupChat)
        {
            if (Social.GroupChat.Open) GroupChatWindow.ShowFor(Social.GroupChat, _settings);
            return;
        }
        if (_lastSender is { } who && Social.Friends.FirstOrDefault(f => f.Name == who) is { } f) OpenChatWith(f);
        else OpenSocial();
    }

    private void OpenProfilePicturePicker()
    {
        var viewer = ViewerFolder(_settings.DetectedRegion);
        var catalog = viewer is null ? null : ProfilePictureCatalog.Load(viewer);
        if (catalog is null)
        {
            MessageDialog.ShowError(Application.Current.MainWindow, "Profile picture",
                "Provenance couldn't find the PlayOnline profile pictures in the PlayOnline Viewer folder.");
            return;
        }
        var w = new ProfilePicturePickerWindow(catalog, _avatars, Social.MyProfilePictureId);
        if (Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) is { } owner) w.Owner = owner;
        if (w.ShowDialog() == true && w.Result is { } pic && pic != Social.MyProfilePictureId)
            Social.SaveProfilePicture(pic);
    }

    [RelayCommand]
    private void OpenSocial()
    {
        if (MultiRunning) return;
        ShowSocialWindow(_settings.Current.SocialSplit ? SocialPart.Friends : SocialPart.Both, activate: true);
    }

    [RelayCommand]
    private void OpenChatRooms()
    {
        if (MultiRunning) return;
        ChatRoomsWindow.ShowFor(Social, _pol.SaveChatPassword, _pol.DropChatPassword, _avatars.ChatEmote);
    }

    private static List<SocialWindow> SocialWindows() => Application.Current.Windows.OfType<SocialWindow>().ToList();

    // unowned, so they stay up while the main window's minimised or in the tray
    private void ShowSocialWindow(SocialPart part, bool activate, Rect? place = null)
    {
        var w = SocialWindows().FirstOrDefault(x => x.Part == part);
        if (w != null)
        {
            // Activate on its own doesn't bring back a minimised or hidden window
            if (!w.IsVisible) w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            if (activate) w.Activate();
            return;
        }
        foreach (var old in SocialWindows().Where(x => (x.Part == SocialPart.Both) != (part == SocialPart.Both))) old.Close();
        w = new SocialWindow(part) { DataContext = Social, ShowActivated = activate };
        if (place is null && part == SocialPart.Messages
            && SocialWindows().FirstOrDefault(x => x.Part == SocialPart.Friends) is { IsVisible: true } fw)
        {
            double left = fw.Left + fw.ActualWidth + 8;
            if (left + w.Width > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth) left = fw.Left - w.Width - 8;
            place = new Rect(left, fw.Top, w.Width, fw.ActualHeight);
        }
        if (place is { } r)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            (w.Left, w.Top, w.Width, w.Height) = (r.Left, r.Top, r.Width, r.Height);
        }
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Show();
    }

    private void OnSocialLayout(SocialViewModel.LayoutAction action)
    {
        bool split = _settings.Current.SocialSplit;
        switch (action)
        {
            case SocialViewModel.LayoutAction.Split: SetSocialSplit(true); break;
            case SocialViewModel.LayoutAction.Merge: SetSocialSplit(false); break;
            case SocialViewModel.LayoutAction.OpenMessages when split:
                ShowSocialWindow(SocialPart.Messages, activate: true);
                break;
            case SocialViewModel.LayoutAction.ShowMessages when split && !SocialWindows().Any(x => x.Part == SocialPart.Messages):
                ShowSocialWindow(SocialPart.Messages, activate: false);
                break;
        }
    }

    private void SetSocialSplit(bool split)
    {
        if (_settings.Current.SocialSplit == split) return;
        var open = SocialWindows().Where(w => w.IsVisible).ToList();
        Rect? area = open.Count == 0 ? null
            : open.Select(w => w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight) : w.RestoreBounds)
                  .Aggregate(Rect.Union);
        SaveSetting(s => s.SocialSplit = split);
        foreach (var w in SocialWindows()) w.Close();
        if (split)
        {
            const double friendsWidth = 340, gap = 8;
            bool conversation = Social.SelectedFriend != null;
            ShowSocialWindow(SocialPart.Friends, activate: !conversation,
                             area is { } a ? new Rect(a.Left, a.Top, friendsWidth, a.Height) : null);
            if (conversation)
                ShowSocialWindow(SocialPart.Messages, activate: true,
                                 area is { } b ? new Rect(b.Left + friendsWidth + gap, b.Top, Math.Max(420, b.Width - friendsWidth - gap), b.Height) : null);
        }
        else
            ShowSocialWindow(SocialPart.Both, activate: true, area is { } a ? new Rect(a.Left, a.Top, Math.Max(720, a.Width), a.Height) : null);
    }

    [RelayCommand]
    private void SignOut()
    {
        // Play's game update needs the session it's holding
        if (MultiRunning || IsLaunching) return;
        if (!EditProfileWindow.ConfirmDrop("Log out")) return;
        EndSession(SessionEnd.LogOut);
    }

    private static void CloseSocialWindows()
    {
        EditProfileWindow.Current?.CloseQuietly();
        ChatRoomsWindow.Current?.CloseQuietly();
        GroupChatWindow.Current?.CloseQuietly();
        foreach (var w in SocialWindows()) w.Close();
    }

    private void EndSession(SessionEnd end)
    {
        Social.SettleSends(end);
        _socialPoll.Stop();
        CloseSocialWindows();
        Social.GroupChat.CloseQuietly();
        _sessionEnding = true;   // before SocialConnected, so the Repair button never shows enabled in between
        SocialConnected = false;
        Task.Run(() =>
        {
            try
            {
                _pol.StopHold();    // the kill waits a few seconds, so keep it off the UI thread
                RefreshStartAll();
                _pol.Prestart();
            }
            finally { Application.Current?.Dispatcher.InvokeAsync(() => { _sessionEnding = false; RefreshRepair(); }); }
        });
    }

    private async Task ConnectSocialAsync()
    {
        var owner = Application.Current?.MainWindow;
        if (SelectedProfile == null && !TryAddAccount()) return;
        // one account for the whole sign-in, so switching mid-way can't mix two up
        if (SelectedProfile is not { } profile) return;
        if (!HasSavedCredentials && !EditAccount(requireAll: true)) return;
        if (!EnsureMemberAgreement(owner)) return;
        // a one-time code counts as used once typed, so the details dialog has to come first
        if (await LoadForSignInAsync(owner, profile) is not { } details) return;
        var (pol, sea, keyGeneration) = details;
        // claimed before the code's asked, so no other copy signs this account in meanwhile
        if (!_pol.TryClaimAccount(profile.Id, out var claimProblem))
        {
            MessageDialog.ShowError(owner, "Account in use", claimProblem ?? "This account is in use in another copy of Provenance.");
            return;
        }
        var otp = profile.UseOtp ? AskOtp(owner, profile, sea) : "";
        if (otp is null)
        {
            _pol.ReleaseClaim();
            return;
        }
        Social.Start(profile.Id);
        _sessionProfileId = profile.Id;
        IsLaunching = true;
        var token = BeginCancellable(ProfileTitle.StartsWith("New account", StringComparison.Ordinal)
            ? "Logging in to PlayOnline" : "Logging in to " + ProfileTitle, "Starting PlayOnline...");
        bool startAfter = false;
        string? lastFailure = null;
        var progress = new Progress<LaunchProgress>(p =>
        {
            OnProgress(p);
            if (p.Stage is "Failed" or "Stopped") lastFailure = p.Detail;
        });
        try
        {
            int code = await _pol.ConnectAsync(pol, sea, otp, profile.Id, progress, PromptHandleName, token,
                                               new SessionOptions(false, null, keyGeneration, HomeSlotFor(profile)));
            await Application.Current!.Dispatcher.InvokeAsync(() => { });   // an empty hop so queued progress reports land first (yes, really)
            if (code == 0)
            {
                ProgressFraction = 1.0;
                _elapsedTimer.Stop();
                SocialConnected = true;
                _socialPoll.Start();
                startAfter = StartGameImmediately || LaunchArgs.Play;
            }
            else
            {
                CanCancel = false;
                await Task.Run(() =>
                {
                    _pol.StopHold();
                    _pol.Prestart();
                });
                var failure = LoginFailure(code, lastFailure, AutoLogin);
                if (failure.AutoOff) AutoLogin = false;
                if (code != PolSessionService.ErrorCancelled)
                    MessageDialog.ShowError(Application.Current?.MainWindow, failure.Title, failure.Text, offerLogs: failure.OfferLogs);
            }
        }
        catch (OperationCanceledException) { await Task.Run(() => _pol.StopHold()); }
        catch (Exception ex)
        {
            Log.Error(ex, "Social connect failed");
            await Task.Run(() => _pol.StopHold());
            MessageDialog.ShowError(Application.Current?.MainWindow, "Couldn't log in",
                ErrorText.Of(ex, "Provenance ran into a problem while logging in. Try again, and if it keeps happening, press Open logs to see what went wrong."),
                offerLogs: true);
        }
        finally
        {
            EndCancellable();
            IsLaunching = false;
        }
        if (startAfter && PlayCommand.CanExecute(null)) await PlayCommand.ExecuteAsync(null);
    }

    // never a retry by itself after a refusal, it'd stretch a three-strikes lockout. A refused password or a handle
    // problem is the user's to fix, anything else might need the log
    internal static (string Title, string Text, bool AutoOff, bool OfferLogs) LoginFailure(int code, string? detail, bool autoLogin)
    {
        bool autoOff = code is PolSessionService.ErrorAuthFailed or PolSessionService.ErrorAccountBlocked
                           or PolSessionService.ErrorDetailsUnreadable && autoLogin;
        var why = detail ?? $"Provenance couldn't log in to PlayOnline (code {code}).";
        if (code == PolSessionService.ErrorAuthFailed)
            why += "\n\nIf your password has changed, press Edit account to update it, then log in again.";
        if (autoOff)
            why += "\n\nProvenance has turned off Log in automatically, so it won't try this account again by itself.";
        return (TitleFor(code), why, autoOff,
                code is PolSessionService.ErrorStalled or PolSessionService.ErrorLauncherFailed or PolSessionService.ErrorUnfinished
                || TitleFor(code) == "Couldn't log in");
    }

    // accounts on one SE ID share an authenticator
    private static string? AskOtp(Window? owner, Profile profile, SeaAccount sea) =>
        OtpDialog.Ask(owner, profile.OtpSource, profile.OtpItem, "sea:" + sea.Username.Trim().ToLowerInvariant(),
                      sea.Username, profile.OtpAccount);

    // both IDs there means the entries exist but won't unwrap (a key that's gone, say)
    internal static string MissingDetailsText((string? PolId, string? SeaId) ids) =>
        !string.IsNullOrEmpty(ids.PolId) && !string.IsNullOrEmpty(ids.SeaId)
            ? PolSessionService.DetailsAgainText
            : "This account's login details are missing. Press Edit account to add them.";

    private async Task<(PolAccount Pol, SeaAccount Sea, int KeyGeneration)?> LoadForSignInAsync(Window? owner, Profile profile)
    {
        if (await PrepareSignInAsync(_credentials, profile, () => SelectedProfile) is not { } details) return null;
        if (details.Missing)
        {
            if (!EditAccount(requireAll: true)) return null;
            details = PrepareSignIn(_credentials, profile.Id);
            if (details.Missing)
            {
                MessageDialog.ShowError(owner, "Account details incomplete", MissingDetailsText(_credentials.ReadIds(profile.Id)));
                return null;
            }
        }
        if (details.Problem is { } problem)
        {
            MessageDialog.ShowError(owner, "Account details incomplete", problem);
            return null;
        }
        return (details.Pol!, details.Sea!, details.KeyGeneration);
    }

    internal sealed record SignInDetails(PolAccount? Pol, SeaAccount? Sea, string? Problem, int KeyGeneration = 0)
    {
        public bool Missing => Pol is null && Problem is null;
    }

    internal static async Task<SignInDetails> PrepareSignInAsync(ICredentialService credentials, string profileId)
    {
        await credentials.CheckKeyAsync();
        return PrepareSignIn(credentials, profileId);
    }

    internal static async Task<SignInDetails?> PrepareSignInAsync(ICredentialService credentials, Profile profile, Func<Profile?> selected)
    {
        await credentials.CheckKeyAsync();
        return ReferenceEquals(selected(), profile) ? PrepareSignIn(credentials, profile.Id) : null;
    }

    internal static SignInDetails PrepareSignIn(ICredentialService credentials, string profileId)
    {
        int generation = CredentialKey.Generation;   // read before the loads, so a key change during them shows up
        var pol = credentials.LoadPolAccount(profileId);
        var sea = credentials.LoadSeaAccount(profileId);
        if (pol is null || sea is null) return new(null, null, null, generation);
        if (ValidateDetails(pol.PolId, pol.Password, sea.Username, sea.Password) is { } problem)
            return new(null, null, problem + " Press Edit account to fix it.", generation);
        return new(new PolAccount(pol.PolId.Trim().ToUpperInvariant(), pol.Password), new SeaAccount(sea.Username.Trim(), sea.Password),
                   null, generation);
    }

    // the US viewer asks once per PlayOnline ID, here it's once per install
    private bool EnsureMemberAgreement(Window? owner)
    {
        if (_settings.Current.AcceptedMemberAgreement) return true;
        if (!PolPaths.Supported(_settings.DetectedRegion)) return true;
        var viewer = ViewerFolder(_settings.DetectedRegion);
        var strings = viewer is null ? null : PolStringTable.Load(viewer);
        if (!AgreementDialog.Ask(owner, strings))
        {
            Log.Information("Member Agreement declined: not signing in");
            ShowNotice("You're not logged in, because you didn't accept the PlayOnline Member Agreement.", "InformationOutline", opens: false);
            return false;
        }
        SaveSetting(s => s.AcceptedMemberAgreement = true);
        return true;
    }

    private static string? PromptHandleName() =>
        Application.Current!.Dispatcher.Invoke(() =>
        {
            var owner = Application.Current.MainWindow;
            var name = InputDialog.Prompt(owner, "Create PlayOnline handle",
                "This PlayOnline account doesn't have a handle yet, and FINAL FANTASY XI needs one to log in.\n\n" +
                "A handle is your name on PlayOnline, and other players can see it. You can rename it later.\n\n" +
                "Enter 1 to 15 English letters, numbers or symbols:",
                maxLength: 15,
                // the viewer's and polcore's rule, plain ASCII and not just spaces
                validate: name => name.Any(c => c < 0x20 || c > 0x7e)
                    ? "Use English letters, numbers and symbols only."
                    : string.IsNullOrWhiteSpace(name) ? "Type a handle name." : null);
            if (name is null) return null;
            // a handle's public straight away, so one last look first
            return MessageDialog.Confirm(owner, "Create PlayOnline handle",
                $"Create the handle {name}? Other players will see it, and you can rename it later by editing your profile.", "CREATE") is null
                ? null : name;
        });

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (MultiRunning) return;
        // POL's own Play gate, and it only asks for each list at sign-in so there's no reload here either
        if (Social.PlayBlockedReason is { } why)
        {
            MessageDialog.ShowError(Application.Current.MainWindow, "Can't start FINAL FANTASY XI", why);
            return;
        }
        _socialPoll.Stop();
        // nothing new goes to the hook from here, the game update still applies its answers
        Social.GameRunning = true;
        IsLaunching = true;
        // like the viewer, the game update comes after the sign-in with the session held meanwhile
        if (!await UpdateGameAsync())
        {
            Social.GameRunning = false;
            IsLaunching = false;
            _socialPoll.Start();
            return;
        }
        if (await LinkBeforePlayAsync() is var linked and not PreLaunchResult.Play)
        {
            Social.GameRunning = false;
            _elapsedTimer.Stop();
            IsLaunching = false;
            _socialPoll.Start();
            if (linked == PreLaunchResult.ShowHandles) OpenSettingsAt(SettingsWindow.HandlesTab);
            return;
        }
        // SE's installer only writes 3 of the game settings
        InstallViewModel.WriteFirstStartSettings(PlayRegion);
        WritePolOptions(_sessionProfileId);
        ResetProgress("Starting FINAL FANTASY XI", "Starting FINAL FANTASY XI...");
        await BackUpGameSettingsAsync(null, text => ProgressText = text);
        OnProgress(new LaunchProgress("Starting game", "Starting FINAL FANTASY XI...", LaunchSteps.StartGame, 0.5));
        try { _pol.SignalPlay(); }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not signal Play");
            Social.GameRunning = false;
            _elapsedTimer.Stop();
            IsLaunching = false;
            _socialPoll.Start();
            MessageDialog.ShowError(Application.Current.MainWindow, "Can't start FINAL FANTASY XI",
                ex is CredentialKeyUnavailableException ? ex.Message : "Provenance couldn't tell PlayOnline to start the game. Try again.",
                offerLogs: true);
            return;
        }
        Social.SettleSends(SessionEnd.Play);
        _gameRunning = true;
        if (await _pol.WaitForPlayAnswerAsync(PlayAnswerTimeout) is { } failed)
        {
            _gameRunning = false;
            Social.GameRunning = false;
            _elapsedTimer.Stop();
            IsLaunching = false;
            CloseSocialWindows();
            SocialConnected = false;
            RefreshStartAll();
            _pol.Prestart();
            MessageDialog.ShowError(Application.Current.MainWindow, "Can't start FINAL FANTASY XI", failed);
            return;
        }
        bool up = false;
        try { up = await BringGameForwardAsync(); }
        catch (Exception ex) { Log.Warning(ex, "Could not bring the game window to the front"); }
        if (up) Handles.GameStarted();
        RequestHide?.Invoke();
        _elapsedTimer.Stop();
        var exit = await _pol.WaitForExitAsync();
        _gameRunning = false;
        Social.GameRunning = false;
        _pol.GameExited();
        var ended = PolSessionService.GameEndedText(exit);
        if (ended is not null) Log.Error("FINAL FANTASY XI didn't start (pol.exe exit code {Code})", exit);
        else if (_settings.Current.ExitAfterGameExits) { RequestExit?.Invoke(); return; }
        IsLaunching = false;
        CloseSocialWindows();
        SocialConnected = false;
        RequestShow?.Invoke();
        _pol.Prestart();
        if (ended is not null) MessageDialog.ShowError(Application.Current.MainWindow, "Can't start FINAL FANTASY XI", ended, offerLogs: true);
    }

    internal static readonly TimeSpan PlayAnswerTimeout = TimeSpan.FromSeconds(15);

    // a test answers the questions itself, and says whether its made-up session is still there
    internal Func<string, bool>? PreLaunchAnswerForTest { get; set; }
    internal Func<int, bool>? SessionAliveForTest { get; set; }

    // where the viewer does it, after the update and right before the game starts
    internal Task<PreLaunchResult> LinkBeforePlayAsync()
    {
        var owner = Application.Current?.MainWindow;
        ResetProgress("Starting FINAL FANTASY XI", "Starting FINAL FANTASY XI...");
        return new PreLaunchLink
        {
            Pid = () => _pol.Pid,
            Ask = text => Asking(() => MessageDialog.Confirm(owner, PreLaunchLink.Title, text, "YES", cancelLabel: "NO") == 0, text),
            Failed = text => Asking(() =>
            {
                MessageDialog.ShowError(owner, PreLaunchLink.Title, text);
                return true;
            }, text),
            Saying = text => ProgressText = text,
            PageWaiting = () => Handles.Busy ? Handles.Note : null,
            Alive = SessionAliveForTest ?? PreLaunchLink.ProcessAlive,
            Read = _ => Social.Last,
        }.RunAsync();
    }

    // the footer's "has to wait till the game's closed" isn't true while the answer can still stop it starting
    private bool Asking(Func<bool> dialog, string text)
    {
        var note = Social.StatusNote;
        Social.StatusNote = "";
        try { return PreLaunchAnswerForTest is { } answer ? answer(text) : dialog(); }
        finally { if (Social.StatusNote == "") Social.StatusNote = note; }
    }

    // Play only comes up after a login, which only a supported region gets
    private PolRegion PlayRegion => _settings.DetectedRegion ?? PolRegion.UnitedStates;

    // the game reads these from the account's option.bin when it starts, so they go in before every start
    private void WritePolOptions(string? profileId)
    {
        if (PolPaths.TryGetInstallFolder(PlayRegion, PolPaths.ViewerProduct) is { } viewer) WritePolOptions(profileId, viewer);
    }

    internal void WritePolOptions(string? profileId, string viewer)
    {
        try
        {
            if (Profiles.FirstOrDefault(p => p.Id == profileId) is not { } profile)
            {
                Log.Warning("The session's account is gone, its POL settings aren't written");
                return;
            }
            var s = _settings.Current;
            using (_settings.HoldLock())
            {
                // another copy may have changed them since this one read settings.json
                if (_settings.ReadSaved() is { } saved)
                {
                    s.ChatLanguageFilter = saved.ChatLanguageFilter ?? s.ChatLanguageFilter;
                    s.AutoDisconnectMinutes = saved.AutoDisconnectMinutes ?? s.AutoDisconnectMinutes;
                }
            }
            PolOptions.Apply(s, viewer, PolOptions.SlotInUse(viewer, profile.HomeSlot), PlayRegion);
        }
        catch (Exception e) { Log.Warning(e, "Could not write the POL settings before the game start"); }
    }

    // a test points it at made-up folders
    internal Func<LauncherSettings, string?, BackupSources?>? GameBackupSourcesForTest { get; set; }

    // right before the game starts. However it goes the game still starts, a failed one only gets a notice
    internal async Task BackUpGameSettingsAsync(string? accountBoot, Action<string>? saying = null)
    {
        var s = _settings.ReadSaved() ?? _settings.Current;
        if (!s.BackupGameSettings) return;
        saying?.Invoke(GameBackups.WorkingText);
        var region = PlayRegion;
        var sources = GameBackupSourcesForTest ?? ((settings, boot) => GameBackups.SourcesFor(settings, region, boot));
        if (await Task.Run(() => GameBackups.BeforePlay(s, () => sources(s, accountBoot))) is { } problem)
            ShowNotice(problem, "AlertCircleOutline", opens: false, stays: true);
    }

    private (string Ffxi, string Viewer, PolRegion Region)? GameFolders() =>
        PolPaths.TryGetInstallFolder(PlayRegion, PolPaths.FfxiProduct) is { } ffxi
        && PolPaths.TryGetInstallFolder(PlayRegion, PolPaths.ViewerProduct) is { } viewer
            ? (ffxi, viewer, PlayRegion) : null;

    private async Task<bool> UpdateGameAsync()
    {
        if (GameFolders() is not { } dirs) return true;
        var ct = BeginCancellable("Updating FINAL FANTASY XI", "Checking for FINAL FANTASY XI updates...");
        var progress = new Progress<PatchProgress>(p => OnProgress(LaunchProgress.Updating("Updating", LaunchSteps.StartGame, p, 0.01, 0.99)));
        try
        {
            await Task.Run(() => PolUpdates.UpdateFfxi(dirs.Ffxi, dirs.Viewer, dirs.Region, progress, ct), ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            ShowNotice("The FINAL FANTASY XI update was stopped. Press Play to continue it.", "InformationOutline", opens: false);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FINAL FANTASY XI update failed");
            bool inUse = ex is UnauthorizedAccessException || ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);
            MessageDialog.ShowError(Application.Current.MainWindow, "Couldn't update FINAL FANTASY XI",
                inUse ? "One of the FINAL FANTASY XI files is in use, so it couldn't be updated. Close every FINAL FANTASY XI window, then press Play again."
                      : ErrorText.Of(ex, "The FINAL FANTASY XI update didn't finish. Press Play to try again, and if it keeps happening, press Open logs to see what went wrong."),
                offerLogs: !ErrorText.Ours(ex));
            return false;
        }
        finally { EndCancellable(); }
    }

    // Windows only hands focus to the last program used, which is the app at the Play click
    // true once the game's window is up
    private async Task<bool> BringGameForwardAsync()
    {
        if (_pol.Pid is not { } pid) return false;
        ThirdPartyHookWindows.AllowForeground(pid);
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until)
        {
            try { using var p = Process.GetProcessById(pid); if (p.HasExited) return false; }
            catch (ArgumentException) { return false; }
            if (ThirdPartyHookWindows.GameWindow(pid) is var h && h != IntPtr.Zero)
            {
                bool front = ThirdPartyHookWindows.BringToFront(h);
                Log.Information("Game window is up (pol.exe {Pid}); brought to the front: {Front}", pid, front);
                return true;
            }
            await Task.Delay(250);
        }
        Log.Warning("No game window from pol.exe {Pid} after 90 s", pid);
        return false;
    }

    private HandlesViewModel? _handles;

    // made once and fed every snapshot, so a change still going when Settings closes is answered all the same
    internal HandlesViewModel Handles
    {
        get
        {
            if (_handles != null) return _handles;
            _handles = new HandlesViewModel(() => _pol.Pid, _avatars.ProfilePicture)
            {
                SwitchBlocked = () => EditProfileWindow.Current is not null
                    ? "Close the profile editor first. It's editing the active handle's profile." : Social.PendingWrite(),
            };
            Social.SnapshotApplied += _ => ShowHandles();
            _handles.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(HandlesViewModel.Busy)) Social.HandleSwitching = _handles.Switching;
            };
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(SocialConnected) or nameof(MultiRunning)) ShowHandles();
            };
            Social.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SocialViewModel.GameRunning)) ShowHandles(); };
            return _handles;
        }
    }

    // a multi-account run's lean sessions take no requests
    private void ShowHandles()
    {
        bool on = SocialConnected && _pol.Pid is not null;
        Handles.Update(on ? Social.Last : null, on,
                       Social.GameRunning ? HandleList.ReadOnlyText : MultiRunning ? HandleList.MultiRunText : null);
        Social.HandleSwitching = Handles.Switching;
    }

    [RelayCommand]
    private void OpenSettings() => OpenSettingsAt(0);

    private void OpenSettingsAt(int tab)
    {
        var hookBefore = _settings.Current.ThirdPartyHookPlatform;
        var handles = Handles;
        ShowHandles();
        var vm = new SettingsViewModel(_settings, multiRunning: () => MultiRunning,
                                       holdPrestarts: _pol.HoldPrestarts, releasePrestarts: _pol.ReleasePrestarts,
                                       largeAddressAwareBlocked: LargeAddressAwareBlocked,
                                       stopPrestart: () => Task.Run(_pol.StopPrestart),
                                       loggedIn: () => IsLaunching || SocialConnected || _pol.Pid is not null,
                                       handles: handles);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SettingsViewModel.ThirdPartyHookIndex)) PlatformChanged(); };
        var w = new SettingsWindow { DataContext = vm };
        w.Tabs.SelectedIndex = tab;
        if (Application.Current.MainWindow is { IsVisible: true } owner) w.Owner = owner;
        _openSettings = vm;
        handles.Opened();
        try { w.ShowDialog(); }
        finally
        {
            _openSettings = null;
            handles.Closed();
        }
        if (vm.RestartWanted)
        {
            RequestRestart?.Invoke();
            return;
        }
        OnPropertyChanged(nameof(CloseTooltip));
        RefreshMultiAccounts();
        if (_settings.Current.ThirdPartyHookPlatform != hookBefore && !SocialConnected && !IsLaunching)
            Task.Run(() => { _pol.StopPrestart(); _pol.Prestart(); });
    }

    public string? PlaySubtitle => _pol.Platform switch
    {
        ThirdPartyHookService.Windower => "WINDOWER LOADED",
        ThirdPartyHookService.Ashita => "ASHITA LOADED",
        _ => null,
    };

    internal void PlatformChanged()
    {
        OnPropertyChanged(nameof(PlaySubtitle));
        OnPropertyChanged(nameof(HasPlayChoices));
        OnPropertyChanged(nameof(PlayChoicesTitle));
    }

    public sealed record PlayChoice(string Name, string Display, bool Current);

    public bool HasPlayChoices => _pol.Platform is ThirdPartyHookService.Windower or ThirdPartyHookService.Ashita;

    public string PlayChoicesTitle => _pol.Platform == ThirdPartyHookService.Ashita ? "Boot configuration" : "Windower profile";

    public string NoPlayChoicesText => _pol.Platform == ThirdPartyHookService.Ashita
        ? "No boot configurations found" : "No Windower profiles found";

    // the same pick Play uses: Ashita falls back to the last config Ashita ran with, Windower's "" is the unnamed one
    public IReadOnlyList<PlayChoice> PlayChoices()
    {
        var s = _settings.Current;
        if (_pol.Platform == ThirdPartyHookService.Ashita)
        {
            var dir = ThirdPartyHookService.DetectAshita(s.AshitaPath);
            var current = string.IsNullOrWhiteSpace(s.AshitaBootConfig) ? ThirdPartyHookService.LastUsedAshitaBootConfig(dir) : s.AshitaBootConfig;
            return AshitaBootFiles.Names(dir)
                .Select(n => new PlayChoice(n.File, n.Shown, n.File.Equals(current, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        if (_pol.Platform == ThirdPartyHookService.Windower)
        {
            var names = ThirdPartyHookService.WindowerProfiles(ThirdPartyHookService.DetectWindower(s.WindowerPath));
            var current = WindowerSettingsFile.OldCopySource(s.WindowerProfile);
            return names.Select(n => new PlayChoice(n, SettingsViewModel.WindowerDisplay(n, names), n == current)).ToList();
        }
        return [];
    }

    public void PickPlayChoice(string name)
    {
        var s = _settings.Current;
        if (_pol.Platform == ThirdPartyHookService.Ashita) s.AshitaBootConfig = name;
        else if (_pol.Platform == ThirdPartyHookService.Windower) s.WindowerProfile = name;
        else return;
        _settings.Save(s);
    }

    // the Repair button's own checks, in its order. The lock comes from the switch's copy of its probe
    private string? LargeAddressAwareBlocked() =>
        IsLaunching || SocialConnected || _pol.Pid is not null || _sessionEnding ? LargeAddressAwareSwitch.LoggedInText
        : MultiRunning ? LargeAddressAwareSwitch.MultiRunText
        : null;

    // the URL comes from a feed, and ShellExecute would happily run a program, a network path or another protocol
    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
        {
            if (!string.IsNullOrWhiteSpace(url)) Log.Warning("Not opening a link that is not a web link: {Url}", url);
            return;
        }
        // the app runs elevated, so a browser started from here would be too. explorer.exe (full path, a bare name's
        // looked for in the app folder first) hands it to the user's own shell.
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
            psi.ArgumentList.Add(u.AbsoluteUri);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Url} through the shell; opening it directly", u.AbsoluteUri);
            try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex2)
            {
                Log.Warning(ex2, "Could not open {Url}", u.AbsoluteUri);
                MessageDialog.ShowError(Application.Current?.MainWindow, "Couldn't open the link",
                    $"Windows didn't open the link. You can copy it from here: {u.AbsoluteUri}");
            }
        }
    }
}
