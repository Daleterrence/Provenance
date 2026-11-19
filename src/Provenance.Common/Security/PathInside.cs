namespace Provenance.Common.Security;

// names off the network or out of a zip get used as admin, so none of them may leave their folder
public static class PathInside
{
    public static string? Resolve(string root, string relative)
    {
        var rel = relative.Replace('/', '\\');
        if (rel.Length == 0 || Path.IsPathRooted(rel) || rel.Contains(':') || rel.Contains('\0') || rel.Split('\\').Any(part => part is "" or "." or ".."))
            return null;
        return Collapse(root, rel);
    }

    // "./x", "a//b" and "sub/../x" are fine, the full path just has to land below root
    public static string? Collapse(string root, string relative)
    {
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0')) return null;
        try
        {
            var top = Top(root);
            var full = Path.GetFullPath(top + relative.Replace('/', '\\'));
            return Below(top, full) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static bool IsUnder(string root, string path)
    {
        var top = Top(root);
        return Below(top, Full(path));
    }

    public static bool Contains(string root, string path)
    {
        var top = Top(root);
        var full = Full(path);
        return string.Equals(full, Path.TrimEndingDirectorySeparator(top), StringComparison.OrdinalIgnoreCase) || Below(top, full);
    }

    public static bool Overlaps(string a, string b) => Contains(a, b) || Contains(b, a);

    private static bool Below(string top, string full) => full.StartsWith(top, StringComparison.OrdinalIgnoreCase) && full.Length > top.Length;

    public static bool Same(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string Top(string root)
    {
        var full = Path.GetFullPath(root);
        return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }
}
