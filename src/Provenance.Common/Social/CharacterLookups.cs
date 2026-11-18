using Provenance.Common.Hook;

namespace Provenance.Common.Social;

// one lookup at a time, 3s apart like the hook, for a character someone's just turned up on that has no name yet or a
// zone to refresh. A failed one waits for that friend's next presence change
public sealed class CharacterLookups(CharacterNames names)
{
    private Dictionary<string, string> _seen = new(StringComparer.Ordinal);
    private readonly List<(PolFriendGame Game, string Friend)> _queue = new();
    // characters asked about this login. One hidden last time may be shown by now, so it gets one more ask
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
    // friends whose request never got written, with the character it was for. They get their ask back at their next presence change
    private readonly Dictionary<string, string> _unsent = new(StringComparer.Ordinal);
    // zones are only ever in memory, they change too often to keep
    private readonly Dictionary<string, string> _zones = new(StringComparer.Ordinal);
    // characters waiting in the queue for their zone, so a name turning up in the cache meanwhile doesn't drop them
    private readonly HashSet<string> _zoneDue = new(StringComparer.Ordinal);
    private string _askingFor = "";
    // ones given up on, in case the hook took them late and still answers
    private readonly List<(string Token, PolFriendGame Game)> _late = new();
    private readonly HookJobWatch _watch = new();
    private DateTime _last = DateTime.MinValue;

    public PolFriendGame? Asking { get; private set; }
    public string Token => _watch.Token ?? "";
    public int Queued => _queue.Count;

    public string? Zone(string key) => _zones.GetValueOrDefault(key);

    // characters that aren't looked up at all, for who's something nothing is shown about
    public Func<PolFriendGame, bool> Skip { get; set; } = _ => false;

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
            if (game is null || Skip(game)) continue;
            bool moved = !_seen.TryGetValue(id, out var was) || was != key;
            if (moved && _unsent.Remove(id, out var unsentKey)) _asked.Remove(unsentKey);
            var known = names.Get(key);
            bool zone = known is not { Why.Length: > 0 } && WantsZoneRead(key, moved);
            bool wanted = known is null ? moved : known.Why.Length > 0 ? AskAgain(key) : zone;
            if (!wanted || Asking?.Character.Key == key || _queue.Exists(q => q.Game.Character.Key == key)) continue;
            if (zone) _zoneDue.Add(key);
            _queue.Add((game, id));
        }
        _seen = now;
    }

    private bool AskAgain(string key) => names.Get(key) is { Why: "private" } && !_asked.Contains(key);

    // whether a friend on a character whose name we know gets a read for the zone. Just once, when they turn up
    private static bool WantsZoneRead(string key, bool turnedUp) => turnedUp;

    private static string? ZoneName(int id) => id != 0 ? FfxiNames.Area(id) : null;

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
        _queue.RemoveAll(q => Satisfied(q.Game.Character.Key) || !_seen.ContainsValue(q.Game.Character.Key) || Skip(q.Game));
        if (_queue.Count == 0) return null;
        (Asking, _askingFor) = _queue[0];
        _queue.RemoveAt(0);
        _asked.Add(Asking.Character.Key);
        _zoneDue.Remove(Asking.Character.Key);
        _watch.Begin(HookLimits.CharacterLookup, now);
        _last = now;
        return Asking;
    }

    private bool Satisfied(string key) => names.Get(key) switch
    {
        null => false,
        { Why.Length: > 0 } => !AskAgain(key),
        _ => !_zoneDue.Contains(key),
    };

    // true once the hook's settled one, kept or not. A late answer to one given up on still counts, as long as it's for
    // the same character
    public bool Answer(string token, string state, PolCharacterLookup? r)
    {
        if (token.Length == 0 || state is "" or "loading") return false;
        bool current = Asking is not null && token == Token;
        int late = current ? -1 : _late.FindIndex(x => x.Token == token);
        if (!current && late < 0) return false;
        var asked = current ? Asking! : _late[late].Game;
        if (r?.Character.Key == asked.Character.Key)
        {
            if (CharacterNames.FromAnswer(state, r) is { } known) names.Remember(asked.Character.Key, known);
            RememberZone(asked.Character.Key, state, r);
        }
        if (current) Asking = null;
        else _late.RemoveAt(late);
        return true;
    }

    // a private or missing record has no zone to keep, and a read that failed some other way leaves the old one
    private void RememberZone(string key, string state, PolCharacterLookup r)
    {
        if (state == "done" && r is { ZoneState: ProfileFieldState.Shown } && ZoneName(r.ZoneId) is { } zone) _zones[key] = zone;
        else if (state is "done" or "failed:-5324" or "failed:no profile") _zones.Remove(key);
    }

    public void Failed()
    {
        if (Asking is not null) _unsent[_askingFor] = Asking.Character.Key;
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
        _zoneDue.Clear();
        _unsent.Clear();
        _late.Clear();
        Asking = null;
        _watch.Forget();
    }
}
