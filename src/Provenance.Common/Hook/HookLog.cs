using System.Text;
using System.Text.RegularExpressions;
using Provenance.Common.IO;

namespace Provenance.Common.Hook;

// reads pol_hook's log and turns each line into a HookEvent. What it looks for are the lg() lines in pol_hook.c, named
// in HookContract.LogLines
public sealed class HookLog
{
    private static readonly Regex Language = new(Regex.Escape(HookContract.LogLines.ViewerLang) + @"(\d+)", RegexOptions.Compiled);
    private static readonly Regex AuthState = new(Regex.Escape(HookContract.LogLines.AuthState) + @"(\d+)", RegexOptions.Compiled);
    private static readonly Regex ProfileDone = new(Regex.Escape(HookContract.LogLines.Profile) + @"(\w+) -> (-?\d+)", RegexOptions.Compiled);
    private static readonly Regex ContentIds = new(Regex.Escape(HookContract.LogLines.ContentIds) + @"(\d+) valid, (\d+) FFXI", RegexOptions.Compiled);
    private static readonly Regex Reason = new(Regex.Escape(HookContract.LogLines.Reason) + @"(-?\d+)", RegexOptions.Compiled);
    private static readonly Regex Blocked = new(Regex.Escape(HookContract.LogLines.AccountBlocked) + @"(\S+)", RegexOptions.Compiled);
    private static readonly Regex LoadFailed = new(Regex.Escape(HookContract.LogLines.LoadFailed) + @"(\w+)", RegexOptions.Compiled);
    private static readonly Regex WatcherPid = new(Regex.Escape(HookContract.LogLines.WatcherRunning) + @"(\d+)\)", RegexOptions.Compiled);

    private readonly StringBuilder _pending = new();
    private long _offset;

    public HookLog(string path, long offset = 0)
    {
        Path = path;
        _offset = offset;
    }

    public string Path { get; }

    // starts at this run's first line, so a reused pid's older lines are skipped
    public static HookLog OfThisRun(string path)
    {
        long start = 0;
        try { if (File.Exists(path)) start = ThisRun(path).Start; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new HookLog(path, start);
    }

    // poor man's tail -f. Lines come back without their line ending, a half-written last line waits for the next read
    public List<string> ReadNewLines()
    {
        var lines = new List<string>();
        if (!File.Exists(Path)) return lines;
        try
        {
            using var fs = SharedFile.OpenRead(Path);
            if (fs.Length < _offset) _offset = 0;
            fs.Seek(_offset, SeekOrigin.Begin);
            var buf = new byte[fs.Length - _offset];
            int read = fs.Read(buf, 0, buf.Length);
            _offset += read;
            _pending.Append(Encoding.Latin1.GetString(buf, 0, read));
        }
        catch (IOException) { return lines; }
        var text = _pending.ToString();
        int last = text.LastIndexOf('\n');
        if (last < 0) return lines;
        lines.AddRange(text[..last].Split('\n').Select(l => l.TrimEnd('\r')));
        _pending.Clear();
        _pending.Append(text[(last + 1)..]);
        return lines;
    }

    // for when pol.exe's gone, so whatever it left without a line ending counts as a line
    public List<string> ReadRemainingLines()
    {
        var lines = ReadNewLines();
        if (_pending.Length > 0) lines.Add(_pending.ToString().TrimEnd('\r'));
        _pending.Clear();
        return lines;
    }

    // the hook only appends, so a reused pid's log starts with an older pol.exe's lines. DllMain's first line opens
    // each run, so the last one's this pol.exe's. Latin1 keeps one char per byte, so Start is a byte offset too
    private static (string Text, long Start) ThisRun(string path)
    {
        using var fs = SharedFile.OpenRead(path);
        using var sr = new StreamReader(fs, Encoding.Latin1, detectEncodingFromByteOrderMarks: false);
        var text = sr.ReadToEnd();
        int at = text.LastIndexOf(HookContract.LogLines.FirstLine, StringComparison.Ordinal);
        int start = at <= 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;
        return (text[start..], start);
    }

    // IO errors are left to the caller
    public static List<HookEvent> EventsOfThisRun(string path) =>
        ThisRun(path).Text.Split('\n').Select(Parse).OfType<HookEvent>().ToList();

    // one string search, it's polled every 100 ms. IO errors are left to the caller
    public static bool PrestartReadyInThisRun(string path) =>
        ThisRun(path).Text.Contains(HookContract.LogLines.PrestartReady, StringComparison.Ordinal);

    public static HookEvent? Parse(string line)
    {
        static bool Has(string line, string text) => line.Contains(text, StringComparison.Ordinal);
        if (Has(line, HookContract.LogLines.ViewerLang))
            return new ViewerStarted(line, Language.Match(line) is { Success: true } lm && int.TryParse(lm.Groups[1].Value, out var lang) ? lang : null);
        if (Has(line, HookContract.LogLines.RealAuth)) return new RealAuthStarted(line);
        if (AuthState.Match(line) is { Success: true } s && int.TryParse(s.Groups[1].Value, out var state)) return new AuthStateChanged(line, state);
        if (Has(line, HookContract.LogLines.AuthOk)) return new AuthSucceeded(line);
        if (ProfileDone.IsMatch(line)) return new ListLoaded(line);
        if (ContentIds.Match(line) is { Success: true } ci && int.TryParse(ci.Groups[1].Value, out var valid) && int.TryParse(ci.Groups[2].Value, out var ffxi))
            return new ContentIdsCounted(line, valid, ffxi);
        if (Has(line, HookContract.LogLines.Held)) return new SessionHeld(line);
        if (Has(line, HookContract.LogLines.StopRequested)) return new StopRequested(line);

        if (Has(line, HookContract.LogLines.DetailsUnreadable)) return new DetailsUnreadable(line);
        if (Has(line, HookContract.LogLines.AuthFailed))
            return new AuthRefused(line, Reason.Match(line) is { Success: true } rm && int.TryParse(rm.Groups[1].Value, out var reason) ? reason : null);
        if (Has(line, HookContract.LogLines.AccountBlocked)) return new AccountBlocked(line, Blocked.Match(line).Groups[1].Value);
        if (Has(line, HookContract.LogLines.LoadFailed)) return new ListLoadFailed(line, LoadFailed.Match(line).Groups[1].Value);
        if (Has(line, HookContract.LogLines.NoHandle)) return new NoHandle(line);
        if (Has(line, HookContract.LogLines.HandleRejected) || Has(line, HookContract.LogLines.HandleClaimFailed)
            || Has(line, HookContract.LogLines.HandleSaveFailed))
            return new HandleRefused(line);
        if (Has(line, HookContract.LogLines.NoSession)) return new NoSession(line);
        if (Has(line, HookContract.LogLines.HandleWanted)) return new HandleWanted(line);

        if (Has(line, HookContract.LogLines.PlayStarted)) return new PlayStarted(line);
        if (Has(line, HookContract.LogLines.PlayUnreadable)) return new PlayUnreadable(line);
        if (Has(line, HookContract.LogLines.PlayRefused)) return new PlayRefused(line);

        if (Has(line, HookContract.LogLines.PrestartReady)) return new PrestartReady(line);
        if (Has(line, HookContract.LogLines.Crash)) return new Crashed(line);
        return null;
    }

    // Provenance Watcher.exe's output, null if it didn't say it started a pol.exe
    public static int? StartedPid(string watcherOutput) =>
        WatcherPid.Match(watcherOutput) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var pid) ? pid : null;

    public static bool Attached(string watcherOutput) =>
        watcherOutput.Contains(HookContract.LogLines.WatcherAttached, StringComparison.Ordinal);
}
