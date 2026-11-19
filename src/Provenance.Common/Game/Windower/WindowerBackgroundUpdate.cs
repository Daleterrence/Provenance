using Serilog;

namespace Provenance.Common.Game.Windower;

internal sealed class WindowerBackgroundUpdate
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _appEnd;
    private readonly Task _task;
    private readonly DateTime _giveUp;
    private string? _notice;

    internal WindowerBackgroundUpdate(string folder, WindowerUpdateRun run, CancellationToken appEnd, TimeSpan limit)
    {
        Folder = folder;
        _appEnd = appEnd;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(appEnd);
        _cts.CancelAfter(limit);
        run.Retarget(_cts.Token);
        _giveUp = DateTime.UtcNow + limit + Grace;
        _task = Task.Run(() => FinishAsync(run));
    }

    public string Folder { get; }

    public bool Finished => _task.IsCompleted;

    internal Task Done => _task;

    internal string? TakeNotice() => Interlocked.Exchange(ref _notice, null);

    internal bool Wait(CancellationToken ct) => _task.Wait(Left(), ct);

    internal async Task<bool> WaitAsync(CancellationToken ct = default)
    {
        using var stopTimer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.WhenAny(_task, Task.Delay(Left(), stopTimer.Token));
        stopTimer.Cancel();
        ct.ThrowIfCancellationRequested();
        return _task.IsCompleted;
    }

    private TimeSpan Left() => _giveUp > DateTime.UtcNow ? _giveUp - DateTime.UtcNow : TimeSpan.Zero;

    private async Task FinishAsync(WindowerUpdateRun run)
    {
        try
        {
            var result = await run.FinishAsync();
            WindowerLauncher.LogUpdate(result);
            if (result.Errors.Count > 0) _notice = WindowerLauncher.UpdateNoticeText;
        }
        catch (OperationCanceledException) when (_appEnd.IsCancellationRequested)
        {
            Log.Information("Windower's update was stopped, Provenance is closing");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Windower's update took longer than its limit, so it was stopped");
            _notice = WindowerLauncher.UpdateNoticeText;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Windower's update failed behind the login");
            _notice = WindowerLauncher.UpdateNoticeText;
        }
        finally
        {
            run.Dispose();
            _cts.Dispose();
        }
    }
}
