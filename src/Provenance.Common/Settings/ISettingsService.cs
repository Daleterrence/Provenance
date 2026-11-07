using System.Text.Json;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Settings;

// %APPDATA%\Provenance\settings.json
public interface ISettingsService
{
    LauncherSettings Current { get; }

    // from the registry, not a setting. A PC only ever has one working install
    PolRegion? DetectedRegion { get; }

    // after an install or a located one, which can be another region than the one found before
    void RedetectRegion() { }

    void Save(LauncherSettings settings);

    // just this change goes into settings.json as it is now, and only when it changes something there. With no file,
    // as here, Current stands in for it
    void SaveChange(Action<LauncherSettings> change)
    {
        var before = JsonSerializer.Serialize(Current);
        change(Current);
        if (JsonSerializer.Serialize(Current) != before) Save(Current);
    }

    // settings only ever change on the thread that made the service, so a worker hands its change over with this. With
    // no such thread, as here, it just runs
    void OnOwnerThread(Action run) => run();

    // only the first failed save of a run raises it, so a full disk doesn't pop a dialog on every change
    event Action<string>? SaveFailed;

    string? LoadProblem { get; }

    // settings.json as it is now, another copy may have saved since
    LauncherSettings? ReadSaved() => null;

    // the lock Save and ReadSaved take, held across both (re-entrant on this thread)
    IDisposable? HoldLock() => null;
}
