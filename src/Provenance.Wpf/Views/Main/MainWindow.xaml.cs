using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Provenance.Common.Social;
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
        TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();
        Tray = new TrayMode(this, ExitFromTray, () => _asking);
        Tray.BackFromTray += async () =>
        {
            if (Vm is { } vm && await SelfUpdate.UpdateWaitingAsync()) SelfUpdate.ShowWaiting(vm.UpdateNotice);
        };
        SelfUpdate.TagChanged += OnTagChanged;
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
        PreviewMouseDown += (_, e) => Friends.DeselectUnlessIn(e.OriginalSource as DependencyObject);
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

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg != TrayCopy.Message) return IntPtr.Zero;
        handled = true;
        return Tray.TryReuse(Vm) ? 1 : 0;
    }

    private void FitWorkArea() => FitTo(SystemParameters.WorkArea.Size);

    internal void FitTo(Size area)
    {
        double scale = Math.Min(1.0, Math.Min((area.Width - 16) / Width, (area.Height - 16) / Height));
        if (scale >= 1.0 || Content is not FrameworkElement root) return;
        root.LayoutTransform = new ScaleTransform(scale, scale);
        root.UseLayoutRounding = false;
        Width *= scale;
        Height *= scale;
    }

    private void OnTagChanged() => Dispatcher.InvokeAsync(() => Vm?.TitleChanged());

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
            old.RequestUpdateRestart -= OnRequestUpdateRestart;
            old.RequestExit -= OnRequestExit;
            old.RequestAttention -= OnRequestAttention;
            old.AttentionCleared -= OnAttentionCleared;
            old.Social.PropertyChanged -= OnSocialChanged;
        }
        if (e.NewValue is MainViewModel @new)
        {
            @new.RequestHide += OnRequestHide;
            @new.RequestShow += OnRequestShow;
            @new.RequestRestart += OnRequestRestart;
            @new.RequestUpdateRestart += OnRequestUpdateRestart;
            @new.RequestExit += OnRequestExit;
            @new.RequestAttention += OnRequestAttention;
            @new.AttentionCleared += OnAttentionCleared;
            @new.Social.PropertyChanged += OnSocialChanged;
        }
        ShowBadge();
    }

    private void OnSocialChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SocialViewModel.NotificationCount)) ShowBadge();
    }

    internal void ShowBadge()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(ShowBadge);
            return;
        }
        int count = Vm?.Social.NotificationCount ?? 0;
        TaskbarItemInfo.Overlay = count > 0 ? BadgePictures.Overlay(NotificationBadge.Label(count)) : null;
        TaskbarItemInfo.Description = NotificationBadge.Description(count);
    }

    private void OnRequestAttention(bool tip, bool flash) => Dispatcher.InvokeAsync(() =>
    {
        if (tip && Vm?.Notice.Current is { } notice && Tray.ShowNotice(notice.Text)) return;
        if (!flash || IsActive || !IsVisible || Vm is not { WantsAttention: true }) return;
        TaskbarFlash.Start(this);
    });

    private void OnAttentionCleared() => Dispatcher.InvokeAsync(() => TaskbarFlash.StopIfFlashing(this));

    private void OnOpenNotice(object sender, RoutedEventArgs e) => Vm?.OpenNotice();

    internal void OnRequestHide()
    {
        if (_exiting || _closed || Vm is not { } vm) return;
        Tray.HideWindow(vm);
    }

    private void OnRequestShow()
    {
        if (!_closed) Tray.ShowWindow();
    }

    private string? _closingWhy;

    private void ExitFromTray()
    {
        _closingWhy ??= "Exit was chosen from the tray icon";
        ExitApp();
    }

    private void OnRequestRestart()
    {
        _closingWhy ??= "restarting to change Windower or Ashita, as asked in Settings";
        App.RestartOnExit = true;
        ExitApp();
    }

    private void OnRequestUpdateRestart()
    {
        _closingWhy ??= "restarting to install an update";
        App.RestartForUpdate = true;
        Dispatcher.InvokeAsync(ExitWithoutAsking);
    }

    // not from inside Closing, Close throws in there
    private void OnRequestExit(string why) => Dispatcher.InvokeAsync(() => ExitBecause(why));

    internal void ExitBecause(string why)
    {
        _closingWhy ??= why;
        ExitWithoutAsking();
    }

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
        AppClosing.Because(_closingWhy ?? "the window was closed");
        Vm?.Social.Messages.SettleSends(SessionEnd.LogOut);
        Tray.Dispose();
        base.OnClosing(e);
    }

    private bool GoToTray(System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || Vm is not { KeepInTrayOnClose: true } vm) return false;
        e.Cancel = true;
        Tray.HideWindow(vm);
        if (vm.Session.Phase != SessionPhase.InGame) Tray.ShowFirstCloseTip(vm);
        return true;
    }

    private bool WaitForLogInCancel(System.ComponentModel.CancelEventArgs e)
    {
        if (Vm is not { LoggingIn: true } loggingIn) return false;
        e.Cancel = true;
        if (_cancellingLogIn) return true;
        _cancellingLogIn = true;
        Dispatcher.InvokeAsync(async () =>
        {
            await loggingIn.CancelLogInAsync();
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
        else Dispatcher.InvokeAsync(Close);
        return true;
    }

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
        _closingWhy = null;
        App.RestartOnExit = false;
        if (!App.RestartForUpdate) return;
        App.RestartForUpdate = false;
        Vm?.RestartCancelled();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        SelfUpdate.TagChanged -= OnTagChanged;
        Tray.Dispose();
        base.OnClosed(e);
    }

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
        if (!_asking) Close();
    }
}
