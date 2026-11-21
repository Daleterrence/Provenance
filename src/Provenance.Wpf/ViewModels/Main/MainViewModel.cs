using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common;
using Provenance.Common.Credentials;
using Provenance.Common.Hook;
using Provenance.Common.Logging;
using Provenance.Common.Settings;
using Serilog;
using Provenance.Common.Feeds;
using Provenance.Common.Game;
using Provenance.Common.Install;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Common.Social;
using Provenance.Common.Login;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Shared;
using Provenance.Wpf.ViewModels.Install;
using Provenance.Wpf.ViewModels.Chat;
using Provenance.Wpf.ViewModels.Social;
using Provenance.Wpf.ViewModels.Settings;

namespace Provenance.Wpf.ViewModels.Main;

public partial class MainViewModel : ObservableObject
{
    private readonly UiThread _ui = new();
    private readonly IDialogs _dialogs;
    private readonly IWindowManager _windows;
    private readonly ISettingsService _settings;
    private readonly PolSession _pol;
    private readonly AvatarService _avatars;
    private readonly FeedService _feeds;
    private readonly GameLaunch _launch;
    private readonly DispatcherTimer _socialPoll, _elapsedTimer;

    internal bool SocialPolling { get => _socialPoll.IsEnabled; set => _socialPoll.IsEnabled = value; }
    internal bool ClockRunning { get => _elapsedTimer.IsEnabled; set => _elapsedTimer.IsEnabled = value; }

    public event Action? RequestHide;
    public event Action? RequestShow;
    public event Action? RequestRestart;
    public event Action? RequestUpdateRestart;
    public event Action? RequestExit;
    public event Action<bool, bool>? RequestAttention;
    public event Action? AttentionCleared;

