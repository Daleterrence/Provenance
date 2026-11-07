using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.Services;
using Provenance.Wpf.Views.Social;
using Provenance.Wpf.ViewModels.Social;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf.Views.Main;

public partial class MainWindow : AppWindow
{
    internal TrayIcon? _tray;
    private bool _exiting, _askingToExit, _cancellingLogIn;
    private readonly ReopenGuard _worldsGuard = new();
    private int _newsShown = -1;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += (_, _) => ReportShown();
        StateChanged += (_, _) => ReportShown();
        Activated += async (_, _) =>
        {
            if (Vm is not { } vm) return;
            vm.Social.MainWindowActive = true;
            await vm.ProbeRepairAsync();
        };
        Deactivated += (_, _) => { if (Vm is { } vm) vm.Social.MainWindowActive = false; };
        FitWorkArea();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // fixed design size, so on a small or scaled screen (1366x768 at 125%, say) the whole thing scales down
    private void FitWorkArea()
    {
        var area = SystemParameters.WorkArea;
        double scale = Math.Min(1.0, Math.Min((area.Width - 16) / Width, (area.Height - 16) / Height));
        if (scale >= 1.0 || Content is not FrameworkElement root) return;
        root.LayoutTransform = new ScaleTransform(scale, scale);
        Width *= scale;
        Height *= scale;
    }

    // the list closes on any click outside it, chip included, and that click must only close it
    private void OnWorldsPopupClosed(object? sender, EventArgs e)
    {
        _worldsGuard.MarkClosed();
        WorldsChip.IsChecked = false;
    }

