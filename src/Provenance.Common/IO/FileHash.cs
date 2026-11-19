using System.Security.Cryptography;

namespace Provenance.Common.IO;

public static class FileHash
{
    public static string Sha256(string path) => Sha256(path, out _);

    public static string Sha256(string path, out long length)
    {
        using var fs = SharedFile.OpenRead(path, 81920, FileOptions.SequentialScan);
        var sha = Sha256(fs);
        length = fs.Position;
        return sha;
    }

    public static string Sha256(Stream s)
    {
        if (s.CanSeek) s.Position = 0;
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
