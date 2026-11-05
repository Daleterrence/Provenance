using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Provenance.Common.Security;
using Serilog;

namespace Provenance.Common.Credentials;

// 32 random bytes in admin-only cred.key, used as DPAPI entropy so a non-admin program only ever gets ciphertext.
// The hook reads it via POL_CRED_KEYFILE, and it's never logged
public static class CredentialKey
{
    public const int Length = 32;
    public const string SavedAccounts =
        "the saved accounts and the KeePassXC connection: each account asks for its sign-in details again, " +
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

    // held while cred.key's read for _inUse, so two threads never read it twice for one use
    private static readonly object InUseGate = new();
    private static byte[]? _inUse;
    private static int _generation;

    // bumped whenever the key in use is dropped, and every WrappedStoreCache remakes its value on its next use
    public static int Generation => Volatile.Read(ref _generation);

    public static byte[] Open(string? path = null, string protects = SavedAccounts)
    {
        if (path is not null) return OpenFile(path, protects);
        lock (InUseGate)
        {
            _inUse ??= OpenFile(FilePath, protects);
            return (byte[])_inUse.Clone();
        }
    }

    // the hook couldn't read an entry (exit 11, or "hold: Play entry unreadable"), so reread it now on this worker
    public static void Reload()
    {
        lock (InUseGate)
        {
            DropInUse();
            try { _inUse = OpenFile(FilePath, SavedAccounts); }
            catch (CredentialKeyUnavailableException) { }
        }
    }

    // under InUseGate. The stores hold their own copies, so wiping this one's safe
    private static void DropInUse()
    {
        if (_inUse is { } old) CryptographicOperations.ZeroMemory(old);
        _inUse = null;
        Interlocked.Increment(ref _generation);
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

    // all under the lock file so two copies never both replace it. Anything that isn't exactly 32 bytes gets a new key
    public static byte[] LoadOrCreate(string? path = null, string protects = SavedAccounts)
    {
        path = Path.GetFullPath(path ?? FilePath);
        // another copy only holds it for milliseconds, so the limit's only hit when that copy died half way
        using var gate = AdminLock.AcquireFile(LockPathFor(path), GateWait);
        var (key, exists) = TryRead(path);
        if (key != null) return key;
        if (exists)
        {
            Log.Warning("The key file {Path} is not a valid key; a new key is made. What it protected ({Protects}) cannot be read any more", path, protects);
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // TryCreate then fails on what's still there
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
        var dir = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(dir, $"{FileName}.{Guid.NewGuid():N}.tmp");
        var key = RandomNumberGenerator.GetBytes(Length);
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(key);
                fs.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(tmp, path, overwrite: false);
                return key;
            }
            catch (IOException) when (File.Exists(path))
            {
                CryptographicOperations.ZeroMemory(key);
                return null;
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

public sealed class CredentialKeyUnavailableException : Exception
{
    public CredentialKeyUnavailableException(Exception inner) : base(CredentialKey.UnavailableText, inner) => Shown.Mark(this);
}
