/* mail: list and read on two lanes, delete on a third, polled once a turn
     list  svc 0x524(out, max), poll 0x528 (1 = done), zeroed 8-aligned array, stride 0x50, valid = MAIL_VALID in flags
     read  svc 0x518, poll 0x51c on a copy of the entry (first 0x48 bytes = server key), body at +0x4c
     push  svc 0xd9c registers evt(0, H) for a stored message, raw(H) for a real-time one (body at H+0x4c) */
#define MAIL_MAX 200   /* the viewer lists 200 in one go */
#define MAIL_SEEN 256
typedef struct {
    __declspec(align(8)) BYTE hdr[sizeof(pol_mail)];   /* 8-aligned: 0x518 rejects anything else */
    int state;                             /* 0 listed, 1 read, 2 failed, 3 real-time, 4 saved, 5 deleted, 6 skip */
    BYTE raw[0x180];                       /* a real-time body, parsed on the next turn */
    int raw_len, raw_pending;
    int mid;
    char subj[400];
    char text[13000];
    BYTE att[0x20];
    int has_att;
} mailrec;
static mailrec g_mail[MAIL_MAX];
/* identities of messages whose slot got reused, so a re-listed one isn't fetched again */
static BYTE g_mail_seen[MAIL_SEEN][0x1a];
static __declspec(align(8)) BYTE g_inbox[sizeof(pol_mail) * MAIL_MAX];
static int g_mail_fresh;
static char g_mail_state[16] = "pending";
static volatile int g_mail_raw_new;
static int g_mail_fails;
static BYTE g_push_q[8][MAIL_KEY_SIZE];

/* body "subject 0x07 text 0x00 [attachment]". Group invitations and their answers add a type-10 {u64 group id,
   char[0x14] name} (svc 0x440 appends it raw). */
static void mail_inbox_parse(BYTE *b, mailrec *m, const char *body, int len)
{
    int i, bel = -1, end;
    for (i = 0; i < len; i++) if (body[i] == 0x07) { bel = i; break; }
    if (bel < 0) m->subj[0] = 0;
    else pol_utf8(b, body, bel > 0x80 ? 0x80 : bel, m->subj, sizeof m->subj);
    for (end = bel + 1; end < len && body[end]; end++) ;
    pol_utf8(b, body + bel + 1, end - bel - 1, m->text, sizeof m->text);
    if (bel >= 0 && pc_mail_att_type(m->hdr) == 10 && len - (end + 1) >= 0x20) {
        memcpy(m->att, body + end + 1, 0x20);
        m->has_att = 1;
    }
}
/* the viewer's message identity (app.dll 0x1005245d): ids, seq, time and slots. Other bytes can differ in a pushed
   copy. */
static int mail_inbox_same(const BYTE *a, const BYTE *c)
{
    return memcmp(a, c, offsetof(pol_mail, name)) == 0 && MAIL(a)->seq == MAIL(c)->seq && MAIL(a)->time == MAIL(c)->time &&
           MAIL(a)->from_slot == MAIL(c)->from_slot && MAIL(a)->to_slot == MAIL(c)->to_slot;
}
static void mail_inbox_ident(const BYTE *h, BYTE out[0x1a])
{
    memcpy(out, h, offsetof(pol_mail, name));
    memcpy(out + 0x10, &MAIL(h)->seq, 8);
    out[0x18] = MAIL(h)->from_slot;
    out[0x19] = MAIL(h)->to_slot;
}
/* the slot with the lowest mid above after and a state in mask (a bit per state), or -1. Mids only go up, so it's
   arrival order */
