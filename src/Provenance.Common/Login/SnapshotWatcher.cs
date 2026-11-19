using Provenance.Common.Hook;
using Provenance.Common.Social;

namespace Provenance.Common.Login;

internal sealed class SnapshotWatcher
{
    private readonly Func<int?> _pid;
    private readonly Action<PolSocialSnapshot> _publish;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private readonly object _readGate = new();

    public SnapshotWatcher(Func<int?> pid, Action<PolSocialSnapshot> publish)
    {
        _pid = pid;
        _publish = publish;
    }

    internal bool Watching => _watcher != null;

    internal void Start(int pid)
    {
        Stop();
        _debounce = new Timer(_ => Read(), null, Timeout.Infinite, Timeout.Infinite);
        var w = new FileSystemWatcher(HookFiles.RequiredDir, Path.GetFileName(SocialSnapshotFiles.PathForPid(pid)))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        // the timer can get disposed with an event still in flight, and a throw here takes the whole app down
        void Kick()
        {
            try { _debounce?.Change(60, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }
        w.Changed += (_, _) => Kick();
        w.Created += (_, _) => Kick();
        w.Renamed += (_, _) => Kick();
        w.EnableRaisingEvents = true;
        _watcher = w;
    }

    internal void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Dispose();
        _debounce = null;
    }

    internal void Read()
    {
        lock (_readGate)
        {
            if (_pid() is not { } pid) return;
            if (SocialSnapshotFiles.TryRead(pid) is { } snap)
                _publish(snap with { Stored = SocialSnapshotFiles.TryReadStore(pid) });
        }
    }
}
