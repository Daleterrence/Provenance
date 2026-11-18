using Provenance.Common.Social;
using Provenance.Wpf.Services;

namespace Provenance.Wpf.ViewModels.Social;

// what's on show at the start of a friend's playing line. Tip is what it's called, for the tooltip and the screen reader
public sealed record PartyIconView(string File, string Tip);

// the icon file at the start of a friend's playing line, by party state. A file that won't load is no icon
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
