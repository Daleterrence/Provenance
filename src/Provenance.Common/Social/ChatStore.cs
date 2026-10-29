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

    public bool HasSubject => !string.IsNullOrWhiteSpace(Subject);

    // a subject typed in the viewer becomes the first line, same as the viewer does it
    public string Display => !HasSubject ? Text
                           : string.IsNullOrEmpty(Text) ? Subject
                           : Subject + "\n" + Text;

    public bool Failed => Outgoing && State.StartsWith("failed", StringComparison.Ordinal);

    public string StateText => State switch
    {
        "sending" or "queued" => "Sending...",
        "sent" => "Delivered to PlayOnline",
        "failed:timeout" => "Not sent: PlayOnline didn't answer.",
        // handed over with no answer, so it might still arrive and isn't offered again
        "unconfirmed" => "Not confirmed: PlayOnline didn't answer. It might still arrive.",
        "failed:no such friend" or "failed:friend list changed" => "Not sent: the friend list changed.",
        "failed:pending" => "Not sent: this person hasn't answered your friend request yet.",
        "failed:self" => "Not sent: you can't send a message to your own handle.",
        "failed:too long" => "Not sent: messages can't be longer than 300 characters.",
        "failed:write" => "Not sent: Provenance couldn't send it to PlayOnline.",
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
        if (_entries.Any(e => e.Key == m.Key)) return false;
        _entries.Add(new ChatEntry(m.Key, m.From, false, subject, text,
                                   m.Time > 0 ? m.Time : DateTimeOffset.UtcNow.ToUnixTimeSeconds(), false, state)
                     { PeerKey = m.FromKey });
        Dirty = true;
        return true;
    }

    public void AddNote(string peer, string peerKey, string text)
    {
        _entries.Add(new ChatEntry("note:" + Guid.NewGuid().ToString("N")[..12], peer, true, "", text,
                                   DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true, "notice") { PeerKey = peerKey });
        Changed();
    }

    public ChatEntry AddOutgoing(string peer, string peerKey, string text, string token)
    {
        var e = new ChatEntry("out:" + token, peer, true, "", text, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true, "sending")
                { PeerKey = peerKey };
        _entries.Add(e);
        Changed();
        return e;
    }

    public bool SetSendState(string token, string state)
    {
        int i = _entries.FindIndex(e => e.Key == "out:" + token);
        if (i < 0 || _entries[i].State == state) return false;
        _entries[i] = _entries[i] with { State = state };
        Changed();
        return true;
    }

    public bool RemoveFailed(string key)
    {
        int i = _entries.FindIndex(e => e.Key == key && e.Failed);
        if (i < 0) return false;
        _entries.RemoveAt(i);
        Changed();
        return true;
    }

    // still sending after maxAge means the hook never answered (the session ended, say)
    public bool ExpireSending(TimeSpan maxAge, IReadOnlySet<string>? keep = null)
    {
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
        if (key.Length == 0) return false;
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

    public IEnumerable<ChatEntry> With(string peer, string key = "") =>
        _entries.Where(e => e.IsWith(peer, key)).OrderBy(e => e.Time);

    public int UnreadFrom(string peer, string key = "") =>
        _entries.Count(e => !e.Outgoing && !e.Read && e.IsWith(peer, key));

    public int Unread => _entries.Count(e => !e.Outgoing && !e.Read);

    public void MarkRead(string peer, string key = "")
    {
        if (Update(e => !e.Outgoing && !e.Read && e.IsWith(peer, key), e => e with { Read = true })) Changed();
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
        return changed;
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
