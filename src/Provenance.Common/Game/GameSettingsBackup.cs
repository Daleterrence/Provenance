using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Provenance.Common.IO;
using Provenance.Common.Security;

namespace Provenance.Common.Game;

public sealed record BackupSources(string? User, string? VirtualStoreUser, string? FfxiUser);

public enum BackupOutcome { Made, Unchanged, NothingThere }

public sealed record BackupResult(BackupOutcome Outcome, string? Zip, IReadOnlyList<string> LeftOut);

public sealed record BackupInfo(string Path, DateTime CreatedUtc, int Characters, int Files)
{
    internal IReadOnlyList<GameSettingsBackup.ManifestFile> Manifest { get; init; } = [];
    internal int Suffix { get; init; }
}

public sealed record RestoreResult(int Restored, BackupResult Before, IReadOnlyList<string> LeftOut);

public sealed class BackupFolderLinkedException(string folder)
    : IOException($"{folder} is a link to somewhere else, so Provenance won't save backups in it.");

public sealed class NotABackupException() : IOException("That file isn't one of Provenance's game settings backups.");

public sealed class BackupBeforeRestoreException(Exception inner) : IOException("The current settings couldn't be backed up first.", inner);

public sealed class RestoredPartlyException(BackupOutcome before, Exception inner) : IOException("Not all of the settings could be put back.", inner)
{
    public BackupOutcome Before { get; } = before;
}

// the game rewrites these in place unlocked, so each copy is checked for a write mid-read
public static partial class GameSettingsBackup
{
    public const int Kept = 5;
    public const string NameStart = "FINAL FANTASY XI settings ";
    internal const string ManifestName = "provenance-backup.json";
    private const string UserDir = "USER", VirtualStoreDir = "VirtualStore", FfxiUserDir = "ffxiuser", Shared = "tig.dat";
    private static readonly string[] FilterLists = ["allowed.txt", "prohibited.txt"];

    private const int ManifestFormat = 1;
    internal const long MaxFile = 16 << 20;
    private const long MaxTotal = 1L << 30;
    private const int MaxEntries = 100_000;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string DefaultFolder =>
        Path.Combine(AppPaths.UserData ?? "Provenance", "Backups");

    public static string FolderOrDefault(string? chosen) =>
        chosen?.Trim() is { Length: > 0 } c && Path.IsPathFullyQualified(c) ? c : DefaultFolder;

    public static IDisposable? TryLock(TimeSpan wait) => AdminLock.TryAcquire("game-settings-backup", wait);

