using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Provenance.Common.Win32;

namespace Provenance.Common.Game;

// Windower and Ashita start pol.exe themselves. Theirs gets found by its parent chain and the hook attached to it
public static class ProcessTree
{
    public readonly record struct Entry(int Pid, int ParentPid, string ExeName);

    public static IReadOnlyList<Entry> Snapshot()
    {
        var list = new List<Entry>();
        var snap = Kernel32.CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snap == InvalidHandle || snap == IntPtr.Zero) return list;
        try
        {
            var pe = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool ok = Kernel32.Process32FirstW(snap, ref pe); ok; ok = Kernel32.Process32NextW(snap, ref pe))
                list.Add(new Entry((int)pe.ProcessId, (int)pe.ParentProcessId, pe.ExeFile));
        }
        finally { Kernel32.CloseHandle(snap); }
        return list;
    }

    // Windows reuses pids, callers compare start times as well
    public static List<int> ChildrenOf(int parentPid) =>
        Snapshot().Where(e => e.ParentPid == parentPid).Select(e => e.Pid).ToList();

    // UTC, straight from the kernel. Null once it's gone or can't be opened
    public static DateTime? StartTime(int pid)
    {
        var h = Kernel32.OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try { return Kernel32.GetProcessTimes(h, out var created, out _, out _, out _) ? DateTime.FromFileTimeUtc(created) : null; }
        finally { Kernel32.CloseHandle(h); }
    }

    // held open, so the pid stays this process's for as long as the handle's kept, then its start (null if unreadable)
    internal static LaunchTree.Opened? Open(int pid)
    {
        var h = Kernel32.OpenProcess(Synchronize | QueryLimitedInformation, false, (uint)pid);
        // access denied is a process that's there but can't be read, anything else is one that's gone
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == AccessDenied ? new LaunchTree.Opened(null, null) : null;
        DateTime? start = Kernel32.GetProcessTimes(h, out var created, out _, out _, out _) ? DateTime.FromFileTimeUtc(created) : null;
        return new LaunchTree.Opened(new SafeProcessHandle(h, ownsHandle: true), start);
    }

    // works on a still-suspended process too
    public static string? ImagePath(int pid)
    {
        var h = Kernel32.OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buf = new StringBuilder(1024);
            uint n = (uint)buf.Capacity;
            return Kernel32.QueryFullProcessImageNameW(h, 0, buf, ref n) ? buf.ToString(0, (int)n) : null;
        }
        finally { Kernel32.CloseHandle(h); }
    }

    private const uint SnapProcess = 0x2;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x100000;
    private const int AccessDenied = 5;
    private static readonly IntPtr InvalidHandle = new(-1);
}
