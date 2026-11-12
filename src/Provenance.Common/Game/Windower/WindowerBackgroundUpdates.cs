namespace Provenance.Common.Game.Windower;

// one per Windower folder for the life of the app
internal sealed class WindowerBackgroundUpdates
{
    internal static WindowerBackgroundUpdates Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, WindowerBackgroundUpdate> _byFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _ending = new();

    private static string Key(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    internal WindowerBackgroundUpdate Start(string folder, WindowerUpdateRun run, TimeSpan limit)
    {
        var key = Key(folder);
        var update = new WindowerBackgroundUpdate(key, run, _ending.Token, limit);
        lock (_gate) _byFolder[key] = update;
        return update;
    }

    internal WindowerBackgroundUpdate? Running(string folder)
    {
        lock (_gate) return _byFolder.TryGetValue(Key(folder), out var update) && !update.Finished ? update : null;
    }

    // the app's closing: everything stops, and anything started after this stops straight away
    internal void Stop(TimeSpan wait)
    {
        _ending.Cancel();
        WindowerBackgroundUpdate[] all;
        lock (_gate) all = [.. _byFolder.Values];
        Task.WaitAll([.. all.Select(u => u.Done)], wait);
    }
}
