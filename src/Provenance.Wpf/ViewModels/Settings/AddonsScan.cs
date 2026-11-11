using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;

namespace Provenance.Wpf.ViewModels.Settings;

internal abstract record AddonsScan;

internal sealed record WindowerScan(IReadOnlyList<WindowerPluginInfo> Plugins, IReadOnlyList<WindowerAddonInfo> Addons, WindowerAddonTree? Tree,
                                    IReadOnlyDictionary<string, WindowerAddonEntry> AddonsXml) : AddonsScan
{
    // the name Windower.exe spells it with, null when it doesn't offer it
    public string? Offered(AddonKind kind, string key) => kind == AddonKind.Addon
        ? Tree?.Addons.FirstOrDefault(a => string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase))?.Name
        : Plugins.FirstOrDefault(p => p.InManifest && string.Equals(p.Listing.Key, key, StringComparison.OrdinalIgnoreCase))?.Listing.Key;
}

internal sealed record AshitaScan(IReadOnlyList<AshitaAddon> Addons, IReadOnlyList<AshitaPlugin> Plugins) : AddonsScan;
