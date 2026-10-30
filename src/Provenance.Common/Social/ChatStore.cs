using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Provenance.Common.Credentials;

namespace Provenance.Common.Social;

public sealed record ChatEntry(string Key, string Peer, bool Outgoing, string Subject, string Text,
                               long Time, bool Read, string State)
{
    // POL ID + handle, hashed by the hook. Entries without one match by name
    public string PeerKey { get; init; } = "";

    // the message header's seq, which orders lines that share a second. Older logs don't have it
    public long Seq { get; init; }

    public DateTimeOffset When => DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime();

    public string WhenText
    {
        get
        {
            var w = When;
            var now = DateTimeOffset.Now;
            var t = w.ToString("t", CultureInfo.CurrentCulture);
            if (w.Date == now.Date) return t;
            if (w.Date == now.Date.AddDays(-1)) return "Yesterday " + t;
            return w.Year == now.Year ? $"{w:d MMM} {t}" : $"{w:d MMM yyyy} {t}";
        }
    }

    public string TimeText => When.ToString("t", CultureInfo.CurrentCulture);

    // never saved, it depends on the lines round it and today's date
    [JsonIgnore]
    public string? DayBreak { get; init; }

    // never the subject (FFXI's is always the same one), unless there's nothing else in the message. Worked out when
    // it's read, so old logs show the same
    public string Display => Text.Length > 0 ? Text : Subject;

    public bool Failed => Outgoing && State.StartsWith("failed", StringComparison.Ordinal);

    public string StateText => State switch
    {
        "sending" or "queued" => "Sending...",
        "sent" => "Delivered to PlayOnline",
        "failed:timeout" => "Not sent: PlayOnline didn't answer.",
        // handed over with no answer, so it might still arrive and isn't offered again
        "unconfirmed" => "Not confirmed: PlayOnline didn't answer. It might still arrive.",
        // still in the game's outbox, so no send again or remove for it here
        "unsent" => "Not sent: FINAL FANTASY XI didn't finish sending it.",
        "failed:no such friend" or "failed:friend list changed" => "Not sent: the friend list changed.",
        "failed:pending" => "Not sent: this person hasn't answered your friend request yet.",
        "failed:self" => "Not sent: you can't send a message to your own handle.",
        "failed:too long" => "Not sent: messages can't be longer than 300 characters, and some characters count as two.",
        "failed:write" => "Not sent: Provenance couldn't send it to PlayOnline.",
        "failed:stopped" => "Not sent: PlayOnline didn't take your recent messages, so sending is off until you log in again.",
        "failed:hourly limit" => "Not sent: you've sent 300 messages in the last hour. Try again later.",
        var s when s.StartsWith("failed", StringComparison.Ordinal) => "Not sent: PlayOnline didn't accept it.",
        _ => "",
    };

    public bool IsWith(string name, string key) =>
        key.Length > 0 && PeerKey.Length > 0 ? PeerKey == key
                                             : string.Equals(Peer, name, StringComparison.OrdinalIgnoreCase);
}

// like the viewer's own pub\homeNN\msg. The server keeps no read flag or sent copy, so those live here
public sealed class ChatStore
{
    private readonly string _path;
    private readonly List<ChatEntry> _entries = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    // the 2 sec poll asks for counts every time, so they're worked out once per change, not per ask
    private int _version;
    private Stats? _stats;

    // format constant, never change it or every old log stops opening
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("Provenance PlayOnline chat log v1");

    // DPAPI-encrypted, it's friends' messages in there
    public ChatStore(string profileId, string? root = null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Provenance", "messages");
        _path = Path.Combine(root, profileId + ".dat");
        if (!File.Exists(_path)) return;
        try
        {
            var plain = Dpapi.Unprotect(File.ReadAllBytes(_path), Entropy);
            if (plain != null && JsonSerializer.Deserialize<List<ChatEntry>>(plain) is { } loaded)
            {
                _entries.AddRange(loaded);
                foreach (var e in loaded) _keys.Add(e.Key);
                return;
            }
        }
        catch { }
        // unreadable here (another Windows account, a damaged file), so set it aside rather than let the next save
        // overwrite it with an empty log
        try { File.Move(_path, _path + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".unreadable"); } catch { }
    }

