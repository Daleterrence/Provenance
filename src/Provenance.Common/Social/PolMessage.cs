using System.Text;

namespace Provenance.Common.Social;

// State is "read" (body downloaded), "pending", "failed", "realtime" or "local" (from the game's message store, not
// on the server any more)
public sealed record PolMessage(string From, int FromSlot, int Kind, long Time, long Seq, string State,
                                string Subject, string Text, int Mid = 0, string FromKey = "")
{
    public DateTimeOffset? When => Time > 0 ? DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime() : null;

    // the in-memory ids are session-masked, this one's stable
    public string Key => $"{From}|{FromSlot}|{Seq}|{Time}";

    public bool HasBody => State is "read" or "realtime" or "local";
    public bool IsChat => Kind == 0;
    public bool IsLocal => State == "local";
    public bool IsMe { get; init; }
    // who it was addressed to (the hook's hashed POL ID + handle) and whether that's my own POL ID
    public string ToKey { get; init; } = "";
    public bool ToMe { get; init; }
    public bool Read { get; init; }
    // the group a removal or disband was about, from its attachment
    public string Group { get; init; } = "";

    public const int KindFriendRequest = 1, KindFriendAccepted = 9, KindFriendDeclined = 10;

    // 0x12 is the server's "removed from friend group", 0x13 "friend group disbanded"
    public const int KindGroupInvite = 0xe, KindGroupAccepted = 0xf, KindGroupDeclined = 0x10,
                     KindGroupRemoved = 0x12, KindGroupDisbanded = 0x13;

    // FFXI only prints a message's text when it's 300 bytes or less, in the Shift-JIS the hook sends
    public const int MaxTextBytes = 300;

    private static readonly Encoding Sjis = MakeSjis("??"), SjisOnly = MakeSjis("");

    // the hook turns what Shift-JIS hasn't got into '?', so 2 a char never lets one through. The fallback's per UTF-16
    // unit, so an emoji counts 4 in case polcore stands in for each half
    private static Encoding MakeSjis(string missing)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, new EncoderReplacementFallback(missing), DecoderFallback.ReplacementFallback);
    }

    public static int TextBytes(string text) => Sjis.GetByteCount(text.Replace("\r", ""));

    // a character Shift-JIS has as two bytes (Japanese, mostly), not one it hasn't got at all
    public static bool HasDoubleByte(string text) =>
        text.Any(c => !char.IsSurrogate(c) && SjisOnly.GetByteCount(new[] { c }) == 2);

    // never half a surrogate pair
    public static string FitBytes(string text, int bytes)
    {
        int used = 0, i = 0;
        while (i < text.Length)
        {
            int len = char.IsSurrogatePair(text, i) ? 2 : 1;
            int n = text[i] == '\r' ? 0 : Sjis.GetByteCount(text.ToCharArray(i, len));
            if (used + n > bytes) break;
            used += n;
            i += len;
        }
        return text[..i];
    }
}

public sealed record PolStoredLines(IReadOnlyList<PolMessage> Received, IReadOnlyList<PolSentLine> Sent);

// a line I sent from the game, out of its message store. State is "sent" (s\a) or "unsent" (still in its outbox)
public sealed record PolSentLine(string To, string ToKey, int ToSlot, long Time, long Seq, string State,
                                 string Subject, string Text, int Mid = 0)
{
    public string Key => $"game:{(ToKey.Length > 0 ? ToKey : To)}|{ToSlot}|{Seq}|{Time}";
    // the handle slot it went from, and whether it went to another POL ID (not one of my own handles)
    public int FromSlot { get; init; } = -1;
    public bool ToOther { get; init; }
}
