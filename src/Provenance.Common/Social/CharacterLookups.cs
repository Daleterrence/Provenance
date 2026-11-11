using Provenance.Common.Hook;

namespace Provenance.Common.Social;

// one lookup at a time, 3s apart like the hook, for an uncached character someone's just turned up on. A failed one
// waits for that friend's next presence change, never a timer
public sealed class CharacterLookups(CharacterNames names)
{
    private Dictionary<string, string> _seen = new(StringComparer.Ordinal);
    private readonly List<(PolFriendGame Game, string Friend)> _queue = new();
    // friends asked about this login. A character hidden last time may be shown by now, so its friend gets one more ask
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
    // ones whose request never got written. They get their ask back at their next presence change
    private readonly HashSet<string> _unsent = new(StringComparer.Ordinal);
    private string _askingFor = "";
    // ones given up on, in case the hook took them late and still answers
    private readonly List<(string Token, PolFriendGame Game)> _late = new();
    private readonly HookJobWatch _watch = new();
    private DateTime _last = DateTime.MinValue;

    public PolFriendGame? Asking { get; private set; }
    public string Token => _watch.Token ?? "";
    public int Queued => _queue.Count;

    // an empty list says nothing about who's on what (the hook hasn't loaded it yet), so it changes nothing
    public void Seen(IReadOnlyCollection<PolFriend> friends)
    {
        if (friends.Count == 0) return;
        names.Refresh();
        var now = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in friends)
        {
            var id = f.Key.Length > 0 ? f.Key : "n:" + f.Name;
            var game = f.Online && !f.Pending ? f.Game : null;
            var key = game?.Character.Key ?? "";
            now[id] = key;
            if (game is null) continue;
            bool moved = !_seen.TryGetValue(id, out var was) || was != key;
            if (moved && _unsent.Remove(id)) _asked.Remove(id);
            bool wanted = names.Get(key) is null ? moved : AskAgain(key, id);
            if (!wanted || Asking?.Character.Key == key || _queue.Exists(q => q.Game.Character.Key == key)) continue;
            _queue.Add((game, id));
        }
        _seen = now;
    }

    private bool AskAgain(string key, string friend) => names.Get(key) is { Why: "private" } && !_asked.Contains(friend);

    // the one to ask the hook for now, if any. Anyone who's left that character meanwhile drops out of the queue
    public PolFriendGame? Next(DateTime now)
    {
        if (Asking is { } stuck && _watch.Overdue(now))
        {
            _late.Add((Token, stuck));
            if (_late.Count > 8) _late.RemoveAt(0);
            Asking = null;
        }
        if (Asking is not null || now - _last < HookLimits.ProfileServerGap) return null;
        names.Refresh();
        _queue.RemoveAll(q => (names.Get(q.Game.Character.Key) is not null && !AskAgain(q.Game.Character.Key, q.Friend))
                              || !_seen.ContainsValue(q.Game.Character.Key));
        if (_queue.Count == 0) return null;
        (Asking, _askingFor) = _queue[0];
        _queue.RemoveAt(0);
        _asked.Add(_askingFor);
        _watch.Begin(HookLimits.CharacterLookup, now);
        _last = now;
        return Asking;
    }

    // true once the hook's settled one, kept or not. A late answer to one given up on still counts, as long as it's for
    // the same character
    public bool Answer(string token, string state, PolCharacterLookup? r)
    {
        if (token.Length == 0 || state is "" or "loading") return false;
        bool current = Asking is not null && token == Token;
        int late = current ? -1 : _late.FindIndex(x => x.Token == token);
        if (!current && late < 0) return false;
        var asked = current ? Asking! : _late[late].Game;
        if (CharacterNames.FromAnswer(state, r) is { } known && r?.Character.Key == asked.Character.Key)
            names.Remember(asked.Character.Key, known);
        if (current) Asking = null;
        else _late.RemoveAt(late);
        return true;
    }

    public void Failed()
    {
        if (Asking is not null) _unsent.Add(_askingFor);
        Asking = null;
    }

    // the give-up clock only starts once the hook's taken it, it waits while the hook's busy with another read
    public void Waiting(string hookToken, bool hookBusy, DateTime now)
    {
        if (Asking is not null) _watch.WaitingTurn(hookToken, hookBusy, now);
    }

    // a new session. Who's on what carries over, so a failed one still waits for a real change
    public void Reset()
    {
        _queue.Clear();
        _asked.Clear();
        _unsent.Clear();
        _late.Clear();
        Asking = null;
        _watch.Forget();
    }
}
