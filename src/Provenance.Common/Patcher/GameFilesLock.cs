using Provenance.Common.Game;
using Provenance.Common.Security;
using Serilog;
using Provenance.Common.IO;

namespace Provenance.Common.Patcher;

public static class GameFilesLock
{
    public enum Job { Install, ViewerUpdate, GameUpdate, Repair, LargeAddressAware }

    public static T Locked<T>(Job job, CancellationToken ct, IProgress<PatchProgress>? progress, Func<T> work)
    {
        using var gate = Lock(job, ct, progress);
        return work();
    }

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

    public static IDisposable Lock(Job job, CancellationToken ct, IProgress<PatchProgress>? progress)
    {
        for (int waits = 0; ; waits++)
        {
            ct.ThrowIfCancellationRequested();
            if (TryLock(job, TimeSpan.FromMilliseconds(500), ct) is { } gate) return gate;
            // every 5s, which a multi-account run's no-progress guard counts as progress
            if (waits % 10 == 0) progress?.Report(new PatchProgress(PatchPhase.Check, WaitingText(Holder()), -1));
        }
    }

    public static bool LockFree()
    {
        using var gate = AdminLock.TryAcquire("update", TimeSpan.FromMilliseconds(150));
        return gate is not null;
    }

    internal static IDisposable? TryLock(Job job, TimeSpan timeout, CancellationToken ct = default) =>
        AdminLock.TryAcquire("update", timeout, ct) is { } gate ? new Holding(gate, job) : null;

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

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { if (_record is not null) File.Delete(_record); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Debug(e, "Could not remove the update lock's note"); }
            _gate.Dispose();
        }
    }
}