static int mail_inbox_next(int after, unsigned mask)
{
    int best = -1;
    for (int i = 0; i < g_s.mail_n; i++)
        if (g_mail[i].mid > after && ((mask >> g_mail[i].state) & 1) && (best < 0 || g_mail[i].mid < g_mail[best].mid))
            best = i;
    return best;
}
static mailrec *mail_inbox_by_mid(int mid)
{
    for (int i = 0; i < g_s.mail_n; i++) if (g_mail[i].mid == mid) return &g_mail[i];
    return NULL;
}
static ULONGLONG mail_inbox_order(const BYTE *h)
{
    return (ULONGLONG)MAIL(h)->time << 32 | MAIL(h)->seq;
}
/* a chat line waits for an earlier one from the same handle that's still to be read, two sends or two reads at once
   can finish in either order */
static int mail_inbox_held(const mailrec *m)
{
    const BYTE *e = m->hdr, *o;
    if (m->state != 1 || pc_mail_kind(e) != 0) return 0;
    for (int i = 0; i < g_s.mail_n; i++) {
        o = g_mail[i].hdr;
        if (g_mail[i].state == 0 && pc_mail_kind(o) == 0 && MAIL(o)->from_lo == MAIL(e)->from_lo &&
            MAIL(o)->from_hi == MAIL(e)->from_hi && MAIL(o)->from_slot == MAIL(e)->from_slot &&
            mail_inbox_order(o) < mail_inbox_order(e))
            return 1;
    }
    return 0;
}
static int mail_inbox_saved_spare(void)
{
    for (int i = mail_inbox_next(0, 1 << 4); i >= 0; i = mail_inbox_next(g_mail[i].mid, 1 << 4))
        if (g_s.del_job < 0 || i != g_s.del_idx) return i;
    return -1;
}
static mailrec *mail_inbox_add(const BYTE *hdr)
{
    BYTE id[0x1a];
    int i;
    for (i = 0; i < g_s.mail_n; i++)
        if (mail_inbox_same(g_mail[i].hdr, hdr)) return NULL;
    mail_inbox_ident(hdr, id);
    for (i = 0; i < g_s.mail_seen_n; i++)
        if (memcmp(g_mail_seen[i], id, sizeof id) == 0) return NULL;
    if (g_s.mail_n < MAIL_MAX) {
        i = g_s.mail_n++;
    } else if ((i = mail_inbox_next(0, 1 << 2 | 1 << 5 | 1 << 6)) >= 0 || (i = mail_inbox_saved_spare()) >= 0) {
        /* the oldest failed, deleted or skipped one makes room, then a saved one whose delete hasn't started (the next
           login fetches it again). Its identity goes in the ring */
        mail_inbox_ident(g_mail[i].hdr, g_mail_seen[g_s.mail_seen_next]);
        g_s.mail_seen_next = (g_s.mail_seen_next + 1) % MAIL_SEEN;
        if (g_s.mail_seen_n < MAIL_SEEN) g_s.mail_seen_n++;
    } else {
        base_log("messages: cache full (%d) - message not kept this session (still on the server)\r\n", MAIL_MAX);
        return NULL;
    }
    memset(&g_mail[i], 0, sizeof g_mail[0]);
    memcpy(g_mail[i].hdr, hdr, MAIL_KEY_SIZE);
    g_mail[i].mid = ++g_s.mail_mid;
    return &g_mail[i];
}
static void mail_inbox_log_hdr(const char *what, const BYTE *h)
{
    base_log("messages: %s slot=%u kind=%u len=%u seq=%u time=%u 3e=%04x 42=%04x\r\n", what,
       MAIL(h)->from_slot, (unsigned)pc_mail_kind(h), MAIL(h)->len,
       MAIL(h)->seq, MAIL(h)->time, MAIL(h)->flags, MAIL(h)->seen);
}
/* push callbacks run inside polcore's IRC processing on the hold loop's thread, H on its stack, so copy and get out */
static void __cdecl mail_inbox_evt(int code, void *arg)
{
    base_log("messages: evt code=%d arg=%p\r\n", code, arg);
    /* 2 friend presence, 3 kind-9 notice, 4-6 groups: polcore already updated its arrays */
    if (code >= 2 && code <= 6) g_snap_dirty = 1;
    if (code == 0 && arg && !IsBadReadPtr(arg, MAIL_KEY_SIZE)) {
        LONG n = g_s.push_n;
        if (n < 8) { memcpy(g_push_q[n], arg, MAIL_KEY_SIZE); g_s.push_n = n + 1; }
        else base_log("messages: notice dropped, 8 are already waiting for the next turn\r\n");
    }
}
static void __cdecl mail_inbox_raw(void *arg)
{
    BYTE *h = (BYTE *)arg;
    if (!h || IsBadReadPtr(h, sizeof(pol_mail))) return;
    mail_inbox_log_hdr("real-time", h);
    if (pc_mail_receipt(h)) return;
    mailrec *m = mail_inbox_add(h);
    if (m) {
        const char *body = MAIL(h)->body;
        DWORD len = MAIL(h)->len;
        if (body && len <= 0x180 && !IsBadReadPtr(body, len)) {
            memcpy(m->raw, body, len);
            m->raw_len = (int)len;
            m->raw_pending = 1;
        }
        m->state = 3;
        g_snap_dirty = g_mail_fresh = g_mail_raw_new = 1;
    }
}

