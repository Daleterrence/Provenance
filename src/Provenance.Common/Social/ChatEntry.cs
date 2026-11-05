using System.Globalization;
using System.Text.Json.Serialization;

namespace Provenance.Common.Social;

public sealed record ChatEntry(string Key, string Peer, bool Outgoing, string Subject, string Text,
                               long Time, bool Read, string State)
{
    // POL ID + handle, hashed by the hook. Entries without one match by name
    public string PeerKey { get; init; } = "";

    // the message header's seq, which orders lines that share a second. Older logs don't have it
    public long Seq { get; init; }

    // for a line I sent: the handle slot it went from (-1 unknown) and whether it went to another POL ID (null unknown)
    public int FromSlot { get; init; } = -1;
    public bool? ToOther { get; init; }

    public DateTimeOffset When => DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime();

    public string WhenText
    {
        get
        {
            var w = When;
            var now = DateTimeOffset.Now;
            var t = w.ToString("t", CultureInfo.CurrentCulture);
            if (w.Date == now.Date) return t;
            if (w.Date == now.Date.AddDays(-1)) return "Yesterday " + t;
            return w.Year == now.Year ? $"{w:d MMM} {t}" : $"{w:d MMM yyyy} {t}";
        }
    }

    public string TimeText => When.ToString("t", CultureInfo.CurrentCulture);

    // never saved, it depends on the lines round it and today's date
    [JsonIgnore]
    public string? DayBreak { get; init; }

    // never the subject (FFXI's is always the same one), unless there's nothing else in the message. Worked out when
    // it's read, so old logs show the same
    public string Display => Text.Length > 0 ? Text : Subject;

    public bool Failed => Outgoing && State.StartsWith("failed", StringComparison.Ordinal);

    public string StateText => State switch
    {
        "sending" or "queued" => "Sending...",
        "sent" => "Delivered to PlayOnline",
        "failed:timeout" => "Not sent: PlayOnline didn't answer.",
        // handed over with no answer, so it might still arrive and isn't offered again
        "unconfirmed" => "Not confirmed: Provenance didn't hear back from PlayOnline. It might still have been sent.",
        // still in the game's outbox, so no send again or remove for it here
        "unsent" => "Not sent: FINAL FANTASY XI didn't finish sending it.",
        "failed:no such friend" or "failed:friend list changed" => "Not sent: the friend list changed.",
        "failed:pending" => "Not sent: this person hasn't accepted your friend request yet.",
        "failed:self" => "Not sent: you can't send a message to your own handle.",
        "failed:too long" => "Not sent: messages can't be longer than 300 characters, and some characters count as two.",
        "failed:write" => "Not sent: Provenance couldn't send it to PlayOnline.",
        "failed:stopped" => "Not sent: PlayOnline didn't take your recent messages, so sending is off until you log in again.",
        "failed:hourly limit" => "Not sent: you've sent 600 messages in the last hour. Try again later.",
        "failed:session" => "Not sent: the PlayOnline session ended before it went.",
        "failed:game" => "Not sent: FINAL FANTASY XI started before it went.",
        "failed:handle changing" => "Not sent: your handle was changing.",
        "failed:returned" => "Not delivered: PlayOnline sent it back.",
        var s when s.StartsWith("failed", StringComparison.Ordinal) => "Not sent: PlayOnline didn't accept it.",
        _ => "",
    };

    public bool IsWith(string name, string key) =>
        key.Length > 0 && PeerKey.Length > 0 ? PeerKey == key
                                             : string.Equals(Peer, name, StringComparison.OrdinalIgnoreCase);
}
