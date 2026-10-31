using System.IO.Compression;
using System.Reflection;
using Provenance.Common.IO;

namespace Provenance.Common.Game.Windower;

// Windower's way of replacing a file: the old one's renamed into updates\temp\<guid> (that works on a loaded DLL or
// EXE too) and the new one moved in
internal static class WindowerFiles
{
    public static string TempDir(string windowerDir) => Path.Combine(windowerDir, "updates", "temp");

    public static string NewTemp(string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        return Path.Combine(tempDir, Guid.NewGuid().ToString());
    }

    // guard throws when a folder on the way has turned into a link, and runs right before every move
    public static string? Backup(string file, string tempDir, Action guard)
    {
        for (int tries = 1; ; tries++)
        {
            if (!File.Exists(file)) return null;
            var to = NewTemp(tempDir);
            try
            {
                guard();
                File.Move(file, to);
                return to;
            }
            catch (FileNotFoundException) { return null; }
            catch (IOException e) when (tries < 10 && e.HResult != LinkFound) { Thread.Sleep(50); }
        }
    }

    // if the new file can't go in, the old one goes back where it was
    public static void Swap(string fresh, string target, string tempDir, Action guard)
    {
        var old = Backup(target, tempDir, guard);
        try
        {
            guard();
            File.Move(fresh, target);
        }
        catch when (old is not null)
        {
            try
            {
                guard();
                File.Move(old, target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // ERROR_REPARSE_POINT_ENCOUNTERED, so a refusal isn't retried like a sharing violation
    public const int LinkFound = unchecked((int)0x80070000 | 4395);

    public static IOException ThroughLink(string what) => new($"{what} goes through a link, so nothing more was written there.") { HResult = LinkFound };

    public static Version FileVersion(string file)
    {
        try
        {
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(file);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart);
        }
        catch (FileNotFoundException) { return new Version(); }
    }

    // Windower compares (and builds its User-Agent from) its assembly version. Only the metadata's read, nothing's loaded
    public static Version LauncherVersion(string exe)
    {
        if (!File.Exists(exe)) return new Version();
        try { return AssemblyName.GetAssemblyName(exe).Version ?? FileVersion(exe); }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return FileVersion(exe);
        }
    }

    // a captive portal's 200 shouldn't end up as Hook.dll
    public static bool LooksLikePe(string file)
    {
        try
        {
            using var f = File.OpenRead(file);
            return f.ReadByte() == 'M' && f.ReadByte() == 'Z';
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool IsPlainName(string name) =>
        name.Length > 0 && name.Trim() == name && name != "." && name != ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    public static string? Inside(string root, string relative)
    {
        if (relative.Length == 0 || relative.Contains(':') || relative.Contains('\0') || Path.IsPathRooted(relative)) return null;
        try
        {
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            return full.StartsWith(top, StringComparison.OrdinalIgnoreCase) && full.Length > top.Length ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static bool IsLinkOrFolder(string path)
    {
        try { return (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    public static bool IsZip(string file)
    {
        try
        {
            using var z = ZipFile.OpenRead(file);
            _ = z.Entries.Count;
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
    }

    // a new temp file with the entry in it, and the entry's own time (like Windower's ExtractToFile) set on the open
    // handle rather than by name afterwards
    public static string WriteEntry(ZipArchiveEntry entry, string tempDir)
    {
        var temp = NewTemp(tempDir);
        try
        {
            using var src = entry.Open();
            using var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            src.CopyTo(fs);
            fs.Flush();
            File.SetLastWriteTime(fs.SafeFileHandle, entry.LastWriteTime.DateTime);
            return temp;
        }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public static void Extract(ZipArchiveEntry entry, string target, string tempDir, Action guard)
    {
        var temp = WriteEntry(entry, tempDir);
        try { Swap(temp, target, tempDir, guard); }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // never through a link: everything goes through FolderPins.DeleteEntry, so a link is removed and not what it
    // points at. Each folder's held while it's emptied
    public static void DeleteTree(FolderPins parent, string dir)
    {
        using (var pins = FolderPins.Hold(dir, dir))
        {
            if (pins is not null)
            {
                List<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { entries = []; }
                foreach (var entry in entries)
                {
                    if (!pins.Intact(dir)) break;
                    FileAttributes a;
                    try { a = File.GetAttributes(entry); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                    if (a.HasFlag(FileAttributes.Directory) && !a.HasFlag(FileAttributes.ReparsePoint)) DeleteTree(pins, entry);
                    else pins.DeleteEntry(entry);
                }
            }
        }
        parent.DeleteEntry(dir);
    }
}

// Windower.exe holds Windower.Update.Mutex around every update and install. A mutex belongs to a thread and awaits hop
// threads, so a thread of its own holds this one
internal sealed class WindowerUpdateMutex : IDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private Thread? _thread;

    private WindowerUpdateMutex() { }

    // null when it's still held elsewhere after wait
    public static Task<WindowerUpdateMutex?> AcquireAsync(string name, TimeSpan wait, CancellationToken ct)
    {
        var hold = new WindowerUpdateMutex();
        var ready = new TaskCompletionSource<WindowerUpdateMutex?>(TaskCreationOptions.RunContinuationsAsynchronously);
        hold._thread = new Thread(() =>
        {
            Mutex m;
            try { m = new Mutex(false, name); }
            catch (Exception e)
            {
                ready.SetException(e);
                return;
            }
            bool owned = false;
            try
            {
                // an abandoned mutex is ours now, and gets released once like any other (Windower takes it twice and
                // keeps it till it exits)
                int got;
                try { got = WaitHandle.WaitAny([m, ct.WaitHandle], wait); }
                catch (AbandonedMutexException e) when (e.MutexIndex == 0) { got = 0; }
                owned = got == 0;
                if (got == 1) ready.SetCanceled(ct);
                else if (!owned) ready.SetResult(null);
                else
                {
                    ready.SetResult(hold);
                    hold._release.Wait();
                }
            }
            catch (Exception e) { ready.TrySetException(e); }
            finally
            {
                if (owned) m.ReleaseMutex();
                m.Dispose();
            }
        }) { IsBackground = true, Name = "Windower update mutex" };
        hold._thread.Start();
        return ready.Task;
    }

    public void Dispose()
    {
        _release.Set();
        _thread?.Join(TimeSpan.FromSeconds(5));
    }
}
