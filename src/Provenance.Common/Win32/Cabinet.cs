using System.Runtime.InteropServices;

namespace Provenance.Common.Win32;

internal static class Cabinet
{
    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FDICopy(IntPtr hfdi, string cabinet, string cabPath, int flags, FnNotify notify, IntPtr decrypt, IntPtr user);

    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FDICreate(FnAlloc alloc, FnFree free, FnOpen open, FnRead read, FnWrite write, FnClose close, FnSeek seek, int cpuType, IntPtr erf);

    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FDIDestroy(IntPtr hfdi);
}

[StructLayout(LayoutKind.Sequential)]
internal struct FdiNotification
{
    public int cb;
    public IntPtr psz1, psz2, psz3, pv, hf;
    public ushort date, time, attribs, setID, iCabinet, iFolder;
    public int fdie;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr FnAlloc(int cb);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FnFree(IntPtr pv);
[UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal delegate IntPtr FnOpen(string file, int oflag, int pmode);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint FnRead(IntPtr hf, IntPtr pv, uint cb);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint FnWrite(IntPtr hf, IntPtr pv, uint cb);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int FnClose(IntPtr hf);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int FnSeek(IntPtr hf, int dist, int seektype);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr FnNotify(int fdint, IntPtr pfdin);
