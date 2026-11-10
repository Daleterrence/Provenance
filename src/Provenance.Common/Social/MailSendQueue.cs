using Provenance.Common.Hook;

namespace Provenance.Common.Social;

// the hook runs 2 sends at once but reads one request file at a time, and a new file replaces an untaken one, so
// lines queue here
public sealed class MailSendQueue
{
    public const int AtOnce = 2;
    private readonly Queue<(int Row, string Name, string Token, string Text)> _queue = new();
    // the pol.exe each went to, and what the hook last said about it and when ("sending" once its PUT's started)
    private readonly record struct SendOut(string Token, DateTime At, int Pid, string HookState = "",
                                           DateTime HookSaid = default);
    private readonly List<SendOut> _inFlight = new();
    private bool _closed;   // the session's ending, nothing more goes to its hook

    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    public int Queued => _queue.Count;

    // one of mine still on its way to the hook or the server, so a line of mine coming in could be its echo
    public bool Going => _queue.Count > 0 || _inFlight.Count > 0;

    public void Finished(string token) => _inFlight.RemoveAll(x => x.Token == token);

    public void HookSays(string token, string state)
    {
        if (_inFlight.FindIndex(x => x.Token == token) is var i and >= 0)
            _inFlight[i] = _inFlight[i] with { HookState = state, HookSaid = Now() };
    }

    // lines that mustn't expire yet. With nothing in flight, queued lines age from when they were typed
    public HashSet<string> StillPending()
    {
        var keep = new HashSet<string>();
        var live = _inFlight.Where(x => Now() - x.At < HookLimits.MailInFlight).ToList();
        if (live.Count == 0) return keep;
        foreach (var x in live) keep.Add("out:" + x.Token);
        foreach (var q in _queue) keep.Add("out:" + q.Token);
        return keep;
    }

    // true when a line in the log changed state
    public bool Add(ChatStore chat, int row, string name, string key, string text, int? pid, bool blocked)
    {
        var token = HookFiles.NewToken();
        chat.AddOutgoing(name, key, text, token);
        _queue.Enqueue((row, name, token, text));
        return Pump(chat, pid, blocked);
    }

    // blocked is the game running or a handle switch. True when a line in the log changed state
    public bool Pump(ChatStore? chat, int? pid, bool blocked)
    {
        bool changed = false;
        int newest = _inFlight.Count - 1;
        for (int i = newest; i >= 0; i--)
        {
            var x = _inFlight[i];
            if (Now() - x.At <= HookLimits.MailInFlight) continue;
            // never answered. Untaken it's withdrawn (only the newest can be), but once the hook has it, it might still
            // go, so it's not re-sent
            bool withdrawn = i == newest &&
                             HookFiles.Withdraw(x.Pid, HookContract.Requests.Send) == Withdrawal.Withdrawn;
            if (!withdrawn && chat?.SetSendState(x.Token, "unconfirmed") == true) changed = true;
            _inFlight.RemoveAt(i);
        }
        while (!_closed && _inFlight.Count < AtOnce && !blocked && _queue.Count > 0 &&
               pid is { } p && !SocialRequests.SendPending(p))
        {
            var (row, name, token, text) = _queue.Dequeue();
            // one that waited long enough to show "not sent" stays that way, they might've sent it again already
            var line = chat?.Entries.FirstOrDefault(e => e.Key == "out:" + token);
            if (line is not { State: "sending" or "queued" }) continue;
            // no subject, like the viewer's own chat window
            if (SocialRequests.Send(p, row, name, token, "", text))
                _inFlight.Add(new(token, Now(), p));
            else chat?.SetSendState(token, "failed:write");
        }
        return changed;
    }

    // the session's ending and its results won't get here. One the hook started, or will in a drain, is unconfirmed,
    // the rest are not sent
    public void Settle(ChatStore? chat, bool drained, string notSent)
    {
        var states = new List<(string, string)>();
        int newest = _inFlight.Count - 1;
        for (int i = newest; i >= 0; i--)
        {
            var x = _inFlight[i];
            // only the newest can still be untaken, the next file's only written once the hook's taken the last
            var w = i == newest ? HookFiles.Withdraw(x.Pid, HookContract.Requests.Send) : Withdrawal.Taken;
            // held a second ago it could've started since, the snapshot that says so takes about 300ms to get here
            bool held = x.HookState == "queued" && Now() - x.HookSaid >= TimeSpan.FromSeconds(1);
            bool went = w == Withdrawal.Taken ? drained || !held
                      : w == Withdrawal.Unknown && drained;   // the hook might take it before the drain
            states.Add((x.Token, went ? "unconfirmed" : notSent));
        }
        foreach (var q in _queue) states.Add((q.Token, notSent));
        chat?.SetSendStates(states);
        _inFlight.Clear();
        _queue.Clear();
        _closed = true;
    }
}
