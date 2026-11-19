using System.Security.Cryptography;

namespace Provenance.Common.Mirror;

public static class MirrorKeys
{
    public static IReadOnlyList<(string Id, string PublicKey)> BuiltIn { get; } =
    [
        ("p1", "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEDMVslMw+4fOgIfdSs2YCrVenuSb8kUxlN5WGNHtCE0DJ3nf0fb/y3y78UjvHXVI7zMZnIaV4bgnToz1bz4RSHA=="),
        ("r1", "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEW9Q20UAitUfOYs1yhejMmDcvz61LonO+qlaprWNT4a2uGYf9X6tg4y4wxSccGD1d53p3jtcj9+R4JCg3iD3jTw=="),
    ];

    public static IReadOnlyList<string> Revoked { get; } = [];

    public static IReadOnlyDictionary<string, ECDsa> Trusted { get; } = TrustedFrom(BuiltIn, Revoked);

    public static IReadOnlyDictionary<string, ECDsa> TrustedFrom(IEnumerable<(string Id, string PublicKey)> table, IEnumerable<string> revoked)
    {
        var gone = revoked.ToHashSet(StringComparer.Ordinal);
        var keys = new Dictionary<string, ECDsa>(StringComparer.Ordinal);
        foreach (var (id, publicKey) in table)
        {
            if (gone.Contains(id)) continue;
            var key = TryImport(publicKey) ?? throw new InvalidOperationException($"The built-in mirror key {id} doesn't import.");
            if (!keys.TryAdd(id, key)) throw new InvalidOperationException($"The built-in mirror key {id} is in the table twice.");
        }
        return keys;
    }

    public static ECDsa? TryImport(string spkiBase64)
    {
        if (string.IsNullOrEmpty(spkiBase64)) return null;
        ECDsa? key = null;
        try
        {
            key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spkiBase64), out _);
            if (key.KeySize == 256 && key.ExportParameters(false).Curve.Oid?.Value == "1.2.840.10045.3.1.7") return key;
        }
        catch (Exception) { }
        key?.Dispose();
        return null;
    }
}
