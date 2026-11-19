using Provenance.Common.Game.ThirdParty;

namespace Provenance.Common.Game;

public readonly record struct GameSaveResult(int Changed, bool Wrote);

public interface IGameSettingsStore
{
    string Key { get; }
    ThirdPartyHook? Platform { get; }
    string? ProfileName { get; }
    string? ProfileDisplay { get; }
    bool SameShapeRenderSizes { get; }
    bool ReadOnly { get; }

    FfxiSettings Load();
    GameSaveResult Save(FfxiSettings settings, FfxiSettings original);
}
