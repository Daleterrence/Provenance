namespace Provenance.Common.Game.Windower;

public sealed record WindowerKey(string Name, int ScanCode, string InternalName);

public static class WindowerKeys
{
    private static readonly (string Name, int Code, string Internal)[] Table =
    [
        ("Escape", 1, "escape"), ("Enter", 28, "enter"), ("Insert", 210, "insert"), ("Delete", 211, "delete"),
        ("Home", 199, "home"), ("End", 207, "end"), ("PageUp", 201, "pageup"), ("PageDown", 209, "pagedown"),
        ("LeftControl", 29, "lctrl"), ("LeftAlt", 56, "lalt"), ("LeftShift", 42, "lshift"), ("RightControl", 157, "rctrl"),
        ("RightAlt", 184, "ralt"), ("RightShift", 54, "rshift"), ("LeftWindows", 219, "lwindows"),
        ("RightWindows", 220, "rwindows"), ("AppMenu", 221, "apps"),
        ("F1", 59, "f1"), ("F2", 60, "f2"), ("F3", 61, "f3"), ("F4", 62, "f4"), ("F5", 63, "f5"), ("F6", 64, "f6"),
        ("F7", 65, "f7"), ("F8", 66, "f8"), ("F9", 67, "f9"), ("F10", 68, "f10"), ("F11", 87, "f11"), ("F12", 88, "f12"),
        ("F13", 100, "f13"), ("F14", 101, "f14"), ("F15", 102, "f15"),
        ("PrintScreen", 183, "sysrq"), ("ScrollLock", 70, "scrolllock"), ("Break", 197, "pause"),
        ("Up", 200, "up"), ("Down", 208, "down"), ("Left", 203, "left"), ("Right", 205, "right"), ("NumLock", 69, "numlock"),
        ("NumPad0", 82, "numpad0"), ("NumPad1", 79, "numpad1"), ("NumPad2", 80, "numpad2"), ("NumPad3", 81, "numpad3"),
        ("NumPad4", 75, "numpad4"), ("NumPad5", 76, "numpad5"), ("NumPad6", 77, "numpad6"), ("NumPad7", 71, "numpad7"),
        ("NumPad8", 72, "numpad8"), ("NumPad9", 73, "numpad9"), ("NumPadEnter", 156, "numpadenter"),
        ("NumPadPlus", 78, "numpad+"), ("NumPadMinus", 74, "numpad-"), ("NumPadMultiply", 55, "numpad*"),
        ("NumPadDivide", 181, "numpad/"), ("NumPadPeriod", 83, "numpad."),
        ("Tab", 15, "tab"), ("Backspace", 14, "backspace"), ("Space", 57, "space"),
        ("A", 30, "a"), ("B", 48, "b"), ("C", 46, "c"), ("D", 32, "d"), ("E", 18, "e"), ("F", 33, "f"), ("G", 34, "g"),
        ("H", 35, "h"), ("I", 23, "i"), ("J", 36, "j"), ("K", 37, "k"), ("L", 38, "l"), ("M", 50, "m"), ("N", 49, "n"),
        ("O", 24, "o"), ("P", 25, "p"), ("Q", 16, "q"), ("R", 19, "r"), ("S", 31, "s"), ("T", 20, "t"), ("U", 22, "u"),
        ("V", 47, "v"), ("W", 17, "w"), ("X", 45, "x"), ("Y", 21, "y"), ("Z", 44, "z"),
        ("Grave", 41, "`"), ("Number1", 2, "1"), ("Number2", 3, "2"), ("Number3", 4, "3"), ("Number4", 5, "4"),
        ("Number5", 6, "5"), ("Number6", 7, "6"), ("Number7", 8, "7"), ("Number8", 9, "8"), ("Number9", 10, "9"),
        ("Number0", 11, "0"), ("Hyphen", 12, "-"), ("Equals", 13, "="), ("LeftBracket", 26, "["), ("RightBracket", 27, "]"),
        ("Backslash", 43, "\\"), ("Semicolon", 39, ";"), ("Apostrophe", 40, "'"), ("Comma", 51, ","), ("Period", 52, "."),
        ("Slash", 53, "/"), ("CapsLock", 58, "capslock"),
        ("PlayPause", 162, "mmplaypause"), ("Stop", 164, "mmstop"), ("NextTrack", 153, "mmnexttrack"),
        ("PreviousTrack", 144, "mmprevtrack"), ("VolumeUp", 176, "mmvolup"), ("VolumeDown", 174, "mmvoldown"),
        ("Mute", 160, "mute"), ("MediaSelect", 237, "mmselect"), ("Power", 222, "power"), ("Sleep", 223, "sleep"),
        ("Wake", 227, "wake"), ("WebBack", 234, "webback"), ("WebForward", 233, "webforward"), ("WebStop", 232, "webstop"),
        ("WebRefresh", 231, "webrefresh"), ("WebHome", 178, "webhome"), ("WebSearch", 229, "websearch"),
        ("WebBookmarks", 230, "webfav"), ("Mail", 236, "webfav"), ("Computer", 235, "mycomputer"),
        ("Calculator", 161, "calculator"), ("Yen", 125, "yen"), ("Kana", 112, "kana"), ("Kanji", 148, "kanji"),
        ("Convert", 121, "convert"), ("NoConvert", 123, "noconvert"), ("Circumflex", 144, "circumflex"),
    ];

    public static IReadOnlyList<WindowerKey> All { get; } = Table.Select(t => new WindowerKey(t.Name, t.Code, t.Internal)).ToList();

    public static WindowerKey Insert { get; } = All.First(k => k.Name == "Insert");

    // PreviousTrack and Circumflex share 144 in Windower's enum, and .NET Framework names 144 Circumflex, so that's
    // what gets saved and sent (with its own internal name)
    private static readonly WindowerKey Circumflex = All.First(k => k.Name == "Circumflex");

    public static WindowerKey? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(k => k.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } k ? Canonical(k) : null;

    public static WindowerKey Canonical(WindowerKey key) => key.ScanCode == 144 ? Circumflex : key;

    public static bool Pickable(WindowerKey key) =>
        key.Name is not ("LeftShift" or "RightShift" or "LeftControl" or "RightControl" or "LeftAlt" or "RightAlt"
            or "LeftWindows" or "RightWindows" or "AppMenu");
}
