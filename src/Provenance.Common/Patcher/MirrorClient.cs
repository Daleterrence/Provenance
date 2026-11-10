using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Provenance.Common.IO;
using Provenance.Common.Mirror;

namespace Provenance.Common.Patcher;

// The trust boundary: pointers and indexes only come back once their signature checks out, and a download only replaces
// its target once its size and SHA-256 match the signed entry
public sealed class MirrorClient(HttpClient http, IReadOnlyDictionary<string, ECDsa> trusted, string baseUrl = MirrorLayout.BaseUrl)
{
    internal const long MaxSignedBytes = 16 * 1024 * 1024;

    public static MirrorClient Shared { get; } = new(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, MirrorKeys.Trusted);

    private readonly Uri _base = new(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");

    public async Task<MirrorPointer?> GetPointerAsync(string region, CancellationToken ct)
    {
        var payload = await GetSignedAsync(MirrorLayout.PointerKey(region), ct);
        if (payload is null) return null;
        try { return MirrorPointer.FromBytes(payload); }
        catch (InvalidDataException) { return null; }
    }

    public async Task<MirrorIndex?> GetIndexAsync(string region, string product, string version, CancellationToken ct)
    {
        var payload = await GetSignedAsync(MirrorLayout.PackedIndexKey(region, product, version), ct);
        if (payload is null) return null;
        try
        {
            var idx = MirrorIndex.FromBytes(payload);
            return idx.Format == MirrorIndex.PackedFormat && idx.Region == region && idx.Product == product && idx.Version == version ? idx : null;
        }
        catch (InvalidDataException) { return null; }
    }

    // the signed bytes come back too, so the caller can keep them and check them again on the next start
    public async Task<(MirrorBuilds List, byte[] Blob)?> GetBuildsAsync(CancellationToken ct)
    {
        var blob = await GetBlobAsync(MirrorLayout.BuildsKey, ct);
        return blob is not null && MirrorBuilds.FromSigned(blob, trusted) is { } list ? (list, blob) : null;
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

    // a failure still reads on to the entry's end, so the next entry in the span can follow
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

    private async Task<byte[]?> GetSignedAsync(string key, CancellationToken ct)
    {
        var blob = await GetBlobAsync(key, ct);
        return blob is not null && SignedBlob.TryOpen(blob, trusted, out var payload) ? payload : null;
    }

    // null on a 404 or a body over 16 MB. The 60s cap covers the whole read, not just the headers.
    private async Task<byte[]?> GetBlobAsync(string key, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(60));
        ct = limit.Token;
        using var resp = await http.GetAsync(new Uri(_base, key), HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxSignedBytes) return null;
        using var ms = new MemoryStream();
        try
        {
            await using var body = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buf, ct)) > 0)
            {
                if (ms.Length + n > MaxSignedBytes) return null;
                ms.Write(buf, 0, n);
            }
        }
        // a connection dropped part way through, so callers fall back as they would for no connection at all
        catch (IOException ex)
        {
            throw new HttpRequestException(HttpRequestError.ResponseEnded, $"the connection dropped while reading {key}: {ex.Message}", ex);
        }
        return ms.ToArray();
    }
}
