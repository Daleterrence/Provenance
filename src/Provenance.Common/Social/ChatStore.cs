using System.Text;
using Provenance.Common.Hook;
using Provenance.Common.IO;

namespace Provenance.Common.Social;

public sealed class ChatStore
{
    private readonly List<ChatEntry> _entries = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    // the game's message store is read again at every login, so without these a deleted line would come back
    private readonly Dictionary<string, long> _deleted = new(StringComparer.Ordinal);
    private bool _deletedDirty;
    public const int KeepDeleted = 5000;
    internal int DeletedCount => _deleted.Count;
    private int _version;
    private Stats? _stats;

    // format constant, never change it or every old log stops opening
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("Provenance PlayOnline chat log v1");
    private static readonly byte[] DeletedEntropy = Encoding.ASCII.GetBytes("Provenance PlayOnline deleted lines v1");

    private readonly EncryptedJsonFile<List<ChatEntry>>? _file;
    private readonly EncryptedJsonFile<Dictionary<string, long>>? _deletedFile;

    public ChatStore(string profileId, string? root = null)
    {
        ProfileId = profileId;
        if (root is null)
        {
            if (AppPaths.UserData is not { } userData) return;
            root = Path.Combine(userData, "messages");
        }
        var path = Path.Combine(root, profileId + ".dat");
        _file = new(path, Entropy, "Messages");
        _deletedFile = new(path[..^4] + ".deleted.dat", DeletedEntropy, "Messages", setAsideUnreadable: false);
        if (_deletedFile.Load() is { } keys)
            foreach (var (k, t) in keys) _deleted[k] = t;
        if (_file.Load() is not { } loaded) return;
        var kept = loaded.Where(e => !_deleted.ContainsKey(e.Key)).ToList();
        _entries.AddRange(kept);
        foreach (var e in kept) _keys.Add(e.Key);
    }

    public IReadOnlyList<ChatEntry> Entries => _entries;

    private bool _dirty, _closed;
    public bool Dirty { get => _dirty; private set => _dirty = value && !_closed; }
    public string ProfileId { get; } = "";

    public bool MergeIncoming(PolMessage m) => m.HasBody && m.IsChat && AddOnce(m, m.Subject, m.Text, "received");

    public bool MergeNotice(PolMessage m, string text) => m.HasBody && AddOnce(m, "", text, "notice");

    private bool AddOnce(PolMessage m, string subject, string text, string state)
    {
        if (_deleted.ContainsKey(m.Key)) return false;
        bool read = m.IsLocal && m.Read;
        if (_keys.Contains(m.Key))
            return read && Stat().UnreadKeys.Contains(m.Key) && Replace(m.Key, e => e with { Read = true });
        Add(new ChatEntry(m.Key, m.From, false, subject, text,
                          m.Time > 0 ? m.Time : DateTimeOffset.UtcNow.ToUnixTimeSeconds(), read, state)
            { PeerKey = m.FromKey, Seq = m.Seq });
        return true;
    }

    public bool AddGameSent(string peer, PolSentLine s)
    {
        if (_deleted.ContainsKey(s.Key)) return false;
        if (_keys.Contains(s.Key))
            return s.State == "sent" && Stat().UnsentKeys.Contains(s.Key) &&
                   Replace(s.Key, e => e with { State = "sent", Time = s.Time > 0 ? s.Time : e.Time, Seq = s.Seq });
        Add(new ChatEntry(s.Key, peer, true, s.Subject, s.Text,
                          s.Time > 0 ? s.Time : DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true,
                          s.State == "sent" ? "sent" : "unsent")
            { PeerKey = s.ToKey, Seq = s.Seq, FromSlot = s.FromSlot, ToOther = s.FromSlot >= 0 ? s.ToOther : null });
        return true;
    }

    public string NameFor(string key) => key.Length > 0 && Stat().NameByKey.TryGetValue(key, out var n) ? n : "";

    public void AddNote(string peer, string peerKey, string text)
    {
        Add(new ChatEntry("note:" + ShortId.New(12), peer, true, "", text,
                          DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true, "notice") { PeerKey = peerKey });
        Save();
    }

    public ChatEntry AddOutgoing(string peer, string peerKey, string text, string token)
    {
        var e = new ChatEntry("out:" + token, peer, true, "", text, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true, "sending")
                { PeerKey = peerKey };
        Add(e);
        Save();
        return e;
    }

