namespace Provenance.Common.Social;

public static class PlayingText
{
    public static (string Long, string Short) For(string name, string? world, string? zone = null)
    {
        if (name.Length > 0 && world is not null)
            return (string.IsNullOrEmpty(zone) ? $"{name} - {world}" : $"{name} - {zone} ({world})", name);
        if (name.Length > 0) return (name, name);
        if (world is not null) return ($"Playing on {world}", $"Playing on {world}");
        return ("", "");
    }
}
