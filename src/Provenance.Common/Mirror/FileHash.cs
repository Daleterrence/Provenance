using System.Security.Cryptography;

namespace Provenance.Common.Mirror;

// lowercase hex SHA-256, which is also how mirror objects are named
public static class FileHash
{
    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
