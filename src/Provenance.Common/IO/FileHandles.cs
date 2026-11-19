using Microsoft.Win32.SafeHandles;
using Provenance.Common.Win32;

namespace Provenance.Common.IO;

internal static class FileHandles
{
    internal const uint ListDirectory = 0x1, ReadAttributes = 0x80, WriteAttributes = 0x100, Delete = 0x10000;
    internal const uint ReadControl = 0x00020000, WriteDac = 0x00040000, WriteOwner = 0x00080000, Synchronize = 0x00100000;
    internal const uint GenericRead = 0x80000000;
    internal const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4, ShareReadWrite = ShareRead | ShareWrite, ShareAll = ShareReadWrite | ShareDelete;
    internal const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    internal const uint LinkItself = BackupSemantics | OpenReparsePoint;
    private const uint OpenExisting = 3;

    internal static SafeFileHandle Open(string path, uint access, uint share, uint flags) =>
        Kernel32.CreateFileW(path, access, share, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);

    // FindFirstFileW stops at 260 characters without \\?\, and a link it can't see must not read as no link
    internal static string LongPath(string full)
    {
        if (full.Length < 248 || full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
            return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    internal static SafeFileHandle OpenLinkItself(string path, uint access, uint share) => Open(path, access, share, LinkItself);

    internal static bool TryInfo(SafeFileHandle h, out ByHandleFileInformation info) => Kernel32.GetFileInformationByHandle(h, out info);

    internal static string? FinalPath(SafeFileHandle h, bool allowUnc)
    {
        var buf = new char[1024];
        int n = Kernel32.GetFinalPathNameByHandleW(h, buf, buf.Length, 0);
        if (n >= buf.Length)
        {
            buf = new char[n];
            n = Kernel32.GetFinalPathNameByHandleW(h, buf, buf.Length, 0);
        }
        if (n <= 0 || n >= buf.Length) return null;
        var path = new string(buf, 0, n);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return allowUnc ? @"\\" + path[8..] : null;
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
}
