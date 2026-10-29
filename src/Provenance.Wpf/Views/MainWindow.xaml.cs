using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Provenance.Wpf.ViewModels;

namespace Provenance.Wpf.Views;

public partial class MainWindow : Window
{
    private Services.TrayIcon? _tray;
    private bool _exiting, _askingToExit, _cancellingSignIn, _flashing;
    private System.Windows.Threading.DispatcherTimer? _traySignOut;
    private DateTime _worldsClosedAt;
    private int _newsShown = -1;

    public MainWindow()
    {
        InitializeComponent();
        WindowFrame.Attach(this);
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += (_, _) => ReportShown();
        StateChanged += (_, _) => ReportShown();
        Activated += async (_, _) =>
        {
            _flashing = false;   // coming to the front stops it
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
        _worldsClosedAt = DateTime.UtcNow;
        WorldsChip.IsChecked = false;
    }

    private void OnWorldsChipMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DateTime.UtcNow - _worldsClosedAt < TimeSpan.FromMilliseconds(300)) e.Handled = true;   // crude, but it works
    }

    private void OnEditProfile(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) EditProfileWindow.ShowFor(vm.Social, this);
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
            old.RequestAttention -= OnRequestAttention;
            old.AttentionCleared -= OnAttentionCleared;
            old.PropertyChanged -= OnVmPropertyChanged;
        }
        if (e.NewValue is MainViewModel @new)
        {
            @new.RequestHide += OnRequestHide;
            @new.RequestShow += OnRequestShow;
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

    private void OnRequestAttention() => Dispatcher.InvokeAsync(() =>
    {
        if (_tray is { Visible: true } && Vm?.Notice is { } text)
        {
            _tray.ShowTip("PlayOnline", text);
            return;
        }
        if (IsActive || !IsVisible || Vm is not { WantsAttention: true }) return;
        TaskbarFlash.Start(this);
        _flashing = true;
    });

    // read in the friends windows, so this window never came to the front to stop it. Queued like the flash, so it
    // can't land before one
    private void OnAttentionCleared() => Dispatcher.InvokeAsync(() =>
    {
        if (!_flashing) return;
        _flashing = false;
        TaskbarFlash.Stop(this);
    });

    private void OnFriendActivated(FriendItem friend) => Vm?.OpenChatWith(friend);

    private void OnOpenNotice(object sender, RoutedEventArgs e) => Vm?.OpenNotice();

    private void OnAvatarClick(object sender, MouseButtonEventArgs e) =>
        Vm?.Social.ChangeProfilePictureCommand.Execute(null);

    private void OnRequestHide() => Dispatcher.Invoke(Hide);

    private void OnRequestShow() => Dispatcher.Invoke(ShowFromTray);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (Vm is { MinimizeGoesToTray: true } vm)
        {
            ToTray(vm, vm.SocialConnected);
            if (vm.SocialConnected) StartTraySignOut(vm);
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
            StartTraySignOut(vm);
            return;
        }
        if (Vm is { SigningIn: true } signing)
        {
            e.Cancel = true;
            if (_cancellingSignIn) return;
            _cancellingSignIn = true;
            Dispatcher.InvokeAsync(async () =>
            {
                await signing.CancelSignInAsync();
                _cancellingSignIn = false;
                Close();
            });
            return;
        }
        if (Vm is { MultiRunning: true } run)
        {
            e.Cancel = true;
            _exiting = false;
            if (run.MultiStopping) { run.StopAllAccountsAndExit(); return; }
            if (MessageDialog.Confirm(this, "Multi-account mode", "Your accounts are still starting. Stop them and close Provenance?",
                                      "STOP AND CLOSE", destructive: true) is not null)
            {
                if (run.MultiRunning) run.StopAllAccountsAndExit();
                else Dispatcher.InvokeAsync(Close);   // the run ended while the question was up, and not from inside Closing
            }
            return;
        }
        // a second close while the unsaved-changes question's up (Alt+F4, tray Exit) waits for its answer
        if (_askingToExit) { e.Cancel = true; return; }
        _askingToExit = true;
        bool go;
        try { go = EditProfileWindow.ConfirmDrop("Exit"); }
        finally { _askingToExit = false; }
        if (!go)
        {
            e.Cancel = true;
            _exiting = false;
            return;
        }
        _tray?.Dispose();
        _tray = null;
        base.OnClosing(e);
    }

    private void ToTray(MainViewModel vm, bool signedIn)
    {
        if (_tray == null)
        {
            _tray = new Services.TrayIcon();
            _tray.OpenRequested += () => Dispatcher.Invoke(() => ShowFromTray());
            _tray.FriendsRequested += () =>
                Dispatcher.Invoke(() => { ShowFromTray(); vm.OpenSocialCommand.Execute(null); });
            _tray.SignOutRequested += () =>
                Dispatcher.Invoke(() => { vm.SignOutCommand.Execute(null); ShowFromTray(); });
            _tray.ExitRequested += () => Dispatcher.Invoke(ExitApp);
        }
        _tray.SetSignedIn(signedIn);
        _tray.Visible = true;
        Hide();   // the friends windows are their own windows and stay as they are
    }

    private void StartTraySignOut(MainViewModel vm)
    {
        if (vm.TraySignOutMinutes <= 0) return;
        _traySignOut ??= new System.Windows.Threading.DispatcherTimer();
        _traySignOut.Interval = TimeSpan.FromMinutes(vm.TraySignOutMinutes);
        _traySignOut.Tick -= OnTraySignOutTick;
        _traySignOut.Tick += OnTraySignOutTick;
        _traySignOut.Start();
    }

    // like the viewer's idle sign-out it asks nothing, so an open profile editor just closes as it is
    private void OnTraySignOutTick(object? sender, EventArgs e)
    {
        _traySignOut?.Stop();
        EditProfileWindow.Current?.CloseQuietly();
        ExitApp();
    }

    private void ShowFromTray()
    {
        _traySignOut?.Stop();
        if (_tray != null) _tray.Visible = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // the uninstaller asks and there's nobody to answer questions, so like the idle sign-out it asks nothing
    internal void ExitForUninstall()
    {
        EditProfileWindow.Current?.CloseQuietly();
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