    public bool SetSendStates(IEnumerable<(string Token, string State)> states)
    {
        bool changed = false;
        foreach (var (token, state) in states)
        {
            int i = _entries.FindIndex(e => e.Key == "out:" + token);
            if (i < 0 || _entries[i].State == state) continue;
            Edit(i, e => e with { State = state });
            changed = true;
        }
        if (changed) Save();
        return changed;
    }

    public bool SetSendState(string token, string state, long time = 0, long seq = 0)
    {
        var key = "out:" + token;
        int i = _keys.Contains(key) ? _entries.FindLastIndex(e => e.Key == key) : -1;
        if (i < 0) return false;
        var cur = _entries[i];
        bool stamp = state == "sent" && time > 0 && (cur.Time != time || cur.Seq != seq);
        if (cur.State == state && !stamp) return false;
        Edit(i, e => stamp ? e with { State = state, Time = time, Seq = seq } : e with { State = state });
        Changed();
        return true;
    }

    public bool SentAt(long time, long seq, int fromSlot) =>
        time > 0 && fromSlot >= 0 &&
        (_deleted.ContainsKey(SentKey(time, seq, fromSlot)) ||
         _entries.Any(e => e.Outgoing && e.Time == time && e.Seq == seq && e.FromSlot == fromSlot && e.ToOther == true &&
                           e.State is "sent" or "failed:returned"));

    private static string SentKey(long time, long seq, int fromSlot) => $"sent:{time}|{seq}|{fromSlot}";

    public static bool CanDelete(ChatEntry e) => !Waiting(e);

    public bool Delete(string key) => DeleteWhere(e => e.Key == key) > 0;

    public int Clear(string peer, string key = "") => DeleteWhere(e => e.IsWith(peer, key));

