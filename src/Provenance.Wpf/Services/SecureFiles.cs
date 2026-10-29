using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Serilog;

namespace Provenance.Wpf.Services;

// C:\ProgramData\Provenance\ipc, only Administrators and SYSTEM can open it. Never %TEMP% or %APPDATA%, where a
// program could swap a file for a link and get written through
public static class SecureFiles
{
    private static readonly object Gate = new();
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static string? _dir;
    private static string? _rootForTest;   // under Gate
    private static int _permissionWrites;

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
        }
    }

    internal static int PermissionWrites => Volatile.Read(ref _permissionWrites);

    public static string PathFor(string name) => Path.Combine(Dir, name);

    public static string SubDir(string name)
    {
        var dir = Path.Combine(Dir, name);
        Lock(new DirectoryInfo(dir));
        return dir;
    }

    private static bool AdminOwned(FileSystemSecurity sec) =>
        sec.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier o && (o == Admins || o == SystemSid);

    // for folders whose files later run as admin. False and untouched when it's a link or an admin didn't make it,
    // so a user's own folder is never emptied
    public static bool LockFolder(string path, IEnumerable<string> ownNames)
    {
        static bool Trusted(FileSystemInfo f) =>
            !f.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
            AdminOwned(f is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)f).GetAccessControl());

        var di = new DirectoryInfo(path);
        if (di.Exists && !Trusted(di)) return false;
        if (di.Parent is { Exists: false } parent) parent.Create();
        Lock(di, usersMayRead: true);

        var failed = new List<string>();
        void Remove(FileSystemInfo f)
        {
            try
            {
                Log.Warning("{Item} in a locked folder was not made by an administrator; it is deleted", f.FullName);
                if (f is DirectoryInfo dir && !f.Attributes.HasFlag(FileAttributes.ReparsePoint)) dir.Delete(recursive: true);
                else f.Delete();   // a link: only the link goes, never what it points at
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warning(e, "Could not delete {Item}", f.FullName);
                failed.Add(f.FullName);
            }
        }
        void Check(FileSystemInfo f)
        {
            if (!Trusted(f)) Remove(f);
            else if (f is DirectoryInfo sub)
            {
                var all = new EnumerationOptions { AttributesToSkip = 0 };
                foreach (var inner in sub.EnumerateFileSystemInfos("*", all).ToList()) Check(inner);
            }
        }
        // only ownNames get cleaned, anything else in the folder stays
        foreach (var name in ownNames)
        {
            var full = Path.Combine(path, name);
            var probe = new FileInfo(full);
            if ((int)probe.Attributes == -1) continue;   // not there, not even as a link
            Check(probe.Attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(full) : probe);
        }
        if (failed.Count > 0)
            throw Provenance.Common.Shown.Mark(new IOException($"Provenance couldn't remove {failed[0]}" +
                (failed.Count > 1 ? $" and {failed.Count - 1} more" : "") + ". Close any program that's using it, then press Try again."));
        return true;
    }

    private static string Ensure()
    {
        var root = _rootForTest ??
                   Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Provenance");
        // any user can make folders in ProgramData, so a "Provenance" some other program made gets taken over first
        Lock(new DirectoryInfo(root), usersMayRead: true);
        var dir = Path.Combine(root, "ipc");
        Lock(new DirectoryInfo(dir));
        return dir;
    }

    private static void Lock(DirectoryInfo di, bool usersMayRead = false)
    {
        // a junction or symlink in its place would redirect every write
        if (di.Exists && di.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            Log.Warning("{Dir} was a link, not a folder; it is replaced", di.FullName);
            di.Delete();
            di.Refresh();
        }
        // not reused, a handle another program opened before the permissions change keeps its access
        if (di.Exists && di.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner &&
            owner != Admins && owner != SystemSid)
        {
            var original = di.FullName;
            var aside = original + ".untrusted-" + Guid.NewGuid().ToString("N")[..8];
            Log.Warning("{Dir} was not made by an administrator; it is moved to {Aside} and made again", original, aside);
            di.MoveTo(aside);   // fails while another program holds it open, and then nothing's used
            di = new DirectoryInfo(original);
        }
        // rewriting the same ACL pushes it down to every file inside, which can fail on a file another copy's hook is
        // replacing right then
        if (di.Exists && IsLocked(di.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), usersMayRead))
            return;
        var sec = Intended(usersMayRead);
        Interlocked.Increment(ref _permissionWrites);
        if (!di.Exists) di.Create(sec);
        else di.SetAccessControl(sec);
    }

    internal static DirectorySecurity Intended(bool usersMayRead)
    {
        var sec = new DirectorySecurity();
        sec.SetOwner(Admins);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        if (usersMayRead)
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    // exactly Intended or it gets locked again
    internal static bool IsLocked(DirectorySecurity current, bool usersMayRead)
    {
        if (current.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != Admins) return false;
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
