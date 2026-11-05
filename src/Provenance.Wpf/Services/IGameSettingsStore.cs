using Provenance.Common.Game;

namespace Provenance.Wpf.Services;

public readonly record struct GameSaveResult(int Changed, bool Wrote);

public interface IGameSettingsStore
{
    // the same Key means the same place to save, so a switch back doesn't reload for nothing
    string Key { get; }
    string? Platform { get; }
    string? ProfileName { get; }
    string? ProfileDisplay { get; }
    bool SameShapeRenderSizes { get; }
    // shown, but never saved to
    bool ReadOnly { get; }

    // throws when there's nothing to load, Shown with the reason
    FfxiSettings Load();
    GameSaveResult Save(FfxiSettings settings, FfxiSettings original);
}
