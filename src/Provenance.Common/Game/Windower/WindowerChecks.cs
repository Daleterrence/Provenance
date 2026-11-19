using Provenance.Common.Security;
using Serilog;

namespace Provenance.Common.Game.Windower;

internal readonly record struct WindowerStamp(bool Known, bool Exists, long Length, long Write, long Created)
{
    public static WindowerStamp Of(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? new(true, true, fi.Length, fi.LastWriteTimeUtc.Ticks, fi.CreationTimeUtc.Ticks) : new(true, false, 0, 0, 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; }
    }

    public bool Same(WindowerStamp other) => Known && other.Known && this == other;
}

internal readonly record struct WindowerStamps(WindowerStamp Hook, WindowerStamp Launcher, WindowerStamp Manifest, WindowerStamp Switch)
{
    public static WindowerStamp Of(string path) => WindowerStamp.Of(path);

    public bool Matches(WindowerStamps other) =>
        Hook.Same(other.Hook) && Launcher.Same(other.Launcher) && Manifest.Same(other.Manifest) && Switch.Same(other.Switch);
}

internal sealed record WindowerChecked(WindowerManifest? Manifest, WindowerStamps Stamps, WindowerUpdateResult? Failed);

internal sealed class WindowerChecks
{
    internal static WindowerChecks Shared { get; } = new();

    internal TimeSpan GoodFor { get; init; } = TimeSpan.FromMinutes(5);
    internal TimeSpan FailedFor { get; init; } = TimeSpan.FromSeconds(30);
    internal Func<long> Clock { get; init; } = () => Environment.TickCount64;
    internal Func<string, string?, WindowerUpdater> NewUpdater { get; init; } = (dir, branch) => new WindowerUpdater(dir, branch);

    private sealed record Held(string Dir, string Branch, WindowerChecked Answer, long At);

    private readonly object _gate = new();
    private readonly CancellationTokenSource _ending = new();
    private Task? _running, _starting;
    private Held? _held;

    private static string Key(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    internal Action Starting()
    {
        var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _starting = begun.Task;
        return () => begun.TrySetResult();
    }

    internal void StartFor(string? configured)
    {
        try
        {
            if (WindowerInstall.Detect(configured) is not { } found) return;
            var dir = Key(found);
            if (FolderPins.Linked(dir)) return;
            var file = WindowerSettingsFile.Load(dir);
            if (file.LoadError is not null) return;
            var updater = NewUpdater(dir, file.Branch);
            Start(dir, updater.Branch, () => updater, WindowerLauncher.UpdateLimit);
        }
        catch (Exception e) { Log.Warning(e, "Windower's update check couldn't be started"); }
    }

    internal void Start(string dir, string branch, Func<WindowerUpdater> updater, TimeSpan limit)
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false } || Fresh(dir, branch) is not null) return;
            _running = Task.Run(() => RunAsync(dir, branch, updater, limit));
        }
    }

    private async Task RunAsync(string dir, string branch, Func<WindowerUpdater> updater, TimeSpan limit)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ending.Token);
            cts.CancelAfter(limit);
            if (await updater().CheckAsync(cts.Token) is { } answer) lock (_gate) _held = new Held(Key(dir), branch, answer, Clock());
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Warning(e, "Windower's update check failed"); }
    }

    internal WindowerChecked? Take(string dir, string branch, CancellationToken ct)
    {
        Task? starting, running;
        lock (_gate) starting = _starting;
        if (starting is { IsCompleted: false }) starting.Wait(TimeSpan.FromSeconds(5), ct);
        lock (_gate) running = _running;
        if (running is { IsCompleted: false }) running.Wait(ct);
        lock (_gate) return Fresh(dir, branch);
    }

    internal void Record(string dir, string branch, WindowerChecked? answer)
    {
        if (answer is not { Failed: null }) return;
        lock (_gate) _held = new Held(Key(dir), branch, answer, Clock());
    }

    private WindowerChecked? Fresh(string dir, string branch)
    {
        if (_held is not { } h || h.Branch != branch || !h.Dir.Equals(Key(dir), StringComparison.OrdinalIgnoreCase)) return null;
        return Clock() - h.At < (h.Answer.Failed is null ? GoodFor : FailedFor).TotalMilliseconds ? h.Answer : null;
    }

    internal void Stop(TimeSpan wait)
    {
        _ending.Cancel();
        Task? running;
        lock (_gate) running = _running;
        running?.Wait(wait);
    }
}
