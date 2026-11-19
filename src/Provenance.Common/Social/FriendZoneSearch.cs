using System.Reflection;
using Serilog;

namespace Provenance.Common.Social;

public sealed class FriendZoneSearch
{
    private readonly Func<int, SearchEndpoint?> _endpoints;
    private readonly TimeProvider _time;
    private readonly SearchRefresh _rule;
    private readonly Lock _gate = new();
    private readonly record struct Seen(int Area, PartyState Party);

    private readonly Dictionary<(int World, uint Id), Seen> _areas = new();
    private readonly Dictionary<(int World, uint Id), string> _anon = new();
    private readonly Dictionary<(int World, uint Id), string> _owners = new();
    private bool _troubled;
    private readonly HashSet<int> _failing = new();
    private int _generation;

    public FriendZoneSearch(Func<int, SearchEndpoint?> endpoints, TimeProvider? time = null)
    {
        _endpoints = endpoints;
        _time = time ?? TimeProvider.System;
        _rule = new SearchRefresh(world => endpoints(world) is not null);
    }

    public static FriendZoneSearch Retail()
    {
        var host = Assembly.GetEntryAssembly()?.GetName().Name ?? "";
        if (host.EndsWith("-tests", StringComparison.Ordinal) || host == "provenance-screens")
            throw new InvalidOperationException("The real search servers aren't for test programs.");
        return new FriendZoneSearch(SearchServers.Retail);
    }

    public event Action? Changed;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public void Tick(IReadOnlyCollection<PolFriend>? friends)
    {
        IReadOnlyList<SearchBatch> due;
        int generation;
        lock (_gate)
        {
            if (friends is not null)
            {
                _rule.Seen(friends);
                var playing = _rule.Playing();
                foreach (var key in _areas.Keys.Where(k => !playing.Contains(k)).ToList()) _areas.Remove(key);
                Remember(friends);
            }
            due = _rule.Due(Now);
            generation = _generation;
        }
        foreach (var batch in due) _ = Task.Run(() => RunAsync(batch, generation));
    }

