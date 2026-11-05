namespace Provenance.Common.Game.Windower;

public static class WindowerProfileNames
{
    // a profile really called Default keeps the name, and the unnamed one says it has none
    internal static string DisplayName(string name, IReadOnlyCollection<string> all) =>
        name.Length > 0 ? name : all.Any(n => n.Equals("Default", StringComparison.OrdinalIgnoreCase)) ? "Default (no name)" : "Default";
}