    public static BackupSources Sources(string ffxiFolder, string? settingsFolder, string? localAppData = null, string? documents = null)
    {
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ffxiFolder));
        var root = Path.GetPathRoot(install);
        localAppData ??= AppPaths.LocalAppData;
        string? virtualStore = root is { Length: 3 } && root[1] == ':' && Path.IsPathFullyQualified(localAppData)
            ? Path.Combine(localAppData, VirtualStoreDir, install[root.Length..], UserDir) : null;
        var settings = string.IsNullOrWhiteSpace(settingsFolder)
            ? Path.Combine(documents ?? AppPaths.Documents, "My Games", "FINAL FANTASY XI")
            : settingsFolder.Trim();
        return new(Path.Combine(install, UserDir), virtualStore, Path.IsPathFullyQualified(settings) ? Path.Combine(settings, FfxiUserDir) : null);
    }

    internal sealed class Manifest
    {
        public int Format { get; set; }
        public DateTime Created { get; set; }
        public List<ManifestFile> Files { get; set; } = [];
    }

    internal sealed class ManifestFile
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public DateTime Written { get; set; }
    }

    public static BackupResult BackUp(BackupSources sources, string folder, DateTime? now = null)
    {
        var leftOut = new List<string>();
        var found = Scan(sources, leftOut);
        if (found.Count == 0) return new(BackupOutcome.NothingThere, null, leftOut);

        folder = Path.GetFullPath(folder);
        using var pins = HoldFolder(folder) ?? throw new BackupFolderLinkedException(folder);
        var newest = List(folder).FirstOrDefault();
        if (newest is not null && Same(newest.Manifest, found)) return new(BackupOutcome.Unchanged, newest.Path, leftOut);

        var taken = new List<BackupReader.Taken>();
        var torn = new List<BackupReader.Found>();
        foreach (var f in found)
        {
            try
            {
                if (BackupReader.ReadSteady(f) is { } t) taken.Add(t);
                else torn.Add(f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { leftOut.Add($"{f.Entry}: {e.Message}"); }
        }
        BackupReader.SettleDown(taken, torn, leftOut);

        var at = now ?? DateTime.Now;
        var manifest = new Manifest { Format = ManifestFormat, Created = at.ToUniversalTime() };
        var fallback = new List<(string Entry, byte[] Data, ManifestFile File)>();
        if (torn.Count > 0)
        {
            using var last = newest is null ? null : TryOpenZip(newest.Path);
            foreach (var f in torn)
            {
                var old = newest?.Manifest.FirstOrDefault(m => m.Name.Equals(f.Entry, StringComparison.OrdinalIgnoreCase));
                if (last is not null && old is not null && ReadEntry(last, old.Name) is { } data)
                {
                    fallback.Add((f.Entry, data, old));
                    leftOut.Add($"{f.Entry}: kept changing, so the copy from {Path.GetFileName(newest!.Path)} went in");
                }
                else leftOut.Add($"{f.Entry}: kept changing, so it's left out");
            }
        }

        if (taken.Count == 0 && fallback.Count == 0) return new(BackupOutcome.NothingThere, null, leftOut);

        var tmp = Path.Combine(folder, $"{NameStart}{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = CreateForUser(tmp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var t in taken)
                {
                    Add(zip, t.File.Entry, t.Data, t.Stat.Written);
                    manifest.Files.Add(new ManifestFile { Name = t.File.Entry, Size = t.Stat.Size, Written = DateTime.FromFileTimeUtc(t.Stat.Written) });
                }
                foreach (var (entry, data, file) in fallback)
                {
                    Add(zip, entry, data, file.Written.ToFileTimeUtc());
                    manifest.Files.Add(file);
                }
                using var m = zip.CreateEntry(ManifestName, CompressionLevel.Optimal).Open();
                JsonSerializer.Serialize(m, manifest, Indented);
            }
            var made = MoveToFreeName(tmp, folder, at);
            Prune(folder, pins, leftOut);
            return new(BackupOutcome.Made, made, leftOut);
        }
        finally { FileDelete.Try(tmp); }
    }

    public static FolderPins? HoldFolder(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var anchor = folder;
        while (!Directory.Exists(anchor))
            anchor = Path.GetDirectoryName(anchor) ?? throw new DirectoryNotFoundException($"Nothing above {folder} is there.");
        return HoldFrom(anchor, folder);
    }

    private static FolderPins? HoldFrom(string anchor, string dir)
    {
        if (Path.GetDirectoryName(anchor) is null && dir.Length > anchor.Length)
        {
            if (FolderPins.ThroughLink(anchor)) return null;
            anchor = Path.Join(anchor, dir[anchor.Length..].TrimStart('\\').Split('\\')[0]);
            Directory.CreateDirectory(anchor);
        }
        return FolderPins.HoldExact(anchor, dir);
    }

    public static IReadOnlyList<BackupInfo> List(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var ours = new List<BackupInfo>();
        foreach (var path in Directory.EnumerateFiles(folder, NameStart + "*.zip"))
        {
            var m = BackupName().Match(Path.GetFileName(path));
            if (!m.Success) continue;
            try
            {
                using var zip = OpenZip(path);
                if (Describe(path, zip) is { } info) ours.Add(info with { Suffix = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 1 });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        return ours.OrderByDescending(b => b.CreatedUtc).ThenByDescending(b => b.Suffix).ToList();
    }

    public static BackupInfo Check(string zipPath)
    {
        try
        {
            using var zip = OpenZip(zipPath);
            return Describe(zipPath, zip) ?? throw new NotABackupException();
        }
        catch (InvalidDataException) { throw new NotABackupException(); }
    }

    public static RestoreResult Restore(string zipPath, BackupSources sources, string folder, Func<string?>? blocked = null)
    {
        // no delete sharing, so the backup made first can't prune this one away mid-restore
        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ZipArchive zip;
        try { zip = new ZipArchive(fs, ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new NotABackupException(); }
        using (zip)
        {
            if (ReadManifest(zip) is not { } manifest) throw new NotABackupException();
            var written = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in manifest.Files) written.TryAdd(f.Name, f.Written);
            try
            {
                if (zip.Entries.Any(e => e.FullName != ManifestName && ReadEntry(e) is null)) throw new NotABackupException();
            }
            catch (InvalidDataException) { throw new NotABackupException(); }
            BackupResult before;
            try { before = BackUp(sources, folder); }
            catch (Exception e) { throw new BackupBeforeRestoreException(e); }
            if (blocked?.Invoke() is { } why) throw Shown.Mark(new InvalidOperationException(why));
            var leftOut = new List<string>(before.LeftOut.Select(l => "before: " + l));
            int restored = 0;
            bool putting = false;
            var pinned = new List<(PinnedDir Pins, IGrouping<TargetDir?, ZipArchiveEntry> Entries)>();
            try
            {
                foreach (var group in zip.Entries.Where(e => e.FullName != ManifestName).GroupBy(e => Target(sources, e.FullName)))
                {
                    if (group.Key is { } dir) pinned.Add((Pin(dir.Root, dir.Sub), group));
                    else leftOut.AddRange(group.Select(e => $"{e.FullName}: there's nowhere to put it on this PC"));
                }
                foreach (var (pins, group) in pinned)
                    foreach (var e in group)
                    {
                        var data = ReadEntry(e) ?? throw new NotABackupException();
                        putting = true;
                        Put(pins.Pins, pins.Dir, e.Name, data, written.TryGetValue(e.FullName, out var at) ? at : null);
                        restored++;
                    }
            }
            catch (Exception e) when (putting && e is not NotABackupException && !Shown.Is(e))
            {
                throw new RestoredPartlyException(before.Outcome, e);
            }
            finally
            {
                foreach (var (pins, _) in pinned) pins.Dispose();
            }
            return new(restored, before, leftOut);
        }
    }

    private static SecurityIdentifier? CurrentUser()
    {
        using var me = WindowsIdentity.GetCurrent();
        return me.User;
    }

    // permissions are set on the file's handle so it can't be swapped for a link first
    private static FileStream CreateForUser(string path)
    {
        var fs = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.ReadPermissions
                                           | FileSystemRights.ChangePermissions, FileShare.None, 4096, FileOptions.None, null);
        try
        {
            if (CurrentUser() is { } me)
            {
                var sec = fs.GetAccessControl();
                sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
                fs.SetAccessControl(sec);
            }
            return fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private static string MoveToFreeName(string tmp, string folder, DateTime at)
    {
        var stem = NameStart + at.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        for (int n = 1; n < 1000; n++)
        {
            var path = Path.Combine(folder, n == 1 ? stem + ".zip" : $"{stem} ({n}).zip");
            if (File.Exists(path) || Directory.Exists(path)) continue;
            try
            {
                File.Move(tmp, path, overwrite: false);
                return path;
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        throw new IOException($"There are already 999 backups called {stem}.");
    }

    private static void Prune(string folder, FolderPins pins, List<string> leftOut)
    {
        foreach (var old in List(folder).Skip(Kept))
            if (!pins.DeleteEntry(old.Path)) leftOut.Add($"{Path.GetFileName(old.Path)}: couldn't be deleted to keep the newest {Kept}");
    }

    [GeneratedRegex(@"^FINAL FANTASY XI settings \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}(?: \((\d{1,4})\))?\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex BackupName();

    // the game writes ids with %x, but nothing stops a hand-made folder in upper case
    [GeneratedRegex(@"^[0-9a-fA-F]{1,8}$")]
    private static partial Regex CharacterFolder();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex PlainName();

    private static bool IsFileName(string name) => FileNames.IsPlainFileName(name) && PlainName().IsMatch(name) && !FileNames.IsDeviceName(name);

    internal static bool IsEntry(string name)
    {
        var parts = name.Split('/');
        if (parts is [VirtualStoreDir, UserDir, ..]) parts = parts[1..];
        return parts switch
        {
            [UserDir, var f] => f.Equals(Shared, StringComparison.OrdinalIgnoreCase),
            [UserDir, var id, var f] => CharacterFolder().IsMatch(id) && IsFileName(f),
            [FfxiUserDir, var f] => FilterLists.Contains(f, StringComparer.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private readonly record struct TargetDir(string Root, string Sub);

    private static TargetDir? Target(BackupSources s, string entry) => entry.Split('/') switch
    {
        [UserDir, _] => s.User is { } u ? new(u, "") : null,
        [UserDir, var id, _] => s.User is { } u ? new(u, id) : null,
        [VirtualStoreDir, UserDir, _] => s.VirtualStoreUser is { } v ? new(v, "") : null,
        [VirtualStoreDir, UserDir, var id, _] => s.VirtualStoreUser is { } v ? new(v, id) : null,
        [FfxiUserDir, _] => s.FfxiUser is { } f ? new(f, "") : null,
        _ => null,
    };

    private static List<BackupReader.Found> Scan(BackupSources s, List<string> leftOut)
    {
        var found = new List<BackupReader.Found>();
        ScanUser(s.User, UserDir + "/", found, leftOut);
        ScanUser(s.VirtualStoreUser, $"{VirtualStoreDir}/{UserDir}/", found, leftOut);
        if (Place(s.FfxiUser, FfxiUserDir, leftOut) is { } settings)
            foreach (var name in FilterLists)
                AddFound(Path.Combine(settings, name), settings, $"{FfxiUserDir}/{name}", false, found, leftOut);
        return found;
    }

    private static void ScanUser(string? root, string prefix, List<BackupReader.Found> found, List<string> leftOut)
    {
        if (Place(root, prefix.TrimEnd('/'), leftOut) is not { } real) return;
        AddFound(Path.Combine(real, Shared), real, prefix + Shared, true, found, leftOut);
        foreach (var dir in Listed(real, prefix, leftOut, Directory.EnumerateDirectories))
        {
            var id = Path.GetFileName(dir);
            if (!CharacterFolder().IsMatch(id))
            {
                leftOut.Add($"{prefix}{id}: not a character folder");
                continue;
            }
            if (FileLinks.IsLink(dir))
            {
                leftOut.Add($"{prefix}{id}: a link, so it's left out");
                continue;
            }
            foreach (var file in Listed(dir, $"{prefix}{id}", leftOut, Directory.EnumerateFiles))
            {
                var name = Path.GetFileName(file);
                if (IsFileName(name)) AddFound(file, dir, $"{prefix}{id}/{name}", true, found, leftOut);
                else leftOut.Add($"{prefix}{id}/{name}: not a name a backup takes");
            }
        }
    }

    private static List<string> Listed(string dir, string what, List<string> leftOut, Func<string, IEnumerable<string>> list)
    {
        try { return list(dir).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            leftOut.Add($"{what}: couldn't be listed ({e.Message})");
            return [];
        }
    }

    // nothing at or below the folder's own name can be a link
    private static string? Place(string? root, string what, List<string> leftOut)
    {
        if (root is null || !Directory.Exists(root)) return null;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Path.GetDirectoryName(full) is not { } parent || !FolderPins.Plain(parent, full, lastIsFolder: true))
        {
            leftOut.Add($"{what}: {full} is a link, so it's left out");
            return null;
        }
        return FolderPins.RealPath(full);
    }

    private static void AddFound(string path, string folder, string entry, bool zeroIsTorn, List<BackupReader.Found> found, List<string> leftOut)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var h = BackupReader.OpenChecked(path, folder, data: false, out var stat);
            found.Add(new(entry, path, folder, zeroIsTorn, stat));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { leftOut.Add($"{entry}: {e.Message}"); }
    }

    private static bool Same(IReadOnlyList<ManifestFile> last, List<BackupReader.Found> now)
    {
        if (last.Count != now.Count) return false;
        var byName = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in last) byName.TryAdd(f.Name, f);
        return now.All(f => byName.TryGetValue(f.Entry, out var m) && m.Size == f.Stat.Size
                            && m.Written.ToUniversalTime().ToFileTimeUtc() == f.Stat.Written);
    }

    private static void Add(ZipArchive zip, string entry, byte[] data, long written)
    {
        var e = zip.CreateEntry(entry, CompressionLevel.Optimal);
        var at = DateTime.FromFileTimeUtc(written).ToLocalTime();
        if (at.Year is >= 1980 and <= 2107) e.LastWriteTime = at;
        using var s = e.Open();
        s.Write(data);
    }

    private static ZipArchive OpenZip(string path)
    {
        var fs = SharedFile.OpenRead(path);
        try { return new ZipArchive(fs, ZipArchiveMode.Read); }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private static ZipArchive? TryOpenZip(string path)
    {
        try { return OpenZip(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    private static BackupInfo? Describe(string path, ZipArchive zip)
    {
        if (ReadManifest(zip) is not { } m) return null;
        var ids = m.Files.Select(f => f.Name.Split('/') switch { [UserDir, var id, _] => id, [VirtualStoreDir, UserDir, var id, _] => id, _ => null })
                   .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new BackupInfo(path, m.Created.ToUniversalTime(), ids, m.Files.Count) { Manifest = m.Files };
    }

    private static Manifest? ReadManifest(ZipArchive zip)
    {
        if (zip.Entries.Count > MaxEntries) return null;
        Manifest? m;
        try
        {
            if (zip.GetEntry(ManifestName) is not { Length: < 64 << 20 } entry) return null;
            using var s = entry.Open();
            m = JsonSerializer.Deserialize<Manifest>(s);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException) { return null; }
        if (m is not { Format: ManifestFormat } || m.Files is null || m.Files.Any(f => f is null || !IsEntry(f.Name))) return null;
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            if (e.FullName == ManifestName) continue;
            if (!IsEntry(e.FullName) || e.Length > MaxFile || !seen.Add(e.FullName) || (total += e.Length) > MaxTotal) return null;
        }
        return m;
    }

    private static byte[]? ReadEntry(ZipArchive zip, string name) =>
        zip.Entries.FirstOrDefault(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } e ? ReadEntry(e) : null;

    // the length in the zip is only a claim, so no more than it is read
    private static byte[]? ReadEntry(ZipArchiveEntry e)
    {
        if (e.Length > MaxFile) return null;
        var data = new byte[e.Length + 1];
        using var s = e.Open();
        int got = 0;
        while (got < data.Length)
        {
            int n = s.Read(data, got, data.Length - got);
            if (n == 0) break;
            got += n;
        }
        return got == e.Length ? data[..got] : null;
    }

    private sealed class PinnedDir(FolderPins pins, string dir) : IDisposable
    {
        public FolderPins Pins => pins;
        public string Dir { get; } = dir;
        public void Dispose() => pins.Dispose();
    }

    private static PinnedDir Pin(string root, string sub)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var parent = Path.GetDirectoryName(full);
        var anchor = parent;
        while (anchor is not null && !Directory.Exists(anchor)) anchor = Path.GetDirectoryName(anchor);
        if (anchor is null || FolderPins.RealPath(anchor) is not { } real) throw new DirectoryNotFoundException($"Nothing above {full} is there.");
        var dir = Path.Join(real, full[anchor.Length..].TrimStart('\\'), sub);
        if (anchor != parent && FolderPins.ThroughLink(anchor)) throw Linked(Path.Join(full, sub));
        var pins = HoldFrom(real, dir) ?? throw Linked(dir);
        return new PinnedDir(pins, dir);
    }

    private static IOException Linked(string dir) =>
        Shown.Mark(new IOException($"Nothing was restored, because {dir} goes through a link to somewhere else."));

    // a new name each time, never opened over one that's there, so a planted link can't catch it
    private static void Put(FolderPins pins, string dir, string name, byte[] data, DateTime? written)
    {
        SafeWrite.Replace(pins, dir, Path.Combine(dir, name), fs =>
        {
            fs.Write(data);
            fs.Flush();
            if (written is { } at) File.SetLastWriteTimeUtc(fs.SafeFileHandle, at.ToUniversalTime());
        });
    }
}
