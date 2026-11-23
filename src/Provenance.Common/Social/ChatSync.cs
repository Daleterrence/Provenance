using Serilog;

namespace Provenance.Common.Social;

public enum ChatNewsKind { Message, Notice, FriendRequest, GroupInvite }

public sealed record ChatNews(ChatNewsKind Kind, string From, string Text = "", int Mid = 0);

public sealed record ChatSyncResult(bool Changed, bool SendsChanged, IReadOnlyList<int> ToAck, IReadOnlyList<ChatNews> News);

public sealed class ChatSync(MailSendQueue sends)
{
    private readonly HashSet<int> _acked = new();
    private readonly HashSet<int> _seenRequests = new();
    private bool _storeTaken;
    // acks and the store copy wait until the log version they're in is on disk
    private readonly List<(int Mid, int Version)> _held = new();
    private (int Pid, int Version)? _storeDue;
    private int? _pid;
    private readonly Dictionary<string, int> _mids = new(StringComparer.Ordinal);
    private readonly HashSet<int> _toRead = new();

    public void NoteRead(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            if (_mids.TryGetValue(key, out var mid)) _toRead.Add(mid);
    }

    public IReadOnlyList<int> TakeRead()
    {
        var mids = _toRead.OrderBy(m => m).ToList();
        _toRead.Clear();
        return mids;
    }

    public void RequeueRead(IEnumerable<int> mids) => _toRead.UnionWith(mids);

    public bool IsAcked(int mid) => _acked.Contains(mid);

    public void MarkAcked(IEnumerable<int> mids) => _acked.UnionWith(mids);

    public void Unack(IEnumerable<int> mids)
    {
        foreach (var mid in mids) _acked.Remove(mid);
    }

