using Microsoft.Win32;
using Provenance.Common;
using Provenance.Common.Game;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.Services;

public sealed class RegistryGameStore(PolRegion? region, Func<bool, RegistryKey?>? openKey = null) : IGameSettingsStore
{
    public string Key => $"registry|{region}";
    public string? Platform => null;
    public string? ProfileName => null;
    public string? ProfileDisplay => null;
    public bool SameShapeRenderSizes => false;
    public bool ReadOnly => false;

    private RegistryKey? Open(bool write) => openKey is not null ? openKey(write) : region is { } r ? FfxiRegistry.Open(r, write) : null;

    // the game makes its settings on its first start
    public FfxiSettings Load()
    {
        using var k = Open(false);
        if (k is null)
            throw Shown.Mark(new InvalidOperationException(region is { } r && PolPaths.TryGetInstallFolder(r, PolPaths.FfxiProduct) != null
                ? "FINAL FANTASY XI is installed, but it has no saved settings yet. Open the FINAL FANTASY XI Config Tool once as an administrator so it saves them, then open these settings again."
                : "FINAL FANTASY XI isn't installed on this PC, so there are no game settings to change."));
        return FfxiRegistry.LoadFrom(k);
    }

    public FfxiSettings LoadWindows()
    {
        using var k = Open(false);
        return k is null ? FfxiSettings.Defaults() : FfxiRegistry.LoadFrom(k);
    }

    public GameSaveResult Save(FfxiSettings settings, FfxiSettings original)
    {
        using var k = Open(true) ?? throw Shown.Mark(new InvalidOperationException("Provenance couldn't open the FINAL FANTASY XI settings."));
        int n = FfxiRegistry.SaveTo(k, settings, original);
        return new(n, n > 0);
    }
}
