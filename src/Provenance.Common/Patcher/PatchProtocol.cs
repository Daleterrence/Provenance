using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Provenance.Common.Patcher;

// app.dll's patch frames (0x102a6235 and friends): u32 length, MD5(frame[8..])[0..4], "POLP", u32 command, body
public static class PatchProtocol
{
    public const int Manifest = 1, ManifestData = 2, Range = 3, RangeData = 4, Version = 7, VersionData = 8;
    public const int MaxRange = 0x10000;

    private static byte[] Frame(int cmd, int length)
    {
        var f = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(f, length);
        "POLP"u8.CopyTo(f.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(12), cmd);
        return f;
    }

    private static byte[] Seal(byte[] f)
    {
        MD5.HashData(f.AsSpan(8)).AsSpan(0, 4).CopyTo(f.AsSpan(4));
        return f;
    }

    private static void Ids(Span<byte> at, string tag, string product)
    {
        Encoding.ASCII.GetBytes(tag.AsSpan(0, Math.Min(3, tag.Length)), at);
        Encoding.ASCII.GetBytes(product.AsSpan(0, 4), at[4..]);
    }

    public static byte[] ManifestRequest(string tag, string product)
    {
        var f = Frame(Manifest, 0x18);
        Ids(f.AsSpan(16), tag, product);
        return Seal(f);
    }

    public static byte[] RangeRequest(long offset, int length, string tag, string product, string path)
    {
        var p = Encoding.Latin1.GetBytes(path);
        var f = Frame(Range, p.Length + 0x25);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(16), checked((uint)offset));
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(20), length);
        Ids(f.AsSpan(24), tag, product);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(32), p.Length + 1);
        p.CopyTo(f.AsSpan(36));
        return Seal(f);
    }

    public static byte[] VersionRequest(string tag, string product, string localVersion)
    {
        var f = Frame(Version, 0x58);
        Ids(f.AsSpan(16), tag, product);
        var v = Encoding.Latin1.GetBytes(localVersion);
        v.AsSpan(0, Math.Min(v.Length, 63)).CopyTo(f.AsSpan(24));
        return Seal(f);
    }

    public static void Check(ReadOnlySpan<byte> f, int cmd)
    {
        if (f.Length < 16 || BinaryPrimitives.ReadInt32LittleEndian(f) != f.Length || !f.Slice(8, 4).SequenceEqual("POLP"u8))
            throw new InvalidDataException("PlayOnline's update server sent a damaged reply.");
        if (!MD5.HashData(f[8..]).AsSpan(0, 4).SequenceEqual(f.Slice(4, 4)))
            throw new InvalidDataException("PlayOnline's update server sent a reply that failed its check.");
        if (BinaryPrimitives.ReadInt32LittleEndian(f[12..]) != cmd)
            throw new InvalidDataException($"PlayOnline's update server sent reply {BinaryPrimitives.ReadInt32LittleEndian(f[12..])}, not {cmd}.");
    }

    public sealed record VersionReply(string Server, string S0, string S1, string S2);

    public static VersionReply ParseVersion(byte[] f)
    {
        Check(f, VersionData);
        if (f.Length < 92) throw new InvalidDataException("PlayOnline's update server sent a short version reply.");
        int len = BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(88));
        if (len < 0 || len >= 0x100 || 92 + len > f.Length) throw new InvalidDataException("PlayOnline's update server sent a bad version.");
        var server = Encoding.Latin1.GetString(f, 92, len).TrimEnd('\0');
        var strings = Encoding.Latin1.GetString(f, 24, 64).Split('\0');
        string At(int i) => i < strings.Length ? strings[i] : "";
        return new VersionReply(server, At(0), At(1), At(2));
    }

    public static ReadOnlyMemory<byte> ParseManifest(byte[] f)
    {
        Check(f, ManifestData);
        if (f.Length < 21 || f[16] != 0x02) throw new InvalidDataException("PlayOnline's update server sent a manifest of an unknown kind.");
        return f.AsMemory(17);
    }

    public static ReadOnlyMemory<byte> ParseRange(byte[] f, int requested)
    {
        Check(f, RangeData);
        if (f.Length < 28) throw new InvalidDataException("PlayOnline's update server sent a short file part.");
        int len = BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(20));
        int pathLen = BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(24));
        if (len < 0 || len > MaxRange || len != requested || pathLen < 0 || 28L + pathLen + len > f.Length)
            throw new InvalidDataException("PlayOnline's update server sent a damaged file part.");
        return f.AsMemory(28 + pathLen, len);
    }
}

