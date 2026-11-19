using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Provenance.Common.IO;
using Provenance.Common.Security;
using Serilog;

namespace Provenance.Common.Credentials;

// DPAPI entropy, so a non-admin program only ever gets ciphertext
public static class CredentialKey
{
    public const int Length = 32;
    public const string SavedAccounts =
        "the saved accounts and the KeePassXC connection: each account asks for its login details again, " +
        "and KeePassXC asks to allow Provenance again";
    public const string UnavailableText = "Provenance can't open the key that protects your saved login details. Restart Provenance and try again.";
    private const string FileName = "cred.key";
    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(10);
    private static string? _filePath;

    [AllowNull]
    public static string FilePath
    {
        get => _filePath ??= ProtectedFolders.PathFor(FileName);
        internal set
        {
            lock (InUseGate)
            {
                _filePath = value;
                DropInUse();
            }
        }
    }

    private static readonly object InUseGate = new();
    private static byte[]? _inUse;
    private static WindowsCredentialStore? _wrapped;

    private static readonly object KeylessGate = new();
    private static WindowsCredentialStore? _keyless;
    private static string _prefix = WindowsCredentialStore.DefaultPrefix;

    internal static string Prefix
    {
        get => Volatile.Read(ref _prefix);
        set
        {
            lock (KeylessGate) { _prefix = value; _keyless = null; }
            lock (InUseGate) DropInUse();
        }
    }

    public static WindowsCredentialStore Keyless
    {
        get { lock (KeylessGate) return _keyless ??= new WindowsCredentialStore(Prefix); }
    }

    public static WindowsCredentialStore Wrapped()
    {
        lock (InUseGate) return _wrapped ??= new WindowsCredentialStore(Prefix, InUse());
    }

    public static Task<WindowsCredentialStore> WrappedAsync() => Task.Run(Wrapped);

    public static async Task WarmUp()
    {
        try { await WrappedAsync().ConfigureAwait(false); }
        catch (CredentialKeyUnavailableException) { }
    }

    internal static byte[] InUse()
    {
        lock (InUseGate)
        {
            _inUse ??= OpenFile(FilePath, SavedAccounts);
            return (byte[])_inUse.Clone();
        }
    }

    public static byte[] Open(string path, string protects = SavedAccounts) => OpenFile(path, protects);

    public static void Reload()
    {
        lock (InUseGate)
        {
            DropInUse();
            try { _inUse = OpenFile(FilePath, SavedAccounts); }
            catch (CredentialKeyUnavailableException) { }
        }
    }

    private static void DropInUse()
    {
        if (_inUse is { } old) CryptographicOperations.ZeroMemory(old);
        _inUse = null;
        _wrapped = null;
    }

    private static byte[] OpenFile(string path, string protects)
    {
        try { return LoadOrCreate(path, protects); }
        catch (Exception ex)
        {
            Log.Error(ex, "Provenance could not open its key file");
            throw new CredentialKeyUnavailableException(ex);
        }
    }

    public static byte[] LoadOrCreate(string? path = null, string protects = SavedAccounts)
    {
        path = Path.GetFullPath(path ?? FilePath);
        using var gate = AdminLock.AcquireFile(LockPathFor(path), GateWait);
        var (key, exists) = TryRead(path);
        if (key != null) return key;
        if (exists)
        {
            Log.Warning("The key file {Path} is not a valid key; a new key is made. What it protected ({Protects}) cannot be read any more", path, protects);
            FileDelete.Try(path);
        }
        return TryCreate(path) ?? throw new IOException($"Provenance could not read or make its credential key {path}.");
    }

    // never the key file itself: the hook opens cred.key with read sharing only, so locking it would lock the hook out
    internal static string LockPathFor(string keyPath) => Path.GetFullPath(keyPath) + ".lock";

    private static (byte[]? Key, bool Exists) TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (fs.Length != Length) return (null, true);
            var key = new byte[Length];
            fs.ReadExactly(key);
            return (key, true);
        }
        catch (FileNotFoundException) { return (null, false); }
    }

    private static byte[]? TryCreate(string path)
    {
        var key = RandomNumberGenerator.GetBytes(Length);
        if (AtomicFile.CreateNew(path, key)) return key;
        CryptographicOperations.ZeroMemory(key);
        return null;
    }
}

public sealed class CredentialKeyUnavailableException : Exception
{
    public CredentialKeyUnavailableException(Exception inner) : base(CredentialKey.UnavailableText, inner) => Shown.Mark(this);
}
