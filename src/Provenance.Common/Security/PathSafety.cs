using System.Security.AccessControl;
using System.Security.Principal;

namespace Provenance.Common.Security;

public static class PathSafety
{
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    public static bool IsAdminOnly(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!OnLocalFixedDrive(full)) return false;
            for (var item = full; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            {
                bool isDir = Directory.Exists(item);
                if (!isDir && !File.Exists(item)) return false;
                var info = isDir ? (FileSystemInfo)new DirectoryInfo(item) : new FileInfo(item);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) && Path.GetPathRoot(item) != item) return false;
                FileSystemSecurity sec = isDir ? ((DirectoryInfo)info).GetAccessControl() : ((FileInfo)info).GetAccessControl();
                if (sec.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !IsTrusted(owner)) return false;
                foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (rule.AccessControlType != AccessControlType.Allow) continue;
                    if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
                    if ((rule.FileSystemRights & WriteRights) == 0) continue;
                    if (rule.IdentityReference is SecurityIdentifier sid && !IsTrusted(sid))
                    {
                        // CREATOR OWNER only covers things made later, and their owner gets checked
                        if (sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)) continue;
                        if (Path.GetPathRoot(item) == item && (rule.FileSystemRights & ~(FileSystemRights.AppendData | FileSystemRights.Synchronize)) == 0) continue;
                        return false;
                    }
                }
            }
            return true;
        }
        catch { return false; }
    }

    private const int MoveRights = 0x00010000 | 0x00000040 | 0x00040000 | 0x00080000 | 0x10000000 | 0x40000000;

    public static string? AdminOnlyProblem(string folder)
    {
        string item = folder;
        try
        {
            var full = Path.GetFullPath(folder);
            if (!OnLocalFixedDrive(full)) return $"{full} isn't on a fixed drive in this PC";
            for (item = full; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item)!)
            {
                var info = new DirectoryInfo(item);
                if (!info.Exists) return $"{item} isn't there";
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) && Path.GetPathRoot(item) != item) return $"{item} is a link";
                var sd = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
                var raw = new RawSecurityDescriptor(sd, 0);
                if (raw.Owner is not { } owner || !IsTrusted(owner)) return $"{item} is owned by {raw.Owner?.Value ?? "nobody"}";
                if (raw.DiscretionaryAcl is not { } acl) return $"{item} has no permissions, so anyone can do anything";
                foreach (GenericAce ace in acl)
                {
                    if (ace is not QualifiedAce q) return $"{item} has a permission Provenance can't read";
                    if (q.AceQualifier != AceQualifier.AccessAllowed || ace.AceFlags.HasFlag(AceFlags.InheritOnly)) continue;
                    if (IsTrusted(q.SecurityIdentifier) || q.SecurityIdentifier.IsWellKnown(WellKnownSidType.CreatorOwnerSid)) continue;
                    if ((q.AccessMask & MoveRights) != 0) return $"{q.SecurityIdentifier.Value} can rename, delete or re-permission {item}";
                }
            }
            return null;
        }
        catch (Exception e) { return $"{item} couldn't be read ({e.GetType().Name})"; }
    }

    public static bool OnLocalFixedDrive(string path)
    {
        try { return OnLocalFixedDrive(Path.GetFullPath(path), root => new DriveInfo(root).DriveType); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    // a share's permissions are whatever its server says and it can change a file between reads, a removable drive can be swapped
    internal static bool OnLocalFixedDrive(string fullPath, Func<string, DriveType> driveType)
    {
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var root = Path.GetPathRoot(fullPath);
        return root is { Length: 3 } && root[1] == ':' && root[2] == '\\' && driveType(root) == DriveType.Fixed;
    }

    internal static bool IsTrusted(SecurityIdentifier sid) =>
        LockedFolder.IsAdminOrSystem(sid) ||
        sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";   // TrustedInstaller
}
