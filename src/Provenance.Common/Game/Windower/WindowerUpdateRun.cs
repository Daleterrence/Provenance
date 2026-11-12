namespace Provenance.Common.Game.Windower;

internal sealed class WindowerUpdateRun : IDisposable
{
    private readonly WindowerUpdater? _updater;
    private readonly WindowerUpdateMutex? _hold;
    private readonly WindowerManifest _manifest = WindowerManifest.Empty;
    private readonly bool _skip;
    private WindowerUpdateFolder? _job;

    internal WindowerUpdateRun(WindowerUpdateResult result) => Result = result;

    internal WindowerUpdateRun(WindowerUpdater updater, WindowerUpdateResult result, WindowerUpdateMutex hold, WindowerUpdateFolder job,
                               WindowerManifest manifest, bool skip)
    {
        _updater = updater;
        Result = result;
        _hold = hold;
        _job = job;
        _manifest = manifest;
        _skip = skip;
    }

    public WindowerUpdateResult Result { get; }

    public bool Open => _job is not null;

    public bool PluginsPending => _job is { } job && _updater!.PluginsPending(job, _manifest, _skip);

    // a clean front half with no plugin to fetch, so what's left only touches res and the addons
    public bool CanSplit => Open && Result.Errors.Count == 0 && !PluginsPending;

    public void Retarget(CancellationToken ct) => _job?.Retarget(ct);

    public async Task<WindowerUpdateResult> FinishAsync()
    {
        if (_job is not { } job) return Result;
        try { await _updater!.RestAsync(job, _manifest, _skip, Result); }
        finally { Dispose(); }
        return Result;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _job, null)?.Dispose();
        _hold?.Dispose();
    }
}
