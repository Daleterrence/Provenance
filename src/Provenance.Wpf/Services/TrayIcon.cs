using System.Windows;

namespace Provenance.Wpf.Services;

public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripItem _friends, _signOut, _exit;
    private readonly Func<string> _friendsTitle;

    public event Action? OpenRequested;
    public event Action? FriendsRequested;
    public event Action? SignOutRequested;
    public event Action? ExitRequested;

    private static int _warmed;

    // a cold menu takes about a second on the UI thread, so one's built and thrown away on a worker first. MTA, or a
    // dead STA thread ends up owning WinForms' system events window
    public static void Warm()
    {
        if (Interlocked.Exchange(ref _warmed, 1) != 0) return;
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

    public TrayIcon(Func<string> friendsTitle)
    {
        _friendsTitle = friendsTitle;
        _icon = new System.Windows.Forms.NotifyIcon { Text = "Provenance (logged in to PlayOnline)", Visible = false };
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/provenance.ico"));
            if (res != null) _icon.Icon = new System.Drawing.Icon(res.Stream);
        }
        catch { }   // the tray still works with the default icon
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

    public void SetLoggedIn(bool loggedIn)
    {
        _friends.Visible = _signOut.Visible = loggedIn;
        _exit.Text = loggedIn ? "Exit and log out" : "Exit";
        _icon.Text = loggedIn ? "Provenance (logged in to PlayOnline)" : "Provenance";
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    internal string FriendsItemText => _friends.Text ?? "";

    public void ShowTip(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, System.Windows.Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
