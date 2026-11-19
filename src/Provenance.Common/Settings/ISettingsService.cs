using System.Text.Json;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Settings;

public interface ISettingsService
{
    LauncherSettings Current { get; }

    PolRegion? DetectedRegion { get; }

    PolRegion RegionOrDefault => DetectedRegion ?? PolRegion.UnitedStates;

    void RedetectRegion() { }

    bool Save(LauncherSettings settings);

    void SaveChange(Action<LauncherSettings> change)
    {
        var before = JsonSerializer.Serialize(Current);
        change(Current);
        if (JsonSerializer.Serialize(Current) != before) Save(Current);
    }

    void OnOwnerThread(Action run) => run();

    event Action<string>? SaveFailed;

    string? LoadProblem { get; }

    string? SaveProblem => null;

    LauncherSettings? ReadSaved() => null;

    IDisposable? HoldLock() => null;
}
