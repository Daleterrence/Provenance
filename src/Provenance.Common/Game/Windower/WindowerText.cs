using System.Text.RegularExpressions;

namespace Provenance.Common.Game.Windower;

internal static partial class WindowerText
{
    public static string Spaces(string text) => Whitespace().Replace(text, " ").Trim();

    public static T? FindByName<T>(IEnumerable<T> items, Func<T, string> nameOf, string name) where T : class =>
        items.FirstOrDefault(i => nameOf(i) == name) ?? items.FirstOrDefault(i => nameOf(i).Equals(name, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
