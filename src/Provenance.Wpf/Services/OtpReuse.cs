using System.IO;
using System.Security.Cryptography;
using System.Text;
using Provenance.Common.Security;
using Serilog;

namespace Provenance.Wpf.Services;

// Codes sent in the last 2 mins. Accounts sharing an authenticator get the same code and SE refuses a reused one,
// so I keep salted HMACs of them
public static class OtpReuse
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(2);
    private const string Protects = "the record of one-time codes sent in the last 2 minutes: those codes are no longer known as used";
    private const string GateName = "otp-recent";

    private static string? _storePath, _keyPath;
    private static Task<byte[]>? _key;
    private static readonly object KeyGate = new();

    private static string StorePath => _storePath ?? ProtectedFolders.PathFor("otp-recent.txt");
    private static string KeyPath => _keyPath ?? ProtectedFolders.PathFor("otp-recent.key");

    // when otp-recent.key can't be opened. Codes still count as used within this run, just not across copies
    private static readonly Lazy<byte[]> FallbackKey = new(() => RandomNumberGenerator.GetBytes(32));

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // not a credential, the salt just stops the hashes being matched against every six-digit code
    private static Task<byte[]> KeyAsync()
    {
        lock (KeyGate)
        {
            return _key ??= Task.Run(() =>
            {
                try { return CredentialKey.Open(KeyPath, Protects); }
                catch (Exception ex)
                {
                    // Open already logged its own failure
                    Log.Warning(ex is CredentialKeyUnavailableException ? null : ex,
                        "The used one-time codes are checked with a key in memory for this run: their key file could not be opened");
                    return FallbackKey.Value;
                }
            });
        }
    }

    public static Task OpenKeyInBackground() => KeyAsync();

    internal static void SetPathsForTest(string? storePath, string? keyPath)
    {
        _storePath = storePath;
        lock (KeyGate)
        {
            _keyPath = keyPath;
            _key = null;
        }
    }

    // test only, so a check can tell the in-memory key from the file's
    internal static byte[] KeyForTest() => KeyAsync().GetAwaiter().GetResult();

    internal static bool IsFallbackKey(byte[] key) => FallbackKey.IsValueCreated && ReferenceEquals(key, FallbackKey.Value);

    // HMAC-SHA256 (uppercase hex) of "<reuse key>\n<code>" in UTF-8
    internal static string HashFor(byte[] key, string reuseKey, string code) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(reuseKey + "\n" + code)));

    internal static List<string> Prune(IEnumerable<string> lines, long nowUnix)
    {
        long oldest = nowUnix - (long)Keep.TotalSeconds;
        return lines.Where(line => line.Split(' ') is [var at, _] && long.TryParse(at, out var t) && t >= oldest).ToList();
    }

    // on a worker, they can wait on the key file's lock and the store's. None of them throws
    public static Task<bool> WasUsedAsync(string key, string code) => Task.Run(() => WasUsed(key, code));

    // the line written (a time and a hash, never the code) for UnmarkAsync, or null
    public static Task<string?> MarkUsedAsync(string key, string code) => Task.Run(() => MarkUsed(key, code));

    public static Task UnmarkAsync(string line) => Task.Run(() => Unmark(line));

    internal static bool WasUsed(string key, string code)
    {
        try
        {
            var h = HashFor(KeyAsync().GetAwaiter().GetResult(), key, code);
            return Read().Any(e => e.Hash == h);
        }
        catch (Exception ex) { Log.Warning(ex, "Could not check the used one-time codes"); return false; }
    }

    internal static string? MarkUsed(string key, string code)
    {
        IDisposable? gate = null;
        try
        {
            var hash = HashFor(KeyAsync().GetAwaiter().GetResult(), key, code);
            try { gate = AdminLock.TryAcquire(GateName, TimeSpan.FromSeconds(3), reentrant: true); }
            catch (Exception ex) { Log.Warning(ex, "Could not take the lock of the used one-time codes; writing without it"); }
            var line = $"{Now} {hash}";
            WriteStore(Read().Select(e => $"{e.At} {e.Hash}").Append(line));
            return line;
        }
        catch (Exception ex) { Log.Warning(ex, "Could not record a used one-time code"); return null; }
        finally { gate?.Dispose(); }
    }

    // removes one copy, so the same code recorded by another copy stays
    internal static void Unmark(string line) => Rewrite(remove: line);

    // the next code's due every 30 secs, plus 2 of slack so a second fetch lands after it changes
    public static int SecondsToNextCode() => 30 - (int)(Now % 30) + 2;

    // older or unreadable lines get cleaned out of the file on the way
    internal static List<(long At, string Hash)> Read()
    {
        try
        {
            if (!File.Exists(StorePath)) return [];
            var all = File.ReadAllLines(StorePath);
            var kept = Prune(all, Now);
            if (kept.Count < all.Length) Rewrite();
            return kept.Select(line => line.Split(' ')).Select(p => (long.Parse(p[0]), p[1])).ToList();
        }
        catch (Exception ex) { Log.Warning(ex, "Could not read the used one-time codes"); return []; }
    }

    // rereads inside the lock so another copy's new line is kept. Re-entrant for the prune (MarkUsed -> Read -> here),
    // and an unmark with no lock just doesn't happen
    private static void Rewrite(string? remove = null)
    {
        IDisposable? gate = null;
        try
        {
            gate = AdminLock.TryAcquire(GateName, TimeSpan.FromSeconds(3), reentrant: remove is null);
            if (gate is null)
            {
                if (remove is not null) Log.Warning("Could not take the lock of the used one-time codes; a cancelled code stays recorded");
                return;
            }
            if (!File.Exists(StorePath)) return;
            var all = File.ReadAllLines(StorePath);
            var kept = Prune(all, Now);
            bool removed = remove is not null && kept.Remove(remove);
            if (!removed && kept.Count == all.Length) return;
            WriteStore(kept);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, remove is null ? "Could not remove old one-time codes from the store"
                                           : "Could not remove a cancelled one-time code from the store");
        }
        finally { gate?.Dispose(); }
    }

    private static void WriteStore(IEnumerable<string> lines)
    {
        var tmp = $"{StorePath}.{Environment.ProcessId}.tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, StorePath, overwrite: true);
    }
}