    public int DeleteKeys(IReadOnlyCollection<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.Ordinal);
        return DeleteWhere(e => set.Contains(e.Key));
    }

    public void PruneDeleted(long olderThan)
    {
        var old = _deleted.Where(kv => kv.Value < olderThan).Select(kv => kv.Key).ToList();
        if (old.Count == 0) return;
        foreach (var k in old) _deleted.Remove(k);
        _deletedDirty = true;
        SaveDeleted();
    }

    private int DeleteWhere(Func<ChatEntry, bool> match)
    {
        var gone = _entries.Where(e => match(e) && CanDelete(e)).ToList();
        if (gone.Count == 0) return 0;
        foreach (var e in gone)
        {
            _deleted[e.Key] = e.Time;
            if (e.Outgoing && e.ToOther == true && e.Time > 0) _deleted[SentKey(e.Time, e.Seq, e.FromSlot)] = e.Time;
            if (e.Outgoing && e.Time > 0 && e.FromSlot >= 0 && e.Key.StartsWith("out:", StringComparison.Ordinal))
                _deleted[StoredSentKey(e.Time, e.Seq, e.FromSlot)] = e.Time;
        }
        if (_deleted.Count > KeepDeleted)
            foreach (var k in _deleted.OrderBy(kv => kv.Value).Take(_deleted.Count - KeepDeleted).Select(kv => kv.Key).ToList())
                _deleted.Remove(k);
        _deletedDirty = true;
        var keys = gone.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        _entries.RemoveAll(e => keys.Contains(e.Key) && CanDelete(e));
        foreach (var k in keys) if (!_entries.Any(e => e.Key == k)) _keys.Remove(k);
        _version++;
        Dirty = true;
        SaveDeleted();
        Save();
        return gone.Count;
    }

    private void SaveDeleted()
    {
        if (!_closed && _deletedFile?.Save(_deleted) == true) _deletedDirty = false;
    }

    public void Discard()
    {
        _closed = true;
        _dirty = _deletedDirty = false;
    }

    public bool SetSentFrom(string token, int fromSlot, bool toOther)
    {
        var key = "out:" + token;
        int i = _keys.Contains(key) ? _entries.FindLastIndex(e => e.Key == key) : -1;
        if (i < 0 || (_entries[i].FromSlot == fromSlot && _entries[i].ToOther == toOther)) return false;
        Edit(i, e => e with { FromSlot = fromSlot, ToOther = toOther });
        return true;
    }

    public bool MarkReturned(long time, long seq, int fromSlot)
    {
        if (fromSlot < 0) return false;
        int i = _entries.FindLastIndex(e => e.Outgoing && e.State == "sent" && e.Time == time && e.Seq == seq &&
                                            e.FromSlot == fromSlot && e.ToOther == true);
        if (i < 0) return false;
        Edit(i, e => e with { State = "failed:returned" });
        return true;
    }

    public bool RemoveFailed(string key)
    {
        int i = _entries.FindIndex(e => e.Key == key && e.Failed);
        if (i < 0) return false;
        _entries.RemoveAt(i);
        if (!_entries.Any(e => e.Key == key)) _keys.Remove(key);
        _version++;
        Changed();
        return true;
    }

    // the hook takes 40 lines a minute, so each one queued behind gets 1.5s on top of the 3 minutes
    public static TimeSpan SendExpiry(int queued) =>
        HookLimits.MailInFlight + TimeSpan.FromMilliseconds(1500 * Math.Max(0, queued));

    public bool ExpireSending(TimeSpan maxAge, IReadOnlySet<string>? keep = null)
    {
        if (Stat().Waiting == 0) return false;
        long cutoff = DateTimeOffset.UtcNow.Add(-maxAge).ToUnixTimeSeconds();
        bool changed = EditWhere(e => e.Outgoing && e.State is "sending" or "queued" && e.Time < cutoff &&
                                      keep?.Contains(e.Key) != true,
                                 e => e with { State = "failed:timeout" });
        if (changed) Changed();
        return changed;
    }

    public bool Adopt(string name, string key)
    {
        if (key.Length == 0 || !Stat().KeylessNames.Contains(name)) return false;
        // handle names get reused, and if this one's already someone else's here the old lines can't be told apart
        if (_entries.Any(e => e.PeerKey.Length > 0 && e.PeerKey != key &&
                              string.Equals(e.Peer, name, StringComparison.OrdinalIgnoreCase)))
            return false;
        return EditWhere(e => e.PeerKey.Length == 0 && string.Equals(e.Peer, name, StringComparison.OrdinalIgnoreCase),
                         e => e with { PeerKey = key });
    }

    public IEnumerable<ChatEntry> With(string peer, string key = "")
    {
        var lines = _entries.Where(e => e.IsWith(peer, key) && (e.Outgoing || e.State != "notice")).ToList();
        int first = lines.FindIndex(Waiting);
        var keyed = new List<(ChatEntry E, bool Tail, (long Time, long Seq) At)>(lines.Count);
        (long Time, long Seq) last = (long.MinValue, long.MinValue);
        for (int i = 0; i < lines.Count; i++)
        {
            var e = lines[i];
            bool mine = e.Key.StartsWith("out:", StringComparison.Ordinal);
            var at = (e.Time, e.Seq);
            if (mine && e.State != "sent" && at.CompareTo(last) < 0) at = last;
            if (mine) last = at;
            keyed.Add((e, first >= 0 && i >= first && mine, at));
        }
        return keyed.OrderBy(x => x.Tail).ThenBy(x => x.Tail ? default : x.At).Select(x => x.E);
    }

    private static bool Waiting(ChatEntry e) => e.Outgoing && e.State is "sending" or "queued";

    public int UnreadFrom(string peer, string key = "")
    {
        var s = Stat();
        return key.Length > 0 ? s.UnreadByKey.GetValueOrDefault(key) + s.UnreadKeyless.GetValueOrDefault(peer)
                              : s.UnreadByName.GetValueOrDefault(peer);
    }

    public (long Time, long Seq) NewestUnread(string peer, string key = "")
    {
        var s = Stat();
        if (key.Length == 0) return s.NewestByName.GetValueOrDefault(peer);
        var a = s.NewestByKey.GetValueOrDefault(key);
        var b = s.NewestKeyless.GetValueOrDefault(peer);
        return a.CompareTo(b) >= 0 ? a : b;
    }

    public int Unread => Stat().Unread;

    public IReadOnlyList<(string Key, ChatEntry Last, IReadOnlySet<string> Names)> Writers => Stat().Writers;

    public IReadOnlyList<string> MarkRead(string peer, string key = "")
    {
        var turned = new List<string>();
        bool Match(ChatEntry e)
        {
            if (e.Outgoing || e.Read || !e.IsWith(peer, key)) return false;
            turned.Add(e.Key);
            return true;
        }
        if (EditWhere(Match, e => e with { Read = true })) Changed();
        return turned;
    }

    public bool IsReadChat(string key) => Stat().ReadKeys.Contains(key);

    public bool HasSentLine(long time, long seq, int fromSlot) =>
        time > 0 && fromSlot >= 0 &&
        (_deleted.ContainsKey(StoredSentKey(time, seq, fromSlot)) ||
         _entries.Any(e => e.Outgoing && e.Time == time && e.Seq == seq && e.FromSlot == fromSlot &&
                           e.Key.StartsWith("out:", StringComparison.Ordinal)));

    private static string StoredSentKey(long time, long seq, int fromSlot) => $"stored-sent:{time}|{seq}|{fromSlot}";

    private void Add(ChatEntry e)
    {
        _entries.Add(e);
        _keys.Add(e.Key);
        _version++;
        Dirty = true;
    }

    private void Edit(int i, Func<ChatEntry, ChatEntry> change)
    {
        _entries[i] = change(_entries[i]);
        _version++;
        Dirty = true;
    }

    private bool EditWhere(Func<ChatEntry, bool> match, Func<ChatEntry, ChatEntry> change)
    {
        bool changed = false;
        for (int i = 0; i < _entries.Count; i++)
            if (match(_entries[i]))
            {
                _entries[i] = change(_entries[i]);
                changed = true;
            }
        if (changed)
        {
            _version++;
            Dirty = true;
        }
        return changed;
    }

    private bool Replace(string key, Func<ChatEntry, ChatEntry> change)
    {
        int i = _entries.FindLastIndex(e => e.Key == key);
        if (i < 0) return false;
        Edit(i, change);
        return true;
    }

    private sealed class Stats
    {
        public int Version, Unread, Waiting;
        public readonly Dictionary<string, int> UnreadByKey = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> UnreadKeyless = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> UnreadByName = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (long, long)> NewestByKey = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (long, long)> NewestKeyless = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (long, long)> NewestByName = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> UnreadKeys = new(StringComparer.Ordinal);
        public readonly HashSet<string> ReadKeys = new(StringComparer.Ordinal);
        public readonly HashSet<string> UnsentKeys = new(StringComparer.Ordinal);
        public readonly HashSet<string> KeylessNames = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> NameByKey = new(StringComparer.Ordinal);
        public List<(string Key, ChatEntry Last, IReadOnlySet<string> Names)> Writers = [];
    }

    private Stats Stat()
    {
        if (_stats is { } cached && cached.Version == _version) return cached;
        var s = new Stats { Version = _version };
        var writers = new Dictionary<string, (ChatEntry Last, HashSet<string> Names)>(StringComparer.Ordinal);
        static void Count(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
        static void Newest(Dictionary<string, (long, long)> d, string k, ChatEntry e)
        {
            var at = (e.Time, e.Seq);
            if (!d.TryGetValue(k, out var old) || at.CompareTo(old) > 0) d[k] = at;
        }
        foreach (var e in _entries)
        {
            if (e.PeerKey.Length > 0 && e.Peer.Length > 0) s.NameByKey[e.PeerKey] = e.Peer;
            if (e.PeerKey.Length == 0) s.KeylessNames.Add(e.Peer);
            if (Waiting(e)) s.Waiting++;
            if (e.Outgoing)
            {
                if (e.State == "unsent") s.UnsentKeys.Add(e.Key);
                continue;
            }
            if (e.State == "notice") continue;
            if (e.Read) s.ReadKeys.Add(e.Key);
            if (!e.Read)
            {
                s.Unread++;
                s.UnreadKeys.Add(e.Key);
                Count(s.UnreadByName, e.Peer);
                Count(e.PeerKey.Length > 0 ? s.UnreadByKey : s.UnreadKeyless, e.PeerKey.Length > 0 ? e.PeerKey : e.Peer);
                Newest(s.NewestByName, e.Peer, e);
                Newest(e.PeerKey.Length > 0 ? s.NewestByKey : s.NewestKeyless, e.PeerKey.Length > 0 ? e.PeerKey : e.Peer, e);
            }
            var id = e.PeerKey.Length > 0 ? "k:" + e.PeerKey : "n:" + e.Peer.ToUpperInvariant();
            if (!writers.TryGetValue(id, out var w))
                w = (e, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            else if (e.Time >= w.Last.Time)
                w.Last = e;
            w.Names.Add(e.Peer);
            writers[id] = w;
        }
        s.Writers = writers.Select(kv => (kv.Key, kv.Value.Last, (IReadOnlySet<string>)kv.Value.Names)).ToList();
        return _stats = s;
    }

    private void Changed() => Dirty = true;

    public static readonly TimeSpan SaveRetry = TimeSpan.FromSeconds(30);
    private DateTime _failedAt;
    internal int SaveCount { get; private set; }

    public bool SaveIfDue(DateTime now)
    {
        if (!Dirty) return true;
        if (now - _failedAt < SaveRetry) return false;
        if (Save()) return true;
        _failedAt = now;
        return false;
    }

    public bool Flush() => !Dirty || Save();

    public bool Save()
    {
        if (_file is null || _closed) return false;
        if (_deletedDirty) SaveDeleted();
        SaveCount++;
        if (!_file.Save(_entries)) return false;
        Dirty = false;
        return true;
    }
}
