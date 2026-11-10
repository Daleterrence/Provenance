using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Provenance.Common.IO;
using Provenance.Common.Win32;
using Serilog;
using static Provenance.Common.IO.FileHandles;

namespace Provenance.Common.Security;

// the permissions, handles and owner checks a locked folder's made of
internal static class LockedFolder
{
    internal static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    internal static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    internal static SecurityIdentifier? OwnerForTest;

    internal static bool IsAdminOrSystem(SecurityIdentifier sid) => sid == Admins || sid == SystemSid;

    private static SecurityIdentifier Owner => OwnerForTest ?? Admins;

    // what isn't Provenance's is moved in here with one rename and cleared later, never deleted where it was
    internal const string BinName = "quarantine";
    internal static readonly ConcurrentDictionary<string, byte> Walked = new(StringComparer.OrdinalIgnoreCase);
    private static int _permissionWrites;

    // tests only: the owner, who's trusted, and hooks at the moments something else could get in
    internal static Func<string, bool>? TrustedForTest;
    internal static Action<string>? BeforeCreateForTest;
    internal static Action<string>? AfterMoveForTest;
    internal static Action<string>? ListedForTest;
    internal static Action<string>? ClearingForTest;
    internal static Task LastClear = Task.CompletedTask;

    internal static int PermissionWrites => Volatile.Read(ref _permissionWrites);

    // made through the held folder's handle with its permissions in one go, or opened there, and kept if it's mine
    internal static SafeFileHandle? HoldOwn(SafeFileHandle parent, string path, string name, IReadOnlyCollection<string> ownNames)
    {
        var sec = Intended(usersMayRead: true);
        var (h, status) = OpenAt(parent, name, HoldAccess | ListDirectory, DirectoryFile, 3 /* FILE_OPEN_IF */, sec, ShareReadWrite);
        if (status != 0)
        {
            h.Dispose();
            return null;
        }
        bool keep = false;
        try
        {
            var seen = Look(h);
            if (!Ours(path, seen)) return null;
            if (!IsLocked(seen.Security!, usersMayRead: true))
            {
                Interlocked.Increment(ref _permissionWrites);
                if (!Write(h, sec, out int failed) || !IsLocked(Look(h).Security ?? new DirectorySecurity(), usersMayRead: true))
                    throw new IOException($"{path} couldn't be locked.", failed == 0 ? null : new Win32Exception(failed));
            }
            var bin = Path.Combine(path, BinName);
            WalkHeld(h, path, deep: true, bin, ownNames.ToHashSet(StringComparer.OrdinalIgnoreCase), shown: true);
            ClearLater(bin);
            keep = true;
            return h;
        }
        finally
        {
            if (!keep) h.Dispose();
        }
    }

    // the held handle asks for nothing sharing can refuse, so a program holding the folder can't stop a start. One that
    // can't be listed now is walked next start
    internal static void WalkHeld(SafeFileHandle held, string path, bool deep, string bin, ISet<string>? only = null, bool shown = false)
    {
        var (list, status) = OpenAt(held, "", ListDirectory | ReadAttributes, DirectoryFile);
        using (list)
        {
            if (status != 0)
            {
                Log.Warning("Couldn't list {Dir} ({Status:X8}), so it's checked next time", path, status);
                return;
            }
            Walk(list, path, deep, bin, only, shown);
        }
    }

    // a listing handle on the bin, which renames can go into
    private static SafeFileHandle OpenBin(string bin)
    {
        using var held = Hold(bin, usersMayRead: false, bin: null).Held!;
        var (list, status) = OpenAt(held, "", ListDirectory | ReadAttributes, DirectoryFile);
        if (status == 0) return list;
        list.Dispose();
        throw new IOException($"Provenance couldn't open {bin} ({status:X8}).");
    }