static int mail_inbox_mine(BYTE *b, const BYTE *h)
{
    return pc_mail_kind(h) == 0 && peers_is_me(b, MAIL(h)->from_lo, MAIL(h)->from_hi);
}

/* every line this session sent to another POL ID: header time and seq and the slot it went from */
#define SENT_IDS 256
static struct { DWORD time, seq; BYTE from_slot; } g_sent_ids[SENT_IDS];
static int mail_inbox_sent_by_me(const BYTE *h)
{
    for (int i = 0; i < g_s.sent_ids_n; i++)
        if (g_sent_ids[i].time == MAIL(h)->time && g_sent_ids[i].seq == MAIL(h)->seq &&
            g_sent_ids[i].from_slot == MAIL(h)->from_slot)
            return 1;
    return 0;
}
/* a zeroed or odd recipient field never counts as someone else's POL ID */
static int mail_inbox_polid_ok(BYTE *b, DWORD lo, DWORD hi)
{
    char id[64];
    if (!lo && !hi) return 0;
    peers_fmt_polid(b, lo, hi, id);
    return lstrlenA(id) == 8;
}
/* an echo of mine: a line this session sent to another POL ID (same time, seq and slot), or a handle writing to itself.
   Anything else from my ID is real */
static int mail_inbox_echo(BYTE *b, const BYTE *h)
{
    DWORD lo = MAIL(h)->to_lo, hi = MAIL(h)->to_hi;
    if (!mail_inbox_mine(b, h)) return 0;
    return mail_inbox_sent_by_me(h) || (mail_inbox_polid_ok(b, lo, hi) && peers_is_me(b, lo, hi) &&
           MAIL(h)->from_slot == MAIL(h)->to_slot);
}
/* the ones that came back, so the app can mark the line it sent */
#define RETURNED_MAX 16
static struct { DWORD time, seq; BYTE from_slot; } g_returned[RETURNED_MAX];
static void mail_inbox_returned(const BYTE *h)
{
    DWORD t = MAIL(h)->time, q = MAIL(h)->seq;
    for (int i = 0; i < g_s.returned_n; i++)
        if (g_returned[i].time == t && g_returned[i].seq == q && g_returned[i].from_slot == MAIL(h)->from_slot) return;
    g_returned[g_s.returned_next].time = t;
    g_returned[g_s.returned_next].seq = q;
    g_returned[g_s.returned_next].from_slot = MAIL(h)->from_slot;
    g_s.returned_next = (g_s.returned_next + 1) % RETURNED_MAX;
    if (g_s.returned_n < RETURNED_MAX) g_s.returned_n++;
    g_snap_dirty = 1;
}
/* a line of mine can't be told from an echo while a send is still going, so it waits unread,
   out of the snapshot's bodies, till they're all answered */
