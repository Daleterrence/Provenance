using System.Globalization;

namespace Provenance.Common.Hook;

// files the app and pol_hook (inside pol.exe) swap. Dir is an admin-only folder handed to the hook as POL_IPC_DIR, so
// nothing unelevated can read or plant them
public static class HookFiles
{
    private static volatile string? _dir;

    public static string NativeDir => Path.Combine(AppContext.BaseDirectory, "native");
    public const string WatcherExe = "Provenance Watcher.exe";

    // set at start-up, and there's no fallback: a file path asked for before that throws
    public static string? Dir
    {
        get => _dir;
        set => _dir = value is not null && string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A folder is needed.", nameof(value))
            : value;
    }

    public static string RequiredDir => _dir ?? throw new InvalidOperationException(
        "The folder of the hook's files is not set (HookFiles.Dir): they need a folder only administrators can open.");

    public static string PathFor(int pid, string name) =>
        Path.Combine(RequiredDir, string.Format(CultureInfo.InvariantCulture, HookContract.Files.Name, pid, name));

    // a request and the .taking the hook renames it to while it reads it
    public static string[] RequestPaths(int pid, string ext)
    {
        var path = PathFor(pid, ext);
        return [path, path + HookContract.Files.Taking];
    }

    public static void DeleteRequest(int pid, string ext)
    {
        foreach (var p in RequestPaths(pid, ext))
            try { File.Delete(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static void Delete(int pid, string name)
    {
        try { File.Delete(PathFor(pid, name)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // what an old process with this pid left that its pol.exe's snapshots and chat rooms would otherwise start from,
    // and every request it never took. Not the stop, ended and go files, those are a session's own
    public static void ClearFor(int pid)
    {
        foreach (var name in new[] { HookContract.Files.Social, HookContract.Files.Store, HookContract.Files.ChatRoom,
                                     HookContract.Files.GroupChat })
            Delete(pid, name);
        foreach (var name in HookContract.Requests.All)
            DeleteRequest(pid, name);
    }

    // log out and exit, or exit having sent nothing. Throws when the file can't be written
    public static void RequestStop(int pid) => File.WriteAllText(PathFor(pid, HookContract.Files.Stop), "stop");

    // the hook inside a third-party hook's pol.exe writes its pid here
    public static string PidFile(string launchId) =>
        Path.Combine(RequiredDir, string.Format(CultureInfo.InvariantCulture, HookContract.Files.PidFile, launchId));

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
                bool remove = ext.Equals(HookContract.Files.Log, StringComparison.OrdinalIgnoreCase)
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
