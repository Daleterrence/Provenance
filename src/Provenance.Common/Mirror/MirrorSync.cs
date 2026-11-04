using System.Security.Cryptography;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Mirror;

// runs as admin off a network index, so every write goes through SafePath and DecodeAsync checks each download first
public static class MirrorSync
{
    // a range holds lots of files, so a few streams are bound by bandwidth rather than round trips
    public const int DefaultParallel = 8;

    private const int Attempts = 3;
    private const long ReportStep = 4 * 1024 * 1024;
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

    public static TimeSpan RoundWait { get; set; } = TimeSpan.FromSeconds(2);

    // stricter than PolUpdater's check, empty segments get refused too
    public static string SafePath(string folder, string relative)
    {
        var rel = relative.Replace('/', '\\');
        if (rel.Length == 0 || Path.IsPathRooted(rel) || rel.Contains(':') || rel.Split('\\').Any(seg => seg is "" or ".." or "."))
            throw new InvalidDataException($"The mirror index names an unsafe path: {relative}");
        var root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(Path.Combine(root, rel));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The mirror index names a path outside its folder: {relative}");
        return full;
    }

    internal static string? FirstUnsafe(string folder, IEnumerable<MirrorEntry> entries)
    {
        try
        {
            foreach (var e in entries) SafePath(folder, e.Path);
            return null;
        }
        catch (InvalidDataException ex) { return ex.Message; }
    }

    public static string NameList(IReadOnlyCollection<string> names, int max = 10) =>
        string.Join(", ", names.Take(max)) + (names.Count > max ? $" and {names.Count - max} more" : "");

