namespace Provenance.Wpf.ViewModels.Settings;

internal sealed record RowData(AddonKind Kind, string Key, string Name, string? Version, string? Author, string? Description)
{
    public bool On { get; init; }
    public bool HasSwitch { get; init; } = true;
    public bool CanChange { get; init; } = true;
    public bool CanInstall { get; init; }
    public bool Incompatible { get; init; }
    public IReadOnlyList<AddonBadge> Badges { get; init; } = [];
    // the team that makes it, when it comes with Windower or Ashita
    public string? Team { get; init; }
    // an addon's folder or a plugin's DLL, only once it's installed
    public string? Location { get; init; }
}
