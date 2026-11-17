using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Provenance.Common.IO;
using Provenance.Common.Security;

namespace Provenance.Common.Feeds;

public static class BannerImages
{
    private const int MaxImageBytes = 8 * 1024 * 1024;

    // redirects by hand: one mustn't take a banner download off POL's site
    internal static HttpClient ImageHttp { get; set; } =
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    internal static async Task<byte[]?> DownloadAsync(Uri u, CancellationToken ct) => (await FetchAsync(u, ct).ConfigureAwait(false)).Bytes;

    // Final is false when asking again later could work (a 5xx, a stalled body)
    private readonly record struct Fetched(byte[]? Bytes, bool Final);

    private static async Task<Fetched> FetchAsync(Uri u, CancellationToken ct)
    {
        for (int hop = 0; hop < 4; hop++)
        {
            if (PlayOnlineSite.UpgradeBannerUrl(u) is not { } safe) return new(null, true);
            u = safe;
            using var resp = await ImageHttp.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode is >= 300 and < 400)
            {
                if (resp.Headers.Location is not { } loc) return new(null, true);
                u = loc.IsAbsoluteUri ? loc : new Uri(u, loc);
                continue;   // the next pass upgrades it, or bails when it's left POL's site
            }
            if (!resp.IsSuccessStatusCode)
                return new(null, (int)resp.StatusCode is >= 400 and < 500 && resp.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests));
            if (resp.Content.Headers.ContentLength > MaxImageBytes) return new(null, true);
            // read in pieces and stop past the cap, so a reply with no length header never gets buffered whole
            // the client's timeout stops at the headers, so the body gets its own
            using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
            read.CancelAfter(ImageHttp.Timeout);
            try
            {
                await using var body = await resp.Content.ReadAsStreamAsync(read.Token).ConfigureAwait(false);
                using var ms = new MemoryStream();
                var chunk = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(chunk, read.Token).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length + n > MaxImageBytes) return new(null, true);
                    ms.Write(chunk, 0, n);
                }
                return new(ms.ToArray(), true);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(null, false); }
        }
        return new(null, true);
    }

    // addresses POL's site answered for with no picture, so a refresh never asks for one again
    // a timeout or no connection isn't an answer, so those get tried on the next refresh
    private static readonly ConcurrentDictionary<string, bool> Failed = new();

    // a topic's banner never changes, one already on disk needs no request
    public static async Task<string?> LocalAsync(string url, CancellationToken ct = default)
    {
        string? address = null;
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u0) || PlayOnlineSite.UpgradeBannerUrl(u0) is not { } u) return null;
            if (FeedCache.Folder("images") is not { } dir) return null;
            var ext = Path.GetExtension(u.AbsolutePath).ToLowerInvariant();
            if (ext is not (".jpg" or ".jpeg" or ".png" or ".gif")) ext = ".img";
            var path = Path.Combine(dir, FileHash.Sha256(Encoding.UTF8.GetBytes(u.AbsoluteUri))[..32] + ext);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }   // still in use, so the prune keeps it
                return path;
            }
            address = u.AbsoluteUri;
            if (Failed.ContainsKey(address)) return null;
            Directory.CreateDirectory(dir);
            var (bytes, final) = await FetchAsync(u, ct).ConfigureAwait(false);
            if (bytes is null)
            {
                if (final) Failed[address] = true;
                return null;
            }
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes || !LooksLikeImage(bytes))
            {
                Failed[address] = true;
                return null;
            }
            await AtomicFile.WriteAsync(path, bytes, ct).ConfigureAwait(false);
            return path;
        }
        catch { return null; }
    }

    internal static void ForgetFailures() => Failed.Clear();

    internal static bool LooksLikeImage(byte[] b) =>
        b.Length >= 4 && ((b[0] == 0xFF && b[1] == 0xD8) || (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||
                          (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46));

    public static void Forget(string? localPath)
    {
        try
        {
            if (localPath is null || FeedCache.Folder("images") is not { } dir) return;
            if (PathInside.IsUnder(dir, localPath)) File.Delete(Path.GetFullPath(localPath));
        }
        catch { }   // a leftover image just gets pruned later
    }

    public static void Prune(TimeSpan maxAge)
    {
        try
        {
            if (FeedCache.Folder("images") is not { } dir || !Directory.Exists(dir)) return;
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var f in Directory.EnumerateFiles(dir))
                if (File.GetLastWriteTimeUtc(f) < cutoff) { FileDelete.Try(f); }
        }
        catch { }   // it runs again next time
    }
}
