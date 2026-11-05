namespace Provenance.Common.Game;

// everything one launch started, by parent pid, with start times checked since a reused pid is always younger than its
// child. Members are held open, so a child still counts once its parent's gone
public sealed class LaunchTree : IDisposable
{
    // Hold's null for one that's there but can't be opened
    public sealed record Opened(IDisposable? Hold, DateTime? StartUtc);

    private sealed record Member(DateTime Start, string ExeName, IDisposable? Hold);

    private readonly int _root;
    private readonly Func<int, Opened?> _open;
    private readonly Dictionary<int, Member> _members = new();
    private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<int> _seen = new();

    // the caller holds the root itself (the launcher's Process), so it counts even after it's exited
    public LaunchTree(int rootPid, DateTime rootStartUtc, Func<int, Opened?>? open = null)
    {
        _root = rootPid;
        _open = open ?? ProcessTree.Open;
        _members[rootPid] = new Member(rootStartUtc, "", null);
    }

    public void Update()
    {
        var takenAt = DateTime.UtcNow;
        Update(ProcessTree.Snapshot(), takenAt);
    }

    // takenAt is read before the snapshot: one that started after it has the pid of something listed that's gone since
    public void Update(IReadOnlyList<ProcessTree.Entry> snapshot, DateTime takenAtUtc)
    {
        _seen = snapshot.Select(e => e.Pid).ToHashSet();
        for (bool added = true; added;)
        {
            added = false;
            foreach (var e in snapshot)
            {
                if (_members.ContainsKey(e.Pid) || e.ParentPid == e.Pid || !_members.TryGetValue(e.ParentPid, out var parent)) continue;
                if (_open(e.Pid) is not { } opened) continue;   // gone already
                if (opened is not { Hold: { } hold, StartUtc: { } start })
                {
                    _unreadable.Add(e.ExeName);
                    opened.Hold?.Dispose();
                    continue;
                }
                if (start < parent.Start || start > takenAtUtc)
                {
                    hold.Dispose();
                    continue;
                }
                _members[e.Pid] = new Member(start, e.ExeName, hold);
                added = true;
            }
        }
        // gone, with nothing naming it as a parent: it can't start anything now, so its handle goes rather than pile up
        // over a long wait (its pid stops counting with it)
        var parents = snapshot.Select(e => e.ParentPid).ToHashSet();
        foreach (var pid in _members.Keys.Where(pid => pid != _root && !_seen.Contains(pid) && !parents.Contains(pid)).ToList())
        {
            _members[pid].Hold?.Dispose();
            _members.Remove(pid);
        }
    }

    // members that were running at the last look (named exeName, or any), the root left out
    public List<(int Pid, DateTime Start)> Alive(string? exeName = null) =>
        _members.Where(m => m.Key != _root && _seen.Contains(m.Key)
                            && (exeName is null || m.Value.ExeName.Equals(exeName, StringComparison.OrdinalIgnoreCase)))
                .Select(m => (m.Key, m.Value.Start)).ToList();

    public bool AnyAlive => _members.Keys.Any(pid => pid != _root && _seen.Contains(pid));

    // one under the launch that couldn't be opened or read: it can't be told apart, so it never becomes a member
    public bool Unreadable(string exeName) => _unreadable.Contains(exeName);

    public void Dispose()
    {
        foreach (var m in _members.Values) m.Hold?.Dispose();
        _members.Clear();
    }
}
