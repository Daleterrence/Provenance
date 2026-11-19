using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Provenance.Common.IO;
using Provenance.Common.Mirror;
using Serilog;

namespace Provenance.Common.Patcher;

// pointers and indexes only come back once their signature checks out, and a download only replaces its target once size and SHA-256 match
public sealed class MirrorClient(HttpClient http, IReadOnlyDictionary<string, ECDsa> trusted, string baseUrl = MirrorLayout.BaseUrl,
                                 MirrorSeen? seen = null, TimeSpan? idleTimeout = null, TimeSpan? blobCeiling = null)
{
    internal const long MaxSignedBytes = 16 * 1024 * 1024;

    // 10 minutes lets a 3.4MB list through at 6KB/s, and the 16MB cap at 27KB/s
    internal static readonly TimeSpan DefaultBlobCeiling = TimeSpan.FromMinutes(10);

#pragma warning disable RS0030
    public static MirrorClient Shared { get; internal set; } =
        new(Named(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }), MirrorKeys.Trusted, MirrorLayout.BaseUrl, MirrorSeen.Shared);
#pragma warning restore RS0030

    private static HttpClient Named(HttpClient http)
    {
        HttpAgent.Set(http);
        return http;
    }

    private readonly Uri _base = new(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");

    public async Task<MirrorPointer?> GetPointerAsync(string region, CancellationToken ct)
    {
        var key = MirrorLayout.PointerKey(region);
        var payload = await GetSignedAsync(key, "pointer", ct);
        if (payload is null) return null;
        MirrorPointer pointer;
        try { pointer = MirrorPointer.FromBytes(payload); }
        catch (InvalidDataException)
        {
            Log.Warning("The mirror's {Key} didn't parse", key);
            return null;
        }
        if (pointer.Region != region)
        {
            Log.Warning("The mirror's {Key} is for region {Other}, not {Region}", key, pointer.Region, region);
            return null;
        }
        if (seen is null || seen.Accept(region, pointer.Published)) return pointer;
        Log.Warning("The mirror's pointer for {Region} is older than one already seen ({Published:u})", region, pointer.Published);
        return null;
    }

    public async Task<MirrorIndex?> GetIndexAsync(string region, string product, string version, CancellationToken ct)
    {
        var key = MirrorLayout.PackedIndexKey(region, product, version);
        var payload = await GetSignedAsync(key, "file list", ct);
        if (payload is null) return null;
        try
        {
            var idx = MirrorIndex.FromBytes(payload);
            if (idx.Format == MirrorIndex.PackedFormat && idx.Region == region && idx.Product == product && idx.Version == version) return idx;
            Log.Warning("The mirror's {Key} is for another region, product or version than the one asked for", key);
            return null;
        }
        catch (InvalidDataException)
        {
            Log.Warning("The mirror's {Key} didn't parse", key);
            return null;
        }
    }

    public async Task<(MirrorBuilds List, byte[] Blob)?> GetBuildsAsync(CancellationToken ct)
    {
        var blob = await GetBlobAsync(MirrorLayout.BuildsKey, "build list", ct);
        if (blob is null) return null;
        if (MirrorBuilds.FromSigned(blob, trusted) is { } list) return (list, blob);
        Log.Warning("The mirror's {Key} failed its signature check or didn't parse", MirrorLayout.BuildsKey);
        return null;
    }

    public async Task<Stream> OpenSpanAsync(string region, string pack, long start, long length, CancellationToken ct, Action<int>? received = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0) return Stream.Null;
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, MirrorLayout.PackKey(region, pack)));
        req.Headers.Range = new RangeHeaderValue(start, start + length - 1);
        var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            resp.EnsureSuccessStatusCode();
            long skip = resp.StatusCode switch
            {
                HttpStatusCode.PartialContent when resp.Content.Headers.ContentRange?.From == start => 0,
                HttpStatusCode.PartialContent => throw new InvalidDataException($"the mirror answered another range of pack {pack}"),
                HttpStatusCode.OK => start,
                _ => throw new HttpRequestException($"the mirror answered {(int)resp.StatusCode} to a range of pack {pack}", null, resp.StatusCode),
            };
            return new WindowStream(await resp.Content.ReadAsStreamAsync(ct), skip, length, received, resp);
        }
        catch
        {
            resp.Dispose();
            throw;
        }
    }

    public static async Task DecodeAsync(Stream span, MirrorEntry entry, IReadOnlyList<string> targets, CancellationToken ct, Action<int>? received = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(targets.Count);
        var input = new WindowStream(span, 0, entry.CompressedSize, received, null);
        var temps = new List<string>();
        try
        {
            foreach (var t in targets) Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            var temp = targets[0] + ".mirror";
            temps.Add(temp);
            string actual;
            await using (var br = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                actual = await InflateAsync(br, output, entry, ct);
            await input.DrainAsync(ct);
            if (actual != entry.Sha256) throw new InvalidDataException($"mirror object hash mismatch for {entry.Path}");
            for (var i = 1; i < targets.Count; i++)
            {
                var copy = targets[i] + ".mirror";
                temps.Add(copy);
                File.Copy(temp, copy, overwrite: true);
                AtomicFile.ReplaceKeepingReadOnly(copy, targets[i]);
            }
            AtomicFile.ReplaceKeepingReadOnly(temp, targets[0]);
        }
        catch (Exception ex)
        {
            foreach (var t in temps) FileDelete.Try(t);
            if (input.Broken || ex is OperationCanceledException) throw;
            await input.DrainAsync(ct);
            throw;
        }
    }

    private static async Task<string> InflateAsync(BrotliStream br, FileStream output, MirrorEntry entry, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await ReadBodyAsync(br, buf, ct)) > 0)
        {
            total += n;
            if (total > entry.Size) throw new InvalidDataException($"mirror object is larger than declared for {entry.Path}");
            hash.AppendData(buf, 0, n);
            await output.WriteAsync(buf.AsMemory(0, n), ct);
        }
        if (total != entry.Size) throw new InvalidDataException($"mirror object size mismatch for {entry.Path}");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed class WindowStream(Stream inner, long skip, long length, Action<int>? read, IDisposable? owner) : Stream
    {
        private long _skip = skip, _left = length;

        public bool Broken { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.IsEmpty || (_skip == 0 && _left == 0)) return 0;
            try
            {
                while (_skip > 0) _skip -= await NextAsync(buffer[..(int)Math.Min(buffer.Length, _skip)], ct);
                if (_left == 0) return 0;
                int n = await NextAsync(buffer[..(int)Math.Min(buffer.Length, _left)], ct);
                _left -= n;
                return n;
            }
            catch
            {
                Broken = true;
                throw;
            }
        }

        public async Task DrainAsync(CancellationToken ct)
        {
            if (_skip == 0 && _left == 0) return;
            var buf = new byte[81920];
            while (await ReadAsync(buf, ct) > 0) { }
        }

        private async ValueTask<int> NextAsync(Memory<byte> buffer, CancellationToken ct)
        {
            int n = await inner.ReadAsync(buffer, ct);
            if (n == 0) throw new HttpIOException(HttpRequestError.ResponseEnded, "the mirror's answer ended early");
            read?.Invoke(n);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && owner is not null)
            {
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // a body that isn't Brotli makes the decoder throw InvalidOperationException, which is really just corrupt content
    private static async Task<int> ReadBodyAsync(BrotliStream br, byte[] buf, CancellationToken ct)
    {
        try { return await br.ReadAsync(buf, ct); }
        catch (InvalidOperationException ex) { throw new InvalidDataException("mirror object is not valid compressed data", ex); }
    }

    private async Task<byte[]?> GetSignedAsync(string key, string noun, CancellationToken ct)
    {
        var blob = await GetBlobAsync(key, noun, ct);
        if (blob is null) return null;
        if (SignedBlob.TryOpen(blob, trusted, out var payload)) return payload;
        Log.Warning("The mirror's {Key} failed its signature check", key);
        return null;
    }

    private static byte[]? TooBig(string key)
    {
        Log.Warning("The mirror's {Key} is over {Max} bytes", key, MaxSignedBytes);
        return null;
    }

    private async Task<byte[]?> GetBlobAsync(string key, string noun, CancellationToken ct)
    {
        var idle = idleTimeout ?? MirrorDownloads.DefaultIdleTimeout;
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ceiling.CancelAfter(blobCeiling ?? DefaultBlobCeiling);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ceiling.Token);
        stall.CancelAfter(idle);
        long got = 0;
        long? total = null;
        var answered = false;
        try
        {
            using var resp = await http.GetAsync(new Uri(_base, key), HttpCompletionOption.ResponseHeadersRead, stall.Token);
            answered = true;
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                Log.Information("The mirror has no {Key} yet", key);
                return null;
            }
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"the mirror answered {(int)resp.StatusCode} for its {noun} ({key})", null, resp.StatusCode);
            total = resp.Content.Headers.ContentLength;
            if (total > MaxSignedBytes) return TooBig(key);
            stall.CancelAfter(idle);
            using var ms = new MemoryStream();
            await using var body = await resp.Content.ReadAsStreamAsync(stall.Token);
            var buf = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buf, stall.Token)) > 0)
            {
                stall.CancelAfter(idle);
                got += n;
                if (ms.Length + n > MaxSignedBytes) return TooBig(key);
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var how = ceiling.IsCancellationRequested
                ? $"took too long ({Progress(got, total)} after {Seconds(blobCeiling ?? DefaultBlobCeiling)}, gave up, {key})"
                : !answered
                    ? $"didn't answer in {Seconds(idle)} ({key})"
                    : $"stalled ({Progress(got, total)} after {Seconds(idle)} with nothing new, {key})";
            throw new HttpRequestException(HttpRequestError.ConnectionError, $"the mirror's {noun} {how}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null && !answered)
        {
            var why = ex.InnerException?.Message ?? ex.Message;
            var what = ex.HttpRequestError == HttpRequestError.NameResolutionError ? "DNS lookup for the mirror failed" : "couldn't connect to the mirror";
            throw new HttpRequestException(ex.HttpRequestError, $"{what} for its {noun} ({key}): {why}", ex);
        }
        catch (IOException ex)
        {
            throw new HttpRequestException(HttpRequestError.ResponseEnded,
                $"the connection dropped mid-file in the mirror's {noun} ({Progress(got, total)}, {key}): {ex.Message}", ex);
        }
    }

    private static string Progress(long got, long? total) => total is { } t ? $"{Size(got)} of {Size(t)}" : Size(got);

    private static string Seconds(TimeSpan span) => span.TotalSeconds >= 10 ? $"{span.TotalSeconds:0}s" : $"{span.TotalSeconds:0.#}s";

    private static string Size(long bytes) => bytes < 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.0} MB");
}
