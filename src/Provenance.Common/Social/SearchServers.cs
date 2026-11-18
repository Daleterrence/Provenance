namespace Provenance.Common.Social;

public readonly record struct SearchEndpoint(string Host, int Port);

public static class SearchServers
{
    // the id the server wants folds the ctsid down to 20 bits, byte 2 being the world and left out
    public static uint Id(CharacterId c) => (c.Ctsid & 0xffff) | ((c.Ctsid >> 8) & 0xf0000);

    private static readonly Dictionary<string, string> Addresses = new(StringComparer.Ordinal)
    {
        ["Bahamut"] = "124.150.154.61",
        ["Shiva"] = "124.150.154.62",
        ["Phoenix"] = "124.150.154.63",
        ["Carbuncle"] = "124.150.154.64",
        ["Fenrir"] = "124.150.154.65",
        ["Sylph"] = "124.150.154.66",
        ["Valefor"] = "124.150.154.67",
        ["Leviathan"] = "124.150.154.68",
        ["Odin"] = "124.150.154.69",
        ["Quetzalcoatl"] = "124.150.154.70",
        ["Siren"] = "124.150.154.71",
        ["Ragnarok"] = "124.150.154.72",
        ["Cerberus"] = "124.150.154.73",
        ["Bismarck"] = "124.150.154.74",
        ["Lakshmi"] = "124.150.154.75",
        ["Asura"] = "124.150.154.76",
        ["Atomos"] = "124.150.154.77",
    };

    public static SearchEndpoint? Retail(int world) =>
        FfxiWorlds.Name(world) is { } name && Addresses.TryGetValue(name, out var host) ? new SearchEndpoint(host, SearchPackets.Port) : null;
}
