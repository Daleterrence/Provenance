using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace Provenance.Wpf.Services;

// C:\ProgramData\Provenance\ipc, only Administrators and SYSTEM can open it. Never %TEMP% or %APPDATA%, where a
// program could swap a file for a link and get written through
public static class SecureFiles
{
    // what isn't Provenance's is moved in here with one rename and cleared later, never deleted where it was
    private const string BinName = "quarantine";
    private static readonly object Gate = new();
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly ConcurrentDictionary<string, byte> Walked = new(StringComparer.OrdinalIgnoreCase);
    private static string? _dir;
    private static string? _rootForTest;   // under Gate
    private static int _permissionWrites;

    // tests only: the owner, who's trusted, and hooks at the moments something else could get in
    internal static SecurityIdentifier? OwnerForTest;
    internal static Func<string, bool>? TrustedForTest;
    internal static Action<string>? BeforeCreateForTest;
    internal static Action<string>? AfterMoveForTest;
    internal static Action<string>? ListedForTest;
    internal static Action<string>? ClearingForTest;
    internal static Action<string>? HeldForTest;
    internal static Task LastClear = Task.CompletedTask;

    private static SecurityIdentifier Owner => OwnerForTest ?? Admins;

    // what this process and its programs make is Administrators' whatever the PC's policy says ("object creator"
    // would make it the user's, and the check would move it out)
    public static bool OwnNewFilesAsAdministrators() => SetTokenOwner(Admins);

