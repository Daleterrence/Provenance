using System.Net.Sockets;

namespace Provenance.Common.Mirror;

internal static class MirrorFailure
{
    public static bool IsLocal(Exception ex) =>
        ex is UnauthorizedAccessException
        || (ex is IOException && ex is not HttpIOException
            && ex.InnerException is not (SocketException or HttpIOException));
}
