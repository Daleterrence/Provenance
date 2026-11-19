using Provenance.Common.Mirror;
using Provenance.Common.PolFormat;
using Provenance.Common.Settings;
using Serilog;

namespace Provenance.Common.Patcher;

public sealed class MirrorCheck
{
    internal static MirrorCheck Shared { get; } = new((folder, ct) => MirrorClient.Shared.GetPointerAsync(folder, ct));

    internal TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(10);
    internal TimeSpan FailedLifetime { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan FetchLimit { get; init; } = TimeSpan.FromSeconds(10);
    internal TimeSpan WaitLimit { get; init; } = TimeSpan.FromSeconds(3);
    internal Func<long> Clock { get; init; } = () => Environment.TickCount64;

    private sealed class Entry(Task<MirrorPointer?> task)
    {
        public Task<MirrorPointer?> Task { get; } = task;
        public long DoneAt;
    }

    private readonly Func<string, CancellationToken, Task<MirrorPointer?>> _fetch;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byFolder = new(StringComparer.Ordinal);

    internal MirrorCheck(Func<string, CancellationToken, Task<MirrorPointer?>> fetch) => _fetch = fetch;

    public static void StartAtOpen(ISettingsService settings)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (settings.DetectedRegion is not { } region || !PolPaths.Supported(region)) return;
                Shared.Fetching(MirrorLayout.RegionFolder(region));
            }
            catch (Exception e) { Log.Warning(e, "The patch mirror's pointer couldn't be fetched at start"); }
        });
    }

    internal VersionCheck Check(PolUpdater updater, CancellationToken ct)
    {
        var product = updater.Product;
        product.CheckStream();
        var local = PatchVer.ReadOrEmpty(product);
        if (Answer(product, local, ct) is { } mirror)
        {
            Log.Information("{Product} version check: the mirror answered {Version}", product.Name, mirror.Server);
            return mirror;
        }
        var check = updater.Check(local, ct);
        Log.Information("{Product} version check: PlayOnline answered {Version}, installed {Local}", product.Name, check.Server, check.Local);
        return check;
    }

    internal VersionCheck? Answer(PatchProduct product, string local, CancellationToken ct)
    {
        if (local.Length == 0) return null;
        var folder = MirrorLayout.RegionFolder(product.Region);
        var fetching = Fetching(folder);
        if (!fetching.Wait(WaitLimit, ct)) return null;
        var pointer = fetching.Result;
        return pointer is not null && pointer.Versions.TryGetValue(product.Game.Slug, out var named)
               && string.Equals(named, local, StringComparison.Ordinal)
            ? VersionCheck.Mirror(local) : null;
    }

    private Task<MirrorPointer?> Fetching(string folder)
    {
        lock (_gate)
        {
            if (_byFolder.TryGetValue(folder, out var have) && Usable(have)) return have.Task;
            Entry? entry = null;
            var task = Task.Run(() => Fetch(folder, () => entry!));
            entry = new Entry(task);
            _byFolder[folder] = entry;
            return task;
        }
    }

    private bool Usable(Entry e)
    {
        if (!e.Task.IsCompleted) return true;
        var age = Clock() - Volatile.Read(ref e.DoneAt);
        return age < (e.Task.Result is null ? FailedLifetime : Lifetime).TotalMilliseconds;
    }

    private async Task<MirrorPointer?> Fetch(string folder, Func<Entry> entry)
    {
        MirrorPointer? pointer = null;
        try
        {
            using var limit = new CancellationTokenSource(FetchLimit);
            pointer = await _fetch(folder, limit.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Information("The patch mirror's pointer for {Region} couldn't be fetched: {Reason}", folder, e.Message);
        }
        lock (_gate) Volatile.Write(ref entry().DoneAt, Clock());
        return pointer;
    }
}
