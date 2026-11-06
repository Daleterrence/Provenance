using Provenance.Common.Hook;

namespace Provenance.Common.Social;

// one lookup at a time, 3 s apart like the hook, for an uncached character someone's just turned up on. A failed one
// waits for that friend's next presence change, never a timer
public sealed class CharacterLookups(CharacterNames names)
{
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(3);
    // polcore fails a read at 100 secs
    public static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(130);

    private Dictionary<string, string> _seen = new(StringComparer.Ordinal);
    private readonly List<PolFriendGame> _queue = new();
    // ones given up on, in case the hook took them late and still answers
    private readonly List<(string Token, PolFriendGame Game)> _late = new();
    private DateTime _last = DateTime.MinValue, _askedAt;

    public PolFriendGame? Asking { get; private set; }
    public string Token { get; private set; } = "";
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
            var key = game?.CharacterKey ?? "";
            now[id] = key;
            if (game is null || (_seen.TryGetValue(id, out var was) && was == key)) continue;
            if (names.Get(key) is not null || Asking?.CharacterKey == key || _queue.Exists(q => q.CharacterKey == key)) continue;
            _queue.Add(game);
        }
        _seen = now;
    }

    // the one to ask the hook for now, if any. Anyone who's left that character meanwhile drops out of the queue
    public PolFriendGame? Next(DateTime now)
    {
        if (Asking is { } stuck && now - _askedAt > GiveUp)
        {
            _late.Add((Token, stuck));
            if (_late.Count > 8) _late.RemoveAt(0);
            Asking = null;
        }
        if (Asking is not null || now - _last < Gap) return null;
        names.Refresh();
        _queue.RemoveAll(q => names.Get(q.CharacterKey) is not null || !_seen.ContainsValue(q.CharacterKey));
        if (_queue.Count == 0) return null;
        Asking = _queue[0];
        _queue.RemoveAt(0);
        Token = HookFiles.NewToken();
        _askedAt = _last = now;
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
        if (CharacterNames.FromAnswer(state, r) is { } known && r?.CharacterKey == asked.CharacterKey)
            names.Remember(asked.CharacterKey, known);
        if (current) Asking = null;
        else _late.RemoveAt(late);
        return true;
    }

    // the request file couldn't be written
    public void Failed() => Asking = null;

    // the give-up clock only starts once the hook's taken it, it waits while the hook's busy with another read
    public void Waiting(string hookToken, bool hookBusy, DateTime now)
    {
        if (Asking is not null && hookToken != Token && hookBusy) _askedAt = now;
    }

    // a new session. Who's on what carries over, so a failed one still waits for a real change
    public void Reset()
    {
        _queue.Clear();
        _late.Clear();
        Asking = null;
        Token = "";
    }
}
