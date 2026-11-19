using System.Globalization;
using Provenance.Common.Win32;

namespace Provenance.Common.PolFormat;

public static class PolLocale
{
    private static readonly HashSet<string> EuropeanVersion = new(StringComparer.OrdinalIgnoreCase)
    {
        "AD", "AL", "AT", "AX", "BA", "BE", "BG", "BY", "CH", "CY", "CZ", "DE", "DK", "EE", "ES", "FI", "FO", "FR",
        "GB", "GG", "GI", "GR", "HR", "HU", "IE", "IM", "IS", "IT", "JE", "LI", "LT", "LU", "LV", "MC", "MD", "ME",
        "MK", "MT", "NL", "NO", "PL", "PT", "RO", "RS", "RU", "SE", "SI", "SJ", "SK", "SM", "TR", "UA", "VA", "XK",
        // Oceania, the US territories out there stay North American
        "AU", "CC", "CK", "CX", "FJ", "FM", "HM", "KI", "MH", "NC", "NF", "NR", "NU", "NZ", "PF", "PG", "PN", "PW",
        "SB", "TK", "TO", "TV", "VU", "WF", "WS",
    };

    public static PolRegion ForCountry(string? country) => country?.Trim().ToUpperInvariant() switch
    {
        "JP" => PolRegion.Japan,
        { } c when EuropeanVersion.Contains(c) => PolRegion.Europe,
        _ => PolRegion.UnitedStates,
    };

    public static PolRegion FromWindows() => ForCountry(HomeCountry() ?? FormatCountry());

    private static string? HomeCountry()
    {
        try
        {
            var name = new char[16];
            int n = Kernel32.GetUserDefaultGeoName(name, name.Length);
            // an unset one comes back as a UN area code like "001", not a country
            return n == 3 && char.IsAsciiLetter(name[0]) && char.IsAsciiLetter(name[1]) ? new string(name, 0, 2) : null;
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    private static string? FormatCountry()
    {
        try
        {
            var code = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            return code.Length == 2 && code != "IV" ? code : null;
        }
        catch (ArgumentException) { return null; }
    }
}
