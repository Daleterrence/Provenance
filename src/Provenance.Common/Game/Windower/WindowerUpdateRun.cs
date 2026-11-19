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

    public bool FromEarlierCheck { get; init; }

    public bool CoreCompared { get; init; }

    public WindowerChecked? Checked()
    {
        var r = Result;
        if (r.Skipped || r.LockTimedOut) return null;
        if (r.Errors.Count > 0) return new WindowerChecked(null, default, r);
        // kept only when both core files really are the manifest's version now, or a login would take it as read
        bool current = r.HookAfter == _manifest.HookVersion && r.LauncherAfter == _manifest.LauncherVersion;
        return CoreCompared && current && _job is not null && _updater is not null && r.HasManifest ? new WindowerChecked(_manifest, _updater.Stamps(), null) : null;
    }

    public bool PluginsPending => _job is { } job && _updater!.PluginsPending(job, _manifest, _skip);

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
