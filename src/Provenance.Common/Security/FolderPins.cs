using System.Text;
using Microsoft.Win32.SafeHandles;
using Provenance.Common.IO;
using Provenance.Common.Win32;

namespace Provenance.Common.Security;

// each folder from the root down is held without delete sharing, so none can be swapped for a link mid-write
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

    public static FolderPins? HoldExact(string root, string dir) => Pinned(new FolderPins(root), dir);

    public static FolderPins HoldExactOrThrow(string root, string dir, string message) =>
        HoldExact(root, dir) ?? throw Shown.Mark(new IOException(message));

    private static FolderPins? HoldFollowingLinksAbove(string top, string dir)
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

    public static FolderPins HoldFollowingLinksAboveOrThrow(string top, string dir)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"{dir} isn't there.");
        return HoldFollowingLinksAbove(top, dir) ?? throw LinkError(dir);
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

    public bool Pin(string dir)
    {
        var full = Rebase(dir);
        if (!Add(_root)) return false;
        if (full.Equals(_root, StringComparison.OrdinalIgnoreCase)) return true;
        if (!PathInside.IsUnder(_root, full)) return false;
        var at = _root;
        foreach (var part in PartsBelow(_root, full))
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

    public bool Intact(string dir)
    {
        var full = Rebase(dir);
        if (!PathInside.Contains(_root, full)) return false;
        var at = _root;
        if (!_held.ContainsKey(at) || !Fine(at)) return false;
        if (full.Length == _root.Length) return true;
        foreach (var part in PartsBelow(_root, full))
        {
            at = Path.Combine(at, part);
            if (!_held.ContainsKey(at) || !Fine(at)) return false;
        }
        return true;
    }

    public void Recheck(string dir, params IEnumerable<string> files)
    {
        if (!Intact(dir)) throw LinkError(dir);
        LinkCheck.Refuse(files);
    }

    public void Release(string dir)
    {
        if (_held.Remove(Rebase(dir), out var h)) h.Dispose();
    }

    public bool DeleteEntry(string path)
    {
        var full = Rebase(path);
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !_held.TryGetValue(parent, out var ph) || !Intact(parent)) return false;
        using var h = FileHandles.OpenLinkItself(FileHandles.LongPath(full), FileHandles.Delete | FileHandles.ReadAttributes | FileHandles.WriteAttributes, FileHandles.ShareAll);
        if (h.IsInvalid) return false;
        var where = FileHandles.FinalPath(h, allowUnc: true);
        var folder = FileHandles.FinalPath(ph, allowUnc: true);
        if (where is null || folder is null || !string.Equals(Path.GetDirectoryName(where), folder, StringComparison.OrdinalIgnoreCase)) return false;
        uint flags = DispositionDelete | DispositionPosix | DispositionIgnoreReadOnly;
        if (Kernel32.SetFileInformationByHandle(h, FileDispositionInfoEx, ref flags, 4)) return true;
        // older Windows: a read-only file's flag is cleared on this handle, and only when this is its one name
        if (!FileHandles.TryInfo(h, out var info)) return false;
        if ((info.FileAttributes & (uint)FileAttributes.ReadOnly) != 0)
        {
            if (info.NumberOfLinks > 1) return false;
            var basic = new BasicInfo { Attributes = info.FileAttributes & ~(uint)FileAttributes.ReadOnly };
            if (basic.Attributes == 0) basic.Attributes = (uint)FileAttributes.Normal;
            if (!Kernel32.SetFileInformationByHandle(h, FileBasicInfo, ref basic, 40)) return false;
        }
        byte yes = 1;
        return Kernel32.SetFileInformationByHandle(h, FileDispositionInfo, ref yes, 1);
    }

    // a folder that's a link, or reached through one (junctioned parent, SUBST drive), isn't where its path says
    public static bool ThroughLink(string root)
    {
        var given = LongName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        using var h = FileHandles.OpenLinkItself(FileHandles.LongPath(given), FileHandles.ReadAttributes, FileHandles.ShareAll);
        if (h.IsInvalid) return false;
        if (Kernel32.GetFileInformationByHandleEx(h, AttributeTagInfo, out var tag, 8) && LinkCheck.IsRedirect(tag.Attributes, tag.ReparseTag)) return true;
        return !SamePlace(FileHandles.FinalPath(h, allowUnc: true), Expected(given));
    }

    public static bool Linked(string dir)
    {
        try { return ThroughLink(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    public static bool DriveMissing(string dir) => DriveTargets.HasDriveLetter(dir) && !DriveTargets.Exists(dir[0]);

    public static string? RealPath(string dir)
    {
        using var h = FileHandles.Open(FileHandles.LongPath(Path.GetFullPath(dir)), FileHandles.ReadAttributes, FileHandles.ShareAll, FileHandles.BackupSemantics);
        return h.IsInvalid ? null : FileHandles.FinalPath(h, allowUnc: true);
    }

    public static bool Plain(string root, string path, bool lastIsFolder)
    {
        if (!PathInside.IsUnder(root, path)) return false;
        var top = Path.GetFullPath(root).TrimEnd('\\');
        var full = Path.GetFullPath(path);
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

    internal static IOException LinkError(string p) => Shown.Mark(new IOException($"Provenance won't write to {p}, it's a link to somewhere else."));

    internal static string[] PartsBelow(string root, string full) =>
        full[(Path.EndsInDirectorySeparator(root) ? root.Length : root.Length + 1)..].Split('\\');

    private string Rebase(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.StartsWith(_given, StringComparison.OrdinalIgnoreCase) && (full.Length == _given.Length || full[_given.Length] == '\\')
            ? _root + full[_given.Length..]
            : full;
    }

    private bool Add(string dir)
    {
        if (_held.ContainsKey(dir)) return Fine(dir);
        var h = FileHandles.OpenLinkItself(FileHandles.LongPath(dir), FileHandles.ListDirectory | FileHandles.ReadAttributes, FileHandles.ShareReadWrite);
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

    private bool Fine(string dir)
    {
        var h = _held[dir];
        if (!Kernel32.GetFileInformationByHandleEx(h, AttributeTagInfo, out var tag, 8) ||
            (tag.Attributes & (uint)FileAttributes.Directory) == 0 || LinkCheck.IsRedirect(tag.Attributes, tag.ReparseTag))
            return false;
        return !dir.Equals(_root, StringComparison.OrdinalIgnoreCase) ||
               SamePlace(FileHandles.FinalPath(h, allowUnc: true), Expected(_root));
    }

    private static string Expected(string path)
    {
        var real = DriveTargets.Real(path);
        return real == path || real.StartsWith(@"\\", StringComparison.Ordinal) ? real : LongName(real);
    }

    private static bool SamePlace(string? a, string b) =>
        a is not null && string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);

    private static string LongName(string path)
    {
        var buf = new StringBuilder(1024);
        uint n = Kernel32.GetLongPathNameW(path, buf, (uint)buf.Capacity);
        return n == 0 || n >= buf.Capacity ? path : Path.TrimEndingDirectorySeparator(buf.ToString(0, (int)n));
    }

    public void Dispose()
    {
        foreach (var h in _held.Values) h.Dispose();
        _held.Clear();
    }

    private const int AttributeTagInfo = 9, FileBasicInfo = 0, FileDispositionInfo = 4, FileDispositionInfoEx = 21;
    private const uint DispositionDelete = 0x1, DispositionPosix = 0x2, DispositionIgnoreReadOnly = 0x10;
}
