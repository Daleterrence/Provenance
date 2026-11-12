using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Social;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf.Views.Main;

public partial class MainWindow : AppWindow
{
    private bool _exiting, _closed, _asking, _cancellingLogIn;

    public MainWindow()
    {
        InitializeComponent();
        Tray = new TrayMode(this, ExitApp, () => _asking);
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += (_, _) => ReportShown();
        StateChanged += (_, _) => ReportShown();
        Activated += async (_, _) =>
        {
            if (Vm is not { } vm) return;
            vm.Social.MainWindowActive = true;
            await vm.Repair.ProbeAsync();
        };
        Deactivated += (_, _) => { if (Vm is { } vm) vm.Social.MainWindowActive = false; };
        FitWorkArea();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    internal TrayMode Tray { get; }

    internal bool Exiting => _exiting;

    internal bool ExitWaitsForAnswer => _asking && Vm?.Multi is not { Running: true };

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(OnWindowMessage);
    }

    // a second copy asking: 1 when this one was in the tray with nobody logged in and has come back for it
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg != TrayCopy.Message) return IntPtr.Zero;
        handled = true;
        return Tray.TryReuse(Vm) ? 1 : 0;
    }

    private void FitWorkArea() => FitTo(SystemParameters.WorkArea.Size);

    // fixed design size, so on a small or scaled screen (1366x768 at 125%, say) the whole thing scales down
    internal void FitTo(Size area)
    {
        double scale = Math.Min(1.0, Math.Min((area.Width - 16) / Width, (area.Height - 16) / Height));
        if (scale >= 1.0 || Content is not FrameworkElement root) return;
        root.LayoutTransform = new ScaleTransform(scale, scale);
        // rounding happens before the scale, so it can't land anything on a pixel and leaves the bottom edge a fraction out
        root.UseLayoutRounding = false;
        Width *= scale;
        Height *= scale;
    }

    private void OnWorldsChip(object sender, RoutedEventArgs e) => Menus.Toggle(WorldsChip, () => WorldsPopup);

    private void OnNoticeHover(object sender, MouseEventArgs e) => Vm?.Notice.Hold(true);
    private void OnNoticeLeave(object sender, MouseEventArgs e) => Vm?.Notice.Hold(false);

    private void ReportShown() => Vm?.News.SetWindowShown(IsVisible && WindowState != WindowState.Minimized);

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
        }
        if (e.NewValue is MainViewModel @new)
        {
            @new.RequestHide += OnRequestHide;
            @new.RequestShow += OnRequestShow;
            @new.RequestRestart += OnRequestRestart;
            @new.RequestExit += OnRequestExit;
            @new.RequestAttention += OnRequestAttention;
            @new.AttentionCleared += OnAttentionCleared;
        }
    }

    // a tray tip only for a notice that's just been shown, never an old one again for a flash.
    // Queued, the snapshot behind this hasn't counted the new message yet
    private void OnRequestAttention(bool tip) => Dispatcher.InvokeAsync(() =>
    {
        if (tip && Vm?.Notice.Current is { } notice && Tray.ShowNotice(notice.Text)) return;
        if (IsActive || !IsVisible || Vm is not { WantsAttention: true }) return;
        TaskbarFlash.Start(this);
    });

    // read in the friends windows, so this window never came to the front to stop it. Queued like the flash, so it
    // can't land before one
    private void OnAttentionCleared() => Dispatcher.InvokeAsync(() => TaskbarFlash.StopIfFlashing(this));

    private void OnOpenNotice(object sender, RoutedEventArgs e) => Vm?.OpenNotice();

    // a hide that lands once the window's going or gone would put up an icon nothing takes down
    internal void OnRequestHide()
    {
        if (_exiting || _closed || Vm is not { } vm) return;
        Tray.HideWindow(vm);
    }

    // a window that's closed can't be shown again, and a login that ends as the app's going still asks
    private void OnRequestShow()
    {
        if (!_closed) Tray.ShowWindow();
    }

    // the same exit as the tray's, which logs a held session out, then the app starts again once it's gone
    private void OnRequestRestart()
    {
        App.RestartOnExit = true;
        ExitApp();
    }

    // not from inside Closing, Close throws in there
    private void OnRequestExit() => Dispatcher.InvokeAsync(ExitWithoutAsking);

    internal void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (Vm is { MinimizeGoesToTray: true } vm)
        {
            Tray.HideWindow(vm);
            return;
        }
        WindowState = WindowState.Minimized;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (GoToTray(e) || WaitForLogInCancel(e) || WaitForAnswer(e) || StopMultiRun(e) || ConfirmDropProfile(e)) return;
        Vm?.Social.Messages.SettleSends(SessionEnd.LogOut);
        Tray.Dispose();
        base.OnClosing(e);
    }

    // closing while logged in (with the setting on) keeps POL logged in from the tray, like an open viewer
    private bool GoToTray(System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || Vm is not { KeepInTrayOnClose: true } vm) return false;
        e.Cancel = true;
        Tray.HideWindow(vm);
        // the tip's about friends and messages staying up to date, and in game they don't
        if (vm.Session.Phase != SessionPhase.InGame) Tray.ShowFirstCloseTip(vm);
        return true;
    }

    private bool WaitForLogInCancel(System.ComponentModel.CancelEventArgs e)
    {
        if (Vm is not { LoggingIn: true } signing) return false;
        e.Cancel = true;
        if (_cancellingLogIn) return true;
        _cancellingLogIn = true;
        // not from inside Closing, Close throws in there
        Dispatcher.InvokeAsync(async () =>
        {
            await signing.CancelLogInAsync();
            _cancellingLogIn = false;
            Close();
        });
        return true;
    }

    private bool StopMultiRun(System.ComponentModel.CancelEventArgs e)
    {
        if (Vm?.Multi is not { Running: true } run) return false;
        e.Cancel = true;
        _exiting = false;
        if (run.Stopping) { run.StopAllAccountsAndExit(); return true; }
        if (!Ask(run.ConfirmStopAccounts))
        {
            StayOpen();
            return true;
        }
        if (run.Running) run.StopAllAccountsAndExit();
        else Dispatcher.InvokeAsync(Close);   // the run ended while the question was up, and not from inside Closing
        return true;
    }

    // a second close while a question's up (Alt+F4, the taskbar's Close) waits for its answer
    private bool WaitForAnswer(System.ComponentModel.CancelEventArgs e)
    {
        if (!_asking) return false;
        e.Cancel = true;
        return true;
    }

    private bool ConfirmDropProfile(System.ComponentModel.CancelEventArgs e)
    {
        if (Ask(() => Vm?.ConfirmExit() ?? true)) return false;
        e.Cancel = true;
        StayOpen();
        return true;
    }

    private bool Ask(Func<bool> question)
    {
        _asking = true;
        try { return question(); }
        finally { _asking = false; }
    }

    private void StayOpen()
    {
        _exiting = false;
        App.RestartOnExit = false;
    }

    // a shutdown closes the window whatever OnClosing says, so the icon can't be left behind
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        Tray.Dispose();
        base.OnClosed(e);
    }

    // the uninstaller, Windows logging off and closing when the game exits have nobody there to answer questions, so
    // it asks nothing and an open profile editor just closes as it is
    internal void ExitWithoutAsking()
    {
        Vm?.CloseProfileEditor();
        if (Vm?.Multi is { Running: true } run)
        {
            run.StopAllAccountsAndExit();
            return;
        }
        ExitApp();
    }

    internal void ExitApp()
    {
        _exiting = true;
        // inside OnClosing with a question up, Close would throw. The answer decides
        if (!_asking) Close();   // the app's exit logs the held session out
    }
}
