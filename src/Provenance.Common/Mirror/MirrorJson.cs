using System.IO.Compression;
using System.Text.Json;

namespace Provenance.Common.Mirror;

internal static class MirrorJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Pack<T>(T value)
    {
        using var ms = new MemoryStream();
        using (var br = new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(br, value, Options);
        return ms.ToArray();
    }

    public static bool Blank(string? s) => string.IsNullOrEmpty(s);

    public static bool IsSha256(string? s) =>
        s is { Length: 64 } && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    // No size cap on the decompression, it only runs once SignedBlob.TryOpen has passed. Only InvalidDataException
    // gets out.
    public static T Unpack<T>(ReadOnlySpan<byte> data) where T : class
    {
        try
        {
            using var input = new MemoryStream(data.ToArray());
            using var br = new BrotliStream(input, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<T>(br, Options) ?? throw new InvalidDataException("mirror JSON is null");
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("mirror data is malformed: " + ex.Message, ex);
        }
    }
}
