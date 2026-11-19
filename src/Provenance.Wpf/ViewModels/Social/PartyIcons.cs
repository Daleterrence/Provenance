using Provenance.Common.Social;
using Provenance.Wpf.Services;

namespace Provenance.Wpf.ViewModels.Social;

public sealed record PartyIconView(string File, string Tip);

public static class PartyIcons
{
    internal static readonly Dictionary<PartyState, string> Files = new()
    {
        [PartyState.Member] = "party.svg",
        [PartyState.Leader] = "party-leader.svg",
    };

    public static IEnumerable<string> All => Files.Values;

    public static string For(PartyState state) =>
        Files.TryGetValue(state, out var file) && SvgIcons.Available(file) ? file : "";
}
