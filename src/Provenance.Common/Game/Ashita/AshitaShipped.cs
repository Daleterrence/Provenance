namespace Provenance.Common.Game.Ashita;

public static class AshitaShipped
{
    private static readonly HashSet<string> Addons = new(StringComparer.OrdinalIgnoreCase)
    {
        "actionparse", "activemon", "affinity", "ahcolors", "ahgo", "allmaps", "aspect", "autojoin", "autologin", "autorespond",
        "blucheck", "blumon", "blusets", "cartographer", "casper", "cfhblock", "chamcham", "changecall", "chatfix", "chatmon",
        "checker", "chime", "cleancs", "clearcolor", "clock", "config", "craftmon", "crosshair", "debuff", "distance",
        "drawdistance", "enternity", "equipmon", "fastswap", "filterless", "filters", "filterscan", "fps", "freemem", "gateway",
        "hideconsole", "hideobs", "hideparty", "hideui", "ime", "imguistyle", "instantah", "instantchat", "invmon", "itemwatch",
        "links", "logincmd", "logs", "macrofix", "mapdot", "minimapmon", "mipmap", "move", "namecolors", "nokb",
        "nomad", "noname", "onevent", "paranormal", "peekaboo", "petinfo", "quicksets", "recast", "renamer", "repeater",
        "sexchange", "singlerace", "skeletonkey", "stepdialog", "stfu", "timestamp", "tokens", "tparty", "trimspells", "truesight",
        "watchdog",
    };

    private static readonly HashSet<string> Plugins = new(StringComparer.OrdinalIgnoreCase)
    {
        "addons", "hardwaremouse", "minimap", "screenshot", "thirdparty", "toon", "winefix",
    };

    private static readonly HashSet<string> PolPlugins = new(StringComparer.OrdinalIgnoreCase) { "extraslots", "quicky", "sandbox" };

    public static bool IsAddon(string name) => Addons.Contains(name);

    public static bool IsPlugin(string name) => Plugins.Contains(name);

    public static bool IsPolPlugin(string name) => PolPlugins.Contains(name);
}
