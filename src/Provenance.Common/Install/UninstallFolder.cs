using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Provenance.Common.Install;

// <root>\Uninstall holds what an elevated uninstall runs, and the root can be somewhere anyone can write to. So it gets
// its own permissions, made or fixed through a handle that's never a link, and it's held while it's written to
public sealed class UninstallFolder : IDisposable
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    // tests only: they don't run as admin, so nothing they make can be Administrators'
    internal static SecurityIdentifier? OwnerForTest;

    private static SecurityIdentifier Owner => OwnerForTest ?? Admins;

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
        var root = CreateFile(parent, ListDirectory | ReadAttributes | Synchronize, ShareReadWrite, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                              BackupSemantics, IntPtr.Zero);
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
            (h, status) = OpenAt(root, Path.GetFileName(full), HoldAccess, 3 /* FILE_OPEN_IF */, Intended());
            if (status != 0) throw new IOException($"Provenance couldn't make {full} ({status:X8}).");
            if (!PlainFolder(h) || Security(h) is not { } sd || !Trusted(sd.GetOwner(typeof(SecurityIdentifier))))
                throw Shown.Mark(new IOException($"{full} wasn't made by Provenance, so it can't hold the uninstall information. Delete or rename it, then try again."));
            if (!IsLocked(sd))
            {
                int failed = Write(h, Intended());
                if (failed != 0 || Security(h) is not { } now || !IsLocked(now))
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
        using (var f = CreateFile(folder, ReadControl | ReadAttributes, ShareAll, IntPtr.Zero, 3, BackupSemantics | OpenReparsePoint, IntPtr.Zero))
            if (f.IsInvalid || !PlainFolder(f) || Security(f) is not { } sd || !IsLocked(sd)) return false;
        using var r = CreateFile(record, ReadControl | ReadAttributes, ShareAll, IntPtr.Zero, 3, OpenReparsePoint, IntPtr.Zero);
        if (r.IsInvalid || !GetFileInformationByHandle(r, out var info) || (info.FileAttributes & (0x10 | 0x400)) != 0) return false;
        return Security(r) is { } rs && Trusted(rs.GetOwner(typeof(SecurityIdentifier))) && !Weak(rs);
    }

    private static bool Trusted(IdentityReference? owner) => owner is SecurityIdentifier s && (s == Owner || s == Admins || s == SystemSid);

    // anyone but the owner, Administrators and SYSTEM can do more than read it
    private static bool Weak(DirectorySecurity sd)
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

    private static DirectorySecurity Intended()
    {
        var sec = new DirectorySecurity();
        sec.SetOwner(Owner);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(Owner, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    private static bool IsLocked(DirectorySecurity current)
    {
        if (current.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != Owner || !current.AreAccessRulesProtected) return false;
        static HashSet<(string, FileSystemRights, InheritanceFlags, PropagationFlags, AccessControlType, bool)> Rules(DirectorySecurity s) =>
            s.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
             .Select(r => (r.IdentityReference.Value, r.FileSystemRights, r.InheritanceFlags, r.PropagationFlags, r.AccessControlType, r.IsInherited))
             .ToHashSet();
        var have = Rules(current);
        return have.SetEquals(Rules(Intended())) && current.GetAccessRules(true, true, typeof(SecurityIdentifier)).Count == have.Count;
    }

    private static bool PlainFolder(SafeFileHandle h) =>
        GetFileInformationByHandle(h, out var info) && (info.FileAttributes & 0x10) != 0 && (info.FileAttributes & 0x400) == 0;

    private static DirectorySecurity? Security(SafeFileHandle h)
    {
        GetKernelObjectSecurity(h, 0x1 | 0x4 /* OWNER, DACL */, null, 0, out uint need);
        if (need == 0) return null;
        var buf = new byte[need];
        if (!GetKernelObjectSecurity(h, 0x1 | 0x4, buf, need, out _)) return null;
        var sec = new DirectorySecurity();
        sec.SetSecurityDescriptorBinaryForm(buf, AccessControlSections.Owner | AccessControlSections.Access);
        return sec;
    }

    // Windows pushes it down to what's inside that inherits, and doesn't go through a link
    private static int Write(SafeFileHandle h, DirectorySecurity sec)
    {
        var raw = new RawSecurityDescriptor(sec.GetSecurityDescriptorBinaryForm(), 0);
        var owner = new byte[raw.Owner!.BinaryLength];
        raw.Owner.GetBinaryForm(owner, 0);
        var dacl = new byte[raw.DiscretionaryAcl!.BinaryLength];
        raw.DiscretionaryAcl.GetBinaryForm(dacl, 0);
        var pinOwner = GCHandle.Alloc(owner, GCHandleType.Pinned);
        var pinDacl = GCHandle.Alloc(dacl, GCHandleType.Pinned);
        try
        {
            return SetSecurityInfo(h, 1 /* SE_FILE_OBJECT */, 0x1 | 0x4 | 0x80000000 /* OWNER, DACL, PROTECTED_DACL */,
                                   pinOwner.AddrOfPinnedObject(), IntPtr.Zero, pinDacl.AddrOfPinnedObject(), IntPtr.Zero);
        }
        finally
        {
            pinOwner.Free();
            pinDacl.Free();
        }
    }

    // through the root's handle, so nothing above it can redirect it, and never through a link
    private static (SafeFileHandle Handle, uint Status) OpenAt(SafeFileHandle parent, string name, uint access, uint disposition, DirectorySecurity sec)
    {
        bool added = false;
        var text = Marshal.StringToHGlobalUni(name);
        var us = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var sd = sec.GetSecurityDescriptorBinaryForm();
        var pSd = Marshal.AllocHGlobal(sd.Length);
        try
        {
            Marshal.Copy(sd, 0, pSd, sd.Length);
            parent.DangerousAddRef(ref added);
            Marshal.StructureToPtr(new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = text }, us, false);
            var oa = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = us,
                Attributes = 0x40 /* OBJ_CASE_INSENSITIVE */, SecurityDescriptor = pSd,
            };
            uint status = NtCreateFile(out var h, access | Synchronize, ref oa, out _, IntPtr.Zero, 0, ShareReadWrite, disposition,
                                       0x1 | 0x00200000 | 0x20 /* DIRECTORY_FILE, OPEN_REPARSE_POINT, SYNCHRONOUS_IO_NONALERT */, IntPtr.Zero, 0);
            return (h, status);
        }
        finally
        {
            if (added) parent.DangerousRelease();
            Marshal.FreeHGlobal(pSd);
            Marshal.FreeHGlobal(us);
            Marshal.FreeHGlobal(text);
        }
    }

    private const uint ReadControl = 0x00020000, WriteDac = 0x00040000, WriteOwner = 0x00080000, Synchronize = 0x00100000;
    private const uint ListDirectory = 0x1, ReadAttributes = 0x80;
    private const uint HoldAccess = ReadControl | WriteDac | WriteOwner | ReadAttributes | ListDirectory;
    private const uint ShareReadWrite = 0x1 | 0x2, ShareAll = 0x1 | 0x2 | 0x4;   // no FILE_SHARE_DELETE: it can't be renamed while held
    private const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;

    // Pack = 4: a FILETIME is two DWORDs, so there's no padding before the first one
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory, ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status, Information;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint info, byte[]? sd, uint length, out uint needed);

    [DllImport("advapi32.dll")]
    private static extern int SetSecurityInfo(SafeFileHandle handle, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("ntdll.dll")]
    private static extern uint NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock io,
                                            IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
}
