using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NaCl;

namespace Provenance.Common.Otp;

public sealed class KeePassXcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
    public const int DatabaseLocked = 1, NoPublicKey = 3, CannotDecrypt = 4, Denied = 6, NotAssociated = 8, NoLogins = 15, BadFrame = -1;
}

public sealed record KeePassXcEntry(string Name, string Login, string Uuid, string? Totp)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Totp = ***");
        return true;
    }
}

// frames are a 4-byte LE length plus ASCII JSON
public sealed class KeePassXcClient : IDisposable
{
    private readonly Stream _toHost, _fromHost;
    private readonly string _clientId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    private readonly byte[] _secretKey, _publicKey;
    private Curve25519XSalsa20Poly1305? _box;

    public KeePassXcClient(Stream toHost, Stream fromHost)
    {
        _toHost = toHost;
        _fromHost = fromHost;
        (_secretKey, _publicKey) = Curve25519XSalsa20Poly1305.KeyPair();
    }

    public static byte[] NewIdKey() => Curve25519XSalsa20Poly1305.KeyPair().Item2;

    public async Task ExchangeKeysAsync(CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(24);
        var reply = await RoundTripAsync(new Dictionary<string, string>
        {
            ["action"] = "change-public-keys", ["publicKey"] = B64(_publicKey), ["nonce"] = B64(nonce), ["clientID"] = _clientId,
        }, nonce, ct);
        var hostKey = Convert.FromBase64String(Str(reply, "publicKey") ?? throw new KeePassXcException(KeePassXcException.NoPublicKey, "KeePassXC didn't send its public key."));
        _box?.Dispose();
        _box = new Curve25519XSalsa20Poly1305(_secretKey, hostKey);
    }

    public async Task<string> AssociateAsync(byte[] idKey, CancellationToken ct)
    {
        var r = await RequestAsync("associate", new Dictionary<string, string>
        {
            ["action"] = "associate", ["key"] = B64(_publicKey), ["idKey"] = B64(idKey),
        }, false, ct);
        return Str(r, "id") ?? throw new KeePassXcException(KeePassXcException.NotAssociated, "KeePassXC didn't name the connection.");
    }

    public async Task<bool> TestAssociateAsync(string id, byte[] idKey, CancellationToken ct)
    {
        try
        {
            var r = await RequestAsync("test-associate", new Dictionary<string, string>
            {
                ["action"] = "test-associate", ["id"] = id, ["key"] = B64(idKey),
            }, false, ct);
            return Str(r, "success") == "true";
        }
        catch (KeePassXcException e) when (e.Code == KeePassXcException.NotAssociated) { return false; }
    }

