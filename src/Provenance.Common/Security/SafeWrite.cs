using Provenance.Common.IO;

namespace Provenance.Common.Security;

// Provenance runs as admin, so a write into a folder a standard user could own has to hold every folder down to the
// file, refuse links, and check again right before the move. Callers say what happens to a backup.
public static class SafeWrite
{
    public enum Backup { None, Keep, DropOld }

    // backup null moves over whatever's there, otherwise the policy is asked under the held folders before anything
    // is written
    public static void Replace(string path, byte[] bytes, string top, Func<string, Backup>? backup = null,
                               IEnumerable<string>? refuse = null, bool flushToDisk = false)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        var bak = full + ".bak";
        var tmp = AtomicFile.TempFor(full);
        List<string> check = [.. refuse ?? [], full];
        if (backup is not null) check.Add(bak);
        check.Add(tmp);
        LinkCheck.Refuse(check);
        using var pins = LinkCheck.Hold(top, dir);
        full = pins.Real(full);
        bak = pins.Real(bak);
        tmp = pins.Real(tmp);
        var plan = backup?.Invoke(full) ?? Backup.None;
        bool replacing = false;
        try
        {
            AtomicFile.WriteNew(tmp, bytes, flushToDisk);
            if (backup is null)
            {
                LinkCheck.Recheck(pins, dir, full);
                File.Move(tmp, full, overwrite: true);
            }
            else
            {
                LinkCheck.Recheck(pins, dir, full, bak);
                replacing = File.Exists(full);
                if (replacing) File.Replace(tmp, full, plan == Backup.Keep ? bak : null);
                else File.Move(tmp, full);
            }
        }
        catch
        {
            // a Replace that failed halfway can leave the temp file as the only copy
            if (!replacing || File.Exists(full)) AtomicFile.Discard(tmp);
            throw;
        }
        if (plan == Backup.DropOld) File.Delete(bak);
    }

    // for folders the caller already holds: the write goes in a temp, the held folders are checked, then it's moved over
    public static void Replace(FolderPins pins, string dir, string path, Action<FileStream> write)
    {
        var tmp = AtomicFile.TempFor(path);
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) write(fs);
            LinkCheck.Recheck(pins, dir);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            AtomicFile.Discard(tmp);
            throw;
        }
    }

    // the first name nameFor gives (1, 2, 3...) that isn't taken gets the bytes and is returned, never over a file
    // that's there. null if none of the first 999 is free
    public static string? CreateFree(string dir, byte[] bytes, Func<int, string> nameFor)
    {
        LinkCheck.Refuse(dir);
        using var pins = LinkCheck.Hold(dir, dir);
        for (int n = 1; n < 1000; n++)
        {
            var name = nameFor(n);
            var target = pins.Real(Path.Combine(dir, name));
            var tmp = AtomicFile.TempFor(target);
            LinkCheck.Refuse(target, tmp);
            if (Path.Exists(target)) continue;
            try
            {
                AtomicFile.WriteNew(tmp, bytes);
                LinkCheck.Recheck(pins, dir, target);
                try
                {
                    File.Move(tmp, target, overwrite: false);
                    return name;
                }
                catch (IOException) when (Path.Exists(target)) { }
            }
            finally { AtomicFile.Discard(tmp); }
        }
        return null;
    }
}