    // checked and locked through a handle that stops the folder being renamed or swapped while it's open. Anything else
    // at the path is moved aside and made again
    internal static (SafeFileHandle? Held, bool Wrote) Hold(string path, bool usersMayRead, string? bin)
    {
        var sec = Intended(usersMayRead);
        SafeFileHandle? held = null;
        Seen seen = default;
        for (int tries = 0; held is null; tries++)
        {
            if (tries == 4) throw new IOException($"Something else keeps making {path} before Provenance can.");
            var h = OpenLinkItself(path, HoldAccess, ShareReadWrite);
            if (h.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                h.Dispose();
                if (error is 2 or 3)
                {
                    BeforeCreateForTest?.Invoke(path);
                    Interlocked.Increment(ref _permissionWrites);
                    CreateLocked(path, sec);   // one something else made first is looked at on the next round, never taken
                    continue;
                }
                // an admin can always open its own folder like this, so access denied means it's someone else's
                if (error != 5) throw new IOException($"Provenance couldn't open {path}.", new Win32Exception(error));
            }
            else
            {
                seen = Look(h);
                if (Ours(path, seen)) { held = h; break; }
                h.Dispose();
            }
            MoveAside(path, bin);
            AfterMoveForTest?.Invoke(path);
        }
        // only when it isn't already exactly right (writing it pushes it down to everything inside), then read back
        if (IsLocked(seen.Security!, usersMayRead)) return (held, false);
        Interlocked.Increment(ref _permissionWrites);
        if (!Write(held, sec, out int failed) || !IsLocked(Look(held).Security ?? new DirectorySecurity(), usersMayRead))
        {
            held.Dispose();
            throw new IOException($"{path} couldn't be locked.", failed == 0 ? null : new Win32Exception(failed));
        }
        return (held, true);
    }

    private static bool Ours(string path, Seen seen) => seen.Read && seen.IsDir && !seen.IsLink && Trusted(path, seen.Owner) && !Weak(seen.Raw);

