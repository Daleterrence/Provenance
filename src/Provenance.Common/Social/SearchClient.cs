using System.Net.Sockets;
using System.Security.Cryptography;

namespace Provenance.Common.Social;

internal sealed record SearchAnswer(bool Ok, string Reason, IReadOnlyList<SearchEntry> Entries)
{
    public static SearchAnswer Failed(string reason) => new(false, reason, []);
}

// one connection and one request, closed after. The limit covers the connect, the write and all of the reply
internal static class SearchClient
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    public static async Task<SearchAnswer> AskAsync(SearchEndpoint at, IReadOnlyList<uint> ids, TimeSpan limit, CancellationToken stop)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timer.CancelAfter(limit);
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(at.Host, at.Port, timer.Token);
            var stream = tcp.GetStream();
            var half = RandomNumberGenerator.GetBytes(4);
            var x = RandomNumberGenerator.GetBytes(4);
            await stream.WriteAsync(SearchPackets.BuildIdList(ids, half, x), timer.Token);
            var entries = new List<SearchEntry>();
            for (int n = 0; n < SearchPackets.MaxPackets; n++)
            {
                var first = new byte[4];
                await stream.ReadExactlyAsync(first, timer.Token);
                int length = SearchPackets.ReplyLength(first);
                if (length == 0) return SearchAnswer.Failed("bad length");
                var packet = new byte[length];
                first.CopyTo(packet, 0);
                await stream.ReadExactlyAsync(packet.AsMemory(4), timer.Token);
                if (!SearchPackets.TryRead(packet, x, out var read) || read is null) return SearchAnswer.Failed("damaged reply");
                entries.AddRange(read.Entries);
                if (read.Final) return new SearchAnswer(true, "", entries);
            }
            return SearchAnswer.Failed("reply never ended");
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { return SearchAnswer.Failed("timed out"); }
        catch (OperationCanceledException) { return SearchAnswer.Failed("stopped"); }
        catch (EndOfStreamException) { return SearchAnswer.Failed("reply cut short"); }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException) { return SearchAnswer.Failed("connection failed"); }
    }
}
