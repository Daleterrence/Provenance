namespace Provenance.Common.Social;

public static class PlayingText
{
    // what goes next to a friend in FFXI: Long when there's room for it, Short when there isn't
    public static (string Long, string Short) For(string name, string? world)
    {
        if (name.Length > 0 && world is not null) return ($"{name} - {world}", name);
        if (name.Length > 0) return (name, name);
        if (world is not null) return ($"Playing on {world}", $"Playing on {world}");
        return ("", "");
    }
}