    // the root goes beside itself in ProgramData, everything else into the bin
    private static void MoveAside(string path, string? bin)
    {
        var h = OpenLinkItself(path, Delete | ReadControl | ReadAttributes | Synchronize, ShareAll);
        if (h.IsInvalid && Marshal.GetLastPInvokeError() == 5)
        {
            h.Dispose();
            h = OpenLinkItself(path, Delete | ReadAttributes | Synchronize, ShareAll);
        }
        using (h)
        {
            if (h.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is 2 or 3) return;
                throw new IOException($"{path} isn't Provenance's and another program has it open, so it can't be moved aside.", new Win32Exception(error));
            }
            // looked at again on the handle that moves it, so an admin's folder that took its place stays
            if (Ours(path, Look(h))) return;
            string to;
            int moveError;
            if (bin is null)
            {
                var name = Path.GetFileName(path) + ".untrusted-" + ShortId.New(8);
                to = Path.Combine(Path.GetDirectoryName(path)!, name);
                using var parent = OpenLinkItself(Path.GetDirectoryName(path)!, ListDirectory | Synchronize, ShareAll);
                moveError = parent.IsInvalid ? Marshal.GetLastPInvokeError() : Rename(h, parent, name);
            }
            else
            {
                var name = Guid.NewGuid().ToString("N");
                to = Path.Combine(bin, name);
                using var b = OpenBin(bin);
                moveError = Rename(h, b, name);
            }
            if (moveError != 0)
                throw new IOException($"{path} isn't Provenance's and couldn't be moved aside.", new Win32Exception(moveError));
            Log.Warning("{Path} was a link, a file or not made by an administrator, so it's moved to {Aside} and made again", path, to);
        }
    }

    // each entry's opened through its folder's handle, never through a link, and only looked at, so a file another
    // program has open with no sharing is fine
    private static void Walk(SafeFileHandle top, string topPath, bool deep, string bin, ISet<string>? only = null, bool shown = false)
    {
        var failed = new List<string>();
        var visited = new List<string>();
        int moved = 0;
        bool credKey = false;
        SafeFileHandle? binHandle = null;
        var queue = new Queue<(SafeFileHandle Handle, string Path)>();
        queue.Enqueue((top, topPath));
        try
        {
            while (queue.TryDequeue(out var dir))
            {
                try
                {
                    var names = List(dir.Handle);
                    ListedForTest?.Invoke(dir.Path);
                    foreach (var name in names)
                    {
                        if (only is not null && ReferenceEquals(dir.Handle, top) && !only.Contains(name)) continue;
                        var full = Path.Combine(dir.Path, name);
                        if (string.Equals(full, bin, StringComparison.OrdinalIgnoreCase)) continue;
                        var (entry, status) = OpenAt(dir.Handle, name, LookAccess, 0);
                        using (entry)
                        {
                            if (Gone(status)) continue;
                            if (status == 0)
                            {
                                var seen = Look(entry);
                                if (!Bad(full, seen))
                                {
                                    if (deep && seen.IsDir && OpenDirAt(dir.Handle, name, full, seen) is { } sub) queue.Enqueue((sub, full));
                                    continue;
                                }
                            }
                            else if (status != StatusAccessDenied)
                            {
                                Log.Warning("Couldn't look at {Item} ({Status:X8}), so it's left alone", full, status);
                                continue;
                            }
                        }
                        switch (MoveToBin(dir.Handle, name, full, bin, ref binHandle))
                        {
                            case Moved.Yes:
                                moved++;
                                credKey |= name.Equals("cred.key", StringComparison.OrdinalIgnoreCase);
                                break;
                            case Moved.InUse:
                                failed.Add(full);
                                break;
                        }
                    }
                    visited.Add(dir.Path);
                }
                finally
                {
                    if (!ReferenceEquals(dir.Handle, top)) dir.Handle.Dispose();
                }
            }
        }
        finally
        {
            binHandle?.Dispose();
            while (queue.TryDequeue(out var left))
                if (!ReferenceEquals(left.Handle, top)) left.Handle.Dispose();
        }
        if (moved > 0)
            Log.Warning("{Count} item(s) in {Dir} weren't Provenance's and were moved to {Bin}{Note}", moved, topPath, bin,
                        credKey ? " (the saved logins' key was one of them, so they need entering again)" : "");
        if (failed.Count > 0)
        {
            var e = new IOException($"Provenance couldn't move {failed[0]}" + (failed.Count > 1 ? $" and {failed.Count - 1} more" : "") +
                                    " out of the way. Close any program that's using it, then press Try again.");
            throw shown ? Provenance.Common.Shown.Mark(e) : e;
        }
        if (only is null) foreach (var v in visited) Walked[v] = 0;
    }

    // links, hard-linked files, and an owner that isn't an admin or can't be read (an admin's own always can). A file
    // whose details can't be read at all is left alone
    private static bool Bad(string path, Seen seen) =>
        seen.Read && (seen.IsLink || !seen.ReadSecurity || !Trusted(path, seen.Owner) || (!seen.IsDir && seen.Links != 1));

    private static SafeFileHandle? OpenDirAt(SafeFileHandle parent, string name, string full, Seen seen)
    {
        var (sub, status) = OpenAt(parent, name, ListDirectory | LookAccess, DirectoryFile);
        if (status == 0)
        {
            var now = Look(sub);
            if (now.Read && now.Id == seen.Id && !Bad(full, now)) return sub;
        }
        sub.Dispose();
        if (!Gone(status)) Log.Warning("Couldn't look inside {Item} ({Status:X8}), so it's left alone", full, status);
        return null;
    }

    private enum Moved { Yes, Gone, InUse }

    private static Moved MoveToBin(SafeFileHandle parent, string name, string full, string bin, ref SafeFileHandle? binHandle)
    {
        var (h, status) = OpenAt(parent, name, Delete | LookAccess, 0);
        if (status == StatusAccessDenied)
        {
            h.Dispose();
            (h, status) = OpenAt(parent, name, Delete | ReadAttributes, 0);
        }
        using (h)
        {
            if (Gone(status)) return Moved.Gone;
            if (status != 0)
            {
                Log.Warning("{Item} isn't Provenance's and another program has it open, so it can't be moved ({Status:X8})", full, status);
                return Moved.InUse;
            }
            // looked at again on the handle that moves it, so one of Provenance's own put there since stays
            if (!Bad(full, Look(h))) return Moved.Gone;
            binHandle ??= OpenBin(bin);
            var to = Guid.NewGuid().ToString("N");
            int error = Rename(h, binHandle, to);
            if (error != 0)
            {
                Log.Warning("{Item} isn't Provenance's and couldn't be moved ({Error})", full, error);
                return Moved.InUse;
            }
            Log.Warning("{Item} in Provenance's folder was a link or wasn't made by an administrator, so it's moved to {To}", full, Path.Combine(bin, to));
            return Moved.Yes;
        }
    }

    internal static void ClearLater(string bin) =>
        LastClear = Task.Run(() =>
        {
            try { ClearBin(bin); }
            catch (Exception e) { Log.Warning(e, "Could not clear {Bin}", bin); }
        });

    // a folder's entries come up into the bin with one rename each, so a deep tree never recurses and a link only
    // ever goes itself
    internal static int ClearBin(string bin)
    {
        using var held = OpenBin(bin);
        int budget = 200_000;
        for (bool progress = true; progress && budget > 0;)
        {
            progress = false;
            var queue = new Queue<string>(List(held));
            if (queue.Count == 0) return 0;
            while (budget-- > 0 && queue.TryDequeue(out var name))
            {
                ClearingForTest?.Invoke(Path.Combine(bin, name));
                var (h, status) = OpenAt(held, name, Delete | ReadAttributes, 0);
                using (h)
                {
                    if (status != 0 || !TryInfo(h, out var info)) continue;
                    if ((info.FileAttributes & 0x10) != 0 && (info.FileAttributes & 0x400) == 0)
                    {
                        var (list, ls) = OpenAt(held, name, ListDirectory | ReadAttributes, DirectoryFile);
                        using (list)
                            if (ls == 0)
                                foreach (var kid in List(list))
                                {
                                    ClearingForTest?.Invoke(Path.Combine(bin, name, kid));
                                    var (k, ks) = OpenAt(list, kid, Delete | ReadAttributes, 0);
                                    using (k)
                                    {
                                        if (ks != 0) continue;
                                        var to = Guid.NewGuid().ToString("N");
                                        if (Rename(k, held, to) == 0) { queue.Enqueue(to); progress = true; }
                                        else if (Remove(k)) progress = true;
                                    }
                                }
                    }
                    if (Remove(h)) progress = true;
                }
            }
        }
        int left = List(held).Count;
        if (left > 0) Log.Information("{Count} item(s) in {Bin} couldn't be removed yet, so they're tried again at the next start", left, bin);
        return left;
    }

    // POSIX delete takes the name away at once, and read-only doesn't stop it
    private static bool Remove(SafeFileHandle h)
    {
        var buf = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buf, 0x1 | 0x2 | 0x10);   // DELETE, POSIX_SEMANTICS, IGNORE_READONLY_ATTRIBUTE
            if (Kernel32.SetFileInformationByHandle(h, 21 /* FileDispositionInfoEx */, buf, 4)) return true;
            Marshal.WriteInt32(buf, 1);
            return Kernel32.SetFileInformationByHandle(h, 4 /* FileDispositionInfo */, buf, 4);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    internal static bool Trusted(string path, SecurityIdentifier? owner) =>
        TrustedForTest is { } fake ? fake(path) : owner is not null && IsAdminOrSystem(owner);

    // anyone but Administrators, SYSTEM and the owner can do more than read it, so a program may already have it
    // open to write
    // inherit-only rules count, they're what its files get
    internal static bool Weak(byte[]? sd)
    {
        if (sd is null) return true;
        if (new RawSecurityDescriptor(sd, 0).DiscretionaryAcl is not { } acl) return true;   // no DACL lets anyone in
        const int readOnly = 0x001200A9 | unchecked((int)0x80000000) | 0x20000000;   // read and execute, GENERIC_READ, GENERIC_EXECUTE
        foreach (GenericAce ace in acl)
        {
            if (ace is not QualifiedAce q) return true;
            if (q.AceQualifier != AceQualifier.AccessAllowed) continue;
            var sid = q.SecurityIdentifier;
            if (sid == Owner || sid == Admins || sid == SystemSid || sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid) || sid.Value == "S-1-3-4") continue;
            if ((q.AccessMask & ~readOnly) != 0) return true;
        }
        return false;
    }

    private static List<string> List(SafeFileHandle dir)
    {
        var names = new List<string>();
        var buf = new byte[64 * 1024];
        for (bool first = true; Kernel32.GetFileInformationByHandleEx(dir, first ? 15 : 14 /* FileFullDirectory(Restart)Info */, buf, buf.Length); first = false)
        {
            for (int off = 0; ;)
            {
                int next = BitConverter.ToInt32(buf, off);
                int length = BitConverter.ToInt32(buf, off + 60);
                var name = Encoding.Unicode.GetString(buf, off + 68, length);
                if (name is not "." and not "..") names.Add(name);
                if (next == 0) break;
                off += next;
            }
        }
        return names;
    }

    // straight to NtSetInformationFile, as SetFileInformationByHandle refuses a folder handle to rename into
    private static int Rename(SafeFileHandle h, SafeFileHandle dir, string name)
    {
        int offRoot = IntPtr.Size, offLength = offRoot + IntPtr.Size, offName = offLength + 4;
        int size = offName + (name.Length + 1) * 2;
        var buf = Marshal.AllocHGlobal(size);
        bool added = false;
        try
        {
            Marshal.Copy(new byte[size], 0, buf, size);
            dir.DangerousAddRef(ref added);
            Marshal.WriteIntPtr(buf, offRoot, dir.DangerousGetHandle());
            Marshal.WriteInt32(buf, offLength, name.Length * 2);
            Marshal.Copy(name.ToCharArray(), 0, buf + offName, name.Length);
            uint status = Ntdll.NtSetInformationFile(h, out _, buf, size, 10 /* FileRenameInformation */);
            return status == 0 ? 0 : Ntdll.RtlNtStatusToDosError(status);
        }
        finally
        {
            if (added) dir.DangerousRelease();
            Marshal.FreeHGlobal(buf);
        }
    }

    private static bool Gone(uint status) => status is 0xC0000034 or 0xC000003A or 0xC0000056 or 0xC0000123;   // not found, path not found, delete pending, deleted


    internal record struct Seen(bool Read, bool IsDir, bool IsLink, int Links, (uint, uint, uint) Id, byte[]? Raw, DirectorySecurity? Security, SecurityIdentifier? Owner)
    {
        public bool ReadSecurity => Raw is not null;
    }

    internal static (byte[]? Raw, DirectorySecurity? Security) ReadSecurity(SafeFileHandle h)
    {
        byte[]? raw = null;
        Advapi32.GetKernelObjectSecurity(h, 0x1 | 0x4 /* OWNER, DACL */, null, 0, out uint need);
        if (need > 0)
        {
            var buf = new byte[need];
            if (Advapi32.GetKernelObjectSecurity(h, 0x1 | 0x4, buf, need, out _)) raw = buf;
        }
        DirectorySecurity? sec = null;
        if (raw is not null)
        {
            sec = new DirectorySecurity();
            sec.SetSecurityDescriptorBinaryForm(raw, AccessControlSections.Owner | AccessControlSections.Access);
        }
        return (raw, sec);
    }

    internal static Seen Look(SafeFileHandle h)
    {
        if (!TryInfo(h, out var info)) return default;
        var (raw, sec) = ReadSecurity(h);
        return new Seen(true, (info.FileAttributes & 0x10) != 0, (info.FileAttributes & 0x400) != 0, (int)info.NumberOfLinks,
                        (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow), raw, sec,
                        sec?.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier);
    }

    // Windows pushes it down to what's inside, and doesn't go through a link
    internal static bool Write(SafeFileHandle h, DirectorySecurity sec, out int error)
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
            error = Advapi32.SetSecurityInfo(h, 1 /* SE_FILE_OBJECT */, 0x1 | 0x4 | 0x80000000 /* OWNER, DACL, PROTECTED_DACL */,
                                             pinOwner.AddrOfPinnedObject(), IntPtr.Zero, pinDacl.AddrOfPinnedObject(), IntPtr.Zero);
            return error == 0;
        }
        finally
        {
            pinOwner.Free();
            pinDacl.Free();
        }
    }

    // opened through the folder's handle, so what's above it can't redirect it, and never through a link
    internal static (SafeFileHandle Handle, uint Status) OpenAt(SafeFileHandle parent, string name, uint access, uint options,
                                                               uint disposition = 1 /* FILE_OPEN */, DirectorySecurity? sec = null, uint share = ShareAll)
    {
        bool added = false;
        var text = Marshal.StringToHGlobalUni(name);
        var us = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var sd = sec?.GetSecurityDescriptorBinaryForm();
        var pSd = sd is null ? IntPtr.Zero : Marshal.AllocHGlobal(sd.Length);
        try
        {
            if (sd is not null) Marshal.Copy(sd, 0, pSd, sd.Length);
            parent.DangerousAddRef(ref added);
            Marshal.StructureToPtr(new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = text }, us, false);
            var oa = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = us, Attributes = 0x40 /* OBJ_CASE_INSENSITIVE */,
                SecurityDescriptor = pSd,
            };
            uint status = Ntdll.NtCreateFile(out var h, access | Synchronize, ref oa, out _, IntPtr.Zero, 0, share, disposition,
                                             options | 0x00200000 | 0x20 /* OPEN_REPARSE_POINT, SYNCHRONOUS_IO_NONALERT */, IntPtr.Zero, 0);
            return (h, status);
        }
        finally
        {
            if (added) parent.DangerousRelease();
            if (pSd != IntPtr.Zero) Marshal.FreeHGlobal(pSd);
            Marshal.FreeHGlobal(us);
            Marshal.FreeHGlobal(text);
        }
    }

    // false when something else made it first, which is never taken as this one
    internal static bool CreateLocked(string path, DirectorySecurity sec)
    {
        var sd = sec.GetSecurityDescriptorBinaryForm();
        var pSd = Marshal.AllocHGlobal(sd.Length);
        try
        {
            Marshal.Copy(sd, 0, pSd, sd.Length);
            var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pSd };
            if (Kernel32.CreateDirectoryW(path, in sa)) return true;
            int error = Marshal.GetLastPInvokeError();
            if (error == 183) return false;   // ERROR_ALREADY_EXISTS
            throw new IOException($"Couldn't make {path}.", new Win32Exception(error));
        }
        finally { Marshal.FreeHGlobal(pSd); }
    }

    internal const uint DirectoryFile = 0x1;
    private const uint LookAccess = ReadControl | ReadAttributes;
    internal const uint HoldAccess = ReadControl | WriteDac | WriteOwner | ReadAttributes | Synchronize;   // none of it is share-checked
    private const uint StatusAccessDenied = 0xC0000022;

    internal static DirectorySecurity Intended(bool usersMayRead) => Intended(Owner, usersMayRead);

    internal static DirectorySecurity Intended(SecurityIdentifier owner, bool usersMayRead)
    {
        var sec = new DirectorySecurity();
        sec.SetOwner(owner);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        if (usersMayRead)
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    // exactly Intended or it gets locked again
    internal static bool IsLocked(DirectorySecurity current, bool usersMayRead) => IsLocked(current, Owner, usersMayRead);

    internal static bool IsLocked(DirectorySecurity current, SecurityIdentifier owner, bool usersMayRead)
    {
        if (current.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier actual || actual != owner) return false;
        if (!current.AreAccessRulesProtected) return false;
        static List<(string, FileSystemRights, InheritanceFlags, PropagationFlags, AccessControlType, bool)> Rules(DirectorySecurity s) =>
            s.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
             .Cast<FileSystemAccessRule>()
             .Select(r => (r.IdentityReference.Value, r.FileSystemRights, r.InheritanceFlags, r.PropagationFlags, r.AccessControlType, r.IsInherited))
             .ToList();
        var have = Rules(current);
        var want = Rules(Intended(owner, usersMayRead));
        return have.Count == want.Count && have.ToHashSet().SetEquals(want);
    }
}
