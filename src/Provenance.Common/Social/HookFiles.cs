using System.Globalization;

namespace Provenance.Common.Social;

// files the app and pol_hook (inside pol.exe) swap. Dir is an admin-only folder handed to the hook as POL_IPC_DIR, so
// nothing unelevated can read or plant them
public static class HookFiles
{
    private static volatile string _dir = Path.GetTempPath();   // placeholder, start-up sets the admin-only one first

    public static string Dir
    {
        get => _dir;
        set => _dir = string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A folder is needed.", nameof(value)) : value;
    }

    public static string PathFor(int pid, string ext) => Path.Combine(Dir, $"pol_hook.{pid}.{ext}");

    // a request, the .taking the hook renames it to while it reads it, and the .tmp the app writes it under first
    public static string[] RequestPaths(int pid, string ext, bool tmp = true)
    {
        var path = PathFor(pid, ext);
        return tmp ? [path, path + ".taking", path + ".tmp"] : [path, path + ".taking"];
    }

    public static void DeleteRequest(int pid, string ext)
    {
        foreach (var p in RequestPaths(pid, ext))
            try { File.Delete(p); } catch { }
    }

    // the hook inside a third-party hook's pol.exe writes its pid here
    public static string PidFile(string launchId) => Path.Combine(Dir, $"provenance.{launchId}.pid");

    // the owning copy may still read its .ended file, and a new process can reuse the pid
    public static readonly TimeSpan OrphanAge = TimeSpan.FromMinutes(10);

    public static int Sweep(string dir, Func<int, bool> isAlive, TimeSpan logAge, DateTime nowUtc)
    {
        int deleted = 0;
        IEnumerable<FileInfo> files;
        try { files = new DirectoryInfo(dir).EnumerateFiles("pol_hook.*").ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
        foreach (var f in files)
        {
            if (!TryParse(f.Name, out int pid, out string ext)) continue;
            try
            {
                bool remove = ext.Equals("log", StringComparison.OrdinalIgnoreCase)
                    ? nowUtc - f.LastWriteTimeUtc > logAge
                    : nowUtc - f.LastWriteTimeUtc > OrphanAge && !isAlive(pid);
                if (!remove) continue;
                f.Refresh();
                if (!f.Exists) continue;
                f.Delete();
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // next start-up has a go
        }
        return deleted;
    }

    private static bool TryParse(string name, out int pid, out string ext)
    {
        pid = 0;
        ext = "";
        const string prefix = "pol_hook.";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        int dot = name.IndexOf('.', prefix.Length);
        if (dot <= prefix.Length || dot == name.Length - 1) return false;
        var digits = name.AsSpan(prefix.Length, dot - prefix.Length);
        foreach (var c in digits) if (c is < '0' or > '9') return false;
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out pid)) return false;
        ext = name[(dot + 1)..];
        return true;
    }
}
