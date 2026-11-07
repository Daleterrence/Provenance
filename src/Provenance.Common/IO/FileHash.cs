using System.Security.Cryptography;

namespace Provenance.Common.IO;

// lowercase hex SHA-256, which is also how mirror objects are named
public static class FileHash
{
    public static string Sha256(string path) => Sha256(path, out _);

    // the sharing lets it hash a file that something else has open, even one pol.exe is running from
    public static string Sha256(string path, out long length)
    {
        using var fs = SharedFile.OpenRead(path, 81920, FileOptions.SequentialScan);
        var sha = Sha256(fs);
        length = fs.Position;
        return sha;
    }

    // from the start of the stream, wherever it's got to
    public static string Sha256(Stream s)
    {
        if (s.CanSeek) s.Position = 0;
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
