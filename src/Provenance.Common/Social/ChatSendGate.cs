namespace Provenance.Common.Social;

// the app's half of the hook's send rule: one line in flight
public sealed class ChatSendGate(TimeSpan giveUp)
{
    private DateTime? _sentAt;
    private string _text = "";
    public string? Token { get; private set; }

    public bool CanSend => Token == null;

    public void Sent(string token, string text, DateTime now)
    {
        Token = token;
        _text = text;
        _sentAt = now;
    }

    // the hook's state for my token with the line as it was typed, or null while it's still going
    public (string State, string Text)? Settle(string? hookToken, string? hookState, DateTime now)
    {
        if (Token != null && hookToken == Token && hookState is { } s &&
            (s == "sent" || s.StartsWith("sent:", StringComparison.Ordinal) || s.StartsWith("failed", StringComparison.Ordinal)))
            return Done(s);
        return Expire(now);
    }

    // a token the hook never took or never answered counts as gone after the give-up, snapshot or not
    public (string State, string Text)? Expire(DateTime now) =>
        Token == null || _sentAt is not { } at || now - at <= giveUp ? null : Done("failed:timeout");

    private (string State, string Text) Done(string state)
    {
        var text = _text;
        Reset();
        return (state, text);
    }

    public void Reset()
    {
        Token = null;
        _text = "";
        _sentAt = null;
    }
}
