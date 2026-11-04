using System.Security.Cryptography;

namespace Provenance.Common.Mirror;

// P-256 SubjectPublicKeyInfo, the private halves live with the publisher and never come anywhere near this
public static class MirrorKeys
{
    public const string PrimaryId = "p1";
    public const string RecoveryId = "r1";

    private const string PrimaryPublic = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEDMVslMw+4fOgIfdSs2YCrVenuSb8kUxlN5WGNHtCE0DJ3nf0fb/y3y78UjvHXVI7zMZnIaV4bgnToz1bz4RSHA==";
    private const string RecoveryPublic = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEW9Q20UAitUfOYs1yhejMmDcvz61LonO+qlaprWNT4a2uGYf9X6tg4y4wxSccGD1d53p3jtcj9+R4JCg3iD3jTw==";

    public static IReadOnlyDictionary<string, ECDsa> Trusted { get; } = Build();

    // one that won't import is a broken build, so it fails here instead of looking like a mirror outage
    private static Dictionary<string, ECDsa> Build() => new()
    {
        [PrimaryId] = TryImport(PrimaryPublic) ?? throw new InvalidOperationException("The built-in primary mirror key doesn't import."),
        [RecoveryId] = TryImport(RecoveryPublic) ?? throw new InvalidOperationException("The built-in recovery mirror key doesn't import."),
    };

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
