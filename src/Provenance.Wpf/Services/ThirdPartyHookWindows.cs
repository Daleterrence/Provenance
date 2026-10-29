using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace Provenance.Wpf.Services;

public static class ThirdPartyHookWindows
{
    // Windower's splash and main window sit there while a held session waits, which can be ages. Only those two get
    // hidden, the main one once its pol.exe is up, so an update or error dialog before that still shows
    public static void HideWindowerWhileHeld(int windowerPid, DateTime started, Func<bool> mainToo, CancellationToken stop) => Task.Run(async () =>
    {
        var logged = new HashSet<IntPtr>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                // the start time catches a reused pid
                try { using var p = Process.GetProcessById(windowerPid); if (p.HasExited || p.StartTime != started) return; }
                catch (ArgumentException) { return; }
                foreach (var h in WindowsOf(windowerPid))
                {
                    if (!IsWindowVisible(h)) continue;
                    // class names as seen live, polled every 250 ms as there's no event for this (sticky tape, I know).
                    // If Windower renames them this quietly stops hiding anything
                    var cls = ClassOf(h);
                    bool splash = cls == "SplashScreen";
                    bool main = cls.StartsWith("HwndWrapper[Windower.exe", StringComparison.OrdinalIgnoreCase) && TitleOf(h) == "Windower"
                                && mainToo();
                    if (!splash && !main) continue;
                    ShowWindow(h, 0 /* SW_HIDE */);
                    if (logged.Add(h))
                        Log.Information("Hid Windower's {Kind} window while PlayOnline is held", splash ? "splash" : "main");
                }
                await Task.Delay(250, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warning(ex, "Windower window hiding stopped"); }
    });

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

    private static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        return GetWindowText(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);
}
