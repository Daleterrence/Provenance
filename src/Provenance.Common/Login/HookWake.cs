using Serilog;

namespace Provenance.Common.Login;

internal sealed class HookWake : IDisposable
{
    private readonly AutoResetEvent _changed = new(false);
    private readonly List<FileSystemWatcher> _watchers = [];

    private HookWake() { }

    internal bool Watching => _watchers.Count > 0;

    internal static HookWake Of(params string[] paths)
    {
        var wake = new HookWake();
        try
        {
            foreach (var path in paths)
            {
                var w = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                w.Changed += (_, _) => wake.Poke();
                w.Created += (_, _) => wake.Poke();
                w.Renamed += (_, _) => wake.Poke();
                w.EnableRaisingEvents = true;
                wake._watchers.Add(w);
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Log.Warning(e, "Couldn't watch the hook's files, so the login checks them every 250ms");
            wake.StopWatching();
        }
        return wake;
    }

    private void Poke()
    {
        try { _changed.Set(); } catch (ObjectDisposedException) { }
    }

    internal bool Wait(TimeSpan max, CancellationToken ct) =>
        WaitHandle.WaitAny([_changed, ct.WaitHandle], max) != WaitHandle.WaitTimeout;

    private void StopWatching()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    public void Dispose()
    {
        StopWatching();
        _changed.Dispose();
    }
}
