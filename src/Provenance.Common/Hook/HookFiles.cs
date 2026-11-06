using System.Globalization;
using Provenance.Common.IO;

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

    // Discard deletes the request and its .taking and can't say if the hook got there first. Withdraw says which
    // happened, so use it when the answer matters.
    public static void Discard(int pid, string name)
    {
        foreach (var p in RequestPaths(pid, name))
            try { File.Delete(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // the hook takes a request by renaming it, so this rename and a take can't both happen. A file a scanner has open
    // gets a few more tries
    public static Withdrawal Withdraw(int pid, string name)
    {
        var path = PathFor(pid, name);
        for (int i = 0; ; i++)
        {
            try
            {
                File.Move(path, path + ".withdrawn", overwrite: true);
                try { File.Delete(path + ".withdrawn"); } catch { }
                return Withdrawal.Withdrawn;
            }
            catch (FileNotFoundException) { return Withdrawal.Taken; }
            catch (IOException) when (i < 4 && File.Exists(path)) { Thread.Sleep(50); }
            catch { return File.Exists(path) ? Withdrawal.Unknown : Withdrawal.Taken; }
        }
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
            Discard(pid, name);
    }

    // log out and exit, or exit having sent nothing. Throws when the file can't be written
    public static void RequestStop(int pid) => File.WriteAllText(PathFor(pid, HookContract.Files.Stop), "stop");

    // what a request carries so a snapshot can say which job it answers
    public static string NewToken() => Guid.NewGuid().ToString("N")[..12];

    // free text for the last field of a request, the one place line breaks survive. Everything else is one line
    public readonly record struct RequestBody(string Text);

    public static RequestBody Body(string text) => new(Clean(text, keepLineBreaks: true));

    // one line per field, in the order the hook reads them. A field's control characters (tabs and line breaks too)
    // become spaces, so it can't split into two. Written whole, false when it couldn't be
    public static bool WriteRequest(int pid, string name, params object[] fields)
    {
        try
        {
            var lines = fields.Select(f => f switch
            {
                RequestBody b => b.Text,
                string s => Clean(s, keepLineBreaks: false),
                IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
                _ => Clean(f.ToString() ?? "", keepLineBreaks: false),
            });
            AtomicFile.Write(PathFor(pid, name), string.Join("\n", lines));
            return true;
        }
        catch { return false; }
    }

    private static string Clean(string s, bool keepLineBreaks) =>
        new(s.Replace("\r", "").Select(c => c < ' ' && !(keepLineBreaks && c == '\n') ? ' ' : c).ToArray());

    // the most of each of the hook's JSON files a reader takes, so one gone wrong is never read whole. The hook caps the
    // store at 3 MB and writes the others well under these
    public static class MaxBytes
    {
        public const int Social = 4 * 1024 * 1024;
        public const int Store = 8 * 1024 * 1024;
        public const int ChatRoom = 4 * 1024 * 1024;
        public const int GroupChat = 4 * 1024 * 1024;
        public const int MyProfile = 1024 * 1024;
    }

    // null when the file isn't there, is over maxBytes or doesn't parse. The hook may have it open for writing and may
    // swap it at any moment, so it's read sharing both
    public static T? ReadJson<T>(int pid, string name, int maxBytes, Func<string, T?> parse) where T : class
    {
        var path = PathFor(pid, name);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > maxBytes) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return parse(sr.ReadToEnd());
        }
        catch { return null; }
    }

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
