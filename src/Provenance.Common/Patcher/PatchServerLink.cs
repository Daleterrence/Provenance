using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Provenance.Common.Patcher;

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
        // Nagle off and an ACK per segment, but no fixed socket buffers, they turn off Windows' receive-window tuning
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
