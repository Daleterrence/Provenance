using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Provenance.Common.Install;

internal sealed class CabExtractor(string cabPath, Func<string, string?> targetFor, Action<long>? written,
                                   CancellationToken ct)
{
    // nothing can throw across FDI's native frames, so a failed alloc is null and anything else waits in _error
    private static readonly FnAlloc Alloc = cb =>
    {
        try { return Marshal.AllocHGlobal(cb); }
        catch { return IntPtr.Zero; }
    };
    private static readonly FnFree Free = Marshal.FreeHGlobal;
    private const int FdintCopyFile = 2, FdintCloseFileInfo = 3, FdintNextCabinet = 4;
    private readonly Dictionary<int, FileStream> _open = new();
    private readonly Dictionary<int, string> _outputs = new();
    private int _next = 1;
    private Exception? _error;
    private byte[] _buf = new byte[1 << 16];

    public static void Extract(string cabPath, Func<string, string?> targetFor, Action<long>? written,
                               CancellationToken ct) => new CabExtractor(cabPath, targetFor, written, ct).Run();

    private void Run()
    {
        FnOpen open = Open;
        FnRead read = Read;
        FnWrite write = Write;
        FnClose close = Close;
        FnSeek seek = Seek;
        FnNotify notify = Notify;
        var erf = Marshal.AllocHGlobal(16);
        IntPtr hfdi = IntPtr.Zero;
        try
        {
            hfdi = FDICreate(Alloc, Free, open, read, write, close, seek, -1 /* cpuUNKNOWN */, erf);
            if (hfdi == IntPtr.Zero) throw Shown.Mark(new InvalidOperationException("Windows couldn't start unpacking the installer."));
            // the name's only a token since Open always opens cabPath, so paths outside the ANSI code page still work
            bool ok = FDICopy(hfdi, "provenance.cab", "\\", 0, notify, IntPtr.Zero, IntPtr.Zero);
            if (_error != null) ExceptionDispatchInfo.Capture(_error).Throw();
            ct.ThrowIfCancellationRequested();
            if (!ok) throw new InvalidDataException($"{Path.GetFileName(cabPath)} could not be extracted (cabinet error {Marshal.ReadInt32(erf)}).");
        }
        finally
        {
            if (hfdi != IntPtr.Zero) FDIDestroy(hfdi);
            GC.KeepAlive((open, read, write, close, seek, notify));   // FDI holds on to these until FDIDestroy
            Marshal.FreeHGlobal(erf);
            foreach (var s in _open.Values) s.Dispose();
            _open.Clear();
        }
    }

    private IntPtr Add(FileStream s)
    {
        int h = _next++;
        _open[h] = s;
        return new IntPtr(h);
    }

    private IntPtr Open(string file, int oflag, int pmode)
    {
        try { return Add(new FileStream(cabPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16)); }
        catch (Exception e) { _error ??= e; return new IntPtr(-1); }
    }

    private uint Read(IntPtr hf, IntPtr pv, uint cb)
    {
        try
        {
            if (_buf.Length < cb) _buf = new byte[cb];
            int n = _open[hf.ToInt32()].Read(_buf, 0, (int)cb);
            Marshal.Copy(_buf, 0, pv, n);
            return (uint)n;
        }
        catch (Exception e) { _error ??= e; return uint.MaxValue; }
    }

    private uint Write(IntPtr hf, IntPtr pv, uint cb)
    {
        if (ct.IsCancellationRequested) return uint.MaxValue;
        try
        {
            if (_buf.Length < cb) _buf = new byte[cb];
            Marshal.Copy(pv, _buf, 0, (int)cb);
            _open[hf.ToInt32()].Write(_buf, 0, (int)cb);
            written?.Invoke(cb);
            return cb;
        }
        catch (Exception e) { _error ??= e; return uint.MaxValue; }
    }

    private int Close(IntPtr hf)
    {
        try
        {
            if (_open.Remove(hf.ToInt32(), out var s)) s.Dispose();
            return 0;
        }
        catch (Exception e) { _error ??= e; return -1; }
    }

    private int Seek(IntPtr hf, int dist, int seektype)
    {
        try { return (int)_open[hf.ToInt32()].Seek(dist, (SeekOrigin)seektype); }
        catch (Exception e) { _error ??= e; return -1; }
    }

    private IntPtr Notify(int fdint, IntPtr pfdin)
    {
        try
        {
            if (ct.IsCancellationRequested) return new IntPtr(-1);
            var n = Marshal.PtrToStructure<FdiNotification>(pfdin);
            switch (fdint)
            {
                case FdintCopyFile:
                {
                    if (targetFor(Marshal.PtrToStringAnsi(n.psz1) ?? "") is not { } target) return IntPtr.Zero;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
                        File.SetAttributes(target, FileAttributes.Normal);
                    var h = Add(new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16));
                    _outputs[h.ToInt32()] = target;
                    return h;
                }
                case FdintCloseFileInfo:
                {
                    // the cabinet's date like Windows Installer, set on the open handle since reopening costs a second
                    // virus scan per file
                    if (_outputs.Remove(n.hf.ToInt32()) && _open.TryGetValue(n.hf.ToInt32(), out var fs) && DosTime(n.date, n.time) is { } utc)
                    {
                        fs.Flush();
                        File.SetLastWriteTimeUtc(fs.SafeFileHandle, utc);
                    }
                    Close(n.hf);
                    return new IntPtr(1);
                }
                case FdintNextCabinet:
                    _error ??= new InvalidDataException($"{Path.GetFileName(cabPath)} continues in another cabinet, which Provenance does not expect.");
                    return new IntPtr(-1);
                default:
                    return IntPtr.Zero;
            }
        }
        catch (Exception e) { _error ??= e; return new IntPtr(-1); }
    }

    // today's UTC offset rather than the one on that date, same as DosDateTimeToFileTime + LocalFileTimeToFileTime
    private static DateTime? DosTime(ushort date, ushort time)
    {
        try
        {
            var local = new DateTime(((date >> 9) & 0x7F) + 1980, (date >> 5) & 0x0F, date & 0x1F,
                                     (time >> 11) & 0x1F, (time >> 5) & 0x3F, (time & 0x1F) * 2, DateTimeKind.Unspecified);
            return DateTime.SpecifyKind(local - TimeZoneInfo.Local.GetUtcOffset(DateTime.Now), DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FdiNotification
    {
        public int cb;
        public IntPtr psz1, psz2, psz3, pv, hf;
        public ushort date, time, attribs, setID, iCabinet, iFolder;
        public int fdie;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FnAlloc(int cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FnFree(IntPtr pv);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate IntPtr FnOpen(string file, int oflag, int pmode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint FnRead(IntPtr hf, IntPtr pv, uint cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint FnWrite(IntPtr hf, IntPtr pv, uint cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnClose(IntPtr hf);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnSeek(IntPtr hf, int dist, int seektype);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FnNotify(int fdint, IntPtr pfdin);

    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FDICreate(FnAlloc alloc, FnFree free, FnOpen open, FnRead read, FnWrite write, FnClose close, FnSeek seek, int cpuType, IntPtr erf);

    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FDICopy(IntPtr hfdi, string cabinet, string cabPath, int flags, FnNotify notify, IntPtr decrypt, IntPtr user);

    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FDIDestroy(IntPtr hfdi);
}
