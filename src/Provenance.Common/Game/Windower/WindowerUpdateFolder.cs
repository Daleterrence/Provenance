using System.IO.Compression;
using Provenance.Common.IO;
using Provenance.Common.Security;
using Serilog;

namespace Provenance.Common.Game.Windower;

// Windower's way of replacing a file: old one renamed into updates\temp\<guid> (works on a loaded DLL or EXE), new one moved in
internal sealed class WindowerUpdateFolder : IDisposable
{
    // ERROR_REPARSE_POINT_ENCOUNTERED, so a refusal isn't retried like a sharing violation
    private const int LinkFound = unchecked((int)0x80070000 | 4395);

    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);
    public readonly string Root;
    public readonly WindowerHttp Http;
    public readonly FolderPins Pins;
    public readonly List<string> Errors;
    public CancellationToken Ct { get; private set; }

    public WindowerUpdateFolder(string root, WindowerHttp http, FolderPins pins, List<string> errors, CancellationToken ct)
    {
        Root = root;
        Http = http;
        Pins = pins;
        Errors = errors;
        Ct = ct;
    }

    public void Retarget(CancellationToken ct) => Ct = ct;

    public static string TempDirOf(string windowerDir) => Path.Combine(windowerDir, "updates", "temp");

    public string TempDir => TempDirOf(Root);

    public static string Reason(Exception e, string ours)
    {
        if (Shown.Is(e)) return e.Message;
        Log.Debug("Windower update: {Line}", $"{ours} ({e.GetType().Name}: {e.Message})");
        return ours;
    }

    public static string WriteFailed(Exception e, string what) =>
        Reason(e, $"{what} couldn't be written. Try again, and if it keeps happening, check nothing else is using it and the drive isn't full.");

    public string NewTemp()
    {
        Directory.CreateDirectory(TempDir);
        return Path.Combine(TempDir, Guid.NewGuid().ToString());
    }

    public async Task<(WindowerFetch Fetch, string? Temp)> FetchToTemp(Uri site, string url, DateTime? since, string what,
                                                                       Func<string, bool>? usable = null, string? unusable = null)
    {
        var temp = NewTemp();
        var f = await Http.DownloadAsync(site, url, temp, since, Ct);
        if (!f.Ok)
        {
            Note(what, f);
            return (f, null);
        }
        if (usable is not null && !usable(temp))
        {
            Errors.Add(unusable!);
            Delete(temp);
            return (f, null);
        }
        return (f, temp);
    }

    public void Note(string what, WindowerFetch f)
    {
        switch (f.Outcome)
        {
            case WindowerFetchOutcome.HttpError:
                Errors.Add($"{what} couldn't be downloaded, {f.Host} answered {f.Status}.");
                break;
            case WindowerFetchOutcome.Unreachable:
                Errors.Add($"{f.Host} couldn't be reached, so nothing else was downloaded from it.");
                break;
            case WindowerFetchOutcome.Broken:
                Errors.Add($"{what} didn't download completely, so it wasn't used.");
                break;
        }
    }

    public bool Ready(string target)
    {
        var dir = Path.GetDirectoryName(target)!;
        bool pinned;
        try { pinned = Pins.Pin(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { pinned = false; }
        if (!pinned)
        {
            if (_refused.Add(dir)) Errors.Add($"{Path.GetRelativePath(Root, dir)} goes through a link, so nothing was written there.");
            return false;
        }
        if (!WindowerFiles.IsLinkOrFolder(target)) return true;
        Errors.Add($"{Path.GetRelativePath(Root, target)} is a link or a folder, so it wasn't replaced.");
        return false;
    }

    public async Task Step(Func<Task> step)
    {
        try { await step(); }
        catch (Exception e) when (e is not OperationCanceledException || !Ct.IsCancellationRequested)
        {
            Errors.Add(Reason(e, "Provenance ran into a problem it didn't expect. Try again in a moment."));
        }
    }

    public void Guard(string target)
    {
        var dir = Path.GetDirectoryName(target)!;
        if (!Pins.Intact(dir) || !Pins.Intact(TempDir) || WindowerFiles.IsLinkOrFolder(target))
            throw ThroughLink(Path.GetRelativePath(Root, target));
    }

    private static IOException ThroughLink(string what) =>
        Shown.Mark(new IOException($"{what} goes through a link, so nothing more was written there.") { HResult = LinkFound });

    public string? Backup(string file)
    {
        for (int tries = 1; ; tries++)
        {
            if (!File.Exists(file)) return null;
            var to = NewTemp();
            try
            {
                Guard(file);
                File.Move(file, to);
                return to;
            }
            catch (FileNotFoundException) { return null; }
            catch (IOException e) when (tries < 10 && e.HResult != LinkFound) { Thread.Sleep(50); }
        }
    }

    public void Swap(string fresh, string target)
    {
        var old = Backup(target);
        try
        {
            Guard(target);
            File.Move(fresh, target);
        }
        catch when (old is not null)
        {
            try
            {
                Guard(target);
                File.Move(old, target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public string WriteEntry(ZipArchiveEntry entry)
    {
        var temp = NewTemp();
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
            FileDelete.Try(temp);
            throw;
        }
    }

    public void Extract(ZipArchiveEntry entry, string target)
    {
        var temp = WriteEntry(entry);
        try { Swap(temp, target); }
        catch
        {
            FileDelete.Try(temp);
            throw;
        }
    }

    public void Move(string from, string to, bool overwrite = false)
    {
        Guard(to);
        File.Move(from, to, overwrite);
    }

    public void Delete(string path) => Pins.DeleteEntry(path);

    public void Cleanup()
    {
        Pins.Release(TempDir);
        DeleteTree(Pins, TempDir);
    }

    // never through a link, everything goes through FolderPins.DeleteEntry
    private static void DeleteTree(FolderPins parent, string dir)
    {
        using (var pins = FolderPins.HoldExact(dir, dir))
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
                    if (a.HasFlag(FileAttributes.Directory) && !(a.HasFlag(FileAttributes.ReparsePoint) && LinkCheck.IsRedirect(entry)))
                        DeleteTree(pins, entry);
                    else pins.DeleteEntry(entry);
                }
            }
        }
        parent.DeleteEntry(dir);
    }

    public void Dispose()
    {
        Http.Dispose();
        Pins.Dispose();
    }
}
