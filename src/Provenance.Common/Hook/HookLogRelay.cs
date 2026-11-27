using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Provenance.Common.Game;
using Serilog;

namespace Provenance.Common.Hook;

public static partial class HookLogRelay
{
    internal const string Marker = "relayed";
    private const int MaxRead = 4 * 1024 * 1024;
    private static readonly TimeSpan TooOld = TimeSpan.FromDays(2);
    private static readonly object Reading = new();

    internal static string Mask(string line)
    {
        int at = line.IndexOf("] ", StringComparison.Ordinal);
        var text = line.AsSpan(at < 0 ? 0 : at + 2);
        if (text.StartsWith("pol_hook: cwd ") || text.StartsWith("pol_hook: SetCurrentDirectory") || text.StartsWith("viewer_init:")) return line;
        bool search = text.StartsWith("friends: search ");
        line = Quoted().Replace(line, "'<hidden>'");
        if (search) line = DoubleQuoted().Replace(line, "\"<hidden>\"");
        line = ChatRoom().Replace(line, "$1<hidden>$2");
        return Launch().Replace(line, "$1<hidden>$2");
    }

    [GeneratedRegex(@"(?<=[ =])'.*'(?=[ ,)]|$)")]
    private static partial Regex Quoted();

    [GeneratedRegex("\".*\"")]
    private static partial Regex DoubleQuoted();

    [GeneratedRegex(@"(chat rooms: (?:creating|entering|leaving) ).*?( \(password\))?$")]
    private static partial Regex ChatRoom();

    [GeneratedRegex(@"(pol_hook: Windower/Ashita launch )\S+( \(pid file written\))")]
    private static partial Regex Launch();

    public static int RelayNew(string dir, int pid, Action<int, string>? write = null)
    {
        lock (Reading) return CopyNew(dir, pid, write ?? ToAppLog);
    }

    private static void ToAppLog(int pid, string line) => Log.Debug("pol_hook {Pid}: {Line:l}", pid, line);

    private static int CopyNew(string dir, int pid, Action<int, string> write)
    {
        try
        {
            string log = HookFiles.PathIn(dir, pid, HookContract.Files.Log), mark = HookFiles.PathIn(dir, pid, Marker);
            if (!File.Exists(log)) return 0;
            using var file = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long from = ReadMark(mark);
            if (from > file.Length) from = 0;
            file.Seek(from, SeekOrigin.Begin);
            var buffer = new byte[(int)Math.Min(file.Length - from, MaxRead)];
            int read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read == 0) return 0;
            int whole = Array.LastIndexOf(buffer, (byte)'\n', read - 1) + 1;
            if (whole == 0)
            {
                if (read == MaxRead) WriteMark(mark, from + read);
                return 0;
            }
            int count = 0;
            foreach (var raw in Encoding.Latin1.GetString(buffer, 0, whole).Split('\n'))
            {
                var line = Printable(raw.TrimEnd('\r'));
                if (line.Length == 0) continue;
                write(pid, Mask(line));
                count++;
            }
            WriteMark(mark, from + whole);
            return count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    public static bool StillWriting(int pid, DateTime lastWriteUtc) =>
        ProcessStamp.Of(pid) is { } now && now.IsRunning() && now.StartUtc <= lastWriteUtc;

    public static int CatchUp(string dir, Func<int, DateTime, bool> stillWriting, Action<int, string>? write = null)
    {
        int count = 0;
        try
        {
            foreach (var log in Directory.GetFiles(dir, "pol_hook.*.log"))
            {
                if (!HookFiles.TryParse(Path.GetFileName(log), out int pid, out string ext) || ext != HookContract.Files.Log) continue;
                var info = new FileInfo(log);
                if (stillWriting(pid, info.LastWriteTimeUtc)) continue;
                string mark = HookFiles.PathIn(dir, pid, Marker);
                if (DateTime.UtcNow - info.LastWriteTimeUtc > TooOld) WriteMark(mark, info.Length);
                else if (ReadMark(mark) != info.Length)
                {
                    Log.Debug("The hook lines that follow are from pol.exe {Pid}, which ended earlier (its log was last written {When:yyyy-MM-dd HH:mm:ss})",
                              pid, info.LastWriteTime);
                    count += RelayNew(dir, pid, write);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return count;
    }

    public static IDisposable Follow(string dir, int pid, Func<bool> stillRunning, TimeSpan every, Action<int, string>? write = null)
    {
        var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                write ??= ToAppLog;
                while (!stop.IsCancellationRequested)
                {
                    // asked first, so what a pol.exe wrote as it ended is still read
                    bool running = stillRunning();
                    lock (Reading)
                        if (!stop.IsCancellationRequested) CopyNew(dir, pid, write);
                    if (!running) return;
                    await Task.Delay(every, stop.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Debug(ex, "Stopped following the hook log of pol.exe {Pid}", pid); }
        });
        return new Following(stop);
    }

    private sealed class Following(CancellationTokenSource stop) : IDisposable
    {
        // waits out a copy that's under way
        public void Dispose()
        {
            stop.Cancel();
            lock (Reading) { }
        }
    }

    private static long ReadMark(string mark)
    {
        try { return File.Exists(mark) && long.TryParse(File.ReadAllText(mark).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long at) ? at : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static void WriteMark(string mark, long at) => File.WriteAllText(mark, at.ToString(CultureInfo.InvariantCulture));

    private static string Printable(string line)
    {
        if (line.All(c => c is >= ' ' and <= '~')) return line;
        return string.Create(line.Length, line, static (to, from) =>
        {
            for (int i = 0; i < to.Length; i++) to[i] = from[i] is >= ' ' and <= '~' ? from[i] : '?';
        });
    }
}
