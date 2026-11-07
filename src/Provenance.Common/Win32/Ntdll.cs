using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Provenance.Common.Win32;

internal static class Ntdll
{
    [DllImport("ntdll.dll")]
    internal static extern uint NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock io,
                                             IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);

    [DllImport("ntdll.dll")]
    internal static extern uint NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock io, IntPtr info, int length, int infoClass);

    [DllImport("ntdll.dll")]
    internal static extern int RtlNtStatusToDosError(uint status);
}

[StructLayout(LayoutKind.Sequential)]
internal struct UnicodeString
{
    public ushort Length, MaximumLength;
    public IntPtr Buffer;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ObjectAttributes
{
    public int Length;
    public IntPtr RootDirectory, ObjectName;
    public uint Attributes;
    public IntPtr SecurityDescriptor, SecurityQualityOfService;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IoStatusBlock
{
    public IntPtr Status, Information;
}
