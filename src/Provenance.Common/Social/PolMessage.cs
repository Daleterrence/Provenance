namespace Provenance.Common.Social;

// State is "read" (body downloaded), "pending", "failed", "realtime", "local" (from the game's message store, not
// on the server any more) or "stored" (a request or invitation held in that store, answered from there)
public sealed record PolMessage(string From, int FromSlot, int Kind, long Time, long Seq, string State,
                                string Subject, string Text, int Mid = 0, string FromKey = "")
{
    public DateTimeOffset? When => Time > 0 ? DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime() : null;

    // the in-memory ids are session-masked, this one's stable
    public string Key => $"{From}|{FromSlot}|{Seq}|{Time}";

    public bool HasBody => State is "read" or "realtime" or "local" or "stored";
    public bool IsChat => Kind == 0;
    public bool IsLocal => State == "local";
    public bool IsStored => State == "stored";
    public bool IsMe { get; init; }
    // who it was addressed to (the hook's hashed POL ID + handle) and whether that's my own POL ID
    public string ToKey { get; init; } = "";
    public bool ToMe { get; init; }
    public bool Read { get; init; }
    // the hook's own check of a request's sender against my ignore list, a handle's row or an all-handles one
    public bool Ignored { get; init; }
    // the group a removal or disband was about, from its attachment
    public string Group { get; init; } = "";

    public const int KindFriendRequest = 1, KindFriendAccepted = 9, KindFriendDeclined = 10;

    // 0x12 is the server's "removed from friend group", 0x13 "friend group disbanded"
    public const int KindGroupInvite = 0xe, KindGroupAccepted = 0xf, KindGroupDeclined = 0x10,
                     KindGroupRemoved = 0x12, KindGroupDisbanded = 0x13;

    // FFXI only prints a message's text when it's 300 bytes or less, in the Shift-JIS the hook sends
    public const int MaxTextBytes = 300;
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
