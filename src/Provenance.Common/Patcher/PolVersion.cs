namespace Provenance.Common.Patcher;

// POL's version value (app.dll 0x1029c0ca) and its up-to-date test (0x1029c057)
public static class PolVersion
{
    public static long Value(string s)
    {
        long v = 0;
        foreach (char ch in s)
        {
            if (ch == '\0' || ch == ' ') break;
            int c = (sbyte)(byte)ch;   // signed char, same as POL, don't "fix" it
            if (c == '_') continue;
            int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'A' and <= 'Z' ? c - 'A' + 10 : 0;
            v = unchecked(v * 37 + d);
        }
        return v;
    }

    public static bool UpToDate(string local, string server) =>
        local.Length == server.Length && Value(local) >= Value(server);
}
