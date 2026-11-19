using System.Globalization;
using Provenance.Common.Hook;
using System.Text.Json.Serialization;

namespace Provenance.Common.Social;

public sealed record ChatEntry(string Key, string Peer, bool Outgoing, string Subject, string Text,
                               long Time, bool Read, string State)
{
    public string PeerKey { get; init; } = "";

    public long Seq { get; init; }

    public int FromSlot { get; init; } = -1;
    public bool? ToOther { get; init; }

    [JsonIgnore]
    public DateTimeOffset When => DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime();

    [JsonIgnore]
    public string TimeText => When.ToString("t", CultureInfo.CurrentCulture);

    [JsonIgnore]
    public string? DayBreak { get; init; }

    // never the subject (FFXI's is always the same one), unless there's nothing else in the message
    [JsonIgnore]
    public string Display => Text.Length > 0 ? Text : Subject;

    [JsonIgnore]
    public bool Failed => Outgoing && State.StartsWith("failed", StringComparison.Ordinal);

    [JsonIgnore]
    public string StateText => State switch
    {
        "sending" or "queued" => "Sending...",
        "sent" => "Delivered to PlayOnline",
        "failed:timeout" => HookFailures.Messages.NoAnswer,
        "unconfirmed" => HookFailures.Messages.Unconfirmed,
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
