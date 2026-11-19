using Provenance.Common.Game.ThirdParty;

namespace Provenance.Wpf.ViewModels.Settings;

internal static class HookFolder
{
    internal static string? Usable(string? path) => string.IsNullOrWhiteSpace(path) ? null : ThirdPartyHooks.Usable(path);

    internal static string? DriveProblem(string? path) => string.IsNullOrWhiteSpace(path) ? null : ThirdPartyHooks.DriveProblem(path);
}