    public IReadOnlyList<ChatEntry> Entries => _entries;

    public bool Dirty { get; private set; }

    public bool MergeIncoming(PolMessage m) => m.HasBody && m.IsChat && AddOnce(m, m.Subject, m.Text, "received");

    public bool MergeNotice(PolMessage m, string text) => m.HasBody && AddOnce(m, "", text, "notice");

    private bool AddOnce(PolMessage m, string subject, string text, string state)
    {
        bool read = m.IsLocal && m.Read;
        // read in game: the line's read here too, even one that came in from the server before Play
        if (_keys.Contains(m.Key))
            return read && Stat().UnreadKeys.Contains(m.Key) && Replace(m.Key, e => e with { Read = true });
        Add(new ChatEntry(m.Key, m.From, false, subject, text,
                          m.Time > 0 ? m.Time : DateTimeOffset.UtcNow.ToUnixTimeSeconds(), read, state)
            { PeerKey = m.FromKey, Seq = m.Seq });
        Dirty = true;
        return true;
    }

    // keyed on the header, and a line from the game's outbox becomes sent if a later look finds it went
    public bool AddGameSent(string peer, PolSentLine s)
    {
        if (_keys.Contains(s.Key))
            return s.State == "sent" && Stat().UnsentKeys.Contains(s.Key) &&
                   Replace(s.Key, e => e with { State = "sent", Time = s.Time > 0 ? s.Time : e.Time, Seq = s.Seq });
        Add(new ChatEntry(s.Key, peer, true, s.Subject, s.Text,
                          s.Time > 0 ? s.Time : DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true,
                          s.State == "sent" ? "sent" : "unsent")
            { PeerKey = s.ToKey, Seq = s.Seq });
        Dirty = true;
        return true;
    }

    public string NameFor(string key) => key.Length > 0 && Stat().NameByKey.TryGetValue(key, out var n) ? n : "";

    public void AddNote(string peer, string peerKey, string text)
    {
        Add(new ChatEntry("note:" + Guid.NewGuid().ToString("N")[..12], peer, true, "", text,
                          DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true, "notice") { PeerKey = peerKey });
        Changed();
    }

    public ChatEntry AddOutgoing(string peer, string peerKey, string text, string token)
    {
        var e = new ChatEntry("out:" + token, peer, true, "", text, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true, "sending")
                { PeerKey = peerKey };
        Add(e);
        Changed();
        return e;
    }

