using System.Globalization;
using Provenance.Common.Hook;
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
        "failed:timeout" => HookFailures.Messages.NoAnswer,
        // handed over with no answer, so it might still arrive and isn't offered again
        "unconfirmed" => HookFailures.Messages.Unconfirmed,
        // still in the game's outbox, so no send again or remove for it here
        "unsent" => HookFailures.Messages.Unsent,
        "failed:no such friend" or "failed:friend list changed" => HookFailures.Messages.FriendListChanged,
        "failed:pending" => HookFailures.Messages.NotAccepted,
        "failed:self" => HookFailures.Messages.ToSelf,
        "failed:too long" => HookFailures.Messages.TooLong,
        "failed:write" => HookFailures.Messages.WriteFailed,
        "failed:stopped" => HookFailures.Messages.Stopped,
        "failed:hourly limit" => HookFailures.Messages.HourlyLimit,
        "failed:session" => HookFailures.Messages.SessionEnded,
        "failed:game" => HookFailures.Messages.GameStarted,
        "failed:handle changing" => HookFailures.Messages.HandleChanging,
        "failed:busy" => HookFailures.Busy,
        "failed:returned" => HookFailures.Messages.Returned,
        var s when s.StartsWith("failed", StringComparison.Ordinal) => HookFailures.Messages.Rejected,
        _ => "",
    };

    public bool IsWith(string name, string key) =>
        key.Length > 0 && PeerKey.Length > 0 ? PeerKey == key
                                             : string.Equals(Peer, name, StringComparison.OrdinalIgnoreCase);
}
