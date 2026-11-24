namespace Provenance.Common.Social;

// State is "read", "pending", "failed", "realtime", "local" (from the game's message store) or "stored" (a request or invitation held in it)
public sealed record PolMessage(string From, int FromSlot, int Kind, long Time, long Seq, string State,
                                string Subject, string Text, int Mid = 0, string FromKey = "")
{
    public DateTimeOffset? When => Time > 0 ? DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime() : null;

    public string Key => $"{From}|{FromSlot}|{Seq}|{Time}";

    public bool HasBody => State is "read" or "realtime" or "local" or "stored";
    public bool IsChat => Kind == 0;
    public bool IsLocal => State == "local";
    public bool IsStored => State == "stored";
    public bool IsMe { get; init; }
    public string ToKey { get; init; } = "";
    public bool ToMe { get; init; }
    public bool Read { get; init; }
    public bool Ignored { get; init; }
    public string Group { get; init; } = "";
    public bool ShownInGame { get; init; }

    public const int KindFriendRequest = 1, KindFriendAccepted = 9, KindFriendDeclined = 10;

    // 0x12 is the server's "removed from friend group", 0x13 "friend group disbanded"
    public const int KindGroupInvite = 0xe, KindGroupAccepted = 0xf, KindGroupDeclined = 0x10,
                     KindGroupRemoved = 0x12, KindGroupDisbanded = 0x13;

    // FFXI only prints a message's text when it's 300 bytes or less, in the Shift-JIS the hook sends
    public const int MaxTextBytes = 300;
}

public sealed record PolStoredLines(IReadOnlyList<PolMessage> Received, IReadOnlyList<PolSentLine> Sent);

public sealed record PolSentLine(string To, string ToKey, int ToSlot, long Time, long Seq, string State,
                                 string Subject, string Text, int Mid = 0)
{
    public string Key => $"game:{(ToKey.Length > 0 ? ToKey : To)}|{ToSlot}|{Seq}|{Time}";
    public int FromSlot { get; init; } = -1;
    public bool ToOther { get; init; }
}