    public MainViewModel(IDialogs dialogs, IWindowManager windows, ICredentialService credentials, ISettingsService settings,
                         FeedService feeds, PolSession pol, AvatarService avatars, TimeProvider? time = null, GameLaunch? launch = null,
                         Func<Task<bool>>? prepareRestart = null)
    {
        _dialogs = dialogs;
        _windows = windows;
        _settings = settings;
        int saveNoticed = 0;
        void SaveNotice()
        {
            if (Interlocked.Exchange(ref saveNoticed, 1) == 0)
                _ui.Post(() => _dialogs.Error("Settings not saved",
                    "Provenance couldn't save your settings, so your changes will only last until Provenance closes.", offerLogs: true));
        }
        _settings.SaveFailed += _ => SaveNotice();
        if (_settings.SaveProblem is not null) SaveNotice();
        if (_settings.LoadProblem is { } loadProblem)
            _ui.Post(() => _dialogs.Info("Settings reset", loadProblem));
        _updateRestart = new UpdateRestart(dialogs, prepareRestart ?? SelfUpdate.PrepareRestartAsync, () => RequestUpdateRestart?.Invoke(),
                                     RestartBlocked);
        UpdateNotice = new UpdateBanner(_updateRestart);
        _pol = pol;
        Session = new SessionState(pol);
        _launch = launch ?? new GameLaunch(pol, settings);
        _avatars = avatars;
        _feeds = feeds;
        _timeLeft = new PhaseTimeLeft(text => TimeLeftText = text);
        News = new NewsViewModel(feeds, _ui, OpenLink, time);
        Accounts = new AccountsViewModel(dialogs, credentials, settings, avatars);
        PlayPlatform = new PlayPlatform(pol, settings);
        Setup = new SetupViewModel(dialogs, settings);
        Setup.Finished += OnSetupFinished;
        Accounts.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AccountsViewModel.HasProfile)) RefreshSetupCard(); };
        _socialPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _socialPoll.Tick += (_, _) => _pol.Poll();
        GameStart = new GameStart(dialogs, windows, settings, pol, _launch, this,
            new GameStartLinks(SetPhase, BeginCancellable, EndCancellable, ResetProgress, OnProgress, () => OpenSettingsAt(SettingsTab.Handles),
                               () => RequestHide?.Invoke(), () => RequestShow?.Invoke(), () => RequestExit?.Invoke()));
        Multi = new MultiRunViewModel(dialogs, settings, credentials, pol, _ui, Accounts, Session, _launch,
            new MultiRunLinks(SetPhase, EnsureMemberAgreement, PromptHandleName, GameStart.BackUpGameSettingsAsync));
        Multi.UpdateBusy = () => UpdatingNow;
        _updateRestart.BusyChanged += () =>
        {
            OnPropertyChanged(nameof(PlayEnabled));
            LogInCommand.NotifyCanExecuteChanged();
            Multi.StartAllAccountsCommand.NotifyCanExecuteChanged();
        };
        Repair = new RepairButtonViewModel(dialogs, windows, settings, pol, Session, () => News.WindowShown, RefreshInstallState);
        Lean = new LeanStartViewModel(dialogs, windows, settings, pol, _ui, Accounts, _launch, this,
            new LeanStartLinks(() => GameInstalled, SetPhase, OnProgress, ResetProgress, EndCancellable, GameStart.BackUpGameSettingsAsync, id => StartSocial(id),
                               OnSnapshot, GameStart.PlayWithAsync, () => RequestShow?.Invoke(), () => RequestExit?.Invoke()));
        _pol.SnapshotUpdated += OnSnapshot;
        _pol.PidChanged += () => _ui.Post(() =>
        {
            Multi.StartAllAccountsCommand.NotifyCanExecuteChanged();
            Repair.Refresh();
        });
        _pol.SessionEnded += why => _ui.Run(() =>
        {
            Log.Information("Session ended: {Why} (logged in here: {Connected})", why, LoggedIn);
            if (!LoggedIn) return;
            EndSession(SessionEnd.Dropped);
            RequestShow?.Invoke();
            _dialogs.Info("PlayOnline session ended", why);
            Log.Information("Session ended: the notice was closed");
        });
        Viewer = new ViewerResources(settings);
        Social = new SocialViewModel(dialogs, windows, _pol, avatars, _settings, Session, Viewer, time);
        Social.OpenChatRequested += OpenChatWith;
        Social.PostToUi = action => _ui.Post(action);
        _socialPoll.Tick += (_, _) => Social.TickZones();
        Accounts.MessagesEnding = Social.StopKeeping;

        Multi.RequestShow += () => RequestShow?.Invoke();
        Multi.RequestExit += () => RequestExit?.Invoke();
        Social.MessageReceived += (from, text) => Alert(NoticeKind.Message, from, $"New message from {from}: {TextBits.Preview(text, 60)}");
        Social.FriendRequestReceived += (who, mid) =>
            Alert(NoticeKind.FriendRequest, null, $"{who} sent you a friend request. Open {FriendsWindowTitle} to answer it.", "AccountPlusOutline",
                  stays: true, request: mid);
        Social.GroupInviteReceived += (who, mid) =>
            Alert(NoticeKind.GroupInvite, who, $"{who} invited you to a friend group. Open {FriendsWindowTitle} to answer it.", "BellOutline",
                  request: mid);
        Social.NoticeReceived += (who, text) => Alert(NoticeKind.Answer, who, text, "BellOutline");
        Social.GroupChatRequested += OpenGroupChat;
        Social.GroupChat.LineArrived += OnGroupChatLine;
        Social.FriendCameOnline += who =>
        {
            if (GameOpen || !_settings.Current.Shows(NoticeKind.FriendOnline) || Notice.Current is { Text.Length: > 0 }) return;
            Notice.Show($"{who} is online now.", "AccountCheckOutline", NoticeTarget.Chat, who);
        };
        Handles = new HandlesViewModel(_dialogs, () => _pol.Pid, _avatars.ProfilePicture, Session, () => Social.Last, time, Viewer.TextOutsideJapan)
        {
            SwitchBlocked = () => _windows.ProfileEditorOpen
                ? "Close the profile editor first. It's editing the active handle's profile." : Social.PendingWrite(),
        };
        Social.SnapshotApplied += _ =>
        {
            Notice.DropIfAnswered(Social.IsWaiting);
            ShowHandles();
        };
        Social.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SocialViewModel.UnseenCount) or nameof(SocialViewModel.GroupChatUnread) && !WantsAttention)
                AttentionCleared?.Invoke();
            if (e.PropertyName == nameof(SocialViewModel.CanPlay)) OnPropertyChanged(nameof(PlayEnabled));
            if (e.PropertyName == nameof(SocialViewModel.HandleSwitching)) _windows.SetBusy(Social.HandleSwitching ? HandleSwitchingText : null);
        };
        Handles.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(HandlesViewModel.Busy)) return;
            Social.HandleSwitching = Handles.Switching;
            if (Handles.Busy) DropHandleNotice();
        };
        RefreshInstallState();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _elapsedTimer.Tick += (_, _) =>
        {
            ElapsedText = TextBits.Seconds(DateTime.UtcNow - _launchStarted);
        };
        Session.PhaseChanged += OnPhaseChanged;
        RefreshSetupCard();
    }

    public SessionState Session { get; }

    public bool LoggedIn => Session.LoggedIn;

    public bool IsLaunching => Session.Launching;

    private LastRaised _loggedInShown, _launchingShown, _inGameShown;

    internal void SetPhase(SessionPhase next)
    {
        if (!SessionState.IsAllowed(Session.Phase, next))
            Log.Warning("Session phase: {From} to {To} isn't a move the app makes", Session.Phase, next);
        var from = Session.Phase;
        if (next is SessionPhase.UpdatingGame or SessionPhase.LoggingOut or SessionPhase.MultiRun) _socialPoll.Stop();
        if (next is SessionPhase.LoggingOut or SessionPhase.MultiRun) _windows.CloseSessionWindows();
        if (next == SessionPhase.LoggingOut) Social.GroupChat.CloseQuietly();
        Session.Set(next);
        switch (next)
        {
            case SessionPhase.LoggedIn:
                _socialPoll.Start();
                _elapsedTimer.Stop();
                break;
            case SessionPhase.InGame:
                Social.Messages.SettleSends(SessionEnd.Play);
                _windows.CloseSessionWindows();
                if (Notice.Current is { Opens: true }) Notice.Take();
                TrayIcon.Warm();
                break;
            case SessionPhase.LoggedOut:
                if (from == SessionPhase.InGame) _windows.CloseSessionWindows();
                _elapsedTimer.Stop();
                if (from is SessionPhase.MultiRun or SessionPhase.MultiStopping) _ = Repair.ProbeAsync();
                break;
        }
    }

    private void OnPhaseChanged()
    {
        bool loggedIn = Session.LoggedIn, launching = Session.Launching;
        bool loggedInChanged = _loggedInShown.Changed(loggedIn), launchingChanged = _launchingShown.Changed(launching);
        bool gameOpenChanged = _inGameShown.Changed(GameOpen);
        if (gameOpenChanged || loggedInChanged) CloseChanged();
        if (!GameOpen) GameUp = false;
        if (loggedInChanged)
        {
            if (loggedIn && (_settings.Current.KeepSignedInInTray || _settings.Current.MinimizeToTray)) TrayIcon.Warm();
            if (!loggedIn) Notice.DropRequest();
        }
        Multi.StartAllAccountsCommand.NotifyCanExecuteChanged();
        LogInCommand.NotifyCanExecuteChanged();
        SignOutCommand.NotifyCanExecuteChanged();
        OpenAccountsCommand.NotifyCanExecuteChanged();
        UpdateNotice.Refresh();
        Social.HandleSwitching = Handles.Switching;
        if (launchingChanged) OnPropertyChanged(nameof(IsLaunching));
        if (loggedInChanged) OnPropertyChanged(nameof(LoggedIn));
        if (loggedInChanged || launchingChanged)
        {
            OnPropertyChanged(nameof(ShowInstallCard));
            RefreshSetupCard();
            OnPropertyChanged(nameof(ShowFriendsCard));
            OnPropertyChanged(nameof(ShowLaunchCard));
        }
        if (gameOpenChanged)
            foreach (var name in new[] { nameof(GameOpen), nameof(PlayText), nameof(PlayName), nameof(PlayEnabled) }) OnPropertyChanged(name);
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
    private readonly PhaseTimeLeft _timeLeft;
    private bool _updatedFirst;

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

    private readonly ProgressLog _progressLog = new();

    private void OnProgress(LaunchProgress p)
    {
        if (p.Kind == LaunchProgressKind.Notice)
        {
            Log.Information("progress: notice: {Detail}", p.Detail);
            Notice.Show(p.Detail, "InformationOutline");
            return;
        }
        if (!string.IsNullOrWhiteSpace(p.Detail)) ProgressText = p.Detail;
        if (p.Fraction >= 0)
        {
            if (p.Patch is not null) _updatedFirst = true;
            double f = _updatedFirst && p.Patch is null
                ? LaunchProgress.LogInUpdateEnd + (1 - LaunchProgress.LogInUpdateEnd) * p.Fraction : p.Fraction;
            if (f > ProgressFraction) ProgressFraction = Math.Min(f, 1.0);
        }
        TrackTimeLeft(p.Patch);
        _progressLog.Write(p);
        if (p.Kind is LaunchProgressKind.Failed or LaunchProgressKind.Stopped) _elapsedTimer.Stop();
    }

    private void TrackTimeLeft(PatchProgress? patch)
    {
        if (patch is null) _timeLeft.Clear();
        else if (patch.Fraction >= 0) _timeLeft.Update(patch.Fraction, patch.Phase);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelLaunch()
    {
        if (_launchCts is not { IsCancellationRequested: false } cts) return;
        CanCancel = false;
        ProgressText = "Cancelling...";
        cts.Cancel();
    }

    public bool MinimizeGoesToTray => _settings.Current.MinimizeToTray && Session.Phase != SessionPhase.InGame;

    public bool KeepInTrayOnClose => LoggedIn && _settings.Current.KeepSignedInInTray;

    public string? CloseTooltip => KeepInTrayOnClose ? "Close to the tray (you'll stay logged in to PlayOnline)"
        : LoggedIn && Session.Phase != SessionPhase.InGame ? "Close and log out of PlayOnline"
        : null;
    public string CloseName => CloseTooltip ?? "Close";

    private void CloseChanged()
    {
        OnPropertyChanged(nameof(CloseTooltip));
        OnPropertyChanged(nameof(CloseName));
    }

    public bool TrayTipShown => _settings.Current.TrayTipShown;

    public void MarkTrayTipShown() => _settings.SaveChange(s => s.TrayTipShown = true);

    private void Alert(NoticeKind kind, string? sender, string text, string icon = "mail", bool stays = false, int request = 0)
    {
        if (GameOpen) return;
        bool notice = _settings.Current.Shows(kind), flash = _settings.Current.Flashes(kind);
        if (notice) Notice.Show(text, icon, sender is null ? NoticeTarget.Friends : NoticeTarget.Chat, sender, request, stays);
        if (notice || flash) RequestAttention?.Invoke(notice, flash);
    }

    public bool WantsAttention => Social.UnseenCount > 0 || (_settings.Current.Flashes(NoticeKind.GroupChat) && Social.GroupChatUnread > 0);

    private void OnGroupChatLine(string group, string who, string text, int count)
    {
        bool notice = _settings.Current.Shows(NoticeKind.GroupChat), flash = _settings.Current.Flashes(NoticeKind.GroupChat);
        if (notice)
        {
            Notice.Show(count == 1 ? $"{who} in {group}: {TextBits.Preview(text, 60)}" : $"{count} new lines in {group}", "Forum",
                        NoticeTarget.GroupChat);
        }
        if (notice || flash) RequestAttention?.Invoke(notice, flash);
    }

    private void OpenGroupChat(GroupsViewModel.GroupItem g)
    {
        if (Multi.Running || GameOpen || Social.Last.GroupList.FirstOrDefault(x => x.Id == g.Id) is not { } group) return;
        if (Social.GroupChat.Open && string.Equals(Social.GroupChat.GroupId, group.Id, StringComparison.OrdinalIgnoreCase))
        {
            _windows.ShowGroupChat(Social.GroupChat);
            return;
        }
        ShowHandles();
        Handles.OpenGroup(group, () =>
        {
            if (Social.GroupChat.Join(group)) _windows.ShowGroupChat(Social.GroupChat);
            else _dialogs.Info(GroupChatViewModel.DialogTitle, HookFailures.GroupChat.JoinFailed);
        });
    }

    internal async Task<int> ConnectProfileAsync(Account p, PolAccount pol, SquareEnixAccount squareEnix, string otp,
                                                 IProgress<LaunchProgress>? progress, CancellationToken token, Func<bool>? stillLean = null) =>
        (await _launch.LogInAsync(new LaunchLogin(p, pol, squareEnix, otp, Accounts.EnsureHomeSlot(p), stillLean is not null, null, progress,
                                                  PromptHandleName, stillLean), token)).Code;

    private bool CanLogin() => !IsLaunching && !Multi.Running && !UpdatingNow;

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private Task LogInAsync() => _connectTask = ConnectSocialAsync();

    private Task? _connectTask;

    internal void HoldLogIn(CancellationTokenSource cts, Task done)
    {
        _launchCts = cts;
        _connectTask = done;
    }

    internal void RaiseExit() => RequestExit?.Invoke();

    public bool LoggingIn => _launchCts != null && _connectTask is { IsCompleted: false };

    public async Task CancelLogInAsync()
    {
        _launchCts?.Cancel();
        if (_connectTask is { } t) await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(15)));
    }

    public NewsViewModel News { get; }

    public AccountsViewModel Accounts { get; }

    public MultiRunViewModel Multi { get; }

    internal LeanStartViewModel Lean { get; }

    public bool LeanTipShown => Lean.TipShown;

    public string LeanTipText => LeanStartViewModel.AutoLoginTip;

    public NoticeBanner Notice { get; } = new();

    private readonly UpdateRestart _updateRestart;

    public UpdateBanner UpdateNotice { get; }

    private string? RestartBlocked() =>
        LoggingIn || Session.Phase is SessionPhase.LoggingIn or SessionPhase.UpdatingGame or SessionPhase.StartingGame
            or SessionPhase.LoggingOut or SessionPhase.MultiRun or SessionPhase.MultiStopping ? RestartBlockedText : null;

    internal const string RestartBlockedText =
        "Provenance is busy logging in, logging out, updating the game or starting and stopping games, so it can't restart to update yet. Try again once that's finished.";

    public bool UpdatingNow => _updateRestart.Busy;

    internal void RestartCancelled()
    {
        SelfUpdate.DropHandOver();
        _updateRestart.Cancelled();
    }

    public string TitleBarText => VersionTag.TitleBarText(VersionTag.OwnText(), SelfUpdate.Tag);

    internal void TitleChanged()
    {
        OnPropertyChanged(nameof(TitleBarText));
    }

    public RepairButtonViewModel Repair { get; }

    public SocialViewModel Social { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallCard))]
    private bool _gameInstalled = true;

    partial void OnGameInstalledChanged(bool value) => RefreshSetupCard();

    public bool ShowInstallCard => !GameInstalled && !IsLaunching && !LoggedIn;
    public bool ShowSetupCard => SetupCardWaits(Setup.Pending, GameInstalled, Accounts.HasProfile) && !IsLaunching && !LoggedIn;
    public bool ShowLogInCard => GameInstalled && !IsLaunching && !LoggedIn && !ShowSetupCard;
    public bool ShowFriendsCard => LoggedIn && (!IsLaunching || GameUp);
    public bool ShowLaunchCard => IsLaunching && !GameUp;

    public bool GameOpen => Session.Phase == SessionPhase.InGame;
    public string PlayText => GameOpen ? "GAME OPEN" : "PLAY";
    public string PlayName => GameOpen ? "Game open" : "Play";
    public bool PlayEnabled => Social.CanPlay && !GameOpen && !UpdatingNow;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowLaunchCard), nameof(ShowFriendsCard))] private bool _gameUp;

    public SetupViewModel Setup { get; }

    private void RefreshSetupCard()
    {
        OnPropertyChanged(nameof(ShowSetupCard));
        OnPropertyChanged(nameof(ShowLogInCard));
        if (ShowSetupCard) _ = Setup.Look();
    }

    private void OnSetupFinished(bool platformChanged)
    {
        PlayPlatform.Changed();
        Multi.Refresh();
        RefreshSetupCard();
        if (platformChanged && !LoggedIn && !IsLaunching) RestartPrestart();
    }

    internal Task PrestartRestarted { get; private set; } = Task.CompletedTask;

    private void StopPrestartLogged()
    {
        try { _pol.Prestarter.StopPrestart(); }
        catch (Exception ex) { Log.Warning(ex, "Stopping the pre-start failed"); }
    }

    private void RestartPrestart() =>
        PrestartRestarted = Task.Run(() =>
        {
            try
            {
                _pol.Prestarter.StopPrestart();
                _pol.Prestarter.Prestart();
            }
            catch (Exception ex) { Log.Warning(ex, "Restarting the pre-start failed"); }
        });

    private void RefreshInstallState()
    {
        var region = _settings.DetectedRegion;
        GameInstalled = GameInstalledIn(region);
        Repair.SetRegion(region);
    }

    internal static bool GameInstalledIn(PolRegion? region) =>
        region is { } r && PolPaths.TryGetInstallFolder(r, GameProduct.Game) is { } f && File.Exists(Path.Combine(f, GameProduct.Game.KeyFile));

    [RelayCommand]
    private void LocateInstall()
    {
        if (_dialogs.PickFolder("Find your FINAL FANTASY XI or PlayOnline folder") is not { } folder) return;
        var region = _settings.RegionOrDefault;
        var problem = GameInstaller.LocateExisting(folder, region, ex => Log.Warning(ex, "Could not record the install folder {Folder}", folder));
        if (problem != null) { _dialogs.Error("Couldn't use that folder", problem); return; }
        _settings.RedetectRegion();
        RefreshInstallState();
        if (GameInstalled) _dialogs.Info("Install found",
            "FINAL FANTASY XI is now set up in Provenance.");
        else _dialogs.Error("Game not found",
            "Provenance still can't find FINAL FANTASY XI in that folder. Choose the folder FINAL FANTASY XI is in, or install the game.");
    }

    [RelayCommand]
    private void DownloadInstall()
    {
        var vm = new InstallViewModel(_dialogs, _settings.DetectedRegion, _pol.Prestarter.HoldPrestarts);
        vm.Installed += () => _ui.Run(() =>
        {
            _settings.RedetectRegion();
            _settings.SaveChange(PolOptions.NewInstall);
            RefreshInstallState();
            Task.Run(() => LargeAddressAwareSwitch.Apply(_settings, "after the install", _pol.Prestarter.HoldPrestarts,
                                                          Session.LargeAddressAwareBlocked));
        });
        _windows.ShowInstall(vm);
    }

    private bool LogsInAtStart(bool suppressed) => LoginAtStart(Accounts.AutoLogin, suppressed, Accounts.HasSavedCredentials);

    internal static bool LoginAtStart(bool autoLogin, bool suppressed, bool hasSavedCredentials) => autoLogin && !suppressed && hasSavedCredentials;

    internal static bool SetupCardWaits(bool pending, bool gameInstalled, bool hasProfile) => pending && gameInstalled && hasProfile;

    private bool StartsAllAtOpen(bool suppressed) => !ShowSetupCard && MultiAccountRules.StartsAtOpen(_settings.Current, suppressed);

    internal bool StartsLean(bool suppressed) =>
        !StartsAllAtOpen(suppressed) && LogsInAtStart(suppressed) && Accounts.SelectedProfile is not null && !ShowSetupCard && Lean.Wanted;

    public void Startup(bool autoLoginSuppressed)
    {
        _pol.Records.EndOrphans();
        _pol.Prestarter.Prestart();
        if (StartsAllAtOpen(autoLoginSuppressed))
        {
            _launch.DropAhead();
            Log.Information("Starting all accounts automatically");
            Multi.StartAllAccountsCommand.Execute(null);
            return;
        }
        if (!LogsInAtStart(autoLoginSuppressed))
        {
            _launch.DropAhead();
            return;
        }
        Log.Information("Logging in to {Account} automatically", Accounts.SelectedProfile?.Id);
        Lean.AtOpen = true;
        try { LogInCommand.Execute(null); }
        finally { Lean.AtOpen = false; }
    }

    internal void OnSnapshot(PolSocialSnapshot snap)
    {
        var timer = ReferenceEquals(Social.Last, PolSocialSnapshot.Empty) ? new StepTimer("First friends and messages load") : null;
        _ui.Run(() =>
        {
            timer?.Lap("wait for the UI thread");
            if (_pol.Pid is null) return;
            if (Multi.Running) { Multi.RunSnapshot = snap; return; }
            if (Lean.Hold(snap)) return;
            try
            {
                Social.AccountName = Accounts.SelectedProfile?.DisplayName ?? "PlayOnline";
                Social.Apply(snap);
                timer?.Lap("apply");
                Accounts.LearnIdentity(snap);
                timer?.Lap("learn identity");
            }
            catch (Exception ex)
            {
                // this runs for the watcher's thread, where a throw would end the process
                Log.Error(ex, "Updating friends and messages failed");
            }
        });
    }

    public void OpenChatWith(FriendItem friend)
    {
        if (Multi.Running || GameOpen) return;
        Social.OpenConversation(friend);
    }

    public void OpenNotice()
    {
        if (Notice.Take() is not { Opens: true } notice) return;
        if (notice.Target == NoticeTarget.GroupChat)
        {
            if (Social.GroupChat.Open) _windows.ShowGroupChat(Social.GroupChat);
            return;
        }
        if (notice.Request > 0 || notice.Target == NoticeTarget.Friends)
        {
            if (Multi.Running || GameOpen) return;
            Social.ShowRequests();
            OpenSocial();
            return;
        }
        if (notice.Sender is { } who && Social.Friends.FirstOrDefault(f => f.Name == who) is { } f) OpenChatWith(f);
        else OpenSocial();
    }

    internal string FriendsWindowTitle => "Friends";

    internal void OpenSocial()
    {
        if (Multi.Running || GameOpen) return;
        _windows.ShowFriends(Social);
    }

    internal void OpenGroups()
    {
        if (Multi.Running || GameOpen) return;
        _windows.ShowGroups(Social);
    }

    [RelayCommand]
    private void ToggleFriends()
    {
        if (Multi.Running || GameOpen) return;
        if (!_windows.CloseIfShown(SecondWindow.Friends)) OpenSocial();
    }

    [RelayCommand]
    private void ToggleGroups()
    {
        if (Multi.Running || GameOpen) return;
        if (!_windows.CloseIfShown(SecondWindow.Groups)) OpenGroups();
    }

    [RelayCommand]
    private void ToggleChatRooms()
    {
        if (Multi.Running || GameOpen) return;
        if (!_windows.CloseIfShown(SecondWindow.ChatRooms)) OpenChatRooms();
    }

    [ObservableProperty] private bool _chatRoomsOpen;

    internal void OpenChatRooms()
    {
        if (Multi.Running || GameOpen) return;
        _windows.ShowChatRooms(Social, _pol.Credentials.SaveChatPassword, _pol.Credentials.DropChatPassword, SvgIcons.Emote, () => ChatRoomsOpen = false);
        ChatRoomsOpen = true;
    }

    internal void SaveMessages()
    {
        try
        {
            Social.SaveChat();
            Social.GroupChat.FlushLog();
        }
        catch (Exception ex) { Log.Warning(ex, "The message log wasn't saved on the way out"); }
    }

    internal bool ConfirmExit() => _windows.ConfirmDropProfileEditor("Exit");

    internal void CloseProfileEditor() => _windows.CloseProfileEditor();

    private bool CanSignOut() => Session.Phase == SessionPhase.LoggedIn;

    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private void SignOut()
    {
        if (Multi.Running || IsLaunching) return;
        if (!_windows.ConfirmDropProfileEditor("Log out")) return;
        EndSession(SessionEnd.LogOut);
    }

    private void EndSession(SessionEnd end)
    {
        Social.Messages.SettleSends(end);
        SetPhase(SessionPhase.LoggingOut);
        Task.Run(() =>
        {
            try
            {
                _pol.StopHold();
                _pol.Prestarter.Prestart();
            }
            finally { _ui.Post(() => { if (Session.Phase == SessionPhase.LoggingOut) SetPhase(SessionPhase.LoggedOut); }); }
        });
    }

    private Task ReleaseHeldSessionAsync() => Task.Run(() =>
    {
        _pol.StopHold();
        _pol.Prestarter.Prestart();
    });

    private async Task ConnectSocialAsync()
    {
        Log.Information("Log in pressed");
        if (Accounts.SelectedProfile == null && !Accounts.TryAddAccount()) { _launch.DropAhead(); return; }
        if (ShowSetupCard) { _launch.DropAhead(); return; }
        if (Accounts.SelectedProfile is not { } profile) { _launch.DropAhead(); return; }
        bool lean = Lean.Open();
        (PolAccount Pol, SquareEnixAccount SquareEnix, string Otp)? details = null;
        try { details = LogInDetails(profile); }
        finally
        {
            if (details is null)
            {
                Lean.Finish();
                _launch.DropAhead();
            }
        }
        if (details is not var (pol, squareEnix, otp)) return;
        Log.Information("Login details read for account {Profile}", profile.Id);
        if (lean) DropHandleNotice();
        else StartSocial(profile.Id);
        Accounts.SessionProfileId = profile.Id;
        SetPhase(SessionPhase.LoggingIn);
        var token = BeginCancellable(Accounts.ProfileTitle.StartsWith("New account", StringComparison.Ordinal)
            ? "Logging in to PlayOnline" : "Logging in to " + Accounts.ProfileTitle, "Starting PlayOnline...");
        bool startAfter = false, loggedIn = false, gameRan = false;
        string? lastFailure = null;
        var progress = new Progress<LaunchProgress>(p =>
        {
            OnProgress(p);
            if (p.Kind is LaunchProgressKind.Failed or LaunchProgressKind.Stopped) lastFailure = p.Detail;
        });
        try
        {
            int code = await ConnectProfileAsync(profile, pol, squareEnix, otp, progress, token, Lean.BeforePol(profile.Id, token));
            await _ui.Flush();   // queued progress reports have to land first (yes, really)
            if (code == 0 && Lean.Running) (code, lastFailure, gameRan) = await Lean.AfterLogInAsync(profile.Id, token);
            if (gameRan) return;
            if (code == 0)
            {
                ProgressFraction = 1.0;
                loggedIn = true;
                startAfter = !lean && Accounts.StartGameImmediately;
            }
            else
            {
                CanCancel = false;
                Lean.Finish();
                await ReleaseHeldSessionAsync();
                var failure = AccountRules.LoginFailure(code, lastFailure, Accounts.AutoLogin);
                if (failure.AutoOff) Accounts.AutoLogin = false;
                if (code != LoginCodes.Cancelled)
                    _dialogs.Error(failure.Title, failure.Text, offerLogs: failure.OfferLogs);
            }
        }
        catch (OperationCanceledException)
        {
            await ReleaseHeldSessionAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "The login failed");
            await ReleaseHeldSessionAsync();
            Lean.Finish();
            _dialogs.Error("Couldn't log in",
                ErrorText.Of(ex, "Provenance ran into a problem while logging in. Try again, and if it keeps happening, press Open logs to see what went wrong."),
                offerLogs: true);
        }
        finally
        {
            EndCancellable();
            if (!gameRan) SetPhase(loggedIn ? SessionPhase.LoggedIn : SessionPhase.LoggedOut);
            Lean.Finish();
            _launch.DropAhead();
        }
        if (startAfter && PlayCommand.CanExecute(null)) await PlayCommand.ExecuteAsync(null);
        if (lean && loggedIn) await Lean.FollowUpAsync();
    }

    private (PolAccount Pol, SquareEnixAccount SquareEnix, string Otp)? LogInDetails(Account profile)
    {
        if (!Accounts.HasSavedCredentials && !Accounts.EditAccount(profile, requireAll: true)) return null;
        if (!EnsureMemberAgreement() || Accounts.LoadForLogIn(profile) is not var (pol, squareEnix)) return null;
        var claim = _launch.Claim(profile, () => Accounts.AskOtp(profile, squareEnix), () => _launch.StartAhead(profile));
        if (claim.InUse is { } inUse) _dialogs.Error("Account in use", inUse);
        return claim.Otp is { } otp && claim.InUse is null ? (pol, squareEnix, otp) : null;
    }

    internal bool EnsureMemberAgreement()
    {
        if (_settings.Current.AcceptedMemberAgreement) return true;
        if (!PolPaths.Supported(_settings.DetectedRegion)) return true;
        if (!_dialogs.AskAgreement(Viewer.Strings()))
        {
            Log.Information("Member Agreement declined: not logging in");
            Notice.Show("You're not logged in, because you didn't accept the PlayOnline Member Agreement.", "InformationOutline");
            return false;
        }
        _settings.SaveChange(s => s.AcceptedMemberAgreement = true);
        return true;
    }

    private string? PromptHandleName() =>
        _ui.Run(() =>
        {
            Lean.HandleAsked();
            var name = _dialogs.Ask("Create PlayOnline handle",
                "This PlayOnline account doesn't have a handle yet, and FINAL FANTASY XI needs one to log in.\n\n" +
                "A handle is your name on PlayOnline, and other players can see it. You can rename it later.\n\n" +
                "Enter 1 to 15 English letters, numbers or symbols:",
                maxLength: PolLimits.HandleName, validate: HandleList.NameProblem);
            if (name is null) return null;
            return _dialogs.Confirm("Create PlayOnline handle",
                $"Create the handle {name}? Other players will see it, and you can rename it later by editing your profile.", "CREATE")
                ? name : null;
        });

    [RelayCommand]
    private Task PlayAsync() => UpdatingNow ? Task.CompletedTask : GameStart.PlayAsync();

    internal GameStart GameStart { get; }

    internal void StartSocial(string profileId, string? storeRoot = null)
    {
        Social.Start(profileId, storeRoot);
        Handles.NewLogin();
        DropHandleNotice();
    }

    internal HandlesViewModel Handles { get; }
    internal const string HandleSwitchingText = "Switching your handle, please wait...";
    internal ViewerResources Viewer { get; }

    private void ShowHandles()
    {
        Handles.Refresh();
        Social.HandleSwitching = Handles.Switching;
    }

    private bool CanOpenAccounts() => !Multi.Running && !GameOpen;

    [RelayCommand(CanExecute = nameof(CanOpenAccounts))]
    private void OpenAccounts()
    {
        _windows.ShowAccounts(Accounts);
        Multi.Refresh();
    }

    [RelayCommand]
    private void OpenSettings() => OpenSettingsAt(SettingsTab.General);

    private void OpenSettingsAt(SettingsTab tab)
    {
        var hookBefore = _settings.Current.ThirdPartyHookPlatform;
        ShowHandles();
        var vm = new SettingsViewModel(_dialogs, _settings, _feeds, Session, holdPrestarts: _pol.Prestarter.HoldPrestarts,
                                       stopPrestart: () => Task.Run(StopPrestartLogged), handles: Handles);
        bool platformPicked = false;
        vm.Hooks.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(HookSettings.Hook)) return;
            platformPicked = true;
            PlayPlatform.Changed();
        };
        Handles.Opened();
        try { _windows.ShowSettings(vm, tab); }
        finally
        {
            vm.Dispose();
            Handles.Closed();
        }
        if (platformPicked) Setup.Done();
        if (vm.Hooks.RestartWanted)
        {
            RequestRestart?.Invoke();
            return;
        }
        CloseChanged();
        Multi.Refresh();
        if (_settings.Current.ThirdPartyHookPlatform != hookBefore && !LoggedIn && !IsLaunching) RestartPrestart();
    }

    public PlayPlatform PlayPlatform { get; }

    public IReadOnlyList<MenuAction> HandleMenu()
    {
        ShowHandles();
        return Handles.SwitchMenu(text => ShowHandleNotice(text, stays: true), text => ShowHandleNotice(text, stays: false));
    }

    private void ShowHandleNotice(string text, bool stays)
    {
        Notice.Show(text, "AlertCircleOutline", stays: stays);
        _handleNotice = Notice.Current;
    }

    private Notice? _handleNotice;

    private void DropHandleNotice()
    {
        Notice.Drop(_handleNotice);
        _handleNotice = null;
    }

    // the URL comes from a feed, ShellExecute would run anything
    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
        {
            if (!string.IsNullOrWhiteSpace(url)) Log.Warning("Not opening a link that is not a web link: {Url}", url);
            return;
        }
        try { Explorer.OpenLink(u); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Url}", u.AbsoluteUri);
            _dialogs.Error("Couldn't open the link", $"Windows didn't open the link. You can copy it from here: {u.AbsoluteUri}");
        }
    }
}
