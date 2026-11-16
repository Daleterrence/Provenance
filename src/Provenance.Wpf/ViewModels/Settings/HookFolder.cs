using Provenance.Common.Game.ThirdParty;

namespace Provenance.Wpf.ViewModels.Settings;

// what the Game page and the setup card make of a Windower or Ashita folder the player typed or browsed to, the same
// folder Play would use
internal static class HookFolder
{
    internal static string? Usable(string? path) => string.IsNullOrWhiteSpace(path) ? null : ThirdPartyHooks.Usable(path);

    internal static string? DriveProblem(string? path) => string.IsNullOrWhiteSpace(path) ? null : ThirdPartyHooks.DriveProblem(path);
}
