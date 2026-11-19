using System.Security.Cryptography;
using System.Text;
using Provenance.Common.IO;

namespace Provenance.Common.Social;

public sealed class AnsweredRequests
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("Provenance PlayOnline answered requests v1");

    private readonly EncryptedJsonFile<Dictionary<string, long>>? _file;
    private readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);

    public AnsweredRequests(string profileId, string? root, DateTime now)
    {
        if (root is null)
        {
            if (AppPaths.UserData is not { } userData) return;
            root = Path.Combine(userData, "messages");
        }
        _file = new(Path.Combine(root, profileId + ".answered.dat"), Entropy, "Messages", setAsideUnreadable: false);
        if (_file.Load() is { } loaded)
            foreach (var (k, t) in loaded) _seen[k] = t;
        Prune(now);
    }

    public int Count => _seen.Count;

    public static string Identity(PolMessage m) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{m.FromKey}|{m.FromSlot}|{m.Seq}|{m.Time}|{m.ToKey}")))[..32];

    public bool Has(PolMessage m) => m.FromKey.Length > 0 && _seen.ContainsKey(Identity(m));

    public void Add(PolMessage m, DateTime now)
    {
        if (m.FromKey.Length == 0) return;
        _seen[Identity(m)] = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds();
        Prune(now);
        _file?.Save(_seen);
    }

    private void Prune(DateTime now)
    {
        var cutoff = new DateTimeOffset(now, TimeSpan.Zero).Add(-Keep).ToUnixTimeSeconds();
        foreach (var k in _seen.Where(p => p.Value < cutoff).Select(p => p.Key).ToList()) _seen.Remove(k);
    }
}
