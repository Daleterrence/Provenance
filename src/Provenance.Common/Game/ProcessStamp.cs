using System.Runtime.InteropServices;
using Provenance.Common.Install;
using Provenance.Common.PolFormat;
using Provenance.Common.Win32;

namespace Provenance.Common.Game;

// pid plus start time, since Windows reuses pids
public readonly record struct ProcessStamp(int Pid, long StartUtcTicks)
{
    public enum Ending { NotThat, ExitedOnItsOwn, Ended }

    internal enum Seen { Gone, Unreadable, Running, Exited }

    internal static Func<int, (Seen Seen, long StartUtcTicks)> Look { get; set; } = LookAtKernel;

    public DateTime StartUtc => new(StartUtcTicks, DateTimeKind.Utc);

    public static ProcessStamp? Of(int pid) =>
        Look(pid) is (Seen.Running or Seen.Exited, var ticks) ? new ProcessStamp(pid, ticks) : null;

    public bool IsRunning() => Pid > 0 && Look(Pid) is (Seen.Running, var ticks) && ticks == StartUtcTicks;

    // Anything can be renamed pol.exe, so the image path has to be an installed viewer's
    public bool IsViewerPol()
    {
        if (!IsRunning() || ProcessTree.ImagePath(Pid) is not { } image) return false;
        try
        {
            return new[] { PolRegion.UnitedStates, PolRegion.Europe, PolRegion.Japan }.Any(region =>
                PolPaths.TryGetInstallFolder(region, GameProduct.Viewer) is { } dir &&
                string.Equals(Path.GetFullPath(Path.Combine(dir, GameProduct.Viewer.KeyFile)), Path.GetFullPath(image), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public bool MightBeRunning() => Pid > 0 && Look(Pid) switch
    {
        (Seen.Running, var ticks) => ticks == StartUtcTicks,
        (Seen.Unreadable, _) => true,
        _ => false,
    };

    public static bool PidRunning(int pid) => Look(pid).Seen is Seen.Running or Seen.Unreadable;

    public Ending EndIfStill(int graceMs = 0, int afterMs = 0, Func<ProcessStamp, bool>? alsoIf = null)
    {
        if (Pid <= 0) return Ending.NotThat;
        var h = Kernel32.OpenProcess(Terminate | Synchronize | QueryLimitedInformation, false, (uint)Pid);
        if (h == IntPtr.Zero) return Ending.NotThat;
        try
        {
            if (!Kernel32.GetProcessTimes(h, out var created, out _, out _, out _)
                || DateTime.FromFileTimeUtc(created).Ticks != StartUtcTicks
                || alsoIf?.Invoke(this) == false)
                return Ending.NotThat;
            if (Exited(h, graceMs)) return Ending.ExitedOnItsOwn;
            if (!Kernel32.TerminateProcess(h, uint.MaxValue)) return Ending.NotThat;
            Exited(h, Math.Max(afterMs, 1000));
            return Ending.Ended;
        }
        finally { Kernel32.CloseHandle(h); }
    }

    private static bool Exited(IntPtr handle, int waitMs) => Kernel32.WaitForSingleObject(handle, (uint)waitMs) == WaitObject0;

    private static (Seen, long) LookAtKernel(int pid)
    {
        if (pid <= 0) return (Seen.Gone, 0);
        var h = Kernel32.OpenProcess(Synchronize | QueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero) return (Marshal.GetLastWin32Error() == AccessDenied ? Seen.Unreadable : Seen.Gone, 0);
        try
        {
            if (!Kernel32.GetProcessTimes(h, out var created, out _, out _, out _)) return (Seen.Unreadable, 0);
            return (Exited(h, 0) ? Seen.Exited : Seen.Running, DateTime.FromFileTimeUtc(created).Ticks);
        }
        finally { Kernel32.CloseHandle(h); }
    }

    private const uint Terminate = 0x1;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x100000;
    private const uint WaitObject0 = 0;
    private const int AccessDenied = 5;
}
