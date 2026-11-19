using Provenance.Common.IO;
using Provenance.Common.Win32;

namespace Provenance.Common.Security;

// OneDrive, Dev Drive and the like use reparse points but don't redirect
public static class LinkCheck
{
    private const uint MountPoint = 0xA0000003, Symlink = 0xA000000C;

    public static bool IsRedirect(uint attributes, uint reparseTag) => ((FileAttributes)attributes).HasFlag(FileAttributes.ReparsePoint) && reparseTag is MountPoint or Symlink;

    public static bool IsRedirect(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.GetPathRoot(full) is { } root && Path.TrimEndingDirectorySeparator(root) == full) return false;
        var handle = Kernel32.FindFirstFileW(FileHandles.LongPath(full), out var data);
        if (handle == -1) return false;
        Kernel32.FindClose(handle);
        return IsRedirect(data.Attributes, data.ReparseTag);
    }

    public static void Refuse(params IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (IsRedirect(p)) throw FolderPins.LinkError(p);
    }
}