static int mail_send_going(void);
static int mail_inbox_undecided(BYTE *b, const mailrec *m)
{
    return mail_send_going() && (m->state == 0 || m->state == 3) && mail_inbox_mine(b, m->hdr);
}
static void mail_inbox_recheck(BYTE *b)
{
    if (!g_mail_raw_new) return;
    g_mail_raw_new = 0;
    for (int i = 0; i < g_s.mail_n; i++)
        if (g_mail[i].state == 3 && mail_inbox_echo(b, g_mail[i].hdr)) {
            base_log("messages: a chat line I sent came back real-time - not shown\r\n");
            mail_inbox_returned(g_mail[i].hdr);
            g_mail[i].state = 6;
        }
}
static int mail_inbox_sender_ignored(BYTE *b, const BYTE *h)
{
    for (int bi = 0; bi < 100; bi++) {
        const pol_friend *a = pc_ignore_row(b, bi);
        if (IsBadReadPtr(a, sizeof *a)) break;
        if (!pc_friend_valid(a)) continue;
        if (a->id_lo != MAIL(h)->from_lo || a->id_hi != MAIL(h)->from_hi) continue;
        if ((a->flags & 3) || pc_friend_slot(a) == MAIL(h)->from_slot) return 1;
    }
    return 0;
}
static void mail_inbox_list_done(int r)
{
    int found = 0, added = 0;
    if (r != 1) {
        if (r != MAIL_SUSPENDED && g_mail_fails++ == 0) {
            base_log("messages: list failed %d - retry once in 30s\r\n", r);
            g_s.mail_retry_at = GetTickCount64() + 30000;
            g_s.mail_refresh = 1;
        } else {
            base_log("messages: list failed %d again - waiting for new mail\r\n", r);
            if (lstrcmpA(g_mail_state, "ok") != 0) lstrcpyA(g_mail_state, "failed");
            g_snap_dirty = 1;
        }
        return;
    }
    g_mail_fails = 0;
    lstrcpyA(g_mail_state, "ok");
    g_snap_dirty = 1;
    for (int i = 0; i < MAIL_MAX; i++) {
        BYTE *e = g_inbox + i * sizeof(pol_mail);
        if (!(MAIL(e)->flags & MAIL_VALID)) continue;
        found++;
        mail_inbox_log_hdr("listed", e);
        if (mail_inbox_add(e)) added++;
    }
    base_log("messages: list done, %d on server, %d new\r\n", found, added);
}
static void mail_inbox_read_done(BYTE *b, int lane, int r)
{
    mailrec *m = &g_mail[g_s.mail_lanes[lane].idx];
    if (r == 1) {
        mail_inbox_parse(b, m, g_s.mail_lanes[lane].body, (int)MAIL(m->hdr)->len);
        m->state = 1;
        g_mail_fresh = 1;
        base_log("messages: body read (%u bytes)\r\n", MAIL(m->hdr)->len);
    } else {
        m->state = 2;
        base_log("messages: body read failed %d\r\n", r);
    }
    MAIL(m->hdr)->body = NULL;
    HeapFree(GetProcessHeap(), 0, g_s.mail_lanes[lane].body);
    g_s.mail_lanes[lane].body = NULL;
    g_snap_dirty = 1;
}
static int mail_send_msg_jobs(void);
static int mail_send_msg_slot_ok(BYTE *b);

static int hold_writes_waiting(void);
static int handles_switching(void);
static int mail_inbox_lane_busy(int kind, int idx)
{
    for (int l = 0; l < FETCH_LANES; l++)
        if (g_s.mail_lanes[l].job >= 0 && g_s.mail_lanes[l].kind == kind && (kind == 1 || g_s.mail_lanes[l].idx == idx)) return 1;
    return 0;
}
/* list or read, -0x100 (no free polcore slot, refused locally) tries again next turn. Only the list waits out the
   30s after a failed one, reads still go */
