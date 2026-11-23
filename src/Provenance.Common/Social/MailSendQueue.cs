using Provenance.Common.Hook;

namespace Provenance.Common.Social;

// the hook runs 2 sends at once but reads one request file at a time, so lines queue here
public sealed class MailSendQueue
{
    public const int AtOnce = 2;
    private readonly Queue<(int Row, string Name, string Token, string Text)> _queue = new();
    private readonly record struct SendOut(string Token, DateTime At, int Pid, string HookState = "",
                                           DateTime HookSaid = default);
    private readonly List<SendOut> _inFlight = new();
    private bool _closed;

    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    public int Queued => _queue.Count;

    public bool Going => _queue.Count > 0 || _inFlight.Count > 0;

    public void Finished(string token) => _inFlight.RemoveAll(x => x.Token == token);

    public void HookSays(string token, string state)
    {
        if (_inFlight.FindIndex(x => x.Token == token) is var i and >= 0)
            _inFlight[i] = _inFlight[i] with { HookState = state, HookSaid = Now() };
    }

    public HashSet<string> StillPending()
    {
        var keep = new HashSet<string>();
        var live = _inFlight.Where(x => Now() - x.At < HookLimits.MailInFlight).ToList();
        if (live.Count == 0) return keep;
        foreach (var x in live) keep.Add("out:" + x.Token);
        foreach (var q in _queue) keep.Add("out:" + q.Token);
        return keep;
    }

    public bool Add(ChatStore chat, int row, string name, string key, string text, int? pid, bool blocked)
    {
        var token = HookFiles.NewToken();
        chat.AddOutgoing(name, key, text, token);
        _queue.Enqueue((row, name, token, text));
        return Pump(chat, pid, blocked);
    }

    public bool Pump(ChatStore? chat, int? pid, bool blocked)
    {
        bool changed = false;
        int newest = _inFlight.Count - 1;
        for (int i = newest; i >= 0; i--)
        {
            var x = _inFlight[i];
            if (Now() - x.At <= HookLimits.MailInFlight) continue;
            bool withdrawn = i == newest &&
                             HookFiles.Withdraw(x.Pid, HookContract.Requests.Send) == Withdrawal.Withdrawn;
            if (!withdrawn && chat?.SetSendState(x.Token, "unconfirmed") == true) changed = true;
            _inFlight.RemoveAt(i);
        }
        while (!_closed && _inFlight.Count < AtOnce && !blocked && _queue.Count > 0 &&
               pid is { } p && !SocialRequests.SendPending(p))
        {
            var (row, name, token, text) = _queue.Dequeue();
            var line = chat?.Entries.FirstOrDefault(e => e.Key == "out:" + token);
            if (line is not { State: "sending" or "queued" }) continue;
            if (SocialRequests.Send(p, row, name, token, "", text))
                _inFlight.Add(new(token, Now(), p));
            else chat?.SetSendState(token, "failed:write");
        }
        return changed;
    }

    public void Settle(ChatStore? chat, bool drained, string notSent)
    {
        var states = new List<(string, string)>();
        int newest = _inFlight.Count - 1;
        for (int i = newest; i >= 0; i--)
        {
            var x = _inFlight[i];
            var w = i == newest ? HookFiles.Withdraw(x.Pid, HookContract.Requests.Send) : Withdrawal.Taken;
            bool held = x.HookState == "queued" && Now() - x.HookSaid >= TimeSpan.FromSeconds(1);
            bool went = w == Withdrawal.Taken ? drained || !held
                      : w == Withdrawal.Unknown && drained;
            states.Add((x.Token, went ? "unconfirmed" : notSent));
        }
        foreach (var q in _queue) states.Add((q.Token, notSent));
        chat?.SetSendStates(states);
        _inFlight.Clear();
        _queue.Clear();
        _closed = true;
    }

    public void Reopen() => _closed = false;

    // lines still waiting here never reached the hook, so they can't go once sending's off
    public bool FailQueued(ChatStore? chat, string state)
    {
        if (_queue.Count == 0) return false;
        var states = _queue.Select(q => (q.Token, state)).ToList();
        _queue.Clear();
        return chat?.SetSendStates(states) == true;
    }
}
