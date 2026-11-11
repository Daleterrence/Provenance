using System.Text;
using Provenance.Common.Win32;

namespace Provenance.Common.Game;

public static class GameWindow
{
    public static IntPtr Find(int polPid) =>
        WindowsOf(polPid).FirstOrDefault(h => User32.IsWindowVisible(h) && ClassOf(h) == "FFXiClass");

    // the app's holding the foreground from the Play click, so pol.exe needs leave to take it
    public static void AllowForeground(int pid) => User32.AllowSetForegroundWindow((uint)pid);

    // false when Windows refuses (the user's moved on to another program)
    public static bool BringToFront(IntPtr h)
    {
        if (User32.IsIconic(h)) User32.ShowWindow(h, 9 /* SW_RESTORE */);
        return User32.SetForegroundWindow(h);
    }

    // every top-level window with its process id, until the callback returns false
    public static void ForEachWindow(Func<IntPtr, int, bool> visit) =>
        User32.EnumWindows((h, _) =>
        {
            User32.GetWindowThreadProcessId(h, out var owner);
            return visit(h, (int)owner);
        }, IntPtr.Zero);

    private static List<IntPtr> WindowsOf(int pid)
    {
        var list = new List<IntPtr>();
        ForEachWindow((h, owner) =>
        {
            if (owner == pid) list.Add(h);
            return true;
        });
        return list;
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        return User32.GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }
}
