using System.Globalization;
using Provenance.Common.Game;
using Provenance.Common.IO;
using Serilog;

namespace Provenance.Common.Hook;

// files the app and pol_hook swap, Dir is admin-only so nothing unelevated can plant them
public static class HookFiles
{
    private static volatile string? _dir;

    public static string NativeDir => Path.Combine(AppContext.BaseDirectory, "native");
    public const string WatcherExe = "Provenance Watcher.exe";
    public const string HookDll = "pol_hook.dll";

    public static string? Dir
    {
        get => _dir;
        set => _dir = value is not null && string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A folder is needed.", nameof(value))
            : value;
    }

    public static string RequiredDir => _dir ?? throw new InvalidOperationException(
        "The folder of the hook's files is not set (HookFiles.Dir): they need a folder only administrators can open.");

    public static string PathFor(int pid, string name) => PathIn(RequiredDir, pid, name);

    internal static string PathIn(string dir, int pid, string name) =>
        Path.Combine(dir, string.Format(CultureInfo.InvariantCulture, HookContract.Files.Name, pid, name));

    public static string[] RequestPaths(int pid, string ext)
    {
        var path = PathFor(pid, ext);
        return [path, path + HookContract.Files.Taking];
    }

    // the hook plays the game's message sound for a message that arrives in the game unless this file's there
    public static void SetGameSound(bool on) => SetSwitchFile(HookContract.Files.Quiet, on, "the game's message sound");

    // the hook prints the friend online and offline lines unless this file's there
    public static void SetGameFriendLines(bool on) => SetSwitchFile(HookContract.Files.NoFriendLines, on, "the friend lines in the game");

    // and the lines for a friend starting or stopping FFXI unless this one is
    public static void SetGamePlayingLines(bool on) => SetSwitchFile(HookContract.Files.NoPlayingLines, on, "the playing lines in the game");

    public static void WriteShared(string name, string text)
    {
        if (_dir is null) return;
        try { AtomicFile.Write(Path.Combine(RequiredDir, name), text); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warning(e, "Could not write {Name} for the hook", name); }
    }

    private static void SetSwitchFile(string name, bool on, string what)
    {
        if (_dir is null) return;
        try
        {
            var path = Path.Combine(RequiredDir, name);
            if (on) File.Delete(path);
            else if (!File.Exists(path)) File.WriteAllBytes(path, []);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warning(e, "Could not change {What}", what); }
    }

    public static void Discard(int pid, string name)
    {
        foreach (var p in RequestPaths(pid, name))
            FileDelete.Try(p);
    }

    // the hook takes a request by renaming it, so this rename and a take can't both happen
    public static Withdrawal Withdraw(int pid, string name)
    {
        var path = PathFor(pid, name);
        for (int i = 0; ; i++)
        {
            try
            {
                File.Move(path, path + ".withdrawn", overwrite: true);
                FileDelete.Try(path + ".withdrawn");
                return Withdrawal.Withdrawn;
            }
            catch (FileNotFoundException) { return Withdrawal.Taken; }
            catch (IOException) when (i < 4 && File.Exists(path)) { Thread.Sleep(50); }
            catch { return File.Exists(path) ? Withdrawal.Unknown : Withdrawal.Taken; }
        }
    }

    public static bool Any(string name)
    {
        try { return _dir is { } dir && Directory.EnumerateFiles(dir, string.Format(CultureInfo.InvariantCulture, HookContract.Files.Name, "*", name)).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static void Delete(int pid, string name)
    {
        FileDelete.Try(PathFor(pid, name));
    }

    public static void ClearFor(int pid)
    {
        foreach (var name in new[] { HookContract.Files.Social, HookContract.Files.Store, HookContract.Files.ChatRoom,
                                     HookContract.Files.GroupChat, HookContract.Files.Lobby, HookContract.Files.Slots })
            Delete(pid, name);
        foreach (var name in HookContract.Requests.All)
            Discard(pid, name);
    }

    public static void RequestStop(int pid) => File.WriteAllText(PathFor(pid, HookContract.Files.Stop), "stop");

    public static void TryRequestStop(int pid)
    {
        try { RequestStop(pid); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warning(e, "Could not ask pol.exe {Pid} to stop", pid); }
    }

    public static void ClearHandoverFiles(int pid)
    {
        foreach (var name in new[] { HookContract.Files.Stop, HookContract.Files.Ended, HookContract.Files.Go }) Delete(pid, name);
    }

    public static void NotePrestart(int pid, ProcessStamp stamp)
    {
        try { File.WriteAllText(PathFor(pid, HookContract.Files.Prestart), stamp.StartUtcTicks.ToString(CultureInfo.InvariantCulture)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warning(e, "Could not note the early pol.exe {Pid} for other copies", pid); }
    }

    internal static void Transient(string path, string text) =>
        AtomicFile.Build(path, tmp => AtomicFile.WriteNew(tmp, new System.Text.UTF8Encoding(false).GetBytes(text)));

    public static string NewToken() => ShortId.New(12);

    public readonly record struct RequestBody(string Text);

    public static RequestBody Body(string text) => new(Clean(text, keepLineBreaks: true));

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
            Transient(PathFor(pid, name), string.Join("\n", lines));
            return true;
        }
        catch { return false; }
    }

    private static string Clean(string s, bool keepLineBreaks) =>
        new(s.Replace("\r", "").Select(c => c < ' ' && !(keepLineBreaks && c == '\n') ? ' ' : c).ToArray());

    // the app's own limits, the hook caps the store itself at 3MB
    public static class MaxBytes
    {
        public const int Social = 8 * 1024 * 1024;
        public const int Store = 8 * 1024 * 1024;
        public const int ChatRoom = 4 * 1024 * 1024;
        public const int GroupChat = 4 * 1024 * 1024;
        public const int MyProfile = 1024 * 1024;
        public const int Slots = 4096;
    }

    // the hook may have it open for writing and swap it at any moment, so it's read sharing both
    public static T? ReadJson<T>(int pid, string name, int maxBytes, Func<string, T?> parse) where T : class
    {
        var path = PathFor(pid, name);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > maxBytes) return null;
            return parse(SharedFile.ReadAllText(path));
        }
        catch { return null; }
    }

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
                    : ext.Equals(HookLogRelay.Marker, StringComparison.OrdinalIgnoreCase)
                        ? !File.Exists(PathIn(dir, pid, HookContract.Files.Log))
                        : nowUtc - f.LastWriteTimeUtc > OrphanAge && !isAlive(pid);
                if (!remove) continue;
                f.Refresh();
                if (!f.Exists) continue;
                f.Delete();
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return deleted;
    }

    internal static bool TryParse(string name, out int pid, out string ext)
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