static void mail_inbox_fetch_start(BYTE *b, int lane)
{
    int i, t;
    char *body;
    if (g_s.mail_refresh && !mail_inbox_lane_busy(1, 0) && GetTickCount64() >= g_s.mail_retry_at) {
        memset(g_inbox, 0, sizeof g_inbox);
        t = pc_mail_list(b, g_inbox, MAIL_MAX);
        if (t == NO_SLOT) return;
        base_log("messages: list start -> %d\r\n", t);
        g_s.mail_refresh = 0;
        if (t < 0) { mail_inbox_list_done(t); return; }
        g_s.mail_lanes[lane].job = t; g_s.mail_lanes[lane].kind = 1;
        return;
    }
    for (i = mail_inbox_next(0, 1); i >= 0; i = mail_inbox_next(g_mail[i].mid, 1)) {
        mailrec *m = &g_mail[i];
        DWORD len = MAIL(m->hdr)->len;
        unsigned kd = pc_mail_kind(m->hdr);
        if (mail_inbox_lane_busy(2, i)) continue;
        if (len == 0 || len > 0x101100) { m->state = 2; continue; }
        if (kd == 0 && mail_inbox_sender_ignored(b, m->hdr)) {
            base_log("messages: chat message from an ignored sender - deleted unread\r\n");
            m->state = 4;
            continue;
        }
        /* the viewer reads one like any other and deletes it once it's saved. A line I sent is never shown as
           received, so it's deleted unread like an ignored sender's */
        if (mail_inbox_echo(b, m->hdr)) {
            base_log("messages: a chat line I sent came back - deleted unread\r\n");
            mail_inbox_returned(m->hdr);
            m->state = 4;
            continue;
        }
        if (mail_inbox_undecided(b, m)) continue;
        /* chat 0, friend request 1, answers 9/10, group invitation 0xe and its notices, the rest stay unread */
        if (!(kd == 0 || kd == 1 || kd == 9 || kd == 10 || kd == 0xe || kd == 0xf || kd == 0x10 ||
              kd == 0x12 || kd == 0x13)) {
            base_log("messages: kind %u is not shown by Provenance - not fetched\r\n", kd);
            m->state = 6;
            continue;
        }
        body = (char *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, len + 1);
        if (!body) return;
        MAIL(m->hdr)->body = body;
        t = pc_mail_read(b, m->hdr);
        if (t != NO_SLOT) base_log("messages: read start -> %d\r\n", t);
        if (t < 0) {
            MAIL(m->hdr)->body = NULL;
            HeapFree(GetProcessHeap(), 0, body);
            if (t != NO_SLOT) m->state = 2;
            return;
        }
        g_s.mail_lanes[lane].job = t; g_s.mail_lanes[lane].kind = 2; g_s.mail_lanes[lane].idx = i; g_s.mail_lanes[lane].body = body;
        return;
    }
}
/* saved by the launcher: delete the server copy (svc 0x508, keyed like the GET). One to a slot this account lost
   is refused (-0x1540) and stays, as in the viewer. */
