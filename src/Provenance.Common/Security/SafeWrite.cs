using Provenance.Common.IO;

namespace Provenance.Common.Security;

// a write into a folder a standard user could own holds every folder down to the file and checks again before the move
public static class SafeWrite
{
    public enum Backup { None, Keep, DropOld }

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
        using var pins = FolderPins.HoldFollowingLinksAboveOrThrow(top, dir);
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
                pins.Recheck(dir, full);
                File.Move(tmp, full, overwrite: true);
            }
            else
            {
                pins.Recheck(dir, full, bak);
                replacing = File.Exists(full);
                if (replacing) File.Replace(tmp, full, plan == Backup.Keep ? bak : null);
                else File.Move(tmp, full);
            }
        }
        catch
        {
            if (!replacing || File.Exists(full)) FileDelete.Try(tmp);
            throw;
        }
        if (plan == Backup.DropOld) File.Delete(bak);
    }

    public static void Replace(FolderPins pins, string dir, string path, Action<FileStream> write)
    {
        AtomicFile.Build(path, tmp =>
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) write(fs);
            pins.Recheck(dir);
        });
    }

    public static string? CreateFree(string dir, byte[] bytes, Func<int, string> nameFor)
    {
        LinkCheck.Refuse(dir);
        using var pins = FolderPins.HoldFollowingLinksAboveOrThrow(dir, dir);
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
                pins.Recheck(dir, target);
                try
                {
                    File.Move(tmp, target, overwrite: false);
                    return name;
                }
                catch (IOException) when (Path.Exists(target)) { }
            }
            finally { FileDelete.Try(tmp); }
        }
        return null;
    }
}
