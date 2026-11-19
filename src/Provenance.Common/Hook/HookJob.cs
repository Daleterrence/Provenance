using System.Text.Json;

namespace Provenance.Common.Hook;

public sealed record HookJob(string Token, string State)
{
    public static readonly HookJob None = new("", "");

    public string? StateFor(string? token) => !string.IsNullOrEmpty(token) && Token == token ? State : null;

    internal static HookJob Read(JsonElement e) => new(JsonFields.Str(e, "token"), JsonFields.Str(e, "state"));

    public static bool IsFailed(string? state) => state?.StartsWith("failed", StringComparison.Ordinal) == true;
}

public sealed class HookJobWatch
{
    private DateTime _since;
    private TimeSpan _limit;

    public string? Token { get; private set; }
    public bool Running => Token is not null;

    public string Begin(TimeSpan limit, DateTime now) => Begin(HookFiles.NewToken(), limit, now);

    public string Begin(string token, TimeSpan limit, DateTime now)
    {
        Token = token;
        _limit = limit;
        _since = now;
        return token;
    }

    public void Forget() => Token = null;

    public void Restart(DateTime now) => _since = now;

    public void WaitingTurn(string hookToken, bool hookBusy, DateTime now)
    {
        if (Running && hookToken != Token && hookBusy) _since = now;
    }

    public TimeSpan Elapsed(DateTime now) => now - _since;

    public bool Overdue(DateTime now) => Elapsed(now) > _limit;

    public bool Reached(DateTime now) => Elapsed(now) >= _limit;

    public string? Settle(string? state, string ok, DateTime now)
    {
        if (Token is null) return null;
        var end = state == ok || HookJob.IsFailed(state) ? state : Overdue(now) ? "timeout" : null;
        if (end is not null) Token = null;
        return end;
    }
}