    // with triggerUnlock a locked database shows KeePassXC's unlock dialog (the call still fails with code 1)
    public async Task<IReadOnlyList<KeePassXcEntry>> GetLoginsAsync(string url, string id, byte[] idKey, bool triggerUnlock, CancellationToken ct)
    {
        var inner = new Dictionary<string, object>
        {
            ["action"] = "get-logins", ["url"] = url,
            ["keys"] = new[] { new Dictionary<string, string> { ["id"] = id, ["key"] = B64(idKey) } },
        };
        var r = await RequestAsync("get-logins", inner, triggerUnlock, ct);
        var list = new List<KeePassXcEntry>();
        if (r.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            foreach (var e in entries.EnumerateArray())
                list.Add(new KeePassXcEntry(Str(e, "name") ?? "", Str(e, "login") ?? "", Str(e, "uuid") ?? "", Str(e, "totp")));
        return list;
    }

    public async Task<string?> GetTotpAsync(string uuid, CancellationToken ct)
    {
        var r = await RequestAsync("get-totp", new Dictionary<string, string> { ["action"] = "get-totp", ["uuid"] = uuid }, false, ct);
        var code = Str(r, "totp");
        return string.IsNullOrEmpty(code) ? null : code;
    }

    public async Task WaitForUnlockAsync(CancellationToken ct)
    {
        while (true)
        {
            var m = await ReadFrameAsync(ct);
            if (Str(m, "action") == "database-unlocked") return;
        }
    }

    private async Task<JsonElement> RequestAsync(string action, object inner, bool triggerUnlock, CancellationToken ct)
    {
        if (_box is null) throw new InvalidOperationException("Exchange keys first.");
        var nonce = RandomNumberGenerator.GetBytes(24);
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(inner));
        var sealedBytes = new byte[plain.Length + XSalsa20Poly1305.TagLength];
        byte[]? opened = null;
        try
        {
            _box.Encrypt(sealedBytes, plain, nonce);
            var envelope = new Dictionary<string, string>
            {
                ["action"] = action, ["message"] = B64(sealedBytes), ["nonce"] = B64(nonce), ["clientID"] = _clientId,
            };
            if (triggerUnlock) envelope["triggerUnlock"] = "true";
            var reply = await RoundTripAsync(envelope, nonce, ct);
            var cipher = Convert.FromBase64String(Str(reply, "message") ?? throw new KeePassXcException(KeePassXcException.CannotDecrypt, "KeePassXC didn't send a message."));
            if (cipher.Length < XSalsa20Poly1305.TagLength) throw new KeePassXcException(KeePassXcException.CannotDecrypt, "KeePassXC sent a message that's too short.");
            opened = new byte[cipher.Length - XSalsa20Poly1305.TagLength];
            if (!_box.TryDecrypt(opened, cipher, Increment(nonce)))
                throw new KeePassXcException(KeePassXcException.CannotDecrypt, "A message from KeePassXC couldn't be opened.");
            using var doc = JsonDocument.Parse(opened);
            var root = doc.RootElement.Clone();
            ThrowIfError(root);
            return root;
        }
        finally
        {
            if (opened != null) CryptographicOperations.ZeroMemory(opened);
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(sealedBytes);
        }
    }

    private async Task<JsonElement> RoundTripAsync(Dictionary<string, string> envelope, byte[] nonce,
                                                   CancellationToken ct)
    {
        await WriteAsync(envelope, ct);
        var reply = await ReadReplyAsync(envelope["action"], ct);
        ThrowIfError(reply);
        if (Str(reply, "nonce") != B64(Increment(nonce))) throw new KeePassXcException(KeePassXcException.CannotDecrypt, "KeePassXC answered with the wrong nonce.");
        return reply;
    }

    // replies come in order, but KeePassXC also sends its own messages (database-locked and so on), which get skipped
    private async Task<JsonElement> ReadReplyAsync(string action, CancellationToken ct)
    {
        while (true)
        {
            var m = await ReadFrameAsync(ct);
            if (Str(m, "action") == action || m.TryGetProperty("errorCode", out _)) return m;
        }
    }

    private static void ThrowIfError(JsonElement m)
    {
        if (!m.TryGetProperty("errorCode", out var c)) return;
        int code = c.ValueKind == JsonValueKind.Number ? c.GetInt32() : int.TryParse(c.GetString(), out var n) ? n : KeePassXcException.BadFrame;
        throw new KeePassXcException(code, Str(m, "error") ?? $"KeePassXC error {code}.");
    }

    private async Task WriteAsync(object message, CancellationToken ct)
    {
        // the default encoder escapes all non-ASCII, which matters because the proxy reads Latin-1
        var body = Encoding.ASCII.GetBytes(JsonSerializer.Serialize(message));
        var frame = new byte[4 + body.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), (uint)body.Length);
        body.CopyTo(frame, 4);
        await _toHost.WriteAsync(frame, ct);
        await _toHost.FlushAsync(ct);
    }

    private async Task<JsonElement> ReadFrameAsync(CancellationToken ct)
    {
        var head = new byte[4];
        await _fromHost.ReadExactlyAsync(head, ct);
        int length = (int)BitConverter.ToUInt32(head, 0);
        if (length is <= 0 or > 1 << 20) throw new KeePassXcException(KeePassXcException.BadFrame, "KeePassXC sent a message that's the wrong size.");
        var body = new byte[length];
        await _fromHost.ReadExactlyAsync(body, ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    // libsodium's sodium_increment
    public static byte[] Increment(byte[] nonce)
    {
        var n = (byte[])nonce.Clone();
        for (int i = 0; i < n.Length; i++) if (++n[i] != 0) break;
        return n;
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b);
    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose()
    {
        _box?.Dispose();
        CryptographicOperations.ZeroMemory(_secretKey);
    }
}
