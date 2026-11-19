using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32;
using Provenance.Common.IO;
using Provenance.Common.Patcher;
using Serilog;

namespace Provenance.Common.PolFormat;

// 24 zero bytes and the version text in 0x100, encrypted to 0x120 with trailer value 0x118
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

    // POL tries the product id alone when the Interface-seeded key doesn't open it, so this does too
    public static string Read(PatchProduct product)
    {
        var path = FileIn(product.Folder);
        if (!File.Exists(path)) return "";
        var blob = File.ReadAllBytes(path);
        return Decode(blob, SeededKey(product))
               ?? Decode(blob, Key(product.Id, null))
               ?? throw new InvalidDataException($"The {product.Name} version file (patch.ver) could not be read.");
    }

    public static string ReadOrEmpty(PatchProduct product)
    {
        try { return Read(product); }
        catch (InvalidDataException e)
        {
            Log.Warning(e, "Treating the {Product} patch.ver as no version, the update check rewrites it", product.Name);
            return "";
        }
    }

    public static string? ReadSeeded(PatchProduct product)
    {
        var path = FileIn(product.Folder);
        return File.Exists(path) ? Decode(File.ReadAllBytes(path), SeededKey(product)) : null;
    }

    public static void Write(PatchProduct product, string version)
    {
        var path = FileIn(product.Folder);
        var bytes = Encode(version, SeededKey(product));
        AtomicFile.ClearReadOnly(path);
        AtomicFile.Write(path, bytes);
    }

    public static void Drop(string folder)
    {
        var path = FileIn(folder);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string FileIn(string folder) => System.IO.Path.Combine(folder, "patch.ver");

    private static byte[] SeededKey(PatchProduct product) =>
        Key(product.Id, product.PatchVerInterface ?? InterfaceValue(product.RegistryHive, product.Id));
}
