namespace Provenance.Common.Game.Windower;

public sealed class WindowerUpdateResult
{
    internal readonly List<string> Plugins = [], Addons = [], ResFetched = [], ResDeleted = [], Problems = [];

    internal WindowerUpdateResult(string branch) => Branch = branch;

    public string Branch { get; }
    public bool Skipped { get; internal set; }
    public string? SkipReason { get; internal set; }
    public bool LockTimedOut { get; internal set; }
    public bool ManifestDownloaded { get; internal set; }
    public bool HasManifest { get; internal set; }
    public Version HookBefore { get; internal set; } = new();
    public Version HookAfter { get; internal set; } = new();
    public Version LauncherBefore { get; internal set; } = new();
    public Version LauncherAfter { get; internal set; } = new();
    public IReadOnlyList<string> PluginsUpdated => Plugins;
    public bool AddonTreeChanged { get; internal set; }
    public IReadOnlyList<string> AddonsUpdated => Addons;
    public int LibFilesWritten { get; internal set; }
    public IReadOnlyList<string> ResUpdated => ResFetched;
    public IReadOnlyList<string> ResRemoved => ResDeleted;
    public IReadOnlyList<string> Errors => Problems;
    public IReadOnlyList<string> UnreachableHosts { get; internal set; } = [];

    public bool HookChanged => HookBefore != HookAfter;
    public bool LauncherChanged => LauncherBefore != LauncherAfter;
    public bool Changed => HookChanged || LauncherChanged || Plugins.Count > 0 || Addons.Count > 0 || LibFilesWritten > 0 ||
                           ResFetched.Count > 0 || ResDeleted.Count > 0;
}
