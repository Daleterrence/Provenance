using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Provenance.Common.Security;
using static Provenance.Common.IO.FileHandles;

namespace Provenance.Common.Install;

// <root>\Uninstall holds what an elevated uninstall runs, and the root may be anyone's to write. So it gets its own
// permissions, set through a handle that's never a link, and is held while written
public sealed class UninstallFolder : IDisposable
{
    // tests only: they don't run as admin, so nothing they make can be Administrators'
    internal static SecurityIdentifier? OwnerForTest;

    private static SecurityIdentifier Owner => OwnerForTest ?? LockedFolder.Admins;

    private readonly SafeFileHandle _root, _folder;

    private UninstallFolder(SafeFileHandle root, SafeFileHandle folder)
    {
        _root = root;
        _folder = folder;
    }

    public void Dispose()
    {
        _folder.Dispose();
        _root.Dispose();
    }

    // made with its permissions in one go, or opened and fixed. Neither it nor the root can be renamed while it's held
    public static UninstallFolder Hold(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var parent = Path.GetDirectoryName(full) ?? throw new IOException($"{full} has no folder above it.");
        var root = Open(parent, ListDirectory | ReadAttributes | Synchronize, ShareReadWrite, BackupSemantics);
        if (root.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            root.Dispose();
            throw new IOException($"Provenance couldn't open {parent}.", new Win32Exception(error));
        }
        SafeFileHandle? h = null;
        try
        {
            uint status;
            (h, status) = LockedFolder.OpenAt(root, Path.GetFileName(full), LockedFolder.HoldAccess | ListDirectory, LockedFolder.DirectoryFile,
                                                 3 /* FILE_OPEN_IF */, Intended(), ShareReadWrite);
            if (status != 0) throw new IOException($"Provenance couldn't make {full} ({status:X8}).");
            if (!PlainFolder(h) || Security(h) is not { } sd || !Trusted(sd.GetOwner(typeof(SecurityIdentifier))))
                throw Shown.Mark(new IOException($"{full} wasn't made by Provenance, so it can't hold the uninstall information. Delete or rename it, then try again."));
            if (!IsLocked(sd))
            {
                if (!LockedFolder.Write(h, Intended(), out int failed) || Security(h) is not { } now || !IsLocked(now))
                    throw new IOException($"{full} couldn't be locked.", failed == 0 ? null : new Win32Exception(failed));
            }
            var held = new UninstallFolder(root, h);
            h = null;
            root = null!;
            return held;
        }
        finally
        {
            h?.Dispose();
            root?.Dispose();
        }
    }

    // a file just written in the held folder: Administrators' whatever this PC's policy says about new files. Only the
    // owner's read and written, a fresh FileSecurity would write an empty DACL and lock Administrators out
    public static void Own(string path)
    {
        var file = new FileInfo(path);
        var sec = file.GetAccessControl(AccessControlSections.Owner);
        sec.SetOwner(Owner);
        file.SetAccessControl(sec);
    }

    // the folder's exactly as Hold leaves it, and nobody else can change the record
    public static bool Protected(string folder, string record)
    {
        using (var f = OpenLinkItself(folder, ReadControl | ReadAttributes, ShareAll))
            if (f.IsInvalid || !PlainFolder(f) || Security(f) is not { } sd || !IsLocked(sd)) return false;
        using var r = Open(record, ReadControl | ReadAttributes, ShareAll, OpenReparsePoint);
        if (r.IsInvalid || !TryInfo(r, out var info) || (info.FileAttributes & (0x10 | 0x400)) != 0) return false;
        return Security(r) is { } rs && RecordOk(rs);
    }

    internal static bool RecordOk(DirectorySecurity rs) => Trusted(rs.GetOwner(typeof(SecurityIdentifier))) && !Weak(rs);

    internal static bool Trusted(IdentityReference? owner) => owner is SecurityIdentifier s && (s == Owner || LockedFolder.IsAdminOrSystem(s));

    // anyone but the owner, Administrators and SYSTEM can do more than read it
    // one file, and it has to line up with what the uninstaller refuses
    internal static bool Weak(DirectorySecurity sd)
    {
        const FileSystemRights read = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
        foreach (FileSystemAccessRule rule in sd.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            if (Trusted(rule.IdentityReference)) continue;
            if ((rule.FileSystemRights & ~read) != 0) return true;
        }
        return false;
    }

    internal static DirectorySecurity Intended() => LockedFolder.Intended(Owner, usersMayRead: true);

    internal static bool IsLocked(DirectorySecurity current) => LockedFolder.IsLocked(current, Owner, usersMayRead: true);

    private static bool PlainFolder(SafeFileHandle h) =>
        TryInfo(h, out var info) && (info.FileAttributes & 0x10) != 0 && (info.FileAttributes & 0x400) == 0;

    private static DirectorySecurity? Security(SafeFileHandle h) => LockedFolder.ReadSecurity(h).Security;
}
