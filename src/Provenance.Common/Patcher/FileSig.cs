using System.Buffers;
using System.Security.Cryptography;

namespace Provenance.Common.Patcher;

// POL's file signature (worker op 15): signed-char byte sum, first 4 MD5 bytes as a LE int, length
public readonly record struct FileSig(int Sum, int Md5Word, long Length)
{
    public static FileSig Of(string path, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Of(fs, ct);
    }

    public static FileSig Of(Stream s, CancellationToken ct = default)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            int sum = 0, n;
            long len = 0;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                md5.AppendData(buf, 0, n);
                for (int i = 0; i < n; i++) sum = unchecked(sum + (sbyte)buf[i]);
                len += n;
            }
            return new FileSig(sum, BitConverter.ToInt32(md5.GetHashAndReset(), 0), len);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }
}
