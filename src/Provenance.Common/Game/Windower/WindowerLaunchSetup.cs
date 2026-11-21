using Microsoft.Win32;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Game.Windower;

public sealed record WindowerLaunchSetup
{
    public required string WindowerDir { get; init; }
    public required string ProfileName { get; init; }
    public bool ProfileFromAccount { get; init; }
    public required string PolExe { get; init; }
    public required PolRegion Region { get; init; }
    public string? FfxiDir { get; init; }
    public IReadOnlyDictionary<string, WindowerAddonList>? Lists { get; init; }
    public WindowerAddonList? SharedExtras { get; init; }
    public IReadOnlyList<string> LeaveOut { get; init; } = [];
    public RegistryKey? RegistryRoot { get; init; }
    public Func<string, string, WindowerUpdater>? NewUpdater { get; init; }
    public TimeSpan UpdateLimit { get; init; } = WindowerLauncher.UpdateLimit;
    internal WindowerBackgroundUpdates Background { get; init; } = WindowerBackgroundUpdates.Shared;
    internal WindowerChecks? Checks { get; init; }
    internal WindowerScanCache? ScanCache { get; init; }
    internal WindowerUpdateAnswer? KnownUpdate { get; init; }
}
