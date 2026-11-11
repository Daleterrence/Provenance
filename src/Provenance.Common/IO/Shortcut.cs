using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Provenance.Common.Win32;

namespace Provenance.Common.IO;

internal static class Shortcut
{
    public static void Create(string lnk, string target, string args, string description, string workDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            if (args.Length > 0) link.SetArguments(args);
            if (description.Length > 0) link.SetDescription(description);
            if (workDir.Length > 0) link.SetWorkingDirectory(workDir);
            ((IPersistFile)link).Save(lnk, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    // the target as the file has it. Never Resolve, that can put up a window or go looking on the network
    internal static string? TargetOf(string lnk)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            var path = new StringBuilder(260);
            link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            var target = path.ToString();
            return Path.IsPathFullyQualified(target) ? target : null;
        }
        // whatever stops it loading, a file that isn't a shortcut included, there's no target to give
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (link is not null) Marshal.ReleaseComObject(link);
        }
    }
}
