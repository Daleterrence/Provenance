using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Social;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf.Views.Main;

public partial class MainWindow : AppWindow
{
    private readonly TrayMode _tray;
    private bool _exiting, _asking, _cancellingLogIn;
    private readonly ReopenGuard _worldsGuard = new();

    public MainWindow()
    {
        InitializeComponent();
        _tray = new TrayMode(this, ExitApp, () => _asking);
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

    internal TrayMode Tray => _tray;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
    }

    // a second copy asking: 1 when this one was in the tray with nobody logged in and has come back for it
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg != TrayCopy.Message) return IntPtr.Zero;
        handled = true;
        return _tray.TryReuse(Vm) ? 1 : 0;
    }

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

    // a tray tip only for a notice that's just been shown, never an old one again for a flash on its own. Queued, the
    // snapshot that raised this hasn't counted the new message yet
    private void OnRequestAttention(bool tip) => Dispatcher.InvokeAsync(() =>
    {
        if (tip && Vm?.Notice.Current is { } notice && _tray.ShowNotice(notice.Text)) return;
        if (IsActive || !IsVisible || Vm is not { WantsAttention: true }) return;
        TaskbarFlash.Start(this);
    });

    // read in the friends windows, so this window never came to the front to stop it. Queued like the flash, so it
    // can't land before one
    private void OnAttentionCleared() => Dispatcher.InvokeAsync(() => TaskbarFlash.StopIfFlashing(this));

    private void OnOpenNotice(object sender, RoutedEventArgs e) => Vm?.OpenNotice();

    private void OnRequestHide() => Hide();

    private void OnRequestShow() => _tray.ShowWindow();

    // the same exit as the tray's, which logs a held session out, then the app starts again once it's gone
    private void OnRequestRestart()
    {
        App.RestartOnExit = true;
        ExitApp();
    }

    private void OnRequestExit() => ExitWithoutAsking();

    internal void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (Vm is { MinimizeGoesToTray: true } vm)
        {
            _tray.HideWindow(vm, vm.LoggedIn);
            return;
        }
        WindowState = WindowState.Minimized;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (GoToTray(e) || WaitForLogInCancel(e) || WaitForAnswer(e) || StopMultiRun(e) || ConfirmDropProfile(e)) return;
        Vm?.Social.Messages.SettleSends(SessionEnd.LogOut);
        _tray.Dispose();
        base.OnClosing(e);
    }

    // closing while signed in (with the setting on) keeps POL signed in from the tray, like an open viewer
    private bool GoToTray(System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || Vm is not { KeepInTrayOnClose: true } vm) return false;
        e.Cancel = true;
        _tray.HideWindow(vm, true);
        _tray.ShowFirstCloseTip(vm);
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
        bool stop;
        _asking = true;
        try { stop = run.ConfirmStopAccounts(); }
        finally { _asking = false; }
        if (!stop)
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
        bool go;
        _asking = true;
        try { go = Vm?.ConfirmExit() ?? true; }
        finally { _asking = false; }
        if (go) return false;
        e.Cancel = true;
        StayOpen();
        return true;
    }

    private void StayOpen()
    {
        _exiting = false;
        App.RestartOnExit = false;
    }

    // a shutdown closes the window whatever OnClosing says, so the icon can't be left behind
    protected override void OnClosed(EventArgs e)
    {
        _tray.Dispose();
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

    private void ExitApp()
    {
        _exiting = true;
        // inside OnClosing with a question up, Close would throw. The answer decides
        if (!_asking) Close();   // the app's exit signs the held session out
    }
}