    private void Remember(IReadOnlyCollection<PolFriend> friends)
    {
        var onList = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in friends)
        {
            var who = f.Key.Length > 0 ? f.Key : f.Name;
            onList.Add(who);
            if (f.Online && !f.Pending && f.Game is { } game) _owners[Key(game)] = who;
        }
        foreach (var key in _anon.Where(a => !onList.Contains(a.Value)).Select(a => a.Key).ToList()) _anon.Remove(key);
        foreach (var key in _owners.Where(o => !onList.Contains(o.Value)).Select(o => o.Key).ToList()) _owners.Remove(key);
    }

    public int? Area(PolFriendGame game)
    {
        lock (_gate)
            return _areas.TryGetValue(Key(game), out var seen) && seen.Area != 0 ? seen.Area : null;
    }

    public PartyState Party(PolFriendGame game)
    {
        lock (_gate)
            return _areas.TryGetValue(Key(game), out var seen) ? seen.Party : PartyState.None;
    }

    public bool IsAnon(PolFriendGame game)
    {
        lock (_gate)
            return _anon.ContainsKey(Key(game));
    }

    internal bool Busy
    {
        get
        {
            lock (_gate)
                return _rule.Busy;
        }
    }

    public bool Troubled
    {
        get
        {
            lock (_gate)
                return _rule.Troubled;
        }
    }

    private static (int, uint) Key(PolFriendGame game) => (game.World, SearchServers.Id(game.Character));

    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _rule.Reset();
            _areas.Clear();
        }
    }

    private async Task RunAsync(SearchBatch batch, int generation)
    {
        var world = FfxiWorlds.Name(batch.World) ?? "?";
        SearchEndpoint? at = null;
        bool answered = false, moved = false;
        string why = "something went wrong";
        var got = new HashSet<uint>();
        var wait = TimeSpan.Zero;
        try
        {
            at = _endpoints(batch.World);
            var ids = batch.Characters.Select(SearchServers.Id).Distinct().ToList();
            answered = true;
            for (int from = 0; from < ids.Count && answered; from += SearchPackets.MaxIds)
            {
                var chunk = ids.GetRange(from, Math.Min(SearchPackets.MaxIds, ids.Count - from));
                var answer = at is { } server
                    ? await SearchClient.AskAsync(server, chunk, SearchClient.Limit, CancellationToken.None)
                    : SearchAnswer.Failed("no server");
                if (answer.Ok)
                {
                    lock (_gate)
                    {
                        if (generation == _generation) moved |= Keep(batch.World, chunk, answer.Entries);
                    }
                    got.UnionWith(chunk);
                    Log.Debug("Friend zones: {World} ({Address}) listed {Found} of {Asked}", world, at?.Host, answer.Entries.Count, chunk.Count);
                    foreach (var e in answer.Entries)
                        Log.Debug("Friend zones: {World} entry flags1 {Flags1:X4} flags2 {Flags2:X8} language {Language:X4}", world, e.Flags1, e.Flags2, e.Language);
                }
                else (answered, why) = (false, answer.Reason);
            }
        }
        catch (Exception ex)
        {
            (answered, why) = (false, "something went wrong");
            Log.Warning(ex, "Friend zones: asking {World} threw", world);
        }
        finally
        {
            var done = Finish(batch.World, answered, generation, got);
            moved |= done.Moved;
            wait = done.Wait;
        }
        Report(batch.World, world, at, answered, why, wait);
        if (!moved) return;
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Warning(ex, "Friend zones: a change handler threw"); }
    }

    private (bool Moved, TimeSpan Wait) Finish(int world, bool answered, int generation, HashSet<uint> got)
    {
        lock (_gate)
        {
            bool moved = generation == _generation && !answered && Drop(world, got);
            _rule.Finished(world, answered, Now);
            moved |= _rule.Troubled != _troubled;
            _troubled = _rule.Troubled;
            return (moved, _rule.BackoffOf(world));
        }
    }

    private void Report(int id, string world, SearchEndpoint? at, bool answered, string why, TimeSpan wait)
    {
        bool recovered, first;
        lock (_gate)
        {
            recovered = answered && _failing.Remove(id);
            first = !answered && _failing.Add(id);
        }
        if (first) Log.Information("Friend zones: {World} ({Address}) didn't answer ({Why}), trying again in {Seconds}s", world, at?.Host, why, (int)wait.TotalSeconds);
        else if (!answered) Log.Debug("Friend zones: {World} still isn't answering ({Why}), next try in {Seconds}s", world, why, (int)wait.TotalSeconds);
        else if (recovered) Log.Information("Friend zones: {World} is answering again", world);
    }

    private bool Keep(int world, List<uint> asked, IReadOnlyList<SearchEntry> found)
    {
        var playing = _rule.Playing();
        var listed = new Dictionary<uint, SearchEntry>();
        foreach (var e in found) listed.TryAdd(e.Id, e);
        bool moved = false;
        foreach (var id in asked)
        {
            bool had = _areas.TryGetValue((world, id), out var was);
            if (listed.TryGetValue(id, out var entry))
            {
                bool anon = (entry.Flags1 & SearchFlags.Anon) != 0;
                moved |= anon ? _anon.TryAdd((world, id), _owners.GetValueOrDefault((world, id), "")) : _anon.Remove((world, id));
                if (!anon && playing.Contains((world, id)))
                {
                    var now = new Seen(entry.Area, PartyOf(entry.Flags1));
                    moved |= !had || was != now;
                    _areas[(world, id)] = now;
                    continue;
                }
            }
            if (had)
            {
                _areas.Remove((world, id));
                moved = true;
            }
        }
        return moved;
    }

    private static PartyState PartyOf(uint flags1) =>
        (flags1 & SearchFlags.PartyLeader) != 0 ? PartyState.Leader : (flags1 & SearchFlags.InParty) != 0 ? PartyState.Member : PartyState.None;

    private bool Drop(int world, HashSet<uint> keep)
    {
        var gone = _areas.Keys.Where(k => k.World == world && !keep.Contains(k.Id)).ToList();
        foreach (var k in gone) _areas.Remove(k);
        return gone.Count > 0;
    }
}
