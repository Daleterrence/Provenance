using Provenance.Common.Game;
using Provenance.Common.Security;
using Serilog;
using Provenance.Common.IO;

namespace Provenance.Common.Patcher;

public static class GameFilesLock
{
    public enum Job { Install, ViewerUpdate, GameUpdate, Repair, LargeAddressAware }

    // a thread-held AdminLock, so call it from a worker and keep the work synchronous
    public static T Locked<T>(Job job, CancellationToken ct, IProgress<PatchProgress>? progress, Func<T> work)
    {
        using var gate = Lock(job, ct, progress);
        return work();
    }

    // what goes after "Another copy of Provenance is". Null is a holder that hasn't said or has gone
    public static string Doing(Job? holder) => holder switch
    {
        Job.Install => "installing FINAL FANTASY XI",
        Job.ViewerUpdate => "updating PlayOnline",
        Job.GameUpdate => "updating FINAL FANTASY XI",
        Job.Repair => "repairing the game files",
        Job.LargeAddressAware => "changing the Large Address Aware setting",
        _ => "using the game files",
    };

    internal static string WaitingText(Job? holder) => $"Waiting for another copy of Provenance to finish {Doing(holder)}...";

    // one lock for every product (installs too), and a copy that dies mid-update lets go with its process
    public static IDisposable Lock(Job job, CancellationToken ct, IProgress<PatchProgress>? progress)
    {
        for (int waits = 0; ; waits++)
        {
            ct.ThrowIfCancellationRequested();
            if (TryLock(job, TimeSpan.FromMilliseconds(500), ct) is { } gate) return gate;
            // every 5 s, which a multi-account run's no-progress guard counts as progress
            if (waits % 10 == 0) progress?.Report(new PatchProgress(PatchPhase.Check, WaitingText(Holder()), -1));
        }
    }

    // a quick look, for whether a repair could start now. The short wait lets another copy's own quick look go by
    public static bool LockFree()
    {
        using var gate = AdminLock.TryAcquire("update", TimeSpan.FromMilliseconds(150));
        return gate is not null;
    }

    // null when another update or repair still held it after the timeout
    internal static IDisposable? TryLock(Job job, TimeSpan timeout, CancellationToken ct = default) =>
        AdminLock.TryAcquire("update", timeout, ct) is { } gate ? new Holding(gate, job) : null;

    // the holder's pid and start time go in with the job, so a copy that died holding the lock can't speak for the next
    private static string RecordPath() => Path.ChangeExtension(AdminLock.PathFor("update"), ".job");

    public static Job? Holder()
    {
        try
        {
            var text = SharedFile.ReadAllText(RecordPath());
            var parts = text.Split(' ');
            if (parts.Length != 3 || !int.TryParse(parts[0], out var pid) || !long.TryParse(parts[1], out var started)
                || !Enum.TryParse<Job>(parts[2], out var job) || !Enum.IsDefined(job)) return null;
            return new ProcessStamp(pid, started).IsRunning() ? job : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private sealed class Holding : IDisposable
    {
        private readonly IDisposable _gate;
        private readonly string? _record;
        private int _disposed;

        public Holding(IDisposable gate, Job job)
        {
            _gate = gate;
            try
            {
                _record = RecordPath();
                var me = ProcessStamp.Of(Environment.ProcessId) ?? throw new InvalidOperationException("Provenance can't read its own start time");
                File.WriteAllText(_record, $"{me.Pid} {me.StartUtcTicks} {job}");
            }
            catch (Exception e) { Log.Debug(e, "Could not note what the update lock is held for"); }
        }

        // the record goes while the lock's still held, or it could take the next holder's with it
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { if (_record is not null) File.Delete(_record); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Debug(e, "Could not remove the update lock's note"); }
            _gate.Dispose();
        }
    }
}
