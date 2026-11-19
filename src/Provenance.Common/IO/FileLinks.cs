using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Provenance.Common.IO;

public static class FileLinks
{
    public static int? Count(string path)
    {
        using var handle = FileHandles.OpenLinkItself(path, FileHandles.ReadAttributes, FileHandles.ShareAll);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 2 or 3) return null;
            throw new IOException($"{path} cannot be opened to count its names (Win32 error {error}).", error);
        }
        return Count(handle);
    }

    public static int Count(SafeFileHandle handle) =>
        FileHandles.TryInfo(handle, out var info)
            ? (int)info.NumberOfLinks
            : throw new IOException($"The file's names cannot be counted (Win32 error {Marshal.GetLastPInvokeError()}).");

    public static bool IsLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }
}
