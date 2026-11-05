using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Provenance.Common.Security;

// For folders other users can write to: each one from the root down is held without delete sharing, so none can be
// swapped for a link mid-write
public sealed class FolderPins : IDisposable
{
    private readonly string _given;
    private readonly string _root;
    private readonly Dictionary<string, SafeFileHandle> _held = new(StringComparer.OrdinalIgnoreCase);

    public FolderPins(string root) : this(root, root) { }

    private FolderPins(string given, string real)
    {
        _given = Path.TrimEndingDirectorySeparator(Path.GetFullPath(given));
        _root = LongName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(real)));
    }

    public string Root => _root;

    // null if a folder on the way is a link or the root's a file, and a file in the way below it throws. Either way
    // whatever was held already is let go
    public static FolderPins? Hold(string root, string dir) => Pinned(new FolderPins(root), dir);

    // Hold, but a link above top is followed (an install moved to another drive, say) and everything's held where it
    // really is. Real gives the path to write to
    public static FolderPins? HoldBelow(string top, string dir)
    {
        var given = Path.TrimEndingDirectorySeparator(Path.GetFullPath(top));
        var real = given;
        if (Path.GetDirectoryName(given) is { } parent)
        {
            if (RealPath(parent) is not { } p) return null;
            real = Path.Join(p, Path.GetFileName(given));
        }
        return Pinned(new FolderPins(given, real), dir);
    }

    private static FolderPins? Pinned(FolderPins pins, string dir)
    {
        try
        {
            if (pins.Pin(dir)) return pins;
        }
        catch
        {
            pins.Dispose();
            throw;
        }
        pins.Dispose();
        return null;
    }

    public string Real(string path) => Rebase(path);

    // The root and every folder down to dir, made where missing. Held ones are checked again, an emptied folder can
    // still be made a link while it's held
    public bool Pin(string dir)
    {
        var full = Rebase(dir);
        if (!Add(_root)) return false;
        if (full.Equals(_root, StringComparison.OrdinalIgnoreCase)) return true;
        if (!full.StartsWith(_root + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        var at = _root;
        foreach (var part in full[(_root.Length + 1)..].Split('\\'))
        {
            at = Path.Combine(at, part);
            if (_held.ContainsKey(at))
            {
                if (!Fine(at)) return false;
                continue;
            }
            if (!Directory.Exists(at)) Directory.CreateDirectory(at);
            if (!Add(at)) return false;
        }
        return true;
    }

    // every folder from the root down to dir is held, still a plain folder, and the root's still where it was. Called
    // right before anything's written, moved or deleted there
    public bool Intact(string dir)
    {
        var full = Rebase(dir);
        if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(_root + "\\", StringComparison.OrdinalIgnoreCase))
            return false;
        var at = _root;
        if (!_held.ContainsKey(at) || !Fine(at)) return false;
        if (full.Length == _root.Length) return true;
        foreach (var part in full[(_root.Length + 1)..].Split('\\'))
        {
            at = Path.Combine(at, part);
            if (!_held.ContainsKey(at) || !Fine(at)) return false;
        }
        return true;
    }

    // so the folder itself can be deleted
    public void Release(string dir)
    {
        if (_held.Remove(Rebase(dir), out var h)) h.Dispose();
    }

    // Deletes a file, link or empty folder through a handle on the entry itself (a link goes, not its target), and only
    // if it really sits in the held folder
    public bool DeleteEntry(string path)
    {
        var full = Rebase(path);
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !_held.TryGetValue(parent, out var ph) || !Intact(parent)) return false;
        using var h = CreateFileW(full, Delete | ReadAttributes | WriteAttributes, ShareRead | ShareWrite | ShareDelete, IntPtr.Zero,
                                  OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (h.IsInvalid) return false;
        var where = FinalPath(h);
        var folder = FinalPath(ph);
        if (where is null || folder is null || !string.Equals(Path.GetDirectoryName(where), folder, StringComparison.OrdinalIgnoreCase)) return false;
        uint flags = DispositionDelete | DispositionPosix | DispositionIgnoreReadOnly;
        if (SetFileInformationByHandle(h, FileDispositionInfoEx, ref flags, 4)) return true;
        // older Windows: a read-only file's flag is cleared on this handle, and only when this is its one name
        if (!GetFileInformationByHandle(h, out var info)) return false;
        if ((info.Attributes & (uint)FileAttributes.ReadOnly) != 0)
        {
            if (info.Links > 1) return false;
            var basic = new BasicInfo { Attributes = info.Attributes & ~(uint)FileAttributes.ReadOnly };
            if (basic.Attributes == 0) basic.Attributes = (uint)FileAttributes.Normal;
            if (!SetFileInformationByHandle(h, FileBasicInfo, ref basic, 40)) return false;
        }
        byte yes = 1;
        return SetFileInformationByHandle(h, FileDispositionInfo, ref yes, 1);
    }

    // Pin's root test without holding anything: a folder that's a link, or is reached through one (a junctioned parent,
    // a SUBST drive), isn't where its path says. Something that won't open isn't counted
    public static bool ThroughLink(string root)
    {
        var given = LongName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        using var h = CreateFileW(given, ReadAttributes, ShareRead | ShareWrite | ShareDelete, IntPtr.Zero, OpenExisting,
                                  BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (h.IsInvalid) return false;
        if (GetFileInformationByHandleEx(h, AttributeTagInfo, out var tag, 8) && LinkCheck.IsRedirect(tag.Attributes, tag.ReparseTag)) return true;
        return !string.Equals(FinalPath(h), given, StringComparison.OrdinalIgnoreCase);
    }

    // ThroughLink for a folder out of settings, where a path that won't even parse just isn't a link. A third-party
    // reparse point (OneDrive's) is fine, the same as for the Windower updater
    public static bool Linked(string dir)
    {
        try { return ThroughLink(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    // where a folder really is, every link on the way (its own too) followed. Null if it won't open
    public static string? RealPath(string dir)
    {
        using var h = CreateFileW(LinkCheck.LongPath(Path.GetFullPath(dir)), ReadAttributes, ShareRead | ShareWrite | ShareDelete, IntPtr.Zero, OpenExisting,
                                  BackupSemantics, IntPtr.Zero);
        return h.IsInvalid ? null : FinalPath(h);
    }

    // no reparse point at all between root and path, and the last part's a folder or file as asked (missing is fine).
    // Stricter than LinkCheck on purpose, it's the hook's plain_path rule
    public static bool Plain(string root, string path, bool lastIsFolder)
    {
        var top = Path.GetFullPath(root).TrimEnd('\\');
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(top + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = full[(top.Length + 1)..].Split('\\');
        var at = top;
        for (int i = 0; i < parts.Length; i++)
        {
            at = Path.Combine(at, parts[i]);
            FileAttributes a;
            try { a = File.GetAttributes(at); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return true; }
            if (a.HasFlag(FileAttributes.ReparsePoint)) return false;
            if (a.HasFlag(FileAttributes.Directory) != (lastIsFolder || i < parts.Length - 1)) return false;
        }
        return true;
    }

    private string Rebase(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.StartsWith(_given, StringComparison.OrdinalIgnoreCase) && (full.Length == _given.Length || full[_given.Length] == '\\')
            ? _root + full[_given.Length..]
            : full;
    }

    // a read-attributes-only handle wouldn't do, those don't count for sharing
    private bool Add(string dir)
    {
        if (_held.ContainsKey(dir)) return Fine(dir);
        var h = CreateFileW(LinkCheck.LongPath(dir), ListDirectory | ReadAttributes, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting,
                            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (h.IsInvalid)
        {
            h.Dispose();
            return false;
        }
        _held[dir] = h;
        if (Fine(dir)) return true;
        _held.Remove(dir);
        h.Dispose();
        return false;
    }

    // still a folder, not a link, and for the root still at the path it was asked for (a parent swapped for a link
    // would move it)
    private bool Fine(string dir)
    {
        var h = _held[dir];
        if (!GetFileInformationByHandleEx(h, AttributeTagInfo, out var tag, 8) ||
            (tag.Attributes & (uint)FileAttributes.Directory) == 0 || LinkCheck.IsRedirect(tag.Attributes, tag.ReparseTag))
            return false;
        return !dir.Equals(_root, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(FinalPath(h), _root, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? FinalPath(SafeFileHandle h)
    {
        var buf = new StringBuilder(1024);
        uint n = GetFinalPathNameByHandleW(h, buf, (uint)buf.Capacity, 0);
        if (n == 0 || n >= buf.Capacity) return null;
        var s = buf.ToString(0, (int)n);
        if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + s[8..];
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    // 8.3 names expanded, so the root can be compared with where its handle really is
    private static string LongName(string path)
    {
        var buf = new StringBuilder(1024);
        uint n = GetLongPathNameW(path, buf, (uint)buf.Capacity);
        return n == 0 || n >= buf.Capacity ? path : Path.TrimEndingDirectorySeparator(buf.ToString(0, (int)n));
    }

    public void Dispose()
    {
        foreach (var h in _held.Values) h.Dispose();
        _held.Clear();
    }

    private const uint ListDirectory = 0x1, ReadAttributes = 0x80, WriteAttributes = 0x100, Delete = 0x10000;
    private const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4, OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    private const int AttributeTagInfo = 9, FileBasicInfo = 0, FileDispositionInfo = 4, FileDispositionInfoEx = 21;
    private const uint DispositionDelete = 0x1, DispositionPosix = 0x2, DispositionIgnoreReadOnly = 0x10;

    private struct AttributeTag
    {
        public uint Attributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential, Size = 40)]
    private struct BasicInfo
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint Attributes;
    }

    // FILETIMEs are two DWORDs, so 4-byte aligned
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct HandleInfo
    {
        public uint Attributes;
        public long Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition,
                                                     uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeTag info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out HandleInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref uint info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref BasicInfo info, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint size);
}