public sealed class PatchServerNetworkException : IOException
{
    public PatchServerNetworkException(string message, Exception? inner = null) : base(message, inner) => Shown.Mark(this);
}

// one request at a time and no retries, same as POL (app.dll at 0x102a6bf5)
public sealed class PatchServerLink : IDisposable
{
    private static readonly TimeSpan Silence = TimeSpan.FromMinutes(2);
    private readonly TcpClient? _tcp;
    private readonly Stream _s;

    public long LastRequestTicks { get; private set; } = Environment.TickCount64;

    private PatchServerLink(TcpClient? tcp, Stream s) => (_tcp, _s) = (tcp, s);

    public static PatchServerLink Over(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new PatchServerLink(null, stream);
    }

    public static PatchServerLink Connect(IPEndPoint ep, CancellationToken ct)
    {
        // Nagle off and an ACK per segment, but no fixed socket buffers, they turn off Windows' receive-window tuning.
        // 381 ms per 64 KB request against POL's 590, which is pretty neat.
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try { tcp.Client.IOControl(unchecked((int)0x98000017) /* SIO_TCP_SET_ACK_FREQUENCY */, BitConverter.GetBytes(1), null); }
        catch (SocketException) { }   // older Windows, delayed ACKs it is
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            tcp.ConnectAsync(ep, limit.Token).AsTask().GetAwaiter().GetResult();
            return new PatchServerLink(tcp, tcp.GetStream());
        }
        catch (Exception e)
        {
            tcp.Dispose();
            if (e is OperationCanceledException && !ct.IsCancellationRequested)
                throw new PatchServerNetworkException("PlayOnline's update server didn't answer. Check your internet connection, then try again.");
            if (e is SocketException) throw new PatchServerNetworkException("Couldn't reach PlayOnline's update server. Check your internet connection, then try again.", e);
            throw;
        }
    }

    public byte[] Exchange(byte[] request, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Silence);
        try
        {
            _s.WriteAsync(request, limit.Token).AsTask().GetAwaiter().GetResult();
            LastRequestTicks = Environment.TickCount64;
            var head = new byte[4];
            _s.ReadExactlyAsync(head, limit.Token).AsTask().GetAwaiter().GetResult();
            int len = BinaryPrimitives.ReadInt32LittleEndian(head);
            if (len < 16 || len > 64 << 20) throw new InvalidDataException("PlayOnline's update server sent a reply of an impossible size.");
            var f = new byte[len];
            head.CopyTo(f, 0);
            _s.ReadExactlyAsync(f.AsMemory(4), limit.Token).AsTask().GetAwaiter().GetResult();
            return f;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PatchServerNetworkException("PlayOnline's update server stopped answering. Try again in a moment.");
        }
        catch (EndOfStreamException e)
        {
            throw new PatchServerNetworkException("PlayOnline's update server closed the connection. Try again in a moment.", e);
        }
        catch (Exception e) when (e is IOException and not PatchServerNetworkException || e is WebSocketException)
        {
            throw new PatchServerNetworkException("The connection to PlayOnline's update server dropped. Try again in a moment.", e);
        }
        // a WebSocketStream stops reading and writing once its WebSocket has closed
        catch (NotSupportedException e) when (!_s.CanRead || !_s.CanWrite)
        {
            throw new PatchServerNetworkException("PlayOnline's update server closed the connection. Try again in a moment.", e);
        }
    }

    public void Dispose()
    {
        _s.Dispose();
        _tcp?.Dispose();
    }
}
