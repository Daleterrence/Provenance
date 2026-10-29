using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Provenance.Wpf.Services;

// The app runs as admin, so anything it runs or trusts mustn't sit where a non-admin program could swap it
public static class PathSafety
{
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // only Administrators, SYSTEM and TrustedInstaller can change the item or any folder above it (a rename up there
    // could replace it), and none of them is a link
    public static bool IsAdminOnly(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
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
                    // inherit-only rules are for the items inside, which get checked on their own
                    if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
                    if ((rule.FileSystemRights & WriteRights) == 0) continue;
                    if (rule.IdentityReference is SecurityIdentifier sid && !IsTrusted(sid))
                    {
                        // CREATOR OWNER only covers things made later, and their owner gets checked
                        if (sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)) continue;
                        // on a drive root, Users may create folders but not change existing ones
                        if (Path.GetPathRoot(item) == item && (rule.FileSystemRights & ~(FileSystemRights.AppendData | FileSystemRights.Synchronize)) == 0) continue;
                        return false;
                    }
                }
            }
            return true;
        }
        catch { return false; }   // can't read it, so it isn't shown to be safe
    }

    private static bool IsTrusted(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";   // TrustedInstaller
}