    public static IReadOnlyList<MirrorEntry> Diff(MirrorIndex from, MirrorIndex to)
    {
        var old = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in from.Files) old[e.Path] = e.Sha256;
        return to.Files.Where(e => !old.TryGetValue(e.Path, out var sha) || sha != e.Sha256).ToList();
    }

    // progress always ends on Done == Total. keepLargeAddressAware is for the app's game folders only, anything else
    // stays exact
    public static async Task<IReadOnlyList<MirrorEntry>> ScanAsync(string folder, MirrorIndex index,
        IProgress<(long Done, long Total)>? progress, CancellationToken ct, bool keepLargeAddressAware = false)
    {
        long total = index.Files.Sum(f => f.Size), done = 0;
        var damaged = new bool[index.Files.Count];
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct };
        await Parallel.ForEachAsync(Enumerable.Range(0, index.Files.Count), opts, async (i, token) =>
        {
            var entry = index.Files[i];
            long counted = 0, pending = 0;
            void Flush() { if (pending > 0) { var n = pending; pending = 0; progress?.Report((Interlocked.Add(ref done, n), total)); } }
            damaged[i] = !await MatchesAsync(folder, entry, keepLargeAddressAware, n => { counted += n; pending += n; if (pending >= ReportStep) Flush(); }, token);
            if (counted < entry.Size) pending += entry.Size - counted;
            Flush();
        });
        return index.Files.Where((_, i) => damaged[i]).ToList();
    }

    private static async Task<bool> MatchesAsync(string folder, MirrorEntry entry, bool keepLargeAddressAware, Action<long> read, CancellationToken ct)
    {
        try
        {
            var path = Path.Combine(folder, entry.Path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != entry.Size) return false;
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[81920];
            int n;
            while ((n = await fs.ReadAsync(buf, ct)) > 0)
            {
                hash.AppendData(buf, 0, n);
                read(n);
            }
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            return actual == entry.Sha256 || (keepLargeAddressAware && LargeAddressAware.Matches(entry.Path, entry.Sha256, actual));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static long DownloadSize(IEnumerable<MirrorEntry> work) => work.DistinctBy(Place).Sum(e => e.CompressedSize);

    private static (string?, long, string, long, long) Place(MirrorEntry e) => (e.Pack, e.Offset, e.Sha256, e.Size, e.CompressedSize);

    // a bad entry fails only itself, a network error or idle timeout its whole range
    public static async Task<IReadOnlyList<MirrorEntry>> DownloadAllAsync(MirrorClient client, string region, string folder,
        IReadOnlyList<MirrorEntry> work, int parallel, IProgress<(long Done, long Total)>? progress, CancellationToken ct,
        Action<MirrorEntry, Exception>? onFailure = null, TimeSpan? idleTimeout = null, Action<MirrorEntry, int, Exception>? onRetry = null)
    {
        var paths = work.Select(e => SafePath(folder, e.Path)).ToArray();
        var idle = idleTimeout ?? DefaultIdleTimeout;
        var locOf = new Location[work.Count];
        var at = new Dictionary<MirrorEntry, Location>(ReferenceEqualityComparer.Instance);
        foreach (var same in Enumerable.Range(0, work.Count).GroupBy(i => Place(work[i])))
        {
            var members = same.OrderBy(i => work[i].Path, StringComparer.Ordinal).ToArray();
            var loc = new Location(work[members[0]], members.Select(i => paths[i]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            foreach (var i in members) locOf[i] = loc;
            at[loc.Entry] = loc;
        }
        long total = at.Values.Sum(l => l.Entry.CompressedSize), done = 0;
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = ct };
        using var stopReports = new CancellationTokenSource();
        var reporter = progress is null ? Task.CompletedTask : ReportEveryAsync(progress, () => (Interlocked.Read(ref done), total), stopReports.Token);
        try
        {
            var pending = at.Values.ToList();
            for (var round = 1; round <= Attempts && pending.Count > 0; round++)
            {
                if (round > 1)
                {
                    for (var i = 0; i < work.Count; i++)
                        if (locOf[i].Last is { } why) onRetry?.Invoke(work[i], round - 1, why);
                    await Task.Delay(RoundWait, ct);
                }
                await Parallel.ForEachAsync(MirrorPacks.Plan(pending.Select(l => l.Entry)), opts, async (span, token) =>
                    await FetchSpanAsync(client, region, span, at, idle, n => Interlocked.Add(ref done, n), token));
                pending = pending.Where(l => l.Last is not null).ToList();
            }
            foreach (var l in pending) Interlocked.Add(ref done, l.Entry.CompressedSize);
        }
        finally
        {
            stopReports.Cancel();
            await reporter;
        }
        for (var i = 0; i < work.Count; i++)
            if (locOf[i].Last is { } why) onFailure?.Invoke(work[i], why);
        progress?.Report((Interlocked.Read(ref done), total));
        return work.Where((_, i) => locOf[i].Last is not null).ToList();
    }

    private static async Task FetchSpanAsync(MirrorClient client, string region, MirrorSpan span, Dictionary<MirrorEntry, Location> at,
        TimeSpan idle, Action<long> add, CancellationToken token)
    {
        // HttpClient.Timeout only covers the headers, this covers every wait for data and each read resets it. One
        // stalled object used to hold up a whole phase for minutes, which was fun
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(idle);
        int i = 0;
        long got = 0;
        try
        {
            await using var body = await client.OpenSpanAsync(region, span.Pack, span.Start, span.Length, cts.Token, _ => cts.CancelAfter(idle));
            var gap = new byte[81920];
            long pos = span.Start;
            for (; i < span.Entries.Count; i++)
            {
                var loc = at[span.Entries[i]];
                var e = loc.Entry;
                if (e.Offset < pos)
                {
                    loc.Last = new InvalidDataException($"mirror entry {e.Path} overlaps the entry before it in its pack");
                    continue;
                }
                for (long left = e.Offset - pos; left > 0;)
                {
                    int n = await body.ReadAsync(gap.AsMemory(0, (int)Math.Min(gap.Length, left)), cts.Token);
                    if (n == 0) throw new HttpIOException(HttpRequestError.ResponseEnded, "the mirror's answer ended early");
                    left -= n;
                }
                got = 0;
                try
                {
                    await MirrorClient.DecodeAsync(body, e, loc.Targets, cts.Token, n => { got += n; add(n); });
                    loc.Last = null;
                }
                // corrupt or a disk error, but all its bytes were read so the next entry can follow
                catch (Exception ex) when (ex is not OperationCanceledException && got == e.CompressedSize && Expected(ex))
                {
                    loc.Last = ex;
                    add(-got);
                }
                got = 0;
                pos = e.Offset + e.CompressedSize;
            }
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested) { Unfinished(ex); }
        catch (Exception ex) when (Expected(ex)) { Unfinished(ex); }

        void Unfinished(Exception ex)
        {
            add(-got);
            for (; i < span.Entries.Count; i++) at[span.Entries[i]].Last = ex;
        }
    }

    private static bool Expected(Exception ex) => ex is InvalidDataException or HttpRequestException or IOException or UnauthorizedAccessException;

    private sealed record Location(MirrorEntry Entry, string[] Targets)
    {
        public Exception? Last { get; set; }
    }

    private static async Task ReportEveryAsync(IProgress<(long Done, long Total)> progress, Func<(long, long)> read, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(ReportInterval);
        try { while (await timer.WaitForNextTickAsync(stop)) progress.Report(read()); }
        catch (OperationCanceledException) { }
    }
}

// reports on the calling thread, unlike Progress<T> which posts to the sync context
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
