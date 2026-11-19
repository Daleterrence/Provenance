namespace Provenance.Common.Game.Ashita;

public static class AshitaPolPlugins
{
    private static readonly (string Name, string Reason)[] Takeovers =
    [
        ("autopol", "Provenance logs you in itself."),
        ("quicky", "Provenance takes you into the game itself."),
        ("extraslots", "Provenance manages your accounts itself."),
    ];

    public static IReadOnlyList<string> TakeoverNames { get; } = Takeovers.Select(t => t.Name).ToList();

    public static bool IsTakeover(string polPlugin) => Takeovers.Any(t => t.Name.Equals(polPlugin, StringComparison.OrdinalIgnoreCase));

    public static string IncompatibleReason(string polPlugin) =>
        Takeovers.FirstOrDefault(t => t.Name.Equals(polPlugin, StringComparison.OrdinalIgnoreCase)).Reason ?? "Provenance does this itself.";
}
