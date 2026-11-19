using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Provenance.Common.Mirror;

// "PXM1", a key id length byte, the key id, a LE ushort signature length, the signature, the payload (ECDSA P-256, IEEE P1363)
public static class SignedBlob
{
    private static readonly byte[] Magic = "PXM1"u8.ToArray();

    public static byte[] Wrap(string keyId, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> payload)
    {
        var id = Encoding.ASCII.GetBytes(keyId);
        if (id.Length is 0 or > 255) throw new ArgumentException("key id must be 1..255 ASCII bytes", nameof(keyId));
        if (signature.Length > ushort.MaxValue) throw new ArgumentException("signature too long", nameof(signature));
        var blob = new byte[Magic.Length + 1 + id.Length + 2 + signature.Length + payload.Length];
        var s = blob.AsSpan();
        Magic.CopyTo(s);
        int o = Magic.Length;
        s[o++] = (byte)id.Length;
        id.CopyTo(s[o..]);
        o += id.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(s[o..], (ushort)signature.Length);
        o += 2;
        signature.CopyTo(s[o..]);
        payload.CopyTo(s[(o + signature.Length)..]);
        return blob;
    }

    public static bool TryOpen(ReadOnlySpan<byte> blob, IReadOnlyDictionary<string, ECDsa> trusted, out byte[] payload)
    {
        payload = [];
        try
        {
            if (blob.Length < Magic.Length + 1 || !blob[..Magic.Length].SequenceEqual(Magic)) return false;
            int o = Magic.Length;
            int idLen = blob[o++];
            if (idLen == 0 || blob.Length < o + idLen + 2) return false;
            var keyId = Encoding.ASCII.GetString(blob.Slice(o, idLen));
            o += idLen;
            int sigLen = BinaryPrimitives.ReadUInt16LittleEndian(blob[o..]);
            o += 2;
            if (sigLen != 64 || blob.Length < o + sigLen) return false;
            var sig = blob.Slice(o, sigLen);
            var body = blob[(o + sigLen)..];
            if (!trusted.TryGetValue(keyId, out var key)) return false;
            if (!key.VerifyData(body, sig, HashAlgorithmName.SHA256)) return false;
            payload = body.ToArray();
            return true;
        }
        catch (Exception) { return false; }
    }

    public static string? KeyId(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < Magic.Length + 1 || !blob[..Magic.Length].SequenceEqual(Magic)) return null;
        int idLen = blob[Magic.Length];
        if (idLen == 0 || blob.Length < Magic.Length + 1 + idLen) return null;
        var id = blob.Slice(Magic.Length + 1, idLen);
        foreach (var b in id)
            if (b is < 0x21 or > 0x7e) return null;
        return Encoding.ASCII.GetString(id);
    }
}
