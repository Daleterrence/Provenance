using System.Buffers.Binary;

namespace Provenance.Common.PolFormat;

// data\doc\sqpolcts.bin, zero-key file cipher: a count at +4, 0x124-byte records from +8. app.dll (0x101b1aef) takes
// a product's wire tag from byte +0x120, 0 W20 on JP and 1 W2U elsewhere
public static class PolContents
{
    private const int RecordSize = 0x124;
    private static readonly string[] Tags = ["W20", "W2U"];

    public static string? WireTag(string viewerDir, int contentId)
    {
        try
        {
            var path = Path.Combine(viewerDir, "data", "doc", "sqpolcts.bin");
            return File.Exists(path) ? WireTag(File.ReadAllBytes(path), contentId) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string? WireTag(ReadOnlySpan<byte> blob, int contentId)
    {
        if (new PolFileCipher(new byte[8]).Decrypt(blob) is not { } r || r.Data.Length < 8) return null;
        var data = r.Data.AsSpan();
        int count = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if (count < 0 || 8L + (long)count * RecordSize > data.Length) return null;
        for (int i = 0; i < count; i++)
        {
            var rec = data.Slice(8 + i * RecordSize, RecordSize);
            if (BinaryPrimitives.ReadInt32LittleEndian(rec) == contentId)
                return rec[0x120] < Tags.Length ? Tags[rec[0x120]] : null;
        }
        return null;
    }

    internal static byte[] Encode(IEnumerable<(int Id, byte Tag)> records)
    {
        var list = records.ToList();
        var data = new byte[8 + list.Count * RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8 + i * RecordSize), list[i].Id);
            data[8 + i * RecordSize + 0x120] = list[i].Tag;
        }
        int length = data.Length;
        Array.Resize(ref data, (length + 7) / 8 * 8);
        return new PolFileCipher(new byte[8]).Encrypt(data, (uint)length);
    }
}