    private void OnWorldsChipMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_worldsGuard.JustClosed) e.Handled = true;
    }

    private void OnEditProfile(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.Social.EditProfile();
    }

    private void OnNewsHover(object sender, MouseEventArgs e) => Vm?.HoldSlides(true);

    private void OnNewsLeave(object sender, MouseEventArgs e)
    {
        if (sender is UIElement { IsKeyboardFocusWithin: true }) return;
        Vm?.HoldSlides(false);
    }

    // focus on the Read link holds the article too, so Enter opens the one shown
    private void OnNewsFocus(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is UIElement card) Vm?.HoldSlides(card.IsKeyboardFocusWithin || card.IsMouseOver);
    }

    private void OnNoticeHover(object sender, MouseEventArgs e) => Vm?.HoldNotice(true);
    private void OnNoticeLeave(object sender, MouseEventArgs e) => Vm?.HoldNotice(false);

    private void OnBannerFailed(object? sender, ExceptionRoutedEventArgs e) => Vm?.BannerFailed();

    private void ReportShown() => Vm?.SetWindowShown(IsVisible && WindowState != WindowState.Minimized);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel old)
        {
            old.RequestHide -= OnRequestHide;
            old.RequestShow -= OnRequestShow;
            old.RequestRestart -= OnRequestRestart;
            old.RequestExit -= OnRequestExit;
            old.RequestAttention -= OnRequestAttention;
            old.AttentionCleared -= OnAttentionCleared;
            old.PropertyChanged -= OnVmPropertyChanged;
        }
        if (e.NewValue is MainViewModel @new)
        {
            @new.RequestHide += OnRequestHide;
            @new.RequestShow += OnRequestShow;
            @new.RequestRestart += OnRequestRestart;
            @new.RequestExit += OnRequestExit;
            @new.RequestAttention += OnRequestAttention;
            @new.AttentionCleared += OnAttentionCleared;
            @new.PropertyChanged += OnVmPropertyChanged;
        }
    }

    // forward (the timer, the next arrow, a later dot) slides in from the right, back from the left
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentNewsIndex) || sender is not MainViewModel vm) return;
        int n = vm.NewsItems.Count, now = vm.CurrentNewsIndex, was = _newsShown;
        if (now < 0) return;   // the pager pushes -1 while the list is rebuilt
        _newsShown = now;
        if (n < 2 || was < 0 || was >= n || was == now) return;
        int dir = now == (was + 1) % n ? 1 : now == (was - 1 + n) % n ? -1 : Math.Sign(now - was);
        AnimateNews(dir);
    }

    private void AnimateNews(int dir)
    {
        if (!SystemParameters.ClientAreaAnimation) return;   // Windows "show animations" is off
        if (NewsContent.RenderTransform is not TranslateTransform tt) return;
        var duration = TimeSpan.FromMilliseconds(340);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(80 * dir, 0, duration) { EasingFunction = ease });
        NewsContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }

    // a tray tip only for a notice that's just been shown, never an old one again for a flash on its own
    private void OnRequestAttention(bool tip) => Dispatcher.InvokeAsync(() =>
    {
        if (tip && _tray is { Visible: true } tray && Vm?.Notice is { } text)
        {
            tray.ShowTip("PlayOnline", text);
            return;
        }
        if (IsActive || !IsVisible || Vm is not { WantsAttention: true }) return;
        TaskbarFlash.Start(this);
    });

    // read in the friends windows, so this window never came to the front to stop it. Queued like the flash, so it
    // can't land before one
    private void OnAttentionCleared() => Dispatcher.InvokeAsync(() => TaskbarFlash.StopIfFlashing(this));

    private void OnFriendActivated(FriendItem friend) => Vm?.OpenChatWith(friend);

    // read fresh each time, so it follows Settings and any profile added since
    private void OnPlayChoices(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not FrameworkElement cog) return;
        var menu = new ContextMenu();
        Menus.Fill(menu, vm.PlayChoicesMenu());
        PlayChoicesIcon.IsOpen = true;
        menu.Closed += (_, _) => PlayChoicesIcon.IsOpen = false;
        Menus.ShowBelow(menu, cog);
    }

    private void OnOpenNotice(object sender, RoutedEventArgs e) => Vm?.OpenNotice();

    private void OnAvatarClick(object sender, MouseButtonEventArgs e) =>
        Vm?.Social.ChangeProfilePictureCommand.Execute(null);

    private void OnRequestHide() => Dispatcher.Invoke(Hide);

    private void OnRequestShow() => Dispatcher.Invoke(ShowFromTray);

    // the same exit as the tray's, which logs a held session out, then the app starts again once it's gone
    private void OnRequestRestart() => Dispatcher.InvokeAsync(() =>
    {
        App.RestartOnExit = true;
        ExitApp();
    });

    private void OnRequestExit() => Dispatcher.InvokeAsync(ExitWithoutAsking);

    internal void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (Vm is { MinimizeGoesToTray: true } vm)
        {
            ToTray(vm, vm.LoggedIn);
            return;
        }
        WindowState = WindowState.Minimized;
    }

    // closing while signed in (with the setting on) keeps POL signed in from the tray, like an open viewer
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting && Vm is { KeepInTrayOnClose: true } vm)
        {
            e.Cancel = true;
            ToTray(vm, true);
            if (!vm.TrayTipShown)
            {
                _tray!.ShowTip("Provenance is still running",
                    "You're still logged in to PlayOnline, so your friends and messages stay up to date. Right-click this icon to exit.");
                vm.MarkTrayTipShown();
            }
            return;
        }
        if (Vm is { LoggingIn: true } signing)
        {
            e.Cancel = true;
            if (_cancellingLogIn) return;
            _cancellingLogIn = true;
            Dispatcher.InvokeAsync(async () =>
            {
                await signing.CancelLogInAsync();
                _cancellingLogIn = false;
                Close();
            });
            return;
        }
        if (Vm is { MultiRunning: true } run)
        {
            e.Cancel = true;
            _exiting = false;
            if (run.MultiStopping) { run.StopAllAccountsAndExit(); return; }
            if (run.ConfirmStopAccounts())
            {
                if (run.MultiRunning) run.StopAllAccountsAndExit();
                else Dispatcher.InvokeAsync(Close);   // the run ended while the question was up, and not from inside Closing
            }
            else App.RestartOnExit = false;
            return;
        }
        // a second close while the unsaved-changes question's up (Alt+F4, tray Exit) waits for its answer
        if (_askingToExit) { e.Cancel = true; return; }
        _askingToExit = true;
        bool go;
        try { go = Vm?.ConfirmExit() ?? true; }
        finally { _askingToExit = false; }
        if (!go)
        {
            e.Cancel = true;
            _exiting = false;
            App.RestartOnExit = false;
            return;
        }
        Vm?.Social.SettleSends(SessionEnd.LogOut);
        _tray?.Dispose();
        _tray = null;
        base.OnClosing(e);
    }

    // a shutdown closes the window whatever OnClosing says, so the icon can't be left behind
    protected override void OnClosed(EventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        base.OnClosed(e);
    }

    private void ToTray(MainViewModel vm, bool loggedIn)
    {
        if (_tray == null)
        {
            _tray = new TrayIcon(() => vm.FriendsWindowTitle);
            _tray.OpenRequested += () => Dispatcher.Invoke(() => ShowFromTray());
            _tray.FriendsRequested += () =>
                Dispatcher.Invoke(() => { ShowFromTray(); vm.OpenSocialCommand.Execute(null); });
            _tray.SignOutRequested += () =>
                Dispatcher.Invoke(() => { vm.SignOutCommand.Execute(null); ShowFromTray(); });
            _tray.ExitRequested += () => Dispatcher.Invoke(ExitApp);
        }
        _tray.SetLoggedIn(loggedIn);
        _tray.Visible = true;
        Hide();   // the friends windows are their own windows and stay as they are
    }

    internal void ShowFromTray()
    {
        if (_tray != null) _tray.Visible = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // the uninstaller, Windows logging off and closing when the game exits have nobody there to answer questions, so
    // it asks nothing and an open profile editor just closes as it is
    internal void ExitWithoutAsking()
    {
        Vm?.CloseProfileEditor();
        if (Vm is { MultiRunning: true } run)
        {
            run.StopAllAccountsAndExit();
            return;
        }
        ExitApp();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();   // the app's exit signs the held session out
    }
}
