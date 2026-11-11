using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common;
using Provenance.Common.Credentials;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Hook;
using Provenance.Common.Settings;
using Serilog;
using Provenance.Common.Feeds;
using Provenance.Common.Game;
using Provenance.Common.Game.Windower;
using Provenance.Common.Game.Ashita;
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
    public event Action? RequestExit;
    public event Action<bool>? RequestAttention;
    public event Action? AttentionCleared;

    public MainViewModel(IDialogs dialogs, IWindowManager windows, ICredentialService credentials, ISettingsService settings,
                         FeedService feeds, PolSession pol, AvatarService avatars, TimeProvider? time = null, GameLaunch? launch = null)
    {
        _dialogs = dialogs;
        _windows = windows;
        _settings = settings;
        _settings.SaveFailed += why => _ui.Post(() =>
            _dialogs.Error("Settings not saved",
                "Provenance couldn't save your settings, so your changes will only last until Provenance closes.", offerLogs: true));
        if (_settings.LoadProblem is { } loadProblem)
            _ui.Post(() => _dialogs.Info("Settings reset", loadProblem));
        _pol = pol;
        Session = new SessionState(pol);
        _launch = launch ?? new GameLaunch(pol, settings);
        _avatars = avatars;
        _feeds = feeds;
        _timeLeft = new PhaseTimeLeft(text => TimeLeftText = text);
        News = new NewsViewModel(feeds, _ui, OpenLink, time);
        Accounts = new AccountsViewModel(dialogs, credentials, settings, avatars);
        _socialPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _socialPoll.Tick += (_, _) => _pol.Poll();
        Multi = new MultiRunViewModel(dialogs, settings, credentials, pol, _ui, Accounts, Session, _launch,
            new MultiRunLinks(SetPhase, EnsureMemberAgreement, PromptHandleName, BackUpGameSettingsAsync));
        Repair = new RepairButtonViewModel(dialogs, windows, settings, pol, Session, () => News.WindowShown, RefreshInstallState);
        _pol.SnapshotUpdated += OnSnapshot;
        _pol.PidChanged += () => _ui.Post(() =>
        {
            Multi.StartAllAccountsCommand.NotifyCanExecuteChanged();
            Repair.Refresh();
        });
        _pol.SessionEnded += why => _ui.Run(() =>
        {
            Log.Information("Session ended: {Why} (signed in here: {Connected})", why, LoggedIn);
            if (!LoggedIn) return;
            EndSession(SessionEnd.Dropped);   // no question asked, the session's gone so the editor couldn't save anyway
            RequestShow?.Invoke();
            _dialogs.Info("PlayOnline session ended", why);
            Log.Information("Session ended: the notice was closed");
        });
        Viewer = new ViewerResources(settings);
        Social = new SocialViewModel(dialogs, windows, _pol, avatars, _settings, Session, Viewer, time);
        Social.OpenChatRequested += OpenChatWith;

        Multi.RequestShow += () => RequestShow?.Invoke();
        Multi.RequestExit += () => RequestExit?.Invoke();
        Social.MessageReceived += (from, text) => Alert(from, $"New message from {from}: {TextBits.Preview(text, 60)}");
        Social.FriendRequestReceived += (who, mid) =>
            Alert(null, $"{who} sent you a friend request. Open {FriendsWindowTitle} to answer it.", "AccountPlusOutline",
                  stays: true, request: mid);
        Social.GroupInviteReceived += (who, mid) =>
            Alert(who, $"{who} invited you to a friend group. Open {FriendsWindowTitle} to answer it.", "BellOutline",
                  request: mid);
        Social.NoticeReceived += (who, text) => Alert(who, text, "BellOutline");
        Social.GroupChatRequested += OpenGroupChat;
        Social.GroupChat.LineArrived += OnGroupChatLine;
        // a quiet one, no taskbar flash, and it never covers a message notice
        Social.FriendCameOnline += who =>
        {
            if (!_settings.Current.NotifyFriendsOnline || Notice.Current is { Text.Length: > 0 }) return;
            Notice.Show($"{who} is online now.", "AccountCheckOutline", NoticeTarget.Chat, who);
        };
        Handles = new HandlesViewModel(_dialogs, () => _pol.Pid, _avatars.ProfilePicture, Session, () => Social.Last, time)
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
        };
        Handles.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HandlesViewModel.Busy)) Social.HandleSwitching = Handles.Switching;
        };
        RefreshInstallState();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _elapsedTimer.Tick += (_, _) =>
        {
            ElapsedText = TextBits.Seconds(DateTime.UtcNow - _launchStarted);
        };
        Session.PhaseChanged += OnPhaseChanged;
    }

    public SessionState Session { get; }

    public bool LoggedIn => Session.LoggedIn;

    public bool IsLaunching => Session.Launching;

    private LastRaised _loggedInShown, _launchingShown;

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
        if (loggedInChanged)
        {
            OnPropertyChanged(nameof(CloseTooltip));
            if (loggedIn && (_settings.Current.KeepSignedInInTray || _settings.Current.MinimizeToTray)) TrayIcon.Warm();
            // the hook numbers mail per session, so a request's notice can't outlive it
            if (!loggedIn) Notice.DropRequest();
        }
        Multi.StartAllAccountsCommand.NotifyCanExecuteChanged();
        LogInCommand.NotifyCanExecuteChanged();
        Social.HandleSwitching = Handles.Switching;
        if (launchingChanged) OnPropertyChanged(nameof(IsLaunching));
        if (loggedInChanged) OnPropertyChanged(nameof(LoggedIn));
        if (loggedInChanged || launchingChanged)
        {
            OnPropertyChanged(nameof(ShowInstallCard));
            OnPropertyChanged(nameof(ShowLogInCard));
            OnPropertyChanged(nameof(ShowFriendsCard));
        }
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
            // after a viewer update at login the login's own steps share what's left, so the bar only moves forward
            if (p.Patch is not null) _updatedFirst = true;
            double f = _updatedFirst && p.Patch is null
                ? LaunchProgress.LogInUpdateEnd + (1 - LaunchProgress.LogInUpdateEnd) * p.Fraction : p.Fraction;
            if (f > ProgressFraction) ProgressFraction = Math.Min(f, 1.0);
        }
        TrackTimeLeft(p.Patch);
        _progressLog.Write(p);
        if (p.Kind is LaunchProgressKind.Failed or LaunchProgressKind.Stopped) _elapsedTimer.Stop();
    }

    // each phase gets its own, a check's pace says nothing about the download after it
    private void TrackTimeLeft(PatchProgress? patch)
    {
        if (patch is not { Fraction: >= 0 } p) _timeLeft.Clear();
        else _timeLeft.Update(p.Fraction, p.Phase);
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

    public string CloseTooltip => !LoggedIn ? "Close"
        : KeepInTrayOnClose ? "Close to the tray (you'll stay logged in to PlayOnline)"
        : "Close and log out of PlayOnline";
    public bool TrayTipShown => _settings.Current.TrayTipShown;

    public void MarkTrayTipShown() => _settings.SaveChange(s => s.TrayTipShown = true);

    private void Alert(string? sender, string text, string icon = "mail", bool stays = false, int request = 0)
    {
        Notice.Show(text, icon, sender is null ? NoticeTarget.Friends : NoticeTarget.Chat, sender, request, stays);
        RequestAttention?.Invoke(true);
    }

    // false once everything's been read or seen, like a message landing in a conversation that's already on screen
    public bool WantsAttention => Social.UnseenCount > 0 || (_settings.Current.GroupChatFlash && Social.GroupChatUnread > 0);

    // lines in the group chat with its window hidden or behind, one notice a batch, as the settings say
    private void OnGroupChatLine(string group, string who, string text, int count)
    {
        bool notice = _settings.Current.GroupChatNotice;
        if (notice)
        {
            Notice.Show(count == 1 ? $"{who} in {group}: {TextBits.Preview(text, 60)}" : $"{count} new lines in {group}", "Forum",
                        NoticeTarget.GroupChat);
        }
        if (notice || _settings.Current.GroupChatFlash) RequestAttention?.Invoke(notice);
    }

    // the Groups tab's Group Chat: the viewer's handle question (0x3eb0) first when it's needed, then the window
    private void OpenGroupChat(GroupsViewModel.GroupItem g)
    {
        if (Multi.Running || Social.Last.GroupList.FirstOrDefault(x => x.Id == g.Id) is not { } group) return;
        if (Social.GroupChat.Open && string.Equals(Social.GroupChat.GroupId, group.Id, StringComparison.OrdinalIgnoreCase))
        {
            _windows.ShowGroupChat(Social.GroupChat);
            return;
        }
        // the question needs this login's handles
        ShowHandles();
        Handles.OpenGroup(group, () =>
        {
            if (Social.GroupChat.Join(group)) _windows.ShowGroupChat(Social.GroupChat);
            else _dialogs.Info(GroupChatViewModel.DialogTitle, HookFailures.GroupChat.JoinFailed);
        });
    }

    internal async Task<int> ConnectProfileAsync(Account p, PolAccount pol, SquareEnixAccount squareEnix, string otp,
                                                 IProgress<LaunchProgress>? progress, CancellationToken token) =>
        (await _launch.LogInAsync(new LaunchLogin(p, pol, squareEnix, otp, Accounts.EnsureHomeSlot(p), false, null, progress, PromptHandleName),
                                  token)).Code;

    private bool CanLogin() => !IsLaunching && !Multi.Running;

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

    public NoticeBanner Notice { get; } = new();

    public RepairButtonViewModel Repair { get; }

    public SocialViewModel Social { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallCard), nameof(ShowLogInCard))]
    private bool _gameInstalled = true;

    public bool ShowInstallCard => !GameInstalled && !IsLaunching && !LoggedIn;
    public bool ShowLogInCard => GameInstalled && !IsLaunching && !LoggedIn;
    public bool ShowFriendsCard => LoggedIn && !IsLaunching;

    private void RefreshInstallState()
    {
        var region = _settings.DetectedRegion;
        GameInstalled = region is { } r &&
                        PolPaths.TryGetInstallFolder(r, GameProduct.Game) is { } f &&
                        File.Exists(Path.Combine(f, GameProduct.Game.KeyFile));
        Repair.SetRegion(region);
    }

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

    public void Startup(bool autoLoginSuppressed)
    {
        _pol.Records.EndOrphans();
        _pol.Prestarter.Prestart();
        if (!Accounts.AutoLogin || autoLoginSuppressed || !Accounts.HasSavedCredentials) return;
        Log.Information("Auto sign-in for {Account}", Accounts.SelectedProfile?.Id);
        LogInCommand.Execute(null);
    }

    internal void OnSnapshot(PolSocialSnapshot snap)
    {
        _ui.Run(() =>
        {
            if (_pol.Pid is null) return;   // read just before a sign-out
            // a run's lean session has no social side and nothing here may write a request to its hook
            if (Multi.Running) { Multi.RunSnapshot = snap; return; }
            try
            {
                Social.AccountName = Accounts.SelectedProfile?.DisplayName ?? "PlayOnline";
                Social.Apply(snap);
                Accounts.LearnIdentity(snap);
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
        if (Multi.Running) return;
        Social.SelectedFriend = Social.Friends.FirstOrDefault(f => f.Is(friend)) ?? friend;
        _windows.ShowMessages(Social);
    }

    public void OpenNotice()
    {
        if (Notice.Take() is not { Opens: true } notice) return;
        if (notice.Target == NoticeTarget.GroupChat)
        {
            if (Social.GroupChat.Open) _windows.ShowGroupChat(Social.GroupChat);
            return;
        }
        if (notice.Sender is { } who && Social.Friends.FirstOrDefault(f => f.Name == who) is { } f) OpenChatWith(f);
        else OpenSocial();
    }

    internal string FriendsWindowTitle => _windows.FriendsTitle;

    [RelayCommand]
    private void OpenSocial()
    {
        if (Multi.Running) return;
        Social.ShowGroups = false;
        _windows.ShowFriends(Social);
    }

    [RelayCommand]
    private void OpenGroups()
    {
        if (Multi.Running) return;
        Social.ShowGroups = true;
        _windows.ShowFriends(Social);
    }

    [RelayCommand]
    private void OpenMessages()
    {
        if (Multi.Running) return;
        _windows.ShowMessages(Social);
    }

    [ObservableProperty] private bool _chatRoomsOpen;

    [RelayCommand]
    private void OpenChatRooms()
    {
        if (Multi.Running) return;
        _windows.ShowChatRooms(Social, _pol.Credentials.SaveChatPassword, _pol.Credentials.DropChatPassword, SvgIcons.Emote, () => ChatRoomsOpen = false);
        ChatRoomsOpen = true;
    }

    // false when an open profile editor's unsaved changes were kept
    internal bool ConfirmExit() => _windows.ConfirmDropProfileEditor("Exit");

    internal void CloseProfileEditor() => _windows.CloseProfileEditor();

    [RelayCommand]
    private void SignOut()
    {
        // Play's game update needs the session it's holding
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
                _pol.StopHold();    // the kill waits a few seconds, so keep it off the UI thread
                _pol.Prestarter.Prestart();
            }
            finally { _ui.Post(() => { if (Session.Phase == SessionPhase.LoggingOut) SetPhase(SessionPhase.LoggedOut); }); }
        });
    }

    private async Task ConnectSocialAsync()
    {
        if (Accounts.SelectedProfile == null && !Accounts.TryAddAccount()) return;
        // one account for the whole login, so switching mid-way can't mix two up
        if (Accounts.SelectedProfile is not { } profile) return;
        if (!Accounts.HasSavedCredentials && !Accounts.EditAccount(requireAll: true)) return;
        if (!EnsureMemberAgreement()) return;
        // a one-time code counts as used once typed, so the details dialog has to come first
        if (Accounts.LoadForLogIn(profile) is not { } details) return;
        var (pol, squareEnix) = details;
        var claim = _launch.Claim(profile, () => Accounts.AskOtp(profile, squareEnix));
        if (claim.InUse is { } inUse)
        {
            _dialogs.Error("Account in use", inUse);
            return;
        }
        if (claim.Otp is not { } otp) return;
        StartSocial(profile.Id);
        Accounts.SessionProfileId = profile.Id;
        SetPhase(SessionPhase.LoggingIn);
        var token = BeginCancellable(Accounts.ProfileTitle.StartsWith("New account", StringComparison.Ordinal)
            ? "Logging in to PlayOnline" : "Logging in to " + Accounts.ProfileTitle, "Starting PlayOnline...");
        bool startAfter = false, loggedIn = false;
        string? lastFailure = null;
        var progress = new Progress<LaunchProgress>(p =>
        {
            OnProgress(p);
            if (p.Kind is LaunchProgressKind.Failed or LaunchProgressKind.Stopped) lastFailure = p.Detail;
        });
        try
        {
            int code = await ConnectProfileAsync(profile, pol, squareEnix, otp, progress, token);
            await _ui.Flush();   // queued progress reports have to land first (yes, really)
            if (code == 0)
            {
                ProgressFraction = 1.0;
                loggedIn = true;
                startAfter = Accounts.StartGameImmediately;
            }
            else
            {
                CanCancel = false;
                await Task.Run(() =>
                {
                    _pol.StopHold();
                    _pol.Prestarter.Prestart();
                });
                var failure = AccountRules.LoginFailure(code, lastFailure, Accounts.AutoLogin);
                if (failure.AutoOff) Accounts.AutoLogin = false;
                if (code != LoginCodes.Cancelled)
                    _dialogs.Error(failure.Title, failure.Text, offerLogs: failure.OfferLogs);
            }
        }
        catch (OperationCanceledException) { await Task.Run(() => _pol.StopHold()); }
        catch (Exception ex)
        {
            Log.Error(ex, "Social connect failed");
            await Task.Run(() => _pol.StopHold());
            _dialogs.Error("Couldn't log in",
                ErrorText.Of(ex, "Provenance ran into a problem while logging in. Try again, and if it keeps happening, press Open logs to see what went wrong."),
                offerLogs: true);
        }
        finally
        {
            EndCancellable();
            SetPhase(loggedIn ? SessionPhase.LoggedIn : SessionPhase.LoggedOut);
        }
        if (startAfter && PlayCommand.CanExecute(null)) await PlayCommand.ExecuteAsync(null);
    }

    // the US viewer asks once per POL ID, here it's once per install
    internal bool EnsureMemberAgreement()
    {
        if (_settings.Current.AcceptedMemberAgreement) return true;
        if (!PolPaths.Supported(_settings.DetectedRegion)) return true;
        if (!_dialogs.AskAgreement(Viewer.Strings()))
        {
            Log.Information("Member Agreement declined: not signing in");
            Notice.Show("You're not logged in, because you didn't accept the PlayOnline Member Agreement.", "InformationOutline");
            return false;
        }
        _settings.SaveChange(s => s.AcceptedMemberAgreement = true);
        return true;
    }

    private string? PromptHandleName() =>
        _ui.Run(() =>
        {
            var name = _dialogs.Ask("Create PlayOnline handle",
                "This PlayOnline account doesn't have a handle yet, and FINAL FANTASY XI needs one to log in.\n\n" +
                "A handle is your name on PlayOnline, and other players can see it. You can rename it later.\n\n" +
                "Enter 1 to 15 English letters, numbers or symbols:",
                maxLength: PolLimits.HandleName,
                // the viewer's and polcore's rule, plain ASCII and not just spaces
                validate: name => name.Any(c => c < 0x20 || c > 0x7e)
                    ? "Use English letters, numbers and symbols only."
                    : string.IsNullOrWhiteSpace(name) ? "Type a handle name." : null);
            if (name is null) return null;
            // a handle's public straight away, so one last look first
            return _dialogs.Confirm("Create PlayOnline handle",
                $"Create the handle {name}? Other players will see it, and you can rename it later by editing your profile.", "CREATE")
                ? name : null;
        });

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (Multi.Running) return;
        // POL's own Play gate, and it only asks for each list at login so there's no reload here either
        if (Social.PlayBlockedReason is { } why)
        {
            _dialogs.Error("Can't start FINAL FANTASY XI", why);
            return;
        }
        // nothing new goes to the hook from here, the game update still applies its answers
        SetPhase(SessionPhase.UpdatingGame);
        // like the viewer, the game update comes after the login with the session held meanwhile
        if (!await UpdateGameAsync())
        {
            SetPhase(SessionPhase.LoggedIn);
            return;
        }
        SetPhase(SessionPhase.StartingGame);
        if (await LinkBeforePlayAsync() is var linked and not PreLaunchResult.Play)
        {
            SetPhase(SessionPhase.LoggedIn);
            if (linked == PreLaunchResult.ShowHandles) OpenSettingsAt(SettingsTab.Handles);
            return;
        }
        _launch.WriteOptions(Accounts.SessionProfileId);
        ResetProgress("Starting FINAL FANTASY XI", "Starting FINAL FANTASY XI...");
        await BackUpGameSettingsAsync(null, text => ProgressText = text);
        OnProgress(new LaunchProgress("Starting game", "Starting FINAL FANTASY XI...", 0.5));
        var signal = _launch.SignalPlay();
        if (!signal.Sent)
        {
            Log.Error(signal.Error, "Could not signal Play");
            SetPhase(SessionPhase.LoggedIn);
            _dialogs.Error("Can't start FINAL FANTASY XI",
                signal.KeyProblem ?? "Provenance couldn't tell PlayOnline to start the game. Try again.", offerLogs: true);
            return;
        }
        SetPhase(SessionPhase.InGame);
        if (await _launch.WaitForPlayAnswerAsync() is { } failed)
        {
            SetPhase(SessionPhase.LoggedOut);
            _pol.Prestarter.Prestart();
            _dialogs.Error("Can't start FINAL FANTASY XI", failed);
            return;
        }
        bool up = false;
        try { up = await BringGameForwardAsync(); }
        catch (Exception ex) { Log.Warning(ex, "Could not bring the game window to the front"); }
        if (up) Handles.GameStarted();
        RequestHide?.Invoke();
        _elapsedTimer.Stop();
        var exit = await _pol.WaitForExitAsync();
        _pol.GameExited();
        var ended = HookExitText.GameEndedText(exit);
        if (ended is not null) Log.Error("FINAL FANTASY XI didn't start (pol.exe exit code {Code})", exit);
        else if (_settings.Current.ExitAfterGameExits) { RequestExit?.Invoke(); return; }
        SetPhase(SessionPhase.LoggedOut);
        RequestShow?.Invoke();
        _pol.Prestarter.Prestart();
        if (ended is not null) _dialogs.Error("Can't start FINAL FANTASY XI", ended, offerLogs: true);
    }

    // a test says whether its made-up session is still there
    internal Func<int, bool>? SessionAliveForTest { get; set; }

    internal Task<PreLaunchResult> LinkBeforePlayAsync()
    {
        ResetProgress("Starting FINAL FANTASY XI", "Starting FINAL FANTASY XI...");
        return _launch.LinkAsync(
            ask: text => Asking(() => _dialogs.YesNo(PreLaunchLink.Title, text), text),
            failed: text => Asking(() =>
            {
                _dialogs.Error(PreLaunchLink.Title, text);
                return true;
            }, text),
            saying: text => ProgressText = text,
            pageWaiting: () => Handles.Busy ? Handles.Note.Text : null,
            alive: SessionAliveForTest,
            read: _ => Social.Last);
    }

    // the footer's "has to wait till the game's closed" isn't true while the answer can still stop it starting
    private bool Asking(Func<bool> dialog, string text)
    {
        var note = Social.StatusNote;
        Social.StatusNote = "";
        try { return dialog(); }
        finally { if (Social.StatusNote == "") Social.StatusNote = note; }
    }

    // a test points it at made-up folders
    internal Func<LauncherSettings, string?, BackupSources?>? GameBackupSourcesForTest { get; set; }

    // however it goes the game still starts, a failed backup only gets a notice
    internal async Task BackUpGameSettingsAsync(string? accountBoot, Action<string>? saying = null)
    {
        if (await _launch.BackUpAsync(accountBoot, saying, GameBackupSourcesForTest) is { } problem)
            Notice.Show(problem, "AlertCircleOutline", stays: true);
    }

    internal async Task<bool> UpdateGameAsync()
    {
        if (_launch.GameFolders() is null) return true;
        var ct = BeginCancellable("Updating FINAL FANTASY XI", "Checking for FINAL FANTASY XI updates...");
        var progress = new Progress<PatchProgress>(p => OnProgress(LaunchProgress.Updating("Updating", p, 0.01, 0.99)));
        try
        {
            var result = await _launch.UpdateGameAsync(progress, ct);
            if (result.Done) return true;
            if (result.Cancelled)
            {
                Notice.Show("The FINAL FANTASY XI update was stopped. Press Play to continue it.", "InformationOutline");
                return false;
            }
            var ex = result.Error!;
            Log.Error(ex, "FINAL FANTASY XI update failed");
            bool inUse = ex is UnauthorizedAccessException || ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);
            _dialogs.Error("Couldn't update FINAL FANTASY XI",
                inUse ? "One of the FINAL FANTASY XI files is in use, so it couldn't be updated. Close every FINAL FANTASY XI window, then press Play again."
                      : ErrorText.Of(ex, "The FINAL FANTASY XI update didn't finish. Press Play to try again, and if it keeps happening, press Open logs to see what went wrong."),
                offerLogs: !ErrorText.Ours(ex));
            return false;
        }
        finally { EndCancellable(); }
    }

    // true once the game's window is up. Windows only hands focus to the last program used, which is the app at the
    // Play click, so pol.exe gets leave to take it first
    private async Task<bool> BringGameForwardAsync()
    {
        if (_pol.Pid is not { } pid) return false;
        GameWindow.AllowForeground(pid);
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until)
        {
            if (!ProcessStamp.PidRunning(pid)) return false;
            if (GameWindow.Find(pid) is var h && h != IntPtr.Zero)
            {
                bool front = GameWindow.BringToFront(h);
                Log.Information("Game window is up (pol.exe {Pid}); brought to the front: {Front}", pid, front);
                return true;
            }
            await Task.Delay(250);
        }
        Log.Warning("No game window from pol.exe {Pid} after 90s", pid);
        return false;
    }

    internal void StartSocial(string profileId, string? storeRoot = null)
    {
        Social.Start(profileId, storeRoot);
        Handles.NewLogin();
    }

    // fed every snapshot, so a change still going when Settings closes is answered all the same
    internal HandlesViewModel Handles { get; }
    internal ViewerResources Viewer { get; }

    private void ShowHandles()
    {
        Handles.Refresh();
        Social.HandleSwitching = Handles.Switching;
    }

    [RelayCommand]
    private void OpenSettings() => OpenSettingsAt(SettingsTab.General);

    private void OpenSettingsAt(SettingsTab tab)
    {
        var hookBefore = _settings.Current.ThirdPartyHookPlatform;
        ShowHandles();
        var vm = new SettingsViewModel(_dialogs, _settings, _feeds, Session, holdPrestarts: _pol.Prestarter.HoldPrestarts,
                                       stopPrestart: () => Task.Run(_pol.Prestarter.StopPrestart), handles: Handles);
        vm.Hooks.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HookSettings.Hook)) PlatformChanged(); };
        Handles.Opened();
        try { _windows.ShowSettings(vm, tab); }
        finally
        {
            vm.Dispose();
            Handles.Closed();
        }
        if (vm.Hooks.RestartWanted)
        {
            RequestRestart?.Invoke();
            return;
        }
        OnPropertyChanged(nameof(CloseTooltip));
        Multi.Refresh();
        if (_settings.Current.ThirdPartyHookPlatform != hookBefore && !LoggedIn && !IsLaunching)
            Task.Run(() => { _pol.Prestarter.StopPrestart(); _pol.Prestarter.Prestart(); });
    }

    public string? PlaySubtitle => _pol.Platform switch
    {
        ThirdPartyHook.Windower => "WINDOWER LOADED",
        ThirdPartyHook.Ashita => "ASHITA LOADED",
        _ => null,
    };

    internal void PlatformChanged()
    {
        OnPropertyChanged(nameof(PlaySubtitle));
        OnPropertyChanged(nameof(HasPlayChoices));
        OnPropertyChanged(nameof(PlayChoicesTitle));
    }

    public sealed record PlayChoice(string Name, string Display, bool Current);

    public bool HasPlayChoices => _pol.Platform is ThirdPartyHook.Windower or ThirdPartyHook.Ashita;

    public string PlayChoicesTitle => _pol.Platform == ThirdPartyHook.Ashita ? "Boot configuration" : "Windower profile";

    public string NoPlayChoicesText => _pol.Platform == ThirdPartyHook.Ashita
        ? "No boot configurations found" : "No Windower profiles found";

    // the same pick Play uses: Ashita falls back to the last config Ashita ran with, Windower's "" is the unnamed one
    public IReadOnlyList<PlayChoice> PlayChoices()
    {
        var target = PlayTarget.For(_settings.Current, null);
        if (target.Platform == ThirdPartyHook.Ashita)
            return AshitaBootFiles.Names(target.Dir)
                .Select(n => new PlayChoice(n.File, n.Shown, target.Is(n.File))).ToList();
        if (target.Platform == ThirdPartyHook.Windower)
        {
            var names = WindowerInstall.Profiles(target.Dir);
            var picked = target.Picked(names);
            return names.Select(n => new PlayChoice(n, WindowerInstall.DisplayName(n, names), n == picked)).ToList();
        }
        return [];
    }

    public IReadOnlyList<MenuAction> PlayChoicesMenu()
    {
        var menu = new List<MenuAction> { new(PlayChoicesTitle, false), MenuAction.Separator };
        var choices = PlayChoices();
        if (choices.Count == 0) menu.Add(new(NoPlayChoicesText, false));
        foreach (var c in choices) menu.Add(new(c.Display, true, () => PickPlayChoice(c.Name)) { Checked = c.Current });
        return menu;
    }

    public void PickPlayChoice(string name)
    {
        var platform = _pol.Platform;
        if (platform is not (ThirdPartyHook.Ashita or ThirdPartyHook.Windower)) return;
        _settings.SaveChange(s =>
        {
            if (platform == ThirdPartyHook.Ashita) s.AshitaBootConfig = name;
            else s.WindowerProfile = name;
        });
    }

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
        // elevated, so a browser started from here would be too. explorer.exe (full path, a bare name's looked for in the
        // app folder first) hands it to the user's shell
        try
        {
            var psi = new ProcessStartInfo(AppPaths.ExplorerExe) { UseShellExecute = false };
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
                _dialogs.Error("Couldn't open the link",
                    $"Windows didn't open the link. You can copy it from here: {u.AbsoluteUri}");
            }
        }
    }
}
