using System.Net;

namespace Provenance.Common.Patcher;

public sealed class PatchSession(PatchProduct product, IPatchConnector connector, string serverName = "update server", IPAddress? pinned = null)
{
    public const long KeepAliveMs = 60_000;
    public const long MaxFileListBytes = 256L << 20;   // the biggest file list is about 6 MB

    public PatchTarget Target { get; private set; } = new(product.Host, product.Port, pinned);

    public PatchConnection Connect(CancellationToken ct) =>
        connector.Open(Target, null, ct) ?? throw new InvalidOperationException("The patch connector gave no connection.");

    public PatchProtocol.VersionReply AskVersion(PatchServerLink link, string local, CancellationToken ct) =>
        PatchProtocol.ParseVersion(link.Exchange(PatchProtocol.VersionRequest(product.TagWire, product.ProductId, local), ct));

    public PolUpdater.RedirectDecision Screen(PatchProtocol.VersionReply r, IPAddress reached)
    {
        if (r.S0 == "unknown") throw new InvalidDataException($"PlayOnline's update server does not know {product.Name}.");
        var redirect = PolUpdater.DecideRedirect(r.S1, reached);
        if (redirect == PolUpdater.RedirectDecision.Refuse) throw new InvalidDataException($"PlayOnline's {serverName} named an unexpected host.");
        return redirect;
    }

    // null when s1 has no address, then Target and closeFirst are as they were
    public PatchConnection? Follow(PatchProtocol.VersionReply r, IDisposable? closeFirst, CancellationToken ct)
    {
        var next = connector.Open(new PatchTarget(r.S1, product.Port, Optional: true), closeFirst, ct);
        if (next is not null) Target = new PatchTarget(r.S1, product.Port, next.Address);
        return next;
    }

    public ReadOnlyMemory<byte> FileList(PatchServerLink link, CancellationToken ct) =>
        PatchProtocol.ParseManifest(link.Exchange(PatchProtocol.ManifestRequest(product.TagWire, product.ProductId), ct));

    public static void UnpackFileList(Stream packed, Stream text, CancellationToken ct, long max = MaxFileListBytes) =>
        PatchCodecs.Lzss(packed, text, ct, max);

    // one request at a time, 64 KB at most. typeByte takes the first byte of the first slice as the file's type and returns it
    public byte Download(PatchServerLink link, string path, long size, Stream to, bool typeByte, Action<long>? moved, CancellationToken ct)
    {
        byte type = 0;
        for (long got = 0; got < size;)
        {
            ct.ThrowIfCancellationRequested();
            int len = (int)Math.Min(size - got, PatchProtocol.MaxRange);
            var data = PatchProtocol.ParseRange(link.Exchange(PatchProtocol.RangeRequest(got, len, product.TagWire, product.ProductId, path), ct), len).Span;
            if (got == 0 && typeByte) { type = data[0]; data = data[1..]; }
            to.Write(data);
            got += len;
            moved?.Invoke(len);
        }
        return type;
    }

    // POL's keep-alive, a version request with no local version once the connection's been quiet for over a minute
    public void KeepAliveIfIdle(PatchServerLink link, long now, CancellationToken ct)
    {
        if (now - link.LastRequestTicks > KeepAliveMs) AskVersion(link, "", ct);
    }
}
