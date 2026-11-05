using System.Runtime.InteropServices;
using System.Text;

namespace Provenance.Common.Game;

public static class ThirdPartyHookWindows
{
    public static IntPtr GameWindow(int polPid) =>
        WindowsOf(polPid).FirstOrDefault(h => IsWindowVisible(h) && ClassOf(h) == "FFXiClass");

    // the app's holding the foreground from the Play click, so pol.exe needs leave to take it
    public static void AllowForeground(int pid) => AllowSetForegroundWindow((uint)pid);

    // false when Windows refuses (the user's moved on to another program)
    public static bool BringToFront(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9 /* SW_RESTORE */);
        return SetForegroundWindow(h);
    }

    private static List<IntPtr> WindowsOf(int pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner == (uint)pid) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        return GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);
}
