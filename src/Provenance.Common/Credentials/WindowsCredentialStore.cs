using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Provenance.Common.Win32;

namespace Provenance.Common.Credentials;

// anything running as this user can read or swap a plain Credential Manager entry, hence the wrap key
public sealed class WindowsCredentialStore
{
    public const string DefaultPrefix = "Provenance";

    // a wrapped blob is this, then DPAPI of the UTF-16LE user name, a NUL and the secret. Entropy is the wrap key plus
    // the target name, so a moved blob won't unwrap (native/pol_cred.h reads it too)
    public const string WrapMagic = "PVW2";

    private static readonly byte[] WrapMagicBytes = Encoding.ASCII.GetBytes(WrapMagic);

    private readonly string _prefix;
    private readonly byte[]? _wrapKey;

    public WindowsCredentialStore(string prefix = DefaultPrefix, byte[]? wrapKey = null)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("prefix must be non-empty", nameof(prefix));
        _prefix = prefix;
        _wrapKey = wrapKey;
    }

    // sessionOnly entries die with the logon session. A one-shot launch entry can't outlive a reboot that way
    public void Save(string key, string userName, string secret, bool sessionOnly = false)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key required", nameof(key));
        if (userName == null) throw new ArgumentNullException(nameof(userName));
        if (secret == null) throw new ArgumentNullException(nameof(secret));
        if (userName.Contains('\0')) throw new ArgumentException("user name can't contain a NUL", nameof(userName));

        var target = FormatTarget(key);
        var plainBytes = Pack(userName, secret);
        byte[] secretBytes = plainBytes;
        IntPtr pSecret = IntPtr.Zero;
        // one try round the wrap and the write, so the plain bytes get cleared even when the wrap throws
        try
        {
            if (_wrapKey != null)
            {
                var entropy = WrapEntropy(_wrapKey, target);
                byte[]? cipher;
                try { cipher = Dpapi.Protect(plainBytes, entropy); }
                finally { CryptographicOperations.ZeroMemory(entropy); }
                if (cipher == null) throw new InvalidOperationException($"Could not wrap the credential for '{target}'.");
                secretBytes = new byte[WrapMagic.Length + cipher.Length];
                WrapMagicBytes.CopyTo(secretBytes, 0);
                Buffer.BlockCopy(cipher, 0, secretBytes, WrapMagic.Length, cipher.Length);
            }

            pSecret = Marshal.AllocHGlobal(secretBytes.Length);
            Marshal.Copy(secretBytes, 0, pSecret, secretBytes.Length);
            // the open user name field is shown to anything that lists the entries, so it stays empty
            Write(target, "", pSecret, (uint)secretBytes.Length,
                  sessionOnly ? CRED_PERSIST_SESSION : CRED_PERSIST_LOCAL_MACHINE);
        }
        finally
        {
            // no plaintext left behind in free heap pages
            if (pSecret != IntPtr.Zero)
            {
                for (int i = 0; i < secretBytes.Length; i++)
                    Marshal.WriteByte(pSecret, i, 0);
                Marshal.FreeHGlobal(pSecret);
            }
            CryptographicOperations.ZeroMemory(plainBytes);
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    public StoredCredential? Read(string key)
    {
        var target = FormatTarget(key);
        return WithEntry<StoredCredential?>(target, null, cred =>
        {
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) throw new CredentialUnreadableException(target);

            var raw = new byte[cred.CredentialBlobSize];
            byte[]? plain = null;
            try
            {
                Marshal.Copy(cred.CredentialBlob, raw, 0, raw.Length);
                if (_wrapKey == null)
                    return (StartsWithMagic(raw) ? null : Unpack(raw)) ?? throw new CredentialUnreadableException(target);
                if (raw.Length <= WrapMagic.Length || !StartsWithMagic(raw)) throw new CredentialUnreadableException(target);
                var entropy = WrapEntropy(_wrapKey, target);
                try { plain = Dpapi.Unprotect(raw[WrapMagic.Length..], entropy); }
                finally { CryptographicOperations.ZeroMemory(entropy); }
                return (plain == null ? null : Unpack(plain)) ?? throw new CredentialUnreadableException(target);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(raw);
                if (plain != null) CryptographicOperations.ZeroMemory(plain);
            }
        });
    }

    public bool Delete(string key)
    {
        var target = FormatTarget(key);
        if (Advapi32.CredDeleteW(target, CRED_TYPE_GENERIC, 0)) return true;
        ThrowUnlessNotFound(Marshal.GetLastWin32Error(), $"CredDeleteW failed for '{target}'");
        return false;
    }

    // the user name's wrapped with the secret, so this needs the key just like Read
    public string? ReadUserName(string key) => Read(key)?.UserName;

    // only copies (then clears) the header and never decrypts, so an entry wrapped with another key passes too
    public bool HasWrappedSecret(string key) => WithEntry(FormatTarget(key), false, cred =>
    {
        if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize <= WrapMagic.Length) return false;
        var head = new byte[WrapMagic.Length];
        try
        {
            Marshal.Copy(cred.CredentialBlob, head, 0, head.Length);
            return StartsWithMagic(head);
        }
        finally { CryptographicOperations.ZeroMemory(head); }
    });

    // with olderThan, newer entries stay put, another running copy might still need them
    public int DeleteByPrefix(string keyPrefix, TimeSpan? olderThan = null)
    {
        DateTime? cutoff = olderThan is { } age ? DateTime.UtcNow - age : null;
        if (string.IsNullOrWhiteSpace(keyPrefix)) throw new ArgumentException("prefix required", nameof(keyPrefix));
        if (!Advapi32.CredEnumerateW(FormatTarget(keyPrefix) + "*", 0, out uint count, out IntPtr list))
        {
            ThrowUnlessNotFound(Marshal.GetLastWin32Error(), "CredEnumerateW failed");
            return 0;
        }

        var targets = new List<string>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(Marshal.ReadIntPtr(list, i * IntPtr.Size));
                if (cred.Type != CRED_TYPE_GENERIC || string.IsNullOrEmpty(cred.TargetName)) continue;
                if (cutoff is { } c)
                {
                    long ft = ((long)(uint)cred.LastWritten.dwHighDateTime << 32) | (uint)cred.LastWritten.dwLowDateTime;
                    if (DateTime.FromFileTimeUtc(ft) > c) continue;
                }
                targets.Add(cred.TargetName);
            }
        }
        finally { Advapi32.CredFree(list); }

        int removed = 0;
        foreach (var t in targets)
            if (Advapi32.CredDeleteW(t, CRED_TYPE_GENERIC, 0)) removed++;
        return removed;
    }

    // never decrypts, so it needs no key
    public bool Exists(string key) => WithEntry(FormatTarget(key), false, _ => true);

    internal bool CopyEntryForTest(string fromKey, string toKey) => WithEntry(FormatTarget(fromKey), false, src =>
    {
        Write(FormatTarget(toKey), src.UserName, src.CredentialBlob, src.CredentialBlobSize, CRED_PERSIST_SESSION);
        return true;
    });

    internal void WriteRawForTest(string key, string userName, byte[] blob)
    {
        var p = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, p, blob.Length);
            Write(FormatTarget(key), userName, p, (uint)blob.Length, CRED_PERSIST_SESSION);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    internal string? UserNameFieldForTest(string key) => WithEntry<string?>(FormatTarget(key), null, c => c.UserName ?? string.Empty);

    private string FormatTarget(string key) => $"{_prefix}:{key}";

    private static T WithEntry<T>(string target, T missing, Func<CREDENTIAL, T> use)
    {
        if (!Advapi32.CredReadW(target, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            ThrowUnlessNotFound(Marshal.GetLastWin32Error(), $"CredReadW failed for '{target}'");
            return missing;
        }
        try { return use(Marshal.PtrToStructure<CREDENTIAL>(credPtr)); }
        finally { Advapi32.CredFree(credPtr); }
    }

    // the caller reads the error first, before anything else can touch it
    private static void ThrowUnlessNotFound(int err, string message)
    {
        if (err != ERROR_NOT_FOUND) throw new Win32Exception(err, message);
    }

    private static void Write(string target, string userName, IntPtr blob, uint blobSize, uint persist)
    {
        var cred = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = target,
            UserName = userName,
            CredentialBlobSize = blobSize,
            CredentialBlob = blob,
            Persist = persist,
            Comment = "Provenance stored credential",
        };
        if (!Advapi32.CredWriteW(ref cred, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CredWriteW failed for '{target}'");
    }

    private static byte[] WrapEntropy(byte[] wrapKey, string target)
    {
        var entropy = new byte[wrapKey.Length + Encoding.Unicode.GetByteCount(target)];
        Buffer.BlockCopy(wrapKey, 0, entropy, 0, wrapKey.Length);
        Encoding.Unicode.GetBytes(target, 0, target.Length, entropy, wrapKey.Length);
        return entropy;
    }

    private static bool StartsWithMagic(ReadOnlySpan<byte> blob) => blob.StartsWith(WrapMagicBytes);

    private static byte[] Pack(string userName, string secret)
    {
        int at = Encoding.Unicode.GetByteCount(userName) + 2;
        var bytes = new byte[at + Encoding.Unicode.GetByteCount(secret)];
        Encoding.Unicode.GetBytes(userName, 0, userName.Length, bytes, 0);
        Encoding.Unicode.GetBytes(secret, 0, secret.Length, bytes, at);
        return bytes;
    }

    // the first NUL ends the user name, a secret can hold more of them
    private static StoredCredential? Unpack(ReadOnlySpan<byte> plain)
    {
        if (plain.Length % 2 != 0) return null;
        for (int i = 0; i < plain.Length; i += 2)
            if (plain[i] == 0 && plain[i + 1] == 0)
                return new StoredCredential(Encoding.Unicode.GetString(plain[..i]), Encoding.Unicode.GetString(plain[(i + 2)..]));
        return null;
    }

    private const uint CRED_TYPE_GENERIC          = 1;
    private const uint CRED_PERSIST_SESSION       = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int  ERROR_NOT_FOUND            = 1168;

}

// prints *** for both, so a stray ToString or log line never leaks a login
public readonly record struct StoredCredential(string UserName, string Secret)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("UserName = ***, Secret = ***");
        return true;
    }
}

// the message only names the target, never the user name, secret or key
public sealed class CredentialUnreadableException(string target)
    : Exception($"Stored credential '{target}' cannot be read with the wrap key.");
