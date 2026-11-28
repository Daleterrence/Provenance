using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Provenance.Common.Win32;

internal static class Kernel32
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int CompareStringEx(string? locale, uint flags, string a, int aLength, string b, int bLength,
                                               IntPtr version, IntPtr reserved, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateDirectoryW(string path, in SecurityAttributes security);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeWaitHandle CreateEventW(in SecurityAttributes sa, bool manualReset, bool initialState, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition,
                                                      uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeMemoryMappedFileHandle CreateFileMappingW(IntPtr file, in SecurityAttributes sa, uint protect, uint sizeHigh,
                                                                         uint sizeLow, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll")]
    internal static extern bool FindClose(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindFirstFileW(string name, out FindData data);

    [DllImport("kernel32.dll")]
    internal static extern int GetACP();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeTag info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, byte[] buffer, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetFinalPathNameByHandleW(SafeFileHandle file, char[] path, int length, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern int GetPrivateProfileSectionA(string section, byte[] buffer, int size, string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetPrivateProfileSectionW(string section, char[] buffer, int size, string file);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern int GetPrivateProfileStringA(string section, string key, string fallback, byte[] buffer, int size, string path);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetUserDefaultGeoName([Out] char[] geoName, int geoNameCount);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr MapViewOfFile(SafeMemoryMappedFileHandle mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref uint info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref BasicInfo info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, IntPtr buffer, int size);

    [DllImport("kernel32.dll")]
    internal static extern uint SetThreadExecutionState(uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    internal static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32.dll")]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WritePrivateProfileStringA(string section, string key, string value, string file);
}

[StructLayout(LayoutKind.Sequential)]
internal struct SecurityAttributes
{
    public int Length;
    public IntPtr Descriptor;
    public int Inherit;
}

// Pack = 4: a FILETIME is two DWORDs, so there's no padding before the first one
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct ByHandleFileInformation
{
    public uint FileAttributes;
    public long CreationTime, LastAccessTime, LastWriteTime;
    public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
}

internal struct AttributeTag
{
    public uint Attributes;
    public uint ReparseTag;
}

[StructLayout(LayoutKind.Sequential, Size = 40)]
internal struct BasicInfo
{
    public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct FindData
{
    public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh, SizeHigh, SizeLow;
    public uint ReparseTag, Reserved;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string ShortName;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct StartupInfo
{
    public int Size;
    public string? Reserved;
    public string? Desktop;
    public string? Title;
    public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
    public short ShowWindow, ReservedSize;
    public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    public IntPtr Process, Thread;
    public int ProcessId, ThreadId;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ProcessEntry
{
    public uint Size, Usage, ProcessId;
    public IntPtr DefaultHeapId;
    public uint ModuleId, Threads, ParentProcessId;
    public int PriorityClassBase;
    public uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
}
