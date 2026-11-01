namespace Provenance.Common.Social;

public enum FriendSearchKind { Handle, Hid, Character }

// the hook makes the same checks and refuses anything else, these just save a trip to it
public static class FriendSearch
{
    public static string Wire(FriendSearchKind kind) => kind switch
    {
        FriendSearchKind.Hid => "hid",
        FriendSearchKind.Character => "character",
        _ => "handle",
    };

    public static FriendSearchKind FromWire(string s) => s switch
    {
        "hid" => FriendSearchKind.Hid,
        "character" => FriendSearchKind.Character,
        _ => FriendSearchKind.Handle,
    };

    public static bool HandleOk(string s) =>
        s.Length is >= 1 and <= 15 && s.All(c => c is >= ' ' and <= '~') && s.Any(c => c != ' ');

    // 00-1234567 or 1234567, the dash and spaces don't count
    public static bool HidOk(string s)
    {
        if (s.Length > 20) return false;
        ulong v = 0;
        int digits = 0;
        foreach (var c in s)
        {
            if (c is '-' or ' ') continue;
            if (c is < '0' or > '9' || ++digits > 13) return false;
            v = v * 10 + (ulong)(c - '0');
        }
        return digits > 0 && v is >= 1 and <= 0xfffffffffff;
    }

    public static bool CharacterOk(string s) => s.Length is >= 1 and <= 15 && s.All(char.IsAsciiLetter);

    public static bool Ok(FriendSearchKind kind, string s) => kind switch
    {
        FriendSearchKind.Hid => HidOk(s),
        FriendSearchKind.Character => CharacterOk(s),
        _ => HandleOk(s),
    };

    // how FFXI keeps a name, and so what any hit for it is called
    public static string InitCap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
}
