using System.Text;
using Provenance.Common.Win32;
using Serilog;

namespace Provenance.Common.Game;

public static class GameWindow
{
    public static IntPtr Find(int polPid) =>
        WindowsOf(polPid).FirstOrDefault(h => User32.IsWindowVisible(h) && ClassOf(h) == "FFXiClass");

    public static void AllowForeground(int pid) => User32.AllowSetForegroundWindow((uint)pid);

    public static bool BringToFront(IntPtr h)
    {
        if (User32.IsIconic(h)) User32.ShowWindow(h, 9 /* SW_RESTORE */);
        return User32.SetForegroundWindow(h);
    }

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

    public static Task<bool> WaitUntilUpAsync(int pid, TimeSpan limit, CancellationToken ct) =>
        WaitUntilAsync(() => Find(pid) != IntPtr.Zero, () => ProcessStamp.PidRunning(pid), limit, ct);

    internal static async Task<bool> WaitUntilAsync(Func<bool> up, Func<bool> alive, TimeSpan limit, CancellationToken ct)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested && alive())
        {
            if (up()) return true;
            try { await Task.Delay(50, ct); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    public static int AskToClose(int pid)
    {
        int asked = 0;
        foreach (var h in WindowsOf(pid))
            if (User32.PostMessageW(h, WmClose, UIntPtr.Zero, IntPtr.Zero)) asked++;
        return asked;
    }

    private const uint WmClose = 0x0010;

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
