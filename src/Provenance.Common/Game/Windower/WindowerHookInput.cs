using Provenance.Common.PolFormat;

namespace Provenance.Common.Game.Windower;

public sealed record WindowerHookInput
{
    public required WindowerProfile Profile { get; init; }
    public required PolRegion? Region { get; init; }
    public required string PlayOnlinePath { get; init; }
    public required string FfxiPath { get; init; }
    public required string WindowerPath { get; init; }
    public required string Branch { get; init; }
    public required string LauncherVersion { get; init; }
    public required string HookVersion { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public IReadOnlyList<string> RequiredPlugins { get; init; } = [];
    public IReadOnlyList<string> Plugins { get; init; } = [];
    public IReadOnlyList<string> Addons { get; init; } = [];
}
