namespace Provenance.Common.Social;

public static class FfxiProfileText
{
    public const string Title = "FINAL FANTASY XI";

    // a character tab's rows in order. Private is a value the record level hides, NotSet anything empty or not in a table
    public static IReadOnlyList<(string Label, string Value)> Rows(PolFfxiProfile p, string privateText, string notSet,
                                                                    Func<string, string> filter)
    {
        string Of((int Value, ProfileFieldState State) v, Func<int, string?> name) =>
            v.State == ProfileFieldState.Private ? privateText
            : v.State == ProfileFieldState.Shown && name(v.Value) is { } n ? n : notSet;
        string world = p.WorldNameState == ProfileFieldState.Private ? privateText
                     : p.WorldNameState == ProfileFieldState.Shown && p.WorldName.Length > 0 ? filter(p.WorldName) : notSet;
        return
        [
            ("World Name", world),
            ("Nation", Of(p.Nation, FfxiNames.Nation)),
            ("Current Area", Of(p.Area, FfxiNames.Area)),
            ("Job", Of(p.Job, FfxiNames.Job)),
            ("Job Level", Of(p.JobLevel, l => l > 0 ? l.ToString() : null)),
            ("Race", Of(p.Race, FfxiNames.Race)),
        ];
    }

    // "FINAL FANTASY XI (Show to friends)", the record level's word in brackets when there is one
    public static string Line(string privacy) => privacy.Length > 0 ? $"{Title} ({privacy})" : Title;
}
