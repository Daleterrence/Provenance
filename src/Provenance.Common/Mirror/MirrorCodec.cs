using System.Buffers;
using System.IO.Compression;

namespace Provenance.Common.Mirror;

public static class MirrorCodec
{
    private const int Quality = 6, Window = 22, Chunk = 81920;

    public static void Compress(Stream source, Stream destination)
    {
        using var enc = new BrotliEncoder(Quality, Window);
        var input = ArrayPool<byte>.Shared.Rent(Chunk);
        var output = ArrayPool<byte>.Shared.Rent(Chunk);
        try
        {
            int n;
            while ((n = source.Read(input, 0, Chunk)) > 0)
            {
                for (int offset = 0; offset < n;)
                {
                    var status = enc.Compress(input.AsSpan(offset, n - offset), output, out int consumed, out int written, isFinalBlock: false);
                    if (status == OperationStatus.InvalidData) throw new InvalidDataException("brotli encode failed");
                    if (written > 0) destination.Write(output, 0, written);
                    offset += consumed;
                }
            }
            OperationStatus fin;
            do
            {
                fin = enc.Compress(ReadOnlySpan<byte>.Empty, output, out _, out int written, isFinalBlock: true);
                if (fin == OperationStatus.InvalidData) throw new InvalidDataException("brotli encode failed");
                if (written > 0) destination.Write(output, 0, written);
            }
            while (fin == OperationStatus.DestinationTooSmall);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
            ArrayPool<byte>.Shared.Return(output);
        }
    }
}
