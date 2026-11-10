using System.Net;
using System.Net.Sockets;

namespace Provenance.Common.Patcher;

public sealed record PatchTarget(string Host, int Port, IPAddress? Pinned = null, bool Optional = false);

public sealed record PatchConnection(PatchServerLink Link, IPAddress Address);

public interface IPatchConnector
{
    // closeFirst goes once the address is known and before the connect, null only when an optional target has no address
    PatchConnection? Open(PatchTarget target, IDisposable? closeFirst, CancellationToken ct);
}

public sealed class DirectPatchConnector : IPatchConnector
{
    public static readonly DirectPatchConnector Instance = new();

    internal Func<string, CancellationToken, IPAddress?> Resolve { get; init; } = (host, ct) =>
        Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).GetAwaiter().GetResult().FirstOrDefault();

    public PatchConnection? Open(PatchTarget target, IDisposable? closeFirst, CancellationToken ct)
    {
        var ip = target.Pinned ?? Resolve(target.Host, ct);
        if (ip is null || (target.Optional && ip.Equals(IPAddress.Any)))
            return target.Optional
                ? null
                : throw Shown.Mark(new IOException("Couldn't find PlayOnline's update server. Check your internet connection, then try again."));
        closeFirst?.Dispose();
        return new PatchConnection(PatchServerLink.Connect(new IPEndPoint(ip, target.Port), ct), ip);
    }
}
