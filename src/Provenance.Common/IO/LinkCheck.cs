using System.Runtime.InteropServices;

namespace Provenance.Common.IO;

// Provenance runs as admin, so a junction or symlink where a standard user can write would send its writes elsewhere.
// OneDrive, Dev Drive and the like use reparse points too, but they don't redirect
public static class LinkCheck
{
    private const uint ReparsePoint = 0x400, MountPoint = 0xA0000003, Symlink = 0xA000000C;

    public static bool IsRedirect(uint attributes, uint reparseTag) => (attributes & ReparsePoint) != 0 && reparseTag is MountPoint or Symlink;

    // a drive root or something that isn't there isn't one
    public static bool IsRedirect(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.GetPathRoot(full) is { } root && Path.TrimEndingDirectorySeparator(root) == full) return false;
        var handle = FindFirstFileW(LongPath(full), out var data);
        if (handle == -1) return false;
        FindClose(handle);
        return IsRedirect(data.Attributes, data.ReparseTag);
    }

    public static void Refuse(params IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (IsRedirect(p)) throw Shown.Mark(new IOException($"Provenance won't write to {p}, it's a link to somewhere else."));
    }

    // FindFirstFileW stops at 260 characters without \\?\, and a link it can't see must not read as no link
    internal static string LongPath(string full)
    {
        if (full.Length < 248 || full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
            return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh, SizeHigh, SizeLow;
        public uint ReparseTag, Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string ShortName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindFirstFileW(string name, out FindData data);

    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr handle);
}
