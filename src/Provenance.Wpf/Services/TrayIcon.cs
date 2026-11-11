using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Provenance.Wpf.Win32;
using Serilog;

namespace Provenance.Wpf.Services;

internal enum TrayState { LoggedOut, LoggedIn, GameOpen }

public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _noticePicture;
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
    internal static System.Drawing.Icon LoadIcon(Func<Stream?> open, bool large = false)
    {
        Exception? why = null;
        try
        {
            using var stream = open();
            // the frame drawn for the size it's shown at: small in the tray, large on a notice. Windows stretches or squeezes any other
            var size = large ? System.Windows.Forms.SystemInformation.IconSize : System.Windows.Forms.SystemInformation.SmallIconSize;
            if (stream is not null) return new System.Drawing.Icon(stream, size);
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
        // a copy, Windows' own one is shared and mustn't be disposed of with this
        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    public TrayIcon(Func<string> friendsTitle)
    {
        _friendsTitle = friendsTitle;
        _icon = new System.Windows.Forms.NotifyIcon { Text = "Provenance (logged in to PlayOnline)", Visible = false, Icon = LoadIcon(AppIcon) };
        _noticePicture = LoadIcon(AppIcon, large: true);
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

    internal int PutBacks, Tips, WithPicture;
    internal System.Drawing.Icon NoticePicture => _noticePicture;

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
        if (ShowWithPicture(title, text)) WithPicture++;
        else _icon.ShowBalloonTip(5000, title, text, System.Windows.Forms.ToolTipIcon.None);
        Tips++;
    }

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo? IconWindow = typeof(System.Windows.Forms.NotifyIcon).GetField("_window", Hidden),
                                       IconId = typeof(System.Windows.Forms.NotifyIcon).GetField("_id", Hidden);

    // WinForms can only give a notice one of Windows' stock pictures or none, so the large frame goes to Windows directly.
    // It wants the icon's window and id for that, and WinForms keeps those to itself
    private bool ShowWithPicture(string title, string text)
    {
        if (IconWindow?.GetValue(_icon) is not System.Windows.Forms.NativeWindow { Handle: var window } || window == IntPtr.Zero
            || IconId?.GetValue(_icon) is not uint id)
            return false;
        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(), hWnd = window, uID = id, uFlags = 0x10,   // NIF_INFO
            szInfoTitle = title, szInfo = text, uTimeout = 5000, szTip = "",
            dwInfoFlags = 0x4 | 0x20,   // NIIF_USER | NIIF_LARGE_ICON
            hBalloonIcon = _noticePicture.Handle,
        };
        return Shell32.Shell_NotifyIconW(1, ref data);   // NIM_MODIFY
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _noticePicture.Dispose();
    }
}
