using System.Windows;
using System.Windows.Interop;
using Provenance.Wpf.ViewModels.Main;
using Provenance.Wpf.Win32;
using Serilog;

namespace Provenance.Wpf.Services;

internal sealed class TrayMode(Window window, Action exit, Func<bool> questionUp) : IDisposable
{
    public static readonly uint TaskbarCreated = User32.RegisterWindowMessageW("TaskbarCreated");

    private const uint Allow = 1;

    private Action? _unfollow;
    private bool _listening;

    internal TrayIcon? Icon { get; private set; }

    public bool InTray => Icon is { Visible: true };

    public event Action? BackFromTray;

    private static TrayState StateOf(MainViewModel vm) =>
        vm.Session.Phase == SessionPhase.InGame ? TrayState.GameOpen
        : vm.LoggedIn ? TrayState.LoggedIn
        : TrayState.LoggedOut;

    public void HideWindow(MainViewModel vm)
    {
        if (Icon == null)
        {
            Icon = new TrayIcon(() => vm.FriendsWindowTitle);
            Icon.OpenRequested += () => { if (!questionUp()) ShowWindow(); };
            Icon.FriendsRequested += () =>
            {
                ShowWindow();
                vm.OpenSocial();
            };
            Icon.SignOutRequested += () =>
            {
                vm.SignOutCommand.Execute(null);
                ShowWindow();
            };
            Icon.ExitRequested += exit;
            // the session can move on while this sits in the tray, a game starting or a Play that's refused
            void Follow()
            {
                if (InTray) Icon.SetState(StateOf(vm));
            }
            vm.Session.PhaseChanged += Follow;
            _unfollow = () => vm.Session.PhaseChanged -= Follow;
        }
        ListenForTaskbar();
        var state = StateOf(vm);
        Icon.SetState(state);
        Icon.Visible = true;
        window.Hide();   // the friends windows are their own windows and stay as they are
        Log.Information("Hidden to the tray ({State})", state.ToString());   // as text, or the log puts it in quotes
    }

    // Explorer isn't elevated and Provenance is, so Windows drops Explorer's message unless it's let through.
    // WinForms never asks for its own window, so the icon's put back from this one
    private void ListenForTaskbar()
    {
        if (_listening || PresentationSource.FromVisual(window) is not HwndSource source) return;
        _listening = true;
        if (!User32.ChangeWindowMessageFilterEx(source.Handle, TaskbarCreated, Allow, IntPtr.Zero))
            Log.Warning("Couldn't ask Windows for the taskbar's rebuild message");
        source.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == TaskbarCreated && InTray)
        {
            Icon!.PutBack();
            Log.Information("Windows asked for the tray icons again, put ours back");
        }
        return IntPtr.Zero;
    }

    public void ShowWindow()
    {
        bool back = InTray;
        if (back) Log.Information("Back from the tray");
        if (Icon != null) Icon.Visible = false;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
        if (back) BackFromTray?.Invoke();
    }

    public void ShowFirstCloseTip(MainViewModel vm)
    {
        if (vm.TrayTipShown || Icon is not { } icon) return;
        icon.ShowTip("Provenance is still running",
            "You're still logged in to PlayOnline, so your friends and messages stay up to date. Right-click this icon to exit.");
        vm.MarkTrayTipShown();
    }

    public bool ShowNotice(string text)
    {
        if (!InTray) return false;
        Icon!.ShowTip("PlayOnline", text);
        return true;
    }

    public bool TryReuse(MainViewModel? vm)
    {
        if (questionUp() || vm is null || !TrayCopy.Reusable(InTray, !window.IsVisible, vm.Session.IdleForReuse))
            return false;
        // queued, the other copy's stuck inside SendMessage until this returns and COM calls fail while it is
        window.Dispatcher.InvokeAsync(ShowWindow);
        return true;
    }

    public void Dispose()
    {
        _unfollow?.Invoke();
        _unfollow = null;
        Icon?.Dispose();
        Icon = null;
    }
}
