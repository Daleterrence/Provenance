using System.IO;
using System.Windows;
using Serilog;

namespace Provenance.Wpf.Services;

internal enum TrayState { LoggedOut, LoggedIn, GameOpen }

public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripItem _friends, _signOut, _exit;
    private readonly Func<string> _friendsTitle;

    public event Action? OpenRequested;
    public event Action? FriendsRequested;
    public event Action? SignOutRequested;
    public event Action? ExitRequested;

    internal static int Warmed;

    // a cold menu takes about a second on the UI thread, so one's built and dropped on a worker first.
    // MTA, or a dead STA thread owns WinForms' system events window
    public static void Warm()
    {
        if (Interlocked.Exchange(ref Warmed, 1) != 0) return;
        new Thread(() =>
        {
            try
            {
                using var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Open", null, (_, _) => { });
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            }
            catch { }   // just means the first close is slow again
        }) { IsBackground = true, Name = "Tray warm-up" }.Start();
    }

    internal static Stream? AppIcon() => Application.GetResourceStream(new Uri("pack://application:,,,/Assets/provenance.ico"))?.Stream;

    // WinForms has no default picture. With none it never adds the icon at all, and still says it's visible
    internal static System.Drawing.Icon LoadIcon(Func<Stream?> open)
    {
        Exception? why = null;
        try
        {
            using var stream = open();
            // the frame drawn for the tray's size. Left to pick, it takes the big one and Windows squeezes it down
            if (stream is not null) return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex)
        {
            why = ex;
        }
        try
        {
            if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } own)
            {
                Log.Warning(why, "Couldn't load the tray icon, using the program's own");
                return own;
            }
        }
        catch (Exception ex)
        {
            why ??= ex;
        }
        Log.Warning(why, "Couldn't load the tray icon or the program's own, using Windows' default one");
        return System.Drawing.SystemIcons.Application;
    }

    public TrayIcon(Func<string> friendsTitle)
    {
        _friendsTitle = friendsTitle;
        _icon = new System.Windows.Forms.NotifyIcon { Text = "Provenance (logged in to PlayOnline)", Visible = false, Icon = LoadIcon(AppIcon) };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Provenance", null, (_, _) => OpenRequested?.Invoke());
        _friends = menu.Items.Add("", null, (_, _) => FriendsRequested?.Invoke());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _signOut = menu.Items.Add("Log out of PlayOnline", null, (_, _) => SignOutRequested?.Invoke());
        _exit = menu.Items.Add("Exit and log out", null, (_, _) => ExitRequested?.Invoke());
        // the friends windows can be split or merged while this sits in the tray
        menu.Opening += (_, _) => _friends.Text = _friendsTitle();
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    // in game Exit leaves the game and its session alone, so it doesn't say it logs out
    internal void SetState(TrayState state)
    {
        bool loggedIn = state == TrayState.LoggedIn;
        _friends.Visible = _signOut.Visible = loggedIn;
        _exit.Text = loggedIn ? "Exit and log out" : "Exit";
        _icon.Text = state switch
        {
            TrayState.LoggedIn => "Provenance (logged in to PlayOnline)",
            TrayState.GameOpen => "Provenance (FINAL FANTASY XI is open)",
            _ => "Provenance",
        };
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    internal int PutBacks, Tips;

    // off and on again, so WinForms adds the icon afresh and doesn't just update one Windows no longer has
    internal void PutBack()
    {
        _icon.Visible = false;
        _icon.Visible = true;
        PutBacks++;
    }

    internal string TipText => _icon.Text ?? "";
    internal string FriendsItemText => _friends.Text ?? "";
    internal bool FriendsItemShown => _friends.Available;
    internal bool SignOutItemShown => _signOut.Available;
    internal string ExitItemText => _exit.Text ?? "";
    internal void ClickOpen() => OpenRequested?.Invoke();
    internal void ClickFriends() => FriendsRequested?.Invoke();
    internal void ClickExit() => ExitRequested?.Invoke();

    public void ShowTip(string title, string text)
    {
        _icon.ShowBalloonTip(5000, title, text, System.Windows.Forms.ToolTipIcon.None);
        Tips++;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
