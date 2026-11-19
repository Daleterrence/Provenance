namespace Provenance.Common.Social;

// the viewer's colour table is ARGB on a light window
public static class ChatLook
{
    public const int Emotes = 23, Colours = 12;

    private static readonly uint[] Table =
    [
        0xff101010, 0xff101010, 0xff101010, 0xffa02048, 0xff101010, 0xff101010, 0xff000079, 0xff103060, 0xff027aa3,
        0xff2b6e1f, 0xff003800, 0xff4d0808, 0xff800080, 0xffb6427c, 0xff9d2a04, 0xff808000, 0xff474747,
    ];

    // past the table (17-23) or 'z' the viewer reads whatever's there, so it's null here
    public static uint? Argb(int colourChar) =>
        colourChar is >= 0 and < 17 && Table[colourChar] != Table[0] ? Table[colourChar] : null;

    // the picker's 12, each the char 5 on from its place
    public static uint PickerArgb(int pick) => Table[pick + 5];

    private static readonly string[] Names =
        ["Black", "Navy", "Dark blue", "Blue", "Green", "Dark green", "Maroon", "Purple", "Pink", "Rust", "Olive", "Grey"];

    public static string ColourName(int pick) => pick >= 0 && pick < Names.Length ? Names[pick] : "No colour";

    public static int Emote(int e) => e is >= 1 and <= Emotes ? e : 0;

    public static string Language(IReadOnlyList<int> categories) => categories.Count > 2 ? categories[2] switch
    {
        300 => "JP",
        301 => "EN",
        302 => "FR",
        303 => "GE",
        _ => "",
    } : "";

    // #, language in 2 hex, use and kind letters, then index. Kind U is a room a user made
    private const int KindAt = 4;
    public static bool Official(string irc) => irc.Length > KindAt && irc[KindAt] != 'U';
}
