namespace Provenance.Common.Game.Windower;

// Windower's console keys as people know them, from the names settings.xml uses
public static class WindowerKeyNames
{
    private static readonly Dictionary<string, string> Named = new()
    {
        ["PageUp"] = "Page Up", ["PageDown"] = "Page Down", ["PrintScreen"] = "Print Screen", ["ScrollLock"] = "Scroll Lock",
        ["Break"] = "Pause", ["Up"] = "Up arrow", ["Down"] = "Down arrow", ["Left"] = "Left arrow", ["Right"] = "Right arrow",
        ["NumLock"] = "Num Lock", ["NumPadEnter"] = "Numpad Enter", ["NumPadPlus"] = "Numpad +", ["NumPadMinus"] = "Numpad -",
        ["NumPadMultiply"] = "Numpad *", ["NumPadDivide"] = "Numpad /", ["NumPadPeriod"] = "Numpad .", ["CapsLock"] = "Caps Lock",
        ["Grave"] = "`", ["Hyphen"] = "-", ["Equals"] = "=", ["LeftBracket"] = "[", ["RightBracket"] = "]", ["Backslash"] = "\\",
        ["Semicolon"] = ";", ["Apostrophe"] = "'", ["Comma"] = ",", ["Period"] = ".", ["Slash"] = "/",
        ["PlayPause"] = "Play/Pause", ["NextTrack"] = "Next track", ["VolumeUp"] = "Volume up", ["VolumeDown"] = "Volume down",
        ["MediaSelect"] = "Media select", ["WebBack"] = "Browser back", ["WebForward"] = "Browser forward",
        ["WebStop"] = "Browser stop", ["WebRefresh"] = "Browser refresh", ["WebHome"] = "Browser home",
        ["WebSearch"] = "Browser search", ["WebBookmarks"] = "Browser favourites", ["NoConvert"] = "No convert",
        // the one key code both names share, saved as Circumflex
        ["Circumflex"] = "Previous track",
    };

    public static string Display(WindowerKey key)
    {
        if (Named.TryGetValue(key.Name, out var name)) return name;
        if (key.Name.StartsWith("NumPad", StringComparison.Ordinal)) return "Numpad " + key.Name[6..];
        if (key.Name.StartsWith("Number", StringComparison.Ordinal)) return key.Name[6..];
        return key.Name;
    }

    public static IReadOnlyList<ConsoleKeyChoice> Pickable() =>
        WindowerKeys.All.Where(WindowerKeys.Pickable).Select(WindowerKeys.Canonical).DistinctBy(k => k.ScanCode)
                    .Select(k => new ConsoleKeyChoice(k, Display(k))).ToList();

    // DirectInput's number for a key, as Windower uses: the scan code, plus 0x80 when extended. Windows swaps Pause and
    // Num Lock, and Print Screen only ever comes as a key up
    public static int DirectInputCode(int virtualKey, int scanCode, bool extended) => virtualKey switch
    {
        0x13 => 197,
        0x90 => 69,
        0x2C => 183,
        _ => (scanCode & 0x7F) | (extended ? 0x80 : 0),
    };
}
