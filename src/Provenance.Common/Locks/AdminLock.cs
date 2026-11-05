using System.Security.Principal;

namespace Provenance.Common.Locks;

// locks between copies of the app (and threads of one): a file opened with no sharing, in an admin-only folder. Any
// program could squat a named mutex, so it isn't one
public static class AdminLock
{
    private static volatile string? _dir;

    // C:\ProgramData\Provenance\ipc\locks, set at start-up. There's deliberately no fallback, a lock taken while it's
    // null throws
    public static string? Dir
    {
        get => _dir;
        set => _dir = value is not null && string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A folder is needed.", nameof(value))
            : value;
    }

    public static readonly TimeSpan RetryEvery = TimeSpan.FromMilliseconds(50);

    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Holder> Held = new(StringComparer.OrdinalIgnoreCase);   // under Gate

    private sealed class Holder(FileStream stream, int threadId, bool reentrant)
    {
        public readonly FileStream Stream = stream;
        public readonly int ThreadId = threadId;
        public readonly bool Reentrant = reentrant;
        public int Count = 1;
    }

    public static string PathFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
            throw new ArgumentException("A plain lock name is needed.", nameof(name));
        var dir = _dir ?? throw new InvalidOperationException(
            "The folder of Provenance's locks is not set (AdminLock.Dir): a lock needs a folder only administrators can open.");
        return Path.Combine(dir, name + ".lock");
    }

    // with the SID in the name, other Windows users' copies never wait on it
    public static string PerUser(string name)
    {
        using var me = WindowsIdentity.GetCurrent();
        var sid = me.User?.Value ?? throw new InvalidOperationException("The current Windows user has no SID.");
        return $"{name}-{sid}";
    }

    // null only when another holder kept it for the whole timeout. Anything else that stops the open throws, never a
    // silent pass
    public static IDisposable? TryAcquire(string name, TimeSpan timeout, CancellationToken ct = default, bool reentrant = false)
    {
        var path = PathFor(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return TryAcquireFile(path, timeout, ct, reentrant);
    }

    // held while the file's open, so a crash can't leave it held (never deleting the file avoids a race).
    // Re-entrant only when both takes ask: nested, same thread, no await
    private static IDisposable? TryAcquireFile(string lockPath, TimeSpan timeout, CancellationToken ct = default, bool reentrant = false)
    {
        var full = Path.GetFullPath(lockPath);
        int me = Environment.CurrentManagedThreadId;
        if (reentrant)
        {
            lock (Gate)
            {
                if (Held.TryGetValue(full, out var mine) && mine.Reentrant && mine.ThreadId == me)
                {
                    mine.Count++;
                    return new Handle(full, mine);
                }
            }
        }
        long started = Environment.TickCount64;   // monotonic, so a clock change can't stretch the wait
        long limit = timeout <= TimeSpan.Zero ? 0
                   : timeout >= TimeSpan.FromDays(1) ? long.MaxValue / 2
                   : (long)Math.Ceiling(timeout.TotalMilliseconds);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(full, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                                            FileOptions.None);
                var holder = new Holder(stream, me, reentrant);
                lock (Gate) Held[full] = holder;
                return new Handle(full, holder);
            }
            catch (IOException e) when (e.HResult is SharingViolation or LockViolation) { }
            if (Environment.TickCount64 - started >= limit) return null;
            if (ct.WaitHandle.WaitOne(RetryEvery)) ct.ThrowIfCancellationRequested();
        }
    }

    public static IDisposable AcquireFile(string lockPath, TimeSpan timeout) =>
        TryAcquireFile(lockPath, timeout) ??
        throw new IOException($"Another copy of Provenance held {Path.GetFileName(lockPath)} for {timeout.TotalSeconds:0.#} s.");

    // the last dispose closes the file, from any thread (so after an await is fine)
    private sealed class Handle(string path, Holder holder) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Gate)
            {
                if (--holder.Count > 0) return;
                if (Held.TryGetValue(path, out var current) && ReferenceEquals(current, holder)) Held.Remove(path);
            }
            holder.Stream.Dispose();
        }
    }
}
