using System.Diagnostics;
using System.IO;
using Provenance.Common.Game;
using Provenance.Wpf.Win32;
using Serilog;

namespace Provenance.Wpf.Services;

// a second Provenance asks the running copies if one's in the tray with nobody logged in,
// and if so that one comes back and the second just closes
internal static class TrayCopy
{
    public static readonly uint Message = User32.RegisterWindowMessageW("Provenance.ReuseTrayCopy");

    private const uint AbortIfHung = 0x2;
    private const int TimeoutMs = 2000, TotalMs = 5000;

    internal static bool Reusable(bool inTray, bool hidden, bool idle) => inTray && hidden && idle;

    internal static IReadOnlyList<(int Pid, string? Image)> Running(string name) =>
        Process.GetProcessesByName(name).Select(p =>
        {
            using (p) return (p.Id, ProcessTree.ImagePath(p.Id));
        }).ToList();

    // only the same exe, a copy of it somewhere else is somebody else's
    internal static IReadOnlyList<int> OtherCopies(IEnumerable<(int Pid, string? Image)> running, int ownPid, string ownImage) =>
        running.Where(r => r.Pid != ownPid && r.Image is { } image && SamePath(image, ownImage)).Select(r => r.Pid).ToList();

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    internal static bool AskWindow(IntPtr window, int timeoutMs) =>
        User32.SendMessageTimeoutW(window, Message, UIntPtr.Zero, IntPtr.Zero, AbortIfHung, (uint)timeoutMs, out var answer) != IntPtr.Zero && answer == 1;

    // every top-level window of those processes, hidden ones too (a copy in the tray has no main window as far as Process
    // can tell)
    internal static bool AskCopies(IReadOnlyCollection<int> pids)
    {
        if (pids.Count == 0) return false;
        bool taken = false;
        var clock = Stopwatch.StartNew();
        GameWindow.ForEachWindow((window, pid) =>
        {
            if (clock.ElapsedMilliseconds > TotalMs) return false;
            if (!pids.Contains(pid)) return true;
            // this is the process the player just started, so the other copy only gets to the front if this one lets it
            GameWindow.AllowForeground(pid);
            taken = AskWindow(window, TimeoutMs);
            return !taken;
        });
        return taken;
    }

    // any failure opens normally
    internal static bool ReuseRunning()
    {
        try
        {
            if (Environment.ProcessPath is not { } own) return false;
            var others = OtherCopies(Running(Path.GetFileNameWithoutExtension(own)), Environment.ProcessId, own);
            if (!AskCopies(others)) return false;
            Log.Information("A copy of Provenance in the tray came back, so this one isn't opening");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not ask the other copies of Provenance, opening as normal");
            return false;
        }
    }
}
