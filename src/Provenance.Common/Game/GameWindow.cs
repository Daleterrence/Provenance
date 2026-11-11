using System.Text;
using Provenance.Common.Win32;
using Serilog;

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

    // true once the game's window is up. Windows only hands focus to the last program used, which is the app at the
    // Play click, so pol.exe gets leave to take it first
    public static async Task<bool> BringForwardWhenUpAsync(int pid)
    {
        AllowForeground(pid);
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until)
        {
            if (!ProcessStamp.PidRunning(pid)) return false;
            if (Find(pid) is var h && h != IntPtr.Zero)
            {
                bool front = BringToFront(h);
                Log.Information("Game window is up (pol.exe {Pid}); brought to the front: {Front}", pid, front);
                return true;
            }
            await Task.Delay(250);
        }
        Log.Warning("No game window from pol.exe {Pid} after 90s", pid);
        return false;
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
