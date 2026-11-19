using System.Runtime.InteropServices;
using System.Security.Principal;
using Provenance.Common.Win32;
using Serilog;
using static Provenance.Common.Security.LockedFolder;

namespace Provenance.Common.Security;

// never %TEMP% or %APPDATA%, where a program could swap a file for a link
public static class ProtectedFolders
{
    private static readonly object Gate = new();
    private static string? _dir;
    private static string? _rootForTest;

    // Administrators' whatever the PC's policy says, "object creator" would make it the user's
    public static bool OwnNewFilesAsAdministrators() => SetTokenOwner(Admins);

    internal static bool SetTokenOwner(SecurityIdentifier owner)
    {
        if (!Advapi32.OpenProcessToken(new IntPtr(-1), 0x0080 | 0x0008 /* TOKEN_ADJUST_DEFAULT, TOKEN_QUERY */, out var token))
        {
            Log.Warning("Could not open this process's token to set the owner of new files ({Error})", Marshal.GetLastPInvokeError());
            return false;
        }
        using (token)
        {
            var sid = new byte[owner.BinaryLength];
            owner.GetBinaryForm(sid, 0);
            var pin = GCHandle.Alloc(sid, GCHandleType.Pinned);
            try
            {
                var tokenOwner = pin.AddrOfPinnedObject();
                if (Advapi32.SetTokenInformation(token, 4 /* TokenOwner */, ref tokenOwner, IntPtr.Size)) return true;
                Log.Warning("New files couldn't be made {Owner}'s ({Error})", owner.Value, Marshal.GetLastPInvokeError());
                return false;
            }
            finally { pin.Free(); }
        }
    }

    public static string IpcDir
    {
        get
        {
            lock (Gate) return _dir ??= Ensure();
        }
    }

    internal static void SetRootForTest(string? root)
    {
        lock (Gate)
        {
            _rootForTest = root;
            _dir = null;
            Walked.Clear();
        }
    }

    internal static void ForgetWalksForTest() => Walked.Clear();

    public static string PathFor(string name) => Path.Combine(IpcDir, name);

    public static string SubDir(string name)
    {
        var dir = Path.Combine(IpcDir, name);
        Lock(dir, usersMayRead: false, Path.Combine(Path.GetDirectoryName(IpcDir)!, BinName));
        return dir;
    }

    public static string RootSubDir(string name, bool usersMayRead = false)
    {
        var root = Path.GetDirectoryName(IpcDir)!;
        var dir = Path.Combine(root, name);
        Lock(dir, usersMayRead, Path.Combine(root, BinName));
        return dir;
    }

    private static string Ensure()
    {
        var root = _rootForTest ?? AppPaths.ProgramData;
        var bin = Path.Combine(root, BinName);
        var (held, _) = Hold(root, usersMayRead: true, bin: null);
        using (held!)
        {
            Hold(bin, usersMayRead: false, bin: null).Held!.Dispose();
            WalkHeld(held!, root, deep: false, bin);
        }
        var dir = Path.Combine(root, "ipc");
        Lock(dir, usersMayRead: false, bin);
        ClearLater(bin);
        return dir;
    }

    private static void Lock(string path, bool usersMayRead, string bin)
    {
        var (held, wrote) = Hold(path, usersMayRead, bin);
        using (held!)
            if (wrote || !Walked.ContainsKey(path)) WalkHeld(held!, path, deep: true, bin);
    }
}
