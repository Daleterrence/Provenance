using Provenance.Common.Hook;

namespace Provenance.Common.Social;

public sealed class ChatSendGate(TimeSpan giveUp)
{
    private readonly HookJobWatch _watch = new();
    private string _text = "";
    public string? Token => _watch.Token;

    public bool CanSend => !_watch.Running;

    public void Sent(string token, string text, DateTime now)
    {
        _watch.Begin(token, giveUp, now);
        _text = text;
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
        !_watch.Running || !_watch.Overdue(now) ? null : Done("failed:timeout");

    private (string State, string Text) Done(string state)
    {
        var text = _text;
        Reset();
        return (state, text);
    }

    public void Reset()
    {
        _watch.Forget();
        _text = "";
    }
}
