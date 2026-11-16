namespace Provenance.Common.Social;

public enum ChatNewsKind { Message, Notice, FriendRequest, GroupInvite }

public sealed record ChatNews(ChatNewsKind Kind, string From, string Text = "", int Mid = 0);

// ToAck are the mids to ack once the log saved (Kept), Unack puts them back if that fails
public sealed record ChatSyncResult(bool Changed, bool SendsChanged, bool Kept, IReadOnlyList<int> ToAck,
                                    IReadOnlyList<ChatNews> News);

public sealed class ChatSync(MailSendQueue sends)
{
    private readonly HashSet<int> _acked = new();
    private readonly HashSet<int> _seenRequests = new();
    private bool _storeTaken;

    public bool IsAcked(int mid) => _acked.Contains(mid);

    public void MarkAcked(IEnumerable<int> mids) => _acked.UnionWith(mids);

    public void Unack(IEnumerable<int> mids)
    {
        foreach (var mid in mids) _acked.Remove(mid);   // again next snapshot
    }

    // POL's copy of a message only goes once mine's saved, like the viewer.
    // open is the conversation on screen, blocked the game running or a handle switch
    public ChatSyncResult Merge(ChatStore chat, PolSocialSnapshot snap, int? pid, bool blocked,
                                (string Name, string Key)? open, Func<PolMessage, string?> noticeText,
                                Func<string, string> filtered)
    {
        bool changed = false;
        var saved = new List<int>();
        var news = new List<ChatNews>();
        foreach (var f in snap.Friends)
            if (f.Key.Length > 0 &&
                snap.Friends.Count(x => string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase)) == 1)
                changed |= chat.Adopt(f.Name, f.Key);   // lines saved while the hook had no key for them
        bool takeStore = snap.Stored != null && !_storeTaken;
        if (takeStore) changed |= MergeStored(chat, snap, noticeText);
        // the recent results too, since an answer can take the send slot before a line's result lands. First, so an
        // echo in the same snapshot finds its time and seq
        foreach (var (token, state, time, seq, _, _) in snap.AllSends)
            if (token.Length > 0 && (state == "sent" || state.StartsWith("failed", StringComparison.Ordinal)))
            {
                changed |= chat.SetSendState(token, state, time, seq);
                sends.Finished(token);
            }
        foreach (var (token, _, _, _, fromSlot, toOther) in snap.SendResults.Where(x => x.FromSlot >= 0))
            changed |= chat.SetSentFrom(token, fromSlot, toOther);
        foreach (var m in snap.Messages)
        {
            var who = snap.Friends.FirstOrDefault(f => f.Key.Length > 0 && f.Key == m.FromKey)?.Name ?? m.From;
            if (m.HasBody && noticeText(m) is { } line)
            {
                // one about my own action (say I disbanded the group) isn't news, but it still goes from the server
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
                news.Add(new(ChatNewsKind.GroupInvite, m.From, Mid: m.Mid));
            if (m.HasBody && m.Kind == PolMessage.KindFriendRequest && m.Mid > 0 && _seenRequests.Add(m.Mid))
                news.Add(new(ChatNewsKind.FriendRequest, m.From, Mid: m.Mid));
            // chat from someone I ignore gets binned unread. The hook only catches listed mail, not a push
            if (m.IsChat && m.FromKey.Length > 0 && snap.Blocked.Any(x => x.Key == m.FromKey))
            {
                if (m.Mid > 0 && _acked.Add(m.Mid)) saved.Add(m.Mid);
                continue;
            }
            // my line to another POL ID coming back wasn't received, so it goes from the server like any read line.
            // An unmatched one waits for a send that could be it
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
            // chat only, a request stays on the server for the viewer and is never deleted unseen
            if (m.HasBody && m.IsChat && m.Mid > 0 && _acked.Add(m.Mid)) saved.Add(m.Mid);
        }
        foreach (var (time, seq, fromSlot) in snap.Returned) changed |= chat.MarkReturned(time, seq, fromSlot);
        foreach (var (token, state) in snap.SendQueue) sends.HookSays(token, state);
        bool sendsChanged = sends.Pump(chat, pid, blocked);
        // a stored send finishes well inside 2 minutes, so nothing says "Sending..." for ever
        changed |= chat.ExpireSending(ChatStore.SendExpiry(sends.Queued), sends.StillPending());
        if (changed && open is { } o && chat.UnreadFrom(o.Name, o.Key) > 0)
            chat.MarkRead(o.Name, o.Key);
        // dirty rather than changed, a log whose last save failed isn't on disk either
        bool kept = chat.SaveIfDue(DateTime.UtcNow);
        if (takeStore && kept && pid is { } storePid)
        {
            SocialSnapshotFiles.DeleteStore(storePid);
            _storeTaken = true;
        }
        return new(changed, sendsChanged, kept, saved, news);
    }

    internal static bool Mine(PolSocialSnapshot snap, PolMessage m) =>
        m.IsMe || (snap.MyKey.Length > 0 && m.FromKey == snap.MyKey);

    // a certain echo only: my line to another POL ID in the log with the same time, seq and slot, or a handle to itself.
    // Anything else from my ID gets shown
    internal static bool Echo(PolSocialSnapshot snap, PolMessage m, ChatStore chat) =>
        m.IsChat && Mine(snap, m) &&
        ((m.ToMe && m.ToKey.Length > 0 && m.ToKey == m.FromKey) || chat.SentAt(m.Time, m.Seq, m.FromSlot));

    // SE's group lines already say what happened, so they don't get the sender's name in front
    private static bool SaysWho(int kind) => kind is not (PolMessage.KindGroupRemoved or PolMessage.KindGroupDisbanded);

    // the game's message store, read by the hook at login: it's history, so no notices, and nothing to ack since none of
    // it's on the server any more
    private static bool MergeStored(ChatStore chat, PolSocialSnapshot snap, Func<PolMessage, string?> noticeText)
    {
        bool changed = false;
        foreach (var s in snap.Stored!.Sent)
        {
            var to = snap.Friends.FirstOrDefault(f => f.Key.Length > 0 && f.Key == s.ToKey)?.Name ??
                     (s.To.Length > 0 ? s.To : chat.NameFor(s.ToKey));
            if (to.Length > 0 || s.ToKey.Length > 0) changed |= chat.AddGameSent(to, s);
        }
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
            }
            else changed |= chat.MergeIncoming(m);
        }
        var times = snap.Stored.Sent.Select(s => s.Time).Concat(snap.Stored.Received.Select(m => m.Time)).Where(t => t > 0).ToList();
        if (times.Count > 0) chat.PruneDeleted(times.Min());
        return changed;
    }
}