    // once sent, my line takes the header's time and seq like the friend's copy. That's polcore's clock, set by the server
    public bool SetSendState(string token, string state, long time = 0, long seq = 0)
    {
        var key = "out:" + token;
        int i = _keys.Contains(key) ? _entries.FindLastIndex(e => e.Key == key) : -1;
        if (i < 0) return false;
        var e = _entries[i];
        bool stamp = state == "sent" && time > 0 && (e.Time != time || e.Seq != seq);
        if (e.State == state && !stamp) return false;
        _entries[i] = stamp ? e with { State = state, Time = time, Seq = seq } : e with { State = state };
        _version++;
        Changed();
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

    // the hook takes 20 lines a minute, so each one queued behind gets 3 secs on top of the 3 mins
    public static TimeSpan SendExpiry(int queued) => TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(3 * Math.Max(0, queued));

    // still sending after maxAge means the hook never answered (the session ended, say)
    public bool ExpireSending(TimeSpan maxAge, IReadOnlySet<string>? keep = null)
    {
        if (Stat().Waiting == 0) return false;
        long cutoff = DateTimeOffset.UtcNow.Add(-maxAge).ToUnixTimeSeconds();
        bool changed = Update(e => e.Outgoing && e.State is "sending" or "queued" && e.Time < cutoff &&
                                   keep?.Contains(e.Key) != true,
                              e => e with { State = "failed:timeout" });
        if (changed) Changed();
        return changed;
    }

    // name-only entries take the key of the one friend with that name, then they survive a rename
    public bool Adopt(string name, string key)
    {
        if (key.Length == 0 || !Stat().KeylessNames.Contains(name)) return false;
        // handle names get reused, and if this one's already someone else's here the old lines can't be told apart
        if (_entries.Any(e => e.PeerKey.Length > 0 && e.PeerKey != key &&
                              string.Equals(e.Peer, name, StringComparison.OrdinalIgnoreCase)))
            return false;
        bool changed = Update(e => e.PeerKey.Length == 0 &&
                                   string.Equals(e.Peer, name, StringComparison.OrdinalIgnoreCase),
                              e => e with { PeerKey = key });
        Dirty |= changed;
        return changed;
    }

    // lines still waiting to go sit at the bottom in the order they were typed, the rest go by time then seq
    public IEnumerable<ChatEntry> With(string peer, string key = "") =>
        _entries.Where(e => e.IsWith(peer, key)).OrderBy(Waiting)
                .ThenBy(e => Waiting(e) ? 0 : e.Time).ThenBy(e => Waiting(e) ? 0 : e.Seq);

    private static bool Waiting(ChatEntry e) => e.Outgoing && e.State is "sending" or "queued";

    // the same answer as counting e.IsWith(peer, key) over the unread lines
    public int UnreadFrom(string peer, string key = "")
    {
        var s = Stat();
        return key.Length > 0 ? s.UnreadByKey.GetValueOrDefault(key) + s.UnreadKeyless.GetValueOrDefault(peer)
                              : s.UnreadByName.GetValueOrDefault(peer);
    }

    public int Unread => Stat().Unread;

    // a group per key (or name, without one): its newest line and every name it's had
    public IReadOnlyList<(string Key, ChatEntry Last, IReadOnlySet<string> Names)> Writers => Stat().Writers;

    public void MarkRead(string peer, string key = "")
    {
        if (Update(e => !e.Outgoing && !e.Read && e.IsWith(peer, key), e => e with { Read = true })) Changed();
    }

    private void Add(ChatEntry e)
    {
        _entries.Add(e);
        _keys.Add(e.Key);
        _version++;
    }

    private bool Replace(string key, Func<ChatEntry, ChatEntry> change)
    {
        int i = _entries.FindLastIndex(e => e.Key == key);
        if (i < 0) return false;
        _entries[i] = change(_entries[i]);
        _version++;
        Dirty = true;
        return true;
    }

    private bool Update(Func<ChatEntry, bool> match, Func<ChatEntry, ChatEntry> change)
    {
        bool changed = false;
        for (int i = 0; i < _entries.Count; i++)
            if (match(_entries[i]))
            {
                _entries[i] = change(_entries[i]);
                changed = true;
            }
        if (changed) _version++;
        return changed;
    }

    private sealed class Stats
    {
        public int Version, Unread, Waiting;
        public readonly Dictionary<string, int> UnreadByKey = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> UnreadKeyless = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> UnreadByName = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> UnreadKeys = new(StringComparer.Ordinal);
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
            if (!e.Read)
            {
                s.Unread++;
                s.UnreadKeys.Add(e.Key);
                Count(s.UnreadByName, e.Peer);
                Count(e.PeerKey.Length > 0 ? s.UnreadByKey : s.UnreadKeyless, e.PeerKey.Length > 0 ? e.PeerKey : e.Peer);
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

    private void Changed()
    {
        Dirty = true;
        Save();
    }

    public bool Save()
    {
        try
        {
            var cipher = Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(_entries), Entropy);
            if (cipher is null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, cipher);
            File.Move(tmp, _path, overwrite: true);
            Dirty = false;
            return true;
        }
        catch { return false; }   // history's a convenience, never worth breaking the session over
    }
}
