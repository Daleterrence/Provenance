namespace Provenance.Common.Social;

public static class FfxiWorlds
{
    private static readonly string[] Names =
    [
        "Undine", "Atomos", "Bahamut", "Shiva", "Titan", "Ramuh", "Phoenix", "Carbuncle", "Fenrir", "Sylph", "Valefor",
        "Alexander", "Leviathan", "Odin", "Ifrit", "Diabolos", "Caitsith", "Quetzalcoatl", "Siren", "Unicorn", "Gilgamesh",
        "Ragnarok", "Pandemonium", "Garuda", "Cerberus", "Kujata", "Bismarck", "Seraph", "Lakshmi", "Asura", "Midgardsormr",
        "Fairy", "Remora", "Hades",
    ];

    public static string? Name(int id) => id is >= 98 and <= 131 ? Names[id - 98] : null;
}