static void mail_inbox_delete_start(BYTE *b)
{
    int i, t;
    for (i = mail_inbox_next(0, 1 << 4); i >= 0; i = mail_inbox_next(g_mail[i].mid, 1 << 4)) {
        BYTE *h = g_mail[i].hdr;
        t = pc_mail_delete(b, h);
        if (t == NO_SLOT) return;
        base_log("messages: delete start -> %d (from slot %u to slot %u)\r\n", t, MAIL(h)->from_slot, MAIL(h)->to_slot);
        if (t < 0) { g_mail[i].state = 5; continue; }
        g_s.del_job = t; g_s.del_idx = i;
        return;
    }
}
static void mail_inbox_pump(BYTE *b)
{
    int i, r, done = 0;
    for (i = 0; i < g_s.push_n; i++) {   /* arrival order */
        mail_inbox_log_hdr("notice", g_push_q[i]);
        mail_inbox_add(g_push_q[i]);
    }
    g_s.push_n = 0;
    /* a real-time body is only copied in the callback, so it's parsed here */
    for (i = 0; i < g_s.mail_n; i++)
        if (g_mail[i].raw_pending) {
            mail_inbox_parse(b, &g_mail[i], (const char *)g_mail[i].raw, g_mail[i].raw_len);
            g_mail[i].raw_pending = 0;
            g_mail[i].raw_len = 0;
            SecureZeroMemory(g_mail[i].raw, sizeof g_mail[i].raw);
            g_snap_dirty = 1;
        }
    mail_inbox_recheck(b);
    /* never cancelled here, polcore fails a stuck job at 100s itself and svc 0x520 stalls every job poll 10s */
    for (i = 0; i < FETCH_LANES; i++)
        if (g_s.mail_lanes[i].job >= 0 &&
            (r = g_s.mail_lanes[i].kind == 1 ? pc_mail_list_check(b, g_s.mail_lanes[i].job) : pc_mail_read_check(b, g_s.mail_lanes[i].job)) != 0) {
            g_s.mail_lanes[i].job = -1;
            if (g_s.mail_lanes[i].kind == 1) mail_inbox_list_done(r); else mail_inbox_read_done(b, i, r);
            done = 1;
        }
    if (g_s.del_job >= 0 && (r = pc_mail_delete_check(b, g_s.del_job)) != 0) {
        g_s.del_job = -1;
        g_mail[g_s.del_idx].state = 5;
        if (r == 1) base_log("messages: deleted from the server after saving\r\n");
        else base_log("messages: delete failed %d - the message stays on the server\r\n", r);
        done = 1;
    }
    /* a freed lane waits a turn, so a waiting send gets it first */
    if (done) return;
    if (!g_s.draining)
        for (i = 0; i < FETCH_LANES && mail_send_msg_slot_ok(b); i++) {
            if (g_s.mail_lanes[i].job >= 0) continue;
            mail_inbox_fetch_start(b, i);
            if (g_s.mail_lanes[i].job < 0) break;   /* nothing to read, or polcore said no */
        }
    /* a delete keeps to the viewer's 2 jobs. In a drain it only goes with no write waiting for a slot, and nothing
       waits for it */
    if (g_s.del_job < 0 && mail_send_msg_jobs() < 2 && (!g_s.draining ||
        (!mail_send_msg_jobs() && !hold_writes_waiting())) && mail_send_msg_slot_ok(b))
        mail_inbox_delete_start(b);
}
#define ACK_MAX (1024 * 1024)   /* about 140,000 mids, far more than a session ever has */
static void mail_inbox_ack_request(void)
{
    char *buf;
    int got, v = -1;
    if (!requests_waiting(REQ_ACK)) return;
    if ((got = requests_take_ex(REQ_ACK, NULL, 0, &buf, ACK_MAX)) < 0) return;
    for (int i = 0; i <= got; i++) {
        char c = buf[i];
        if (c >= '0' && c <= '9') v = v > 9999999 ? v : (v < 0 ? 0 : v * 10) + (c - '0');
        else {
            mailrec *m = v > 0 ? mail_inbox_by_mid(v) : NULL;
            if (m && (m->state == 1 || m->state == 3)) {
                m->state = m->state == 1 ? 4 : 5;
                m->text[0] = 0; m->subj[0] = 0;
                m->raw_pending = 0; m->raw_len = 0;
                SecureZeroMemory(m->raw, sizeof m->raw);
                g_snap_dirty = 1;
            }
            v = -1;
        }
    }
    HeapFree(GetProcessHeap(), 0, buf);
}
