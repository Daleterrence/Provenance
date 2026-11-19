using System.Buffers.Binary;

namespace Provenance.Common.PolFormat;

// records from +8, 0x20 each: name, type +0xa (0 is a number), min/max +0xc/+0x10, value +0x18
public static class PolOptionFile
{
    // 0 is on, 1 off
    public const string ChatFilter = "GOPN1_HF";
    // minutes, -1 is off
    public const string AutoDisconnect = "GOPN1_AO";

    public const int MaxSize = 1024 * 1024;

    private const int RecordSize = 0x20;

    private static readonly PolFileCipher Cipher = new(new byte[8]);

    public static byte[]? Decrypt(ReadOnlySpan<byte> file)
    {
        if (file.Length > MaxSize) return null;
        return Cipher.Decrypt(file) is { } r && r.Value == r.Data.Length ? r.Data : null;
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
        if (plain.Length == 0 || plain.Length % 8 != 0 || plain.Length > MaxSize - 8)
            throw new ArgumentException("option.bin data has to be whole 8-byte blocks", nameof(plain));
        return Cipher.Encrypt(plain, (uint)plain.Length);
    }

    private static int Find(byte[] plain, string name)
    {
        if (plain.Length < 8 || name.Length != 8) return -1;
        int count = BinaryPrimitives.ReadInt32LittleEndian(plain);
        if (count <= 0 || count > (plain.Length - 8) / RecordSize) return -1;
        for (int i = 0; i < count; i++)
        {
            int at = 8 + i * RecordSize;
            bool same = true;
            for (int c = 0; c < 8 && same; c++) same = plain[at + c] == name[c];
            if (same) return BinaryPrimitives.ReadInt16LittleEndian(plain.AsSpan(at + 0xa)) == 0 ? at : -1;
        }
        return -1;
    }

    public static int? Read(byte[] plain, string name) =>
        Find(plain, name) is var at and >= 0 ? BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(at + 0x18)) : null;

    public static bool Write(byte[] plain, string name, int value)
    {
        int at = Find(plain, name);
        if (at < 0) return false;
        int min = BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(at + 0xc));
        int max = BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(at + 0x10));
        if ((min != 0 || max != 0) && (value < min || value > max)) return false;
        BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(at + 0x18), value);
        return true;
    }
}
