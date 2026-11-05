using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32;

namespace Provenance.Common.PolFormat;

// patch.ver (app.dll mode 400, polcore svc 0x1248/0x124c): 24 zero bytes and the version text in 0x100, encrypted to
// 0x120 with trailer value 0x118
public static class PatchVer
{
    private const int TextOffset = 24, TextSize = 0x100, PlainSize = 0x118;

    public static byte[] Key(int productId, string? interfaceValue)
    {
        var fold = new byte[8];
        var text = Encoding.Latin1.GetBytes(interfaceValue ?? "");
        for (int i = 0; i < text.Length; i++) fold[i % 8] += text[i];
        BinaryPrimitives.WriteUInt64LittleEndian(fold, unchecked(BinaryPrimitives.ReadUInt64LittleEndian(fold) + (ulong)productId));
        return fold;
    }

    public static string? InterfaceValue(string hive, int productId)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = root.OpenSubKey(hive + @"\Interface");
        return key?.GetValue(productId.ToString("0000")) as string;
    }

    public static string? Decode(ReadOnlySpan<byte> blob, byte[] key8)
    {
        if (new PolFileCipher(key8).Decrypt(blob) is not { } r || r.Data.Length < PlainSize) return null;
        var text = r.Data.AsSpan(TextOffset, TextSize);
        int end = text.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? text : text[..end]);
    }

    public static byte[] Encode(string version, byte[] key8)
    {
        var plain = new byte[PlainSize];
        var text = Encoding.Latin1.GetBytes(version);
        if (text.Length >= TextSize) throw new ArgumentException("The version text is too long.", nameof(version));
        text.CopyTo(plain, TextOffset);
        return new PolFileCipher(key8).Encrypt(plain, PlainSize);
    }
}
