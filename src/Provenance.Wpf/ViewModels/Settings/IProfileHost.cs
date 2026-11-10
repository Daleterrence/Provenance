using Provenance.Common.Game.Windower;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Settings;

public interface IProfileHost
{
    bool IsWindower { get; }
    bool IsAshita { get; }
    string? WindowerDir { get; }
    bool WindowerLinked { get; }
    string? AshitaDir { get; }
    HookProfileItem? WindowerProfile { get; }
    HookProfileItem? AshitaBootConfig { get; }
    StatusLine ProfileStatus { get; }
    string? WindowerProblem { get; set; }
    WindowerSettingsFile? ReadWindower(string dir);
    void RefreshThirdPartyHookLists(string? windowerKeep = null, string? ashitaKeep = null);
    void SaveThirdPartyHook();
    void NotifyProfilesChanged();
    event Action? WindowerReload, AshitaReload, ProfilesChanged;
}
