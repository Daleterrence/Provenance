using Provenance.Common.Hook;

namespace Provenance.Common.Social;

public sealed class CharacterLookups(CharacterNames names)
{
    private Dictionary<string, string> _seen = new(StringComparer.Ordinal);
    private readonly List<(PolFriendGame Game, string Friend)> _queue = new();
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _unsent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _zones = new(StringComparer.Ordinal);
    private readonly HashSet<string> _zoneDue = new(StringComparer.Ordinal);
    private string _askingFor = "";
    private readonly List<(string Token, PolFriendGame Game)> _late = new();
    private readonly HookJobWatch _watch = new();
    private DateTime _last = DateTime.MinValue;

    public PolFriendGame? Asking { get; private set; }
    public string Token => _watch.Token ?? "";
    public int Queued => _queue.Count;

    public string? Zone(string key) => _zones.GetValueOrDefault(key);

    public Func<PolFriendGame, bool> Skip { get; set; } = _ => false;

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
            bool zone = known is not { Why.Length: > 0 } && moved;
            bool wanted = known is null ? moved : known.Why.Length > 0 ? AskAgain(key) : zone;
            if (!wanted || Asking?.Character.Key == key || _queue.Exists(q => q.Game.Character.Key == key)) continue;
            if (zone) _zoneDue.Add(key);
            _queue.Add((game, id));
        }
        _seen = now;
    }

    private bool AskAgain(string key) => names.Get(key) is { Why: "private" } && !_asked.Contains(key);

    private static string? ZoneName(int id) => id != 0 ? FfxiNames.Area(id) : null;

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

    public void Waiting(string hookToken, bool hookBusy, DateTime now)
    {
        if (Asking is not null) _watch.WaitingTurn(hookToken, hookBusy, now);
    }

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
