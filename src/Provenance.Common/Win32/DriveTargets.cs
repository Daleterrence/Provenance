using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Provenance.Common.Win32;

// A handle opened through a mapped or SUBST drive reports the folder behind the letter, so a path has to be turned
// into that before it's compared with where its handle really is
internal static class DriveTargets
{
    private const uint DriveRemote = 4;

    // what the system says a drive letter points at, swapped out by the tests
    internal static Func<char, string?> Lookup = SystemLookup;

    // the share a persistent mapping in this user's own registry hive points at, swapped out by the tests
    internal static Func<char, string?> NetworkLookup = FromRegistry;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string device, StringBuilder target, uint size);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnectionW(string local, StringBuilder remote, ref int length);

    // a letter that goes to a share, asked of the system and nothing else, swapped out by the tests
    internal static Func<char, bool> Remote = c => GetDriveTypeW(char.ToUpperInvariant(c) + @":\") == DriveRemote;

    public static bool HasDriveLetter(string path) => path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

    // the path with its drive letter replaced by where that really goes, a letter that goes nowhere left alone
    public static string Real(string path)
    {
        for (int i = 0; i < 4 && HasDriveLetter(path); i++)
        {
            if (Lookup(char.ToUpperInvariant(path[0])) is not { Length: > 0 } target) break;
            path = Path.TrimEndingDirectorySeparator(target) + path[2..];
        }
        return path;
    }

    // a letter this session doesn't have, but the player's own mapped it to a share, becomes that share's path. Anything
    // else is left as it is
    public static string Resolve(string path)
    {
        if (!HasDriveLetter(path) || Exists(path[0]) || NetworkLookup(char.ToUpperInvariant(path[0])) is not { Length: > 2 } share
            || !share.StartsWith(@"\\", StringComparison.Ordinal)) return path;
        return share.TrimEnd('\\') + path[2..];
    }

    // false for a letter nothing's behind, which is what an elevated process sees of a drive mapped in the player's own
    // session
    public static bool Exists(char letter) => GetDriveTypeW(char.ToUpperInvariant(letter) + @":\") > 1;

    private static string? FromRegistry(char letter)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Network\" + letter);
            return key?.GetValue("RemotePath") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? SystemLookup(char letter)
    {
        var drive = letter + ":";
        if (GetDriveTypeW(drive + @"\") == DriveRemote)
        {
            var remote = new StringBuilder(1024);
            int n = remote.Capacity;
            return WNetGetConnectionW(drive, remote, ref n) == 0 ? remote.ToString() : null;
        }
        var target = new StringBuilder(1024);
        if (QueryDosDeviceW(drive, target, (uint)target.Capacity) == 0) return null;
        var first = target.ToString().Split('\0')[0];
        if (first.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + first[8..];
        return first.StartsWith(@"\??\", StringComparison.Ordinal) ? first[4..] : null;
    }
}
