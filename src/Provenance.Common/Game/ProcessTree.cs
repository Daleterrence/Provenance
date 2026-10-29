using System.Runtime.InteropServices;
using System.Text;

namespace Provenance.Common.Game;

// Windower and Ashita start pol.exe themselves. Theirs gets found by its parent chain and the hook attached to it
public static class ProcessTree
{
    public readonly record struct Entry(int Pid, int ParentPid, string ExeName);

    public static IReadOnlyList<Entry> Snapshot()
    {
        var list = new List<Entry>();
        var snap = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snap == InvalidHandle || snap == IntPtr.Zero) return list;
        try
        {
            var pe = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool ok = Process32FirstW(snap, ref pe); ok; ok = Process32NextW(snap, ref pe))
                list.Add(new Entry((int)pe.ProcessId, (int)pe.ParentProcessId, pe.ExeFile));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    // Windows reuses pids, callers compare start times as well
    public static List<int> ChildrenOf(int parentPid) =>
        Snapshot().Where(e => e.ParentPid == parentPid).Select(e => e.Pid).ToList();

    // exited parents still count, so a child of an already-closed hook gets found
    public static int? FindDescendant(IReadOnlyList<Entry> snapshot, int rootPid, string exeName,
                                      IReadOnlySet<int> known, int depth = 3)
    {
        var parentOf = new Dictionary<int, int>();
        foreach (var e in snapshot) parentOf[e.Pid] = e.ParentPid;
        foreach (var e in snapshot)
        {
            if (!e.ExeName.Equals(exeName, StringComparison.OrdinalIgnoreCase) || known.Contains(e.Pid) || e.Pid == rootPid) continue;
            int at = e.Pid;
            for (int step = 0; step < depth && parentOf.TryGetValue(at, out var parent); step++)
            {
                if (parent == rootPid) return e.Pid;
                if (parent == at || parent == 0) break;
                at = parent;
            }
        }
        return null;
    }

    // works on a still-suspended process too
    public static string? ImagePath(int pid)
    {
        var h = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buf = new StringBuilder(1024);
            uint n = (uint)buf.Capacity;
            return QueryFullProcessImageNameW(h, 0, buf, ref n) ? buf.ToString(0, (int)n) : null;
        }
        finally { CloseHandle(h); }
    }

    private const uint SnapProcess = 0x2;
    private const uint QueryLimitedInformation = 0x1000;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
