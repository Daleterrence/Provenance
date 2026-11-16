using Provenance.Common.Mirror;
using Provenance.Common.PolFormat;
using Provenance.Common.Settings;
using Serilog;

namespace Provenance.Common.Patcher;

// Stands in for SE's version check at a login or a game start when the mirror's signed pointer names exactly the
// version that's installed. Any doubt at all and SE's asked as it always was
public sealed class MirrorCheck
{
    internal static MirrorCheck Shared { get; } = new((folder, ct) => MirrorClient.Shared.GetPointerAsync(folder, ct));

    // the pointer only moves when the publisher sees a new version, which is about once a day, so 10 minutes adds
    // little to how far behind it can be. A failure is only remembered for as long as it takes a login to retry.
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

    // never throws, and does nothing without a supported install
    public static void StartAtOpen(ISettingsService settings)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (settings.DetectedRegion is not { } region || !PolPaths.Supported(region)) return;
                if (MirrorLayout.RegionFolder(region) is { } folder) Shared.Fetching(folder);
            }
            catch (Exception e) { Log.Warning(e, "The patch mirror's pointer couldn't be fetched at start"); }
        });
    }

    internal VersionCheck Check(PolUpdater updater, CancellationToken ct)
    {
        var product = updater.Product;
        product.CheckStream();
        var local = PatchVer.Read(product);
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
        if (local.Length == 0 || MirrorLayout.RegionFolder(product.Region) is not { } folder) return null;
        var fetching = Fetching(folder);
        if (!fetching.Wait(WaitLimit, ct)) return null;
        var pointer = fetching.Result;
        return pointer is not null && pointer.Region == folder && pointer.Versions.TryGetValue(product.Game.Slug, out var named)
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

    // a task that can't fault, so a login that stops waiting leaves nothing unobserved behind it
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
