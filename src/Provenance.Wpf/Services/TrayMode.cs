using System.Windows;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf.Services;

internal sealed class TrayMode(Window window, Action exit, Func<bool> questionUp) : IDisposable
{
    internal TrayIcon? Icon { get; private set; }

    public bool InTray => Icon is { Visible: true };

    public void HideWindow(MainViewModel vm, bool loggedIn)
    {
        if (Icon == null)
        {
            Icon = new TrayIcon(() => vm.FriendsWindowTitle);
            Icon.OpenRequested += ShowWindow;
            Icon.FriendsRequested += () =>
            {
                ShowWindow();
                vm.OpenSocialCommand.Execute(null);
            };
            Icon.SignOutRequested += () =>
            {
                vm.SignOutCommand.Execute(null);
                ShowWindow();
            };
            Icon.ExitRequested += exit;
        }
        Icon.SetLoggedIn(loggedIn);
        Icon.Visible = true;
        window.Hide();   // the friends windows are their own windows and stay as they are
    }

    public void ShowWindow()
    {
        if (questionUp()) return;
        if (Icon != null) Icon.Visible = false;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
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
        Icon?.Dispose();
        Icon = null;
    }
}