    internal static bool SetTokenOwner(SecurityIdentifier owner)
    {
        if (!OpenProcessToken(new IntPtr(-1), 0x0080 | 0x0008 /* TOKEN_ADJUST_DEFAULT, TOKEN_QUERY */, out var token))
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
                var tokenOwner = pin.AddrOfPinnedObject();   // TOKEN_OWNER is just the SID pointer
                if (SetTokenInformation(token, 4 /* TokenOwner */, ref tokenOwner, IntPtr.Size)) return true;
                Log.Warning("New files couldn't be made {Owner}'s ({Error})", owner.Value, Marshal.GetLastPInvokeError());
                return false;
            }
            finally { pin.Free(); }
        }
    }

    public static string Dir
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

    internal static int PermissionWrites => Volatile.Read(ref _permissionWrites);

    public static string PathFor(string name) => Path.Combine(Dir, name);

    public static string SubDir(string name)
    {
        var dir = Path.Combine(Dir, name);
        Lock(dir, usersMayRead: false, Path.Combine(Path.GetDirectoryName(Dir)!, BinName));
        return dir;
    }

    // beside ipc rather than in it, for files that are nothing to do with the hook
    public static string RootSubDir(string name, bool usersMayRead = false)
    {
        var root = Path.GetDirectoryName(Dir)!;
        var dir = Path.Combine(root, name);
        Lock(dir, usersMayRead, Path.Combine(root, BinName));
        return dir;
    }

    public sealed class FolderRefusedException(string message) : IOException(message);

    // the installer's download folder and the ffxi-download in it, held with no delete sharing until it's disposed
    public sealed class WorkFolder(string path, SafeFileHandle chosen, SafeFileHandle work) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            work.Dispose();
            chosen.Dispose();
        }
    }

    // DXSETUP and the MSIs run as admin from here, so after the check nothing goes by name: NTFS won't rename a folder
    // that's held, or one above it. Null when every ffxi-download name there is someone else's
    public static WorkFolder? HoldWorkFolder(string chosen, IReadOnlyCollection<string> ownNames, Func<string, string?> problem)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosen));
        var missing = new Stack<string>();
        var existing = full;
        for (; !Directory.Exists(existing); existing = Path.GetDirectoryName(existing) ?? throw new FolderRefusedException($"{full} has no drive"))
            missing.Push(existing);
        // checked before anything's made, and what's made is locked, so it never inherits anyone's write access
        if (problem(existing) is { } before) throw new FolderRefusedException(before);
        while (missing.TryPop(out var dir)) CreateLocked(dir, Intended(usersMayRead: true));
        // listing access so sharing applies, and read sharing only: a program that has it open to write or delete fails
        // this, and none can open it that way later
        var held = CreateFile(full, ListDirectory | ReadControl | ReadAttributes | Synchronize, 0x1, IntPtr.Zero, 3, OpenLinkItself, IntPtr.Zero);
        if (held.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            held.Dispose();
            if (error == 32)
                throw Provenance.Common.Shown.Mark(new IOException($"Another program has {full} open. Close it, then press Try again."));
            throw new FolderRefusedException($"{full} couldn't be opened ({error})");
        }
        bool keep = false;
        try
        {
            HeldForTest?.Invoke(full);
            var seen = Look(held);
            if (!seen.Read || !seen.IsDir || seen.IsLink) throw new FolderRefusedException($"{full} is a link or not a folder");
            if (RealPath(held) is not { } real || !string.Equals(real, full, StringComparison.OrdinalIgnoreCase))
                throw new FolderRefusedException($"{full} is really {RealPath(held) ?? "somewhere it can't tell"}");
            if (problem(full) is { } why) throw new FolderRefusedException(why);
            foreach (var name in Enumerable.Range(1, 9).Select(i => i == 1 ? "ffxi-download" : $"ffxi-download-{i}"))
            {
                var path = Path.Combine(full, name);
                if (HoldOwn(held, path, name, ownNames) is not { } work)
                {
                    Log.Warning("{Folder} was not made by Provenance; it is left alone", path);
                    continue;
                }
                keep = true;
                return new WorkFolder(path, held, work);
            }
            return null;
        }
        finally
        {
            if (!keep) held.Dispose();
        }
    }

    // made through the held folder's handle with its permissions in one go, or opened there, and only kept when it's ours
    private static SafeFileHandle? HoldOwn(SafeFileHandle parent, string path, string name, IReadOnlyCollection<string> ownNames)
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

    private static string Ensure()
    {
        var root = _rootForTest ??
                   Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Provenance");
        var bin = Path.Combine(root, BinName);
        // any user can make folders in ProgramData, so a "Provenance" some other program made gets moved aside first
        var (held, _) = Hold(root, usersMayRead: true, bin: null);
        using (held!)
        {
            Hold(bin, usersMayRead: false, bin: null).Held!.Dispose();
            // only what's at the top: each of its folders is walked when it's first used
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
        // once per run, unless its permissions had to be written just now
        using (held!)
            if (wrote || !Walked.ContainsKey(path)) WalkHeld(held!, path, deep: true, bin);
    }

    // the held handle asks for nothing sharing can refuse, so a program holding the folder can't stop a start. One that
    // can't be listed now is walked next start
    private static void WalkHeld(SafeFileHandle held, string path, bool deep, string bin, ISet<string>? only = null, bool shown = false)
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
    private static (SafeFileHandle? Held, bool Wrote) Hold(string path, bool usersMayRead, string? bin)
    {
        var sec = Intended(usersMayRead);
        SafeFileHandle? held = null;
        Seen seen = default;
        for (int tries = 0; held is null; tries++)
        {
            if (tries == 4) throw new IOException($"Something else keeps making {path} before Provenance can.");
            var h = CreateFile(path, HoldAccess, ShareReadWrite, IntPtr.Zero, 3 /* OPEN_EXISTING */, OpenLinkItself, IntPtr.Zero);
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
        var h = CreateFile(path, Delete | ReadControl | ReadAttributes | Synchronize, ShareAll, IntPtr.Zero, 3, OpenLinkItself, IntPtr.Zero);
        if (h.IsInvalid && Marshal.GetLastPInvokeError() == 5)
        {
            h.Dispose();
            h = CreateFile(path, Delete | ReadAttributes | Synchronize, ShareAll, IntPtr.Zero, 3, OpenLinkItself, IntPtr.Zero);
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
                var name = Path.GetFileName(path) + ".untrusted-" + Guid.NewGuid().ToString("N")[..8];
                to = Path.Combine(Path.GetDirectoryName(path)!, name);
                using var parent = CreateFile(Path.GetDirectoryName(path)!, ListDirectory | Synchronize, ShareAll, IntPtr.Zero, 3, OpenLinkItself, IntPtr.Zero);
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

    private static void ClearLater(string bin) =>
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
        {
            int budget = 200_000;
            for (int attempt = 0; ; attempt++)
            {
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
                            if (status != 0 || !GetFileInformationByHandle(h, out var info)) continue;
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
                if (left == 0) return 0;
                if (attempt == 3 || budget <= 0)
                {
                    Log.Information("{Count} item(s) in {Bin} couldn't be removed yet, so they're tried again at the next start", left, bin);
                    return left;
                }
                Thread.Sleep(500);   // a virus scanner can have a file in there open for a moment
            }
        }
    }

    // POSIX delete takes the name away at once, and read-only doesn't stop it
    private static bool Remove(SafeFileHandle h)
    {
        var buf = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buf, 0x1 | 0x2 | 0x10);   // DELETE, POSIX_SEMANTICS, IGNORE_READONLY_ATTRIBUTE
            if (SetFileInformationByHandle(h, 21 /* FileDispositionInfoEx */, buf, 4)) return true;
            Marshal.WriteInt32(buf, 1);
            return SetFileInformationByHandle(h, 4 /* FileDispositionInfo */, buf, 4);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool Trusted(string path, SecurityIdentifier? owner) =>
        TrustedForTest is { } fake ? fake(path) : owner is not null && (owner == Admins || owner == SystemSid);

    // anyone but Administrators, SYSTEM and the owner can do more than read it, so a program may already have it
    // open to write
    private static bool Weak(byte[]? sd)
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

    private record struct Seen(bool Read, bool IsDir, bool IsLink, int Links, (uint, uint, uint) Id, byte[]? Raw, DirectorySecurity? Security, SecurityIdentifier? Owner)
    {
        public bool ReadSecurity => Raw is not null;
    }

    private static Seen Look(SafeFileHandle h)
    {
        if (!GetFileInformationByHandle(h, out var info)) return default;
        byte[]? raw = null;
        GetKernelObjectSecurity(h, 0x1 | 0x4 /* OWNER, DACL */, null, 0, out uint need);
        if (need > 0)
        {
            var buf = new byte[need];
            if (GetKernelObjectSecurity(h, 0x1 | 0x4, buf, need, out _)) raw = buf;
        }
        DirectorySecurity? sec = null;
        if (raw is not null)
        {
            sec = new DirectorySecurity();
            sec.SetSecurityDescriptorBinaryForm(raw, AccessControlSections.Owner | AccessControlSections.Access);
        }
        return new Seen(true, (info.FileAttributes & 0x10) != 0, (info.FileAttributes & 0x400) != 0, (int)info.NumberOfLinks,
                        (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow), raw, sec,
                        sec?.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier);
    }

    // Windows pushes it down to what's inside, and doesn't go through a link
    private static bool Write(SafeFileHandle h, DirectorySecurity sec, out int error)
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
            error = SetSecurityInfo(h, 1 /* SE_FILE_OBJECT */, 0x1 | 0x4 | 0x80000000 /* OWNER, DACL, PROTECTED_DACL */,
                                    pinOwner.AddrOfPinnedObject(), IntPtr.Zero, pinDacl.AddrOfPinnedObject(), IntPtr.Zero);
            return error == 0;
        }
        finally
        {
            pinOwner.Free();
            pinDacl.Free();
        }
    }

    private static List<string> List(SafeFileHandle dir)
    {
        var names = new List<string>();
        var buf = new byte[64 * 1024];
        for (bool first = true; GetFileInformationByHandleEx(dir, first ? 15 : 14 /* FileFullDirectory(Restart)Info */, buf, buf.Length); first = false)
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

    // opened through the folder's handle, so what's above it can't redirect it, and never through a link
    private static (SafeFileHandle Handle, uint Status) OpenAt(SafeFileHandle parent, string name, uint access, uint options,
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
            uint status = NtCreateFile(out var h, access | Synchronize, ref oa, out _, IntPtr.Zero, 0, share, disposition,
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
            uint status = NtSetInformationFile(h, out _, buf, size, 10 /* FileRenameInformation */);
            return status == 0 ? 0 : RtlNtStatusToDosError(status);
        }
        finally
        {
            if (added) dir.DangerousRelease();
            Marshal.FreeHGlobal(buf);
        }
    }

    // where the handle really is, with every link on the way followed, in the form GetFullPath gives
    private static string? RealPath(SafeFileHandle h)
    {
        var buf = new char[32_768];
        int n = GetFinalPathNameByHandle(h, buf, buf.Length, 0);
        if (n <= 0 || n >= buf.Length) return null;
        var path = new string(buf, 0, n);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return null;
        return Path.TrimEndingDirectorySeparator(path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path);
    }

    private static bool Gone(uint status) => status is 0xC0000034 or 0xC000003A or 0xC0000056 or 0xC0000123;   // not found, path not found, delete pending, deleted

    // how many names the file has (hard links), read without following a link. Null when it can't be told
    internal static int? Names(string path)
    {
        using var handle = CreateFile(path, ReadAttributes, ShareAll, IntPtr.Zero, 3, OpenLinkItself, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return null;
        return (int)info.NumberOfLinks;
    }

    // false when something else made it first, which is never taken as this one
    private static bool CreateLocked(string path, DirectorySecurity sec)
    {
        var sd = sec.GetSecurityDescriptorBinaryForm();
        var pSd = Marshal.AllocHGlobal(sd.Length);
        try
        {
            Marshal.Copy(sd, 0, pSd, sd.Length);
            var sa = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), lpSecurityDescriptor = pSd };
            if (CreateDirectory(path, ref sa)) return true;
            int error = Marshal.GetLastPInvokeError();
            if (error == 183) return false;   // ERROR_ALREADY_EXISTS
            throw new IOException($"Couldn't make {path}.", new Win32Exception(error));
        }
        finally { Marshal.FreeHGlobal(pSd); }
    }

    private const uint Delete = 0x00010000, ReadControl = 0x00020000, WriteDac = 0x00040000, WriteOwner = 0x00080000, Synchronize = 0x00100000;
    private const uint ListDirectory = 0x1, ReadAttributes = 0x80, DirectoryFile = 0x1;
    private const uint LookAccess = ReadControl | ReadAttributes;
    private const uint HoldAccess = ReadControl | WriteDac | WriteOwner | ReadAttributes | Synchronize;   // none of it is share-checked
    private const uint ShareReadWrite = 0x1 | 0x2, ShareAll = 0x1 | 0x2 | 0x4;   // no FILE_SHARE_DELETE: it can't be renamed while held
    private const uint OpenLinkItself = 0x00200000 | 0x02000000;   // FILE_FLAG_OPEN_REPARSE_POINT, FILE_FLAG_BACKUP_SEMANTICS
    private const uint StatusAccessDenied = 0xC0000022;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

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

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int infoClass, ref IntPtr info, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")]
    private static extern bool CreateDirectory(string path, ref SecurityAttributes security);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern int GetFinalPathNameByHandle(SafeFileHandle handle, char[] path, int length, int flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, byte[] buffer, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, IntPtr buffer, int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint info, byte[]? sd, uint length, out uint needed);

    [DllImport("advapi32.dll")]
    private static extern int SetSecurityInfo(SafeFileHandle handle, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("ntdll.dll")]
    private static extern uint NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock io, IntPtr info, int length, int infoClass);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(uint status);

    [DllImport("ntdll.dll")]
    private static extern uint NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock io,
                                            IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);

    internal static DirectorySecurity Intended(bool usersMayRead)
    {
        var sec = new DirectorySecurity();
        sec.SetOwner(Owner);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(Owner, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        if (usersMayRead)
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    // exactly Intended or it gets locked again
    internal static bool IsLocked(DirectorySecurity current, bool usersMayRead)
    {
        if (current.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != Owner) return false;
        if (!current.AreAccessRulesProtected) return false;
        static List<(string, FileSystemRights, InheritanceFlags, PropagationFlags, AccessControlType, bool)> Rules(DirectorySecurity s) =>
            s.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
             .Cast<FileSystemAccessRule>()
             .Select(r => (r.IdentityReference.Value, r.FileSystemRights, r.InheritanceFlags, r.PropagationFlags, r.AccessControlType, r.IsInherited))
             .ToList();
        var have = Rules(current);
        var want = Rules(Intended(usersMayRead));
        return have.Count == want.Count && have.ToHashSet().SetEquals(want);
    }
}