    public ChatSyncResult Merge(ChatStore chat, PolSocialSnapshot snap, int? pid, bool blocked,
                                IReadOnlyCollection<(string Name, string Key)> open, Func<PolMessage, string?> noticeText,
                                Func<string, string> filtered)
    {
        bool changed = false;
        var saved = new List<int>();
        var news = new List<ChatNews>();
        if (_pid is { } was && pid is { } now && now != was)
        {
            // mids are counted per pol.exe, so the last one's mean nothing to this one
            _held.Clear();
            _acked.Clear();
            _mids.Clear();
            _toRead.Clear();
            _seenRequests.Clear();
        }
        if (chat.HasKeyless)
            foreach (var f in snap.Friends)
                if (f.Key.Length > 0 &&
                    snap.Friends.Count(x => string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase)) == 1)
                    changed |= chat.Adopt(f.Name, f.Key);
        bool takeStore = snap.Stored != null && !_storeTaken;
        if (takeStore) changed |= MergeStored(chat, snap, noticeText, _toRead, _mids);
        changed |= TakeSendResults(chat, snap);
        foreach (var m in snap.Messages)
        {
            if (m.IsChat && m.HasBody && m.Mid > 0) _mids[m.Key] = m.Mid;
            var who =snap.Friends.FirstOrDefault(f => f.Key.Length > 0 && f.Key == m.FromKey)?.Name ?? m.From;
            if (m.HasBody && noticeText(m) is { } line)
            {
                if (!Mine(snap, m) && chat.MergeNotice(m, line))
                {
                    changed = true;
                    news.Add(new(ChatNewsKind.Notice, who,
                        SaysWho(m.Kind) ? $"{who} {char.ToLowerInvariant(line[0])}{line[1..]}" : line));
                }
                if (m.Mid > 0 && _acked.Add(m.Mid)) saved.Add(m.Mid);
                continue;
            }
            if (m.HasBody && m.Kind == PolMessage.KindGroupInvite && m.Mid > 0 && _seenRequests.Add(m.Mid))
            {
                Log.Information("A group invitation showed up (from the {Source})", m.IsStored ? "store" : "server");
                news.Add(new(ChatNewsKind.GroupInvite, m.From, Mid: m.Mid));
            }
            if (m.HasBody && m.Kind == PolMessage.KindFriendRequest && m.Mid > 0 && _seenRequests.Add(m.Mid))
            {
                Log.Information("A friend request showed up (from the {Source})", m.IsStored ? "store" : "server");
                news.Add(new(ChatNewsKind.FriendRequest, m.From, Mid: m.Mid));
            }
            // chat from someone I ignore gets binned unread. The hook only catches listed mail, not a push
            if (m.IsChat && m.FromKey.Length > 0 && snap.Blocked.Any(x => x.Key == m.FromKey))
            {
                if (m.Mid > 0 && _acked.Add(m.Mid)) saved.Add(m.Mid);
                continue;
            }
            if (Echo(snap, m, chat))
            {
                changed |= chat.MarkReturned(m.Time, m.Seq, m.FromSlot);
                if (m.HasBody && m.Mid > 0 && _acked.Add(m.Mid)) saved.Add(m.Mid);
                continue;
            }
            if (m.IsChat && Mine(snap, m) && sends.Going) continue;
            if (chat.MergeIncoming(m))
            {
                changed = true;
                news.Add(new(ChatNewsKind.Message, who, filtered(m.Text.Length > 0 ? m.Text : m.Subject)));
            }
            if (m.HasBody && m.IsChat && m.Mid > 0 && _acked.Add(m.Mid)) saved.Add(m.Mid);
        }
        var (pumped, sendsChanged) = PumpSends(chat, snap, pid, blocked);
        changed |= pumped;
        if (changed)
            foreach (var (name, key) in open)
                if (chat.UnreadFrom(name, key) > 0) NoteRead(chat.MarkRead(name, key));
        chat.SaveIfDue(DateTime.UtcNow);
        _pid = pid;
        foreach (var mid in saved) _held.Add((mid, chat.Version));
        if (takeStore && pid is { } storePid)
        {
            _storeTaken = true;
            _storeDue = (storePid, chat.Version);
        }
        return new(changed, sendsChanged, TakeKept(chat, pid), news);
    }

    // the sampler's snapshot carries the send results but no messages, so only the sends are taken from it
    public (bool Changed, bool SendsChanged) MergeSends(ChatStore chat, PolSocialSnapshot snap, int? pid, bool blocked)
    {
        bool changed = TakeSendResults(chat, snap);
        var (pumped, sendsChanged) = PumpSends(chat, snap, pid, blocked);
        chat.SaveIfDue(DateTime.UtcNow);
        return (changed | pumped, sendsChanged);
    }

    private bool TakeSendResults(ChatStore chat, PolSocialSnapshot snap)
    {
        bool changed = false;
        foreach (var (token, state, time, seq, _, _) in snap.AllSends)
            if (token.Length > 0 && (state == "sent" || state.StartsWith("failed", StringComparison.Ordinal)))
            {
                changed |= chat.SetSendState(token, state, time, seq);
                sends.Finished(token);
            }
        foreach (var (token, _, _, _, fromSlot, toOther) in snap.SendResults.Where(x => x.FromSlot >= 0))
            changed |= chat.SetSentFrom(token, fromSlot, toOther);
        return changed;
    }

    private (bool Changed, bool SendsChanged) PumpSends(ChatStore chat, PolSocialSnapshot snap, int? pid, bool blocked)
    {
        bool changed = false;
        foreach (var (time, seq, fromSlot) in snap.Returned) changed |= chat.MarkReturned(time, seq, fromSlot);
        foreach (var (token, state) in snap.SendQueue) sends.HookSays(token, state);
        bool sendsChanged = sends.Pump(chat, pid, blocked);
        changed |= chat.ExpireSending(ChatStore.SendExpiry(sends.Queued), sends.StillPending());
        return (changed, sendsChanged);
    }

    public IReadOnlyList<int> TakeKept(ChatStore chat, int? pid)
    {
        if (pid != _pid) return [];
        int saved = chat.SavedVersion;
        if (_storeDue is { } store && store.Version <= saved)
        {
            SocialSnapshotFiles.DeleteStore(store.Pid);
            _storeDue = null;
        }
        var kept = _held.Where(h => h.Version <= saved).Select(h => h.Mid).ToList();
        _held.RemoveAll(h => h.Version <= saved);
        return kept;
    }

    internal static bool Mine(PolSocialSnapshot snap, PolMessage m) =>
        m.IsMe || (snap.MyKey.Length > 0 && m.FromKey == snap.MyKey);

    internal static bool Echo(PolSocialSnapshot snap, PolMessage m, ChatStore chat) =>
        m.IsChat && Mine(snap, m) &&
        ((m.ToMe && m.ToKey.Length > 0 && m.ToKey == m.FromKey) || chat.SentAt(m.Time, m.Seq, m.FromSlot));

    private static bool SaysWho(int kind) => kind is not (PolMessage.KindGroupRemoved or PolMessage.KindGroupDisbanded);

    public static bool MergeGameSent(ChatStore chat, PolSocialSnapshot snap, IEnumerable<PolSentLine> lines)
    {
        bool changed = false;
        var hasSentLine = chat.SentLines();
        foreach (var s in lines)
        {
            if (hasSentLine(s.Time, s.Seq, s.FromSlot)) continue;
            var to = snap.Friends.FirstOrDefault(f => f.Key.Length > 0 && f.Key == s.ToKey)?.Name ??
                     (s.To.Length > 0 ? s.To : chat.NameFor(s.ToKey));
            if (to.Length > 0 || s.ToKey.Length > 0) changed |= chat.AddGameSent(to, s);
        }
        return changed;
    }

    private static bool MergeStored(ChatStore chat, PolSocialSnapshot snap, Func<PolMessage, string?> noticeText,
                                    HashSet<int> toRead, Dictionary<string, int> mids)
    {
        bool changed = MergeGameSent(chat, snap, snap.Stored!.Sent);
        foreach (var m in snap.Stored.Received)
        {
            if (m.IsChat && m.FromKey.Length > 0 && snap.Blocked.Any(x => x.Key == m.FromKey)) continue;
            if (Echo(snap, m, chat))
            {
                changed |= chat.MarkReturned(m.Time, m.Seq, m.FromSlot);
                continue;
            }
            if (noticeText(m) is { } line)
            {
                if (!Mine(snap, m)) changed |= chat.MergeNotice(m, line);
                continue;
            }
            changed |= chat.MergeIncoming(m);
            if (!m.IsChat || m.Mid <= 0) continue;
            mids[m.Key] = m.Mid;
            if (!m.Read && chat.IsReadChat(m.Key)) toRead.Add(m.Mid);
        }
        var times = snap.Stored.Sent.Select(s => s.Time).Concat(snap.Stored.Received.Select(m => m.Time)).Where(t => t > 0).ToList();
        if (times.Count > 0) chat.PruneDeleted(times.Min());
        return changed;
    }
}
