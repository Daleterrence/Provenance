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
    private readonly DispatcherTimer _socialPoll, _elapsedTimer;

    public event Action? RequestHide;
    public event Action? RequestShow;
    public event Action? RequestRestart;
    public event Action? RequestExit;
    // true when there's a notice to show as a tray tip, false for the taskbar alone
    public event Action<bool>? RequestAttention;
    public event Action? AttentionCleared;

    public MainViewModel(IDialogs dialogs, IWindowManager windows, ICredentialService credentials, ISettingsService settings,
                         FeedService feeds, PolSession pol, AvatarService avatars)
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
        _avatars = avatars;
        _timeLeft = new PhaseTimeLeft(text => TimeLeftText = text);
        News = new NewsViewModel(feeds, _ui, OpenLink);
        Accounts = new AccountsViewModel(dialogs, credentials, settings, avatars);
        _socialPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _socialPoll.Tick += (_, _) => _pol.Poll();
        Multi = new MultiRunViewModel(dialogs, windows, settings, credentials, pol, _ui, Accounts,
            new MultiRunLinks(LoggedInOrLaunching, _socialPoll.Stop, EnsureMemberAgreement, PromptHandleName, GameFolders,
                              () => PlayRegion, WritePolOptions, BackUpGameSettingsAsync));
        Repair = new RepairButtonViewModel(dialogs, windows, settings, pol, () => News.WindowShown, LoggedInOrLaunching, () => Multi.Running,
                                           RefreshInstallState, RefreshLargeAddressAware);
        _pol.SnapshotUpdated += OnSnapshot;
        _pol.SessionEnded += why => _ui.Run(() =>
        {
            Log.Information("Session ended: {Why} (signed in here: {Connected})", why, LoggedIn);
            Multi.StartAllAccountsCommand.NotifyCanExecuteChanged();
            if (!LoggedIn) return;
            EndSession(SessionEnd.Dropped);   // no question asked, the session's gone so the editor couldn't save anyway
            RequestShow?.Invoke();
            _dialogs.Info("PlayOnline session ended", why);
            Log.Information("Session ended: the notice was closed");
        });
        Social = new SocialViewModel(dialogs, windows, _pol, avatars, _settings);
        Social.OpenChatRequested += OpenChatWith;

        Multi.RequestShow += () => RequestShow?.Invoke();
        Multi.RequestExit += () => RequestExit?.Invoke();
        Multi.Changed += () => _openSettings?.MultiRunChanged();
        Multi.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MultiRunViewModel.Running)) return;
            Social.GroupChatBlocked = Multi.Running;
            Repair.Refresh();
            if (!Multi.Running) _ = Repair.ProbeAsync();
            LogInCommand.NotifyCanExecuteChanged();
            ShowHandles();
        };
        Social.MessageReceived += (from, text) => Alert(from, $"New message from {from}: {Preview(text, 60)}");
        Social.FriendRequestReceived += (who, mid) =>
            Alert(null, $"{who} sent you a friend request. Open {FriendsWindowTitle} to answer it.", "AccountPlusOutline",
                  stays: true, request: mid);
        Social.GroupInviteReceived += (who, mid) =>
            Alert(who, $"{who} invited you to a friend group. Open {FriendsWindowTitle} to answer it.", "BellOutline",
                  request: mid);
        Social.NoticeReceived += (who, text) => Alert(who, text, "BellOutline");
        Social.GroupChatRequested += OpenGroupChat;
        Social.GroupChat.LineArrived += OnGroupChatLine;
        Social.SnapshotApplied += _ => Notice.DropIfAnswered(Social.IsWaiting);
        Social.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SocialViewModel.UnseenCount) or nameof(SocialViewModel.GroupChatUnread) && !WantsAttention)
                AttentionCleared?.Invoke();
        };
        // a quiet one, no taskbar flash, and it never covers a message notice
        Social.FriendCameOnline += who =>
        {
            if (!_settings.Current.NotifyFriendsOnline || Notice.Current is { Text.Length: > 0 }) return;
            Notice.Show($"{who} is online now.", "AccountCheckOutline", NoticeTarget.Chat, who);
        };
        Handles = new HandlesViewModel(_dialogs, () => _pol.Pid, _avatars.ProfilePicture)
        {
            SwitchBlocked = () => _windows.ProfileEditorOpen
                ? "Close the profile editor first. It's editing the active handle's profile." : Social.PendingWrite(),
        };
        Social.SnapshotApplied += _ => ShowHandles();
        Handles.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HandlesViewModel.Busy)) Social.HandleSwitching = Handles.Switching;
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LoggedIn)) ShowHandles();
        };
        Social.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SocialViewModel.GameRunning)) ShowHandles(); };
        RefreshInstallState();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _elapsedTimer.Tick += (_, _) =>
        {
            ElapsedText = Seconds(DateTime.UtcNow - _launchStarted);
        };
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

    private bool _gameRunning;
    private SettingsViewModel? _openSettings;

    public bool MinimizeGoesToTray => _settings.Current.MinimizeToTray && !_gameRunning;

    public bool KeepInTrayOnClose => LoggedIn && _settings.Current.KeepSignedInInTray;

    public string CloseTooltip => !LoggedIn ? "Close"
        : KeepInTrayOnClose ? "Close to the tray (you'll stay logged in to PlayOnline)"
        : "Close and log out of PlayOnline";
    partial void OnLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(CloseTooltip));
        RefreshStartButtons();
        if (value && (_settings.Current.KeepSignedInInTray || _settings.Current.MinimizeToTray)) TrayIcon.Warm();
        RefreshLargeAddressAware();
        // the hook numbers mail per session, so a request's notice can't outlive it
        if (!value) Notice.DropRequest();
    }

    partial void OnIsLaunchingChanged(bool value)
    {
        RefreshStartButtons();
        RefreshLargeAddressAware();
    }

    private bool LoggedInOrLaunching() => IsLaunching || LoggedIn;

    private void RefreshStartButtons()
    {
        Multi.StartAllAccountsCommand.NotifyCanExecuteChanged();
        Repair.StartCommand.NotifyCanExecuteChanged();
    }

    // Pid has no change notice, so this gets called wherever it changes (any thread)
    private void RefreshStartAll() =>
        _ui.Post(() =>
        {
            Multi.StartAllAccountsCommand.NotifyCanExecuteChanged();
            Repair.Refresh();
        });

    // an open Settings page's Large Address Aware row goes by the same sign-in and run state as Repair
    private void RefreshLargeAddressAware() => _ = _openSettings?.ReadLargeAddressAwareAsync();
    public bool TrayTipShown => _settings.Current.TrayTipShown;

    public void MarkTrayTipShown() => _settings.SaveChange(s => s.TrayTipShown = true);

    private void Alert(string? sender, string text, string icon = "mail", bool stays = false, int request = 0)
    {
        Notice.Show(text, icon, sender is null ? NoticeTarget.Friends : NoticeTarget.Chat, sender, request, stays);
        RequestAttention?.Invoke(true);
    }

    // false once everything's been read or seen, like a message landing in a conversation that's already on screen
    public bool WantsAttention => Social.UnseenCount > 0 || (_settings.Current.GroupChatFlash && Social.GroupChatUnread > 0);

    private static string Preview(string text, int max)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf(' ', max);
        return (cut > max / 2 ? text[..cut] : text[..max]).TrimEnd() + "...";
    }

    // lines in the group chat with its window hidden or behind, one notice a batch, as the settings say
    private void OnGroupChatLine(string group, string who, string text, int count)
    {
        bool notice = _settings.Current.GroupChatNotice;
        if (notice)
        {
            Notice.Show(count == 1 ? $"{who} in {group}: {Preview(text, 60)}" : $"{count} new lines in {group}", "Forum",
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

    internal Task<int> ConnectProfileAsync(Account p, PolAccount pol, SquareEnixAccount squareEnix, string otp,
                                           IProgress<LaunchProgress>? progress, CancellationToken token) =>
        _pol.ConnectAsync(pol, squareEnix, otp, p.Id, progress, PromptHandleName, token,
                          new SessionOptions(false, null, Accounts.EnsureHomeSlot(p), HandleKey: p.HandleKey));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LogInCommand))]
    [NotifyPropertyChangedFor(nameof(ShowInstallCard), nameof(ShowLogInCard), nameof(ShowFriendsCard))]
    private bool _isLaunching;

    private bool CanLogin() => !IsLaunching && !Multi.Running;

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private Task LogInAsync() => _connectTask = ConnectSocialAsync();

    private Task? _connectTask;

    public bool LoggingIn => _launchCts != null && !_gameRunning && _connectTask is { IsCompleted: false };

    public async Task CancelLogInAsync()
    {
        _launchCts?.Cancel();
        if (_connectTask is { } t) await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(15)));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallCard), nameof(ShowLogInCard), nameof(ShowFriendsCard))]
    private bool _loggedIn;

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

    private static string? ViewerFolder(PolRegion? region) =>
        region is { } r ? PolPaths.TryGetInstallFolder(r, GameProduct.Viewer) : null;

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
        var region = _settings.DetectedRegion ?? PolRegion.UnitedStates;
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
        var vm = new InstallViewModel(_dialogs, _settings.DetectedRegion, _pol.HoldPrestarts, _pol.ReleasePrestarts);
        vm.Installed += () => _ui.Run(() =>
        {
            _settings.RedetectRegion();
            _settings.SaveChange(PolOptions.NewInstall);
            RefreshInstallState();
            Task.Run(() => LargeAddressAwareSwitch.Apply(_settings, "after the install", _pol.HoldPrestarts, _pol.ReleasePrestarts,
                                                          LargeAddressAwareBlocked));
        });
        _windows.ShowInstall(vm);
    }

    public void Startup(bool autoLoginSuppressed)
    {
        _pol.EndOrphans();
        _pol.Prestart();
        if (!Accounts.AutoLogin || autoLoginSuppressed || !Accounts.HasSavedCredentials) return;
        Log.Information("Auto sign-in for {Account}", Accounts.SelectedProfile?.Id);
        LogInCommand.Execute(null);
    }

    private void OnSnapshot(PolSocialSnapshot snap)
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
                if (!LoggedIn) { LoggedIn = true; _socialPoll.Start(); }
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

    // the window OpenSocial brings up, where requests and invitations are answered
    internal string FriendsWindowTitle => _windows.FriendsTitle;

    [RelayCommand]
    private void OpenSocial()
    {
        if (Multi.Running) return;
        Social.ShowGroups = false;
        _windows.ShowFriends(Social);
    }

    // groups live on the friends side's Groups tab
    [RelayCommand]
    private void OpenGroups()
    {
        if (Multi.Running) return;
        Social.ShowGroups = true;
        _windows.ShowFriends(Social);
    }

    // split, that's the Messages window on its own
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
        _windows.ShowChatRooms(Social, _pol.SaveChatPassword, _pol.DropChatPassword, SvgIcons.Emote, () => ChatRoomsOpen = false);
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
        _socialPoll.Stop();
        _windows.CloseSessionWindows();
        Social.GroupChat.CloseQuietly();
        Repair.SessionEnding = true;   // before LoggedIn, so the Repair button never shows enabled in between
        LoggedIn = false;
        Task.Run(() =>
        {
            try
            {
                _pol.StopHold();    // the kill waits a few seconds, so keep it off the UI thread
                RefreshStartAll();
                _pol.Prestart();
            }
            finally { _ui.Post(() => { Repair.SessionEnding = false; Repair.Refresh(); }); }
        });
    }

    private async Task ConnectSocialAsync()
    {
        if (Accounts.SelectedProfile == null && !Accounts.TryAddAccount()) return;
        // one account for the whole sign-in, so switching mid-way can't mix two up
        if (Accounts.SelectedProfile is not { } profile) return;
        if (!Accounts.HasSavedCredentials && !Accounts.EditAccount(requireAll: true)) return;
        if (!EnsureMemberAgreement()) return;
        // a one-time code counts as used once typed, so the details dialog has to come first
        if (Accounts.LoadForLogIn(profile) is not { } details) return;
        var (pol, squareEnix) = details;
        // claimed before the code's asked, so no other copy signs this account in meanwhile
        if (!_pol.TryClaimAccount(profile.Id, out var claimProblem))
        {
            _dialogs.Error("Account in use", claimProblem ?? "This account is in use in another copy of Provenance.");
            return;
        }
        var otp = profile.UseOtp ? Accounts.AskOtp(profile, squareEnix) : "";
        if (otp is null)
        {
            _pol.ReleaseClaim();
            return;
        }
        Social.Start(profile.Id);
        Accounts.SessionProfileId = profile.Id;
        IsLaunching = true;
        var token = BeginCancellable(Accounts.ProfileTitle.StartsWith("New account", StringComparison.Ordinal)
            ? "Logging in to PlayOnline" : "Logging in to " + Accounts.ProfileTitle, "Starting PlayOnline...");
        bool startAfter = false;
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
                _elapsedTimer.Stop();
                LoggedIn = true;
                _socialPoll.Start();
                startAfter = Accounts.StartGameImmediately;
            }
            else
            {
                CanCancel = false;
                await Task.Run(() =>
                {
                    _pol.StopHold();
                    _pol.Prestart();
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
            IsLaunching = false;
        }
        if (startAfter && PlayCommand.CanExecute(null)) await PlayCommand.ExecuteAsync(null);
    }

    // the US viewer asks once per POL ID, here it's once per install
    internal bool EnsureMemberAgreement()
    {
        if (_settings.Current.AcceptedMemberAgreement) return true;
        if (!PolPaths.Supported(_settings.DetectedRegion)) return true;
        var viewer = ViewerFolder(_settings.DetectedRegion);
        var strings = viewer is null ? null : PolStringTable.Load(viewer);
        if (!_dialogs.AskAgreement(strings))
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
        // POL's own Play gate, and it only asks for each list at sign-in so there's no reload here either
        if (Social.PlayBlockedReason is { } why)
        {
            _dialogs.Error("Can't start FINAL FANTASY XI", why);
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
            if (linked == PreLaunchResult.ShowHandles) OpenSettingsAt(SettingsTab.Handles);
            return;
        }
        // SE's installer only writes 3 of the game settings
        FfxiSettings.WriteFirstStartSettings(PlayRegion);
        WritePolOptions(Accounts.SessionProfileId);
        ResetProgress("Starting FINAL FANTASY XI", "Starting FINAL FANTASY XI...");
        await BackUpGameSettingsAsync(null, text => ProgressText = text);
        OnProgress(new LaunchProgress("Starting game", "Starting FINAL FANTASY XI...", 0.5));
        try { _pol.SignalPlay(); }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not signal Play");
            Social.GameRunning = false;
            _elapsedTimer.Stop();
            IsLaunching = false;
            _socialPoll.Start();
            _dialogs.Error("Can't start FINAL FANTASY XI",
                ex is CredentialKeyUnavailableException ? ex.Message : "Provenance couldn't tell PlayOnline to start the game. Try again.",
                offerLogs: true);
            return;
        }
        Social.Messages.SettleSends(SessionEnd.Play);
        _gameRunning = true;
        if (await _pol.WaitForPlayAnswerAsync(PlayAnswerTimeout) is { } failed)
        {
            _gameRunning = false;
            Social.GameRunning = false;
            _elapsedTimer.Stop();
            IsLaunching = false;
            _windows.CloseSessionWindows();
            LoggedIn = false;
            RefreshStartAll();
            _pol.Prestart();
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
        _gameRunning = false;
        Social.GameRunning = false;
        _pol.GameExited();
        var ended = PolSession.GameEndedText(exit);
        if (ended is not null) Log.Error("FINAL FANTASY XI didn't start (pol.exe exit code {Code})", exit);
        else if (_settings.Current.ExitAfterGameExits) { RequestExit?.Invoke(); return; }
        IsLaunching = false;
        _windows.CloseSessionWindows();
        LoggedIn = false;
        RequestShow?.Invoke();
        _pol.Prestart();
        if (ended is not null) _dialogs.Error("Can't start FINAL FANTASY XI", ended, offerLogs: true);
    }

    internal static readonly TimeSpan PlayAnswerTimeout = TimeSpan.FromSeconds(15);

    // a test says whether its made-up session is still there
    internal Func<int, bool>? SessionAliveForTest { get; set; }

    // where the viewer does it, after the update and right before the game starts
    internal Task<PreLaunchResult> LinkBeforePlayAsync()
    {
        ResetProgress("Starting FINAL FANTASY XI", "Starting FINAL FANTASY XI...");
        return new PreLaunchLink
        {
            Pid = () => _pol.Pid,
            Ask = text => Asking(() => _dialogs.YesNo(PreLaunchLink.Title, text), text),
            Failed = text => Asking(() =>
            {
                _dialogs.Error(PreLaunchLink.Title, text);
                return true;
            }, text),
            Saying = text => ProgressText = text,
            PageWaiting = () => Handles.Busy ? Handles.Note.Text : null,
            Alive = SessionAliveForTest ?? ProcessStamp.PidRunning,
            Read = _ => Social.Last,
        }.RunAsync();
    }

    // the footer's "has to wait till the game's closed" isn't true while the answer can still stop it starting
    private bool Asking(Func<bool> dialog, string text)
    {
        var note = Social.StatusNote;
        Social.StatusNote = "";
        try { return dialog(); }
        finally { if (Social.StatusNote == "") Social.StatusNote = note; }
    }

    // Play only comes up after a login, which only a supported region gets
    private PolRegion PlayRegion => _settings.DetectedRegion ?? PolRegion.UnitedStates;

    // the game reads these from the account's option.bin when it starts, so they go in before every start
    private void WritePolOptions(string? profileId)
    {
        if (PolPaths.TryGetInstallFolder(PlayRegion, GameProduct.Viewer) is { } viewer) WritePolOptions(profileId, viewer);
    }

    internal void WritePolOptions(string? profileId, string viewer)
    {
        try
        {
            if (Accounts.Profiles.FirstOrDefault(p => p.Id == profileId) is not { } profile)
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
        saying?.Invoke(GameBackupPolicy.WorkingText);
        var region = PlayRegion;
        var sources = GameBackupSourcesForTest ?? ((settings, boot) => GameBackupPolicy.SourcesFor(settings, region, boot));
        if (await Task.Run(() => GameBackupPolicy.BeforePlay(s, () => sources(s, accountBoot))) is { } problem)
            Notice.Show(problem, "AlertCircleOutline", stays: true);
    }

    private (string Ffxi, string Viewer, PolRegion Region)? GameFolders() =>
        PolPaths.TryGetInstallFolder(PlayRegion, GameProduct.Game) is { } ffxi
        && PolPaths.TryGetInstallFolder(PlayRegion, GameProduct.Viewer) is { } viewer
            ? (ffxi, viewer, PlayRegion) : null;

    internal async Task<bool> UpdateGameAsync()
    {
        if (GameFolders() is not { } dirs) return true;
        var ct = BeginCancellable("Updating FINAL FANTASY XI", "Checking for FINAL FANTASY XI updates...");
        var progress = new Progress<PatchProgress>(p => OnProgress(LaunchProgress.Updating("Updating", p, 0.01, 0.99)));
        try
        {
            await Task.Run(() => PolUpdates.UpdateFfxi(dirs.Ffxi, dirs.Viewer, dirs.Region, progress, ct), ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            Notice.Show("The FINAL FANTASY XI update was stopped. Press Play to continue it.", "InformationOutline");
            return false;
        }
        catch (Exception ex)
        {
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
        Log.Warning("No game window from pol.exe {Pid} after 90 s", pid);
        return false;
    }

    // fed every snapshot, so a change still going when Settings closes is answered all the same
    internal HandlesViewModel Handles { get; }

    // a multi-account run's lean sessions take no requests
    private void ShowHandles()
    {
        bool on = LoggedIn && _pol.Pid is not null;
        Handles.Update(on ? Social.Last : null, on,
                       Social.GameRunning ? HandleList.ReadOnlyText : Multi.Running ? HandleList.MultiRunText : null);
        Social.HandleSwitching = Handles.Switching;
    }

    [RelayCommand]
    private void OpenSettings() => OpenSettingsAt(SettingsTab.General);

    private void OpenSettingsAt(SettingsTab tab)
    {
        var hookBefore = _settings.Current.ThirdPartyHookPlatform;
        ShowHandles();
        var vm = new SettingsViewModel(_dialogs, _settings, multiRunning: () => Multi.Running,
                                       holdPrestarts: _pol.HoldPrestarts, releasePrestarts: _pol.ReleasePrestarts,
                                       largeAddressAwareBlocked: LargeAddressAwareBlocked,
                                       stopPrestart: () => Task.Run(_pol.StopPrestart),
                                       loggedIn: () => IsLaunching || LoggedIn || _pol.Pid is not null,
                                       handles: Handles);
        vm.Hooks.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HookSettings.Hook)) PlatformChanged(); };
        _openSettings = vm;
        Handles.Opened();
        try { _windows.ShowSettings(vm, tab); }
        finally
        {
            _openSettings = null;
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
            Task.Run(() => { _pol.StopPrestart(); _pol.Prestart(); });
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

    // the Repair button's own checks, in its order. The lock comes from the switch's copy of its probe
    private string? LargeAddressAwareBlocked() =>
        IsLaunching || LoggedIn || _pol.Pid is not null || Repair.SessionEnding ? LargeAddressAwareSwitch.LoggedInText
        : Multi.Running ? LargeAddressAwareSwitch.MultiRunText
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
