using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Provenance.Common.Win32;

namespace Provenance.Common.Game;

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

    public static List<int> ChildrenOf(int parentPid) =>
        Snapshot().Where(e => e.ParentPid == parentPid).Select(e => e.Pid).ToList();

    internal static LaunchTree.Opened? Open(int pid)
    {
        var h = Kernel32.OpenProcess(Synchronize | QueryLimitedInformation, false, (uint)pid);
        // access denied is a process that's there but can't be read, anything else is one that's gone
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == AccessDenied ? new LaunchTree.Opened(null, null) : null;
        DateTime? start = Kernel32.GetProcessTimes(h, out var created, out _, out _, out _) ? DateTime.FromFileTimeUtc(created) : null;
        return new LaunchTree.Opened(new SafeProcessHandle(h, ownsHandle: true), start);
    }

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
