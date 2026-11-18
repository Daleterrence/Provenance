namespace Provenance.Common.Social;

internal sealed record SearchBatch(int World, IReadOnlyList<CharacterId> Characters);

// when each world's search server gets asked. Someone starting FFXI or changing character asks at once (10s apart at
// most), otherwise it's a refresh every 30s, and a world that doesn't answer is left alone for longer each time
internal sealed class SearchRefresh(Func<int, bool> hasServer)
{
    public static readonly TimeSpan NewPlayerGap = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);
    // the back-off a world has to reach before the friends list says something
    public static readonly TimeSpan TroubleBackoff = TimeSpan.FromMinutes(2);

    private sealed class World
    {
        public List<CharacterId> Characters = [];
        public DateTime? Last;
        public DateTime Until;
        public TimeSpan Backoff;
        public bool Dirty, InFlight;
    }

    private readonly Dictionary<int, World> _worlds = new();
    private Dictionary<string, string> _seen = new(StringComparer.Ordinal);

    // only for a list that's loaded, an empty one then means nobody's playing
    public void Seen(IReadOnlyCollection<PolFriend> friends)
    {
        var now = new Dictionary<string, string>(StringComparer.Ordinal);
        var onWorld = new Dictionary<int, Dictionary<string, CharacterId>>();
        foreach (var f in friends)
        {
            if (!f.Online || f.Pending || f.Game is not { } game || !hasServer(game.World)) continue;
            var who = f.Key.Length > 0 ? f.Key : f.Name;
            now[who] = game.Character.Key;
            if (!_seen.TryGetValue(who, out var was) || was != game.Character.Key) WorldFor(game.World).Dirty = true;
            if (!onWorld.TryGetValue(game.World, out var list)) onWorld[game.World] = list = new Dictionary<string, CharacterId>();
            list[game.Character.Key] = game.Character;
        }
        _seen = now;
        foreach (var (id, w) in _worlds) w.Characters = onWorld.TryGetValue(id, out var here) ? [.. here.Values] : [];
        foreach (var (id, here) in onWorld)
            if (!_worlds.ContainsKey(id)) WorldFor(id).Characters = [.. here.Values];
    }

    private World WorldFor(int id)
    {
        if (!_worlds.TryGetValue(id, out var w)) _worlds[id] = w = new World();
        return w;
    }

    // the worlds to ask now, each marked as in flight until Finished says otherwise
    public IReadOnlyList<SearchBatch> Due(DateTime now)
    {
        var due = new List<SearchBatch>();
        foreach (var (id, w) in _worlds.OrderBy(p => p.Key))
        {
            if (w.Characters.Count == 0) continue;
            if (w.InFlight || now < w.Until) continue;
            if (w.Last is { } last && (now - last < NewPlayerGap || (!w.Dirty && now - last < RefreshEvery))) continue;
            w.Last = now;
            w.Dirty = false;
            w.InFlight = true;
            due.Add(new SearchBatch(id, [.. w.Characters]));
        }
        return due;
    }

    public void Finished(int world, bool answered, DateTime now)
    {
        if (!_worlds.TryGetValue(world, out var w)) return;
        w.InFlight = false;
        if (answered)
        {
            w.Backoff = TimeSpan.Zero;
            w.Until = DateTime.MinValue;
            return;
        }
        w.Backoff = w.Backoff == TimeSpan.Zero ? FirstBackoff : TimeSpan.FromTicks(Math.Min(w.Backoff.Ticks * 2, MaxBackoff.Ticks));
        w.Until = now + w.Backoff;
        w.Dirty = true;
    }

    // some world that's being asked about is at the trouble step. One nobody is playing on isn't being asked
    public bool Troubled => _worlds.Values.Any(w => w.Backoff >= TroubleBackoff && w.Characters.Count > 0);

    public bool Busy => _worlds.Values.Any(w => w.InFlight);

    public TimeSpan BackoffOf(int world) => _worlds.TryGetValue(world, out var w) ? w.Backoff : TimeSpan.Zero;

    // who's playing now, as world and id
    public HashSet<(int World, uint Id)> Playing()
    {
        var all = new HashSet<(int, uint)>();
        foreach (var (id, w) in _worlds)
            foreach (var c in w.Characters) all.Add((id, SearchServers.Id(c)));
        return all;
    }

    // a new login forgets who's playing, but a request still out and a world's back-off stay as they were
    public void Reset()
    {
        foreach (var w in _worlds.Values)
        {
            w.Characters = [];
            w.Dirty = false;
        }
        _seen = new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
