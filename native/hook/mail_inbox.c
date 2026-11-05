/* messages: list and reads on two lanes, delete on a third, polled once a turn:
     list  svc 0x524(out, max), poll 0x528 (1 = done) into a zeroed 8-aligned array, stride 0x50, valid entries
           have W(+0x3e) & 0x8000
     read  svc 0x518, poll 0x51c on a copy of the entry (first 0x48 bytes = the server key), body at +0x4c
     push  svc 0xd9c registers evt(0, H) for a stored message, raw(H) for a real-time one (body at H+0x4c) */
#define MAIL_MAX 64
#define MAIL_SEEN 256
typedef struct {
    __declspec(align(8)) BYTE hdr[0x50];   /* 8-aligned: 0x518 rejects anything else */
    int state;                             /* 0 listed, 1 read, 2 failed, 3 real-time, 4 saved, 5 deleted, 6 skip */
    int mid;
    char subj[400];
    char text[13000];
    BYTE att[0x20];
    int has_att;
} mailrec;
static mailrec g_mail[MAIL_MAX];
static int g_mail_n, g_mail_mid;
/* identities of messages whose slot got reused, so a re-listed one isn't fetched again */
static BYTE g_mail_seen[MAIL_SEEN][0x1a];
static int g_mail_seen_n, g_mail_seen_next;
static __declspec(align(8)) BYTE g_inbox[0x50 * MAIL_MAX];
/* two reads at once like the viewer's fetch queue, kind 1 list (one at a time), 2 read */
#define FETCH_LANES 2
static struct { int job, kind, idx; char *body; } g_rd[FETCH_LANES] = { { -1 }, { -1 } };
static int g_del_job = -1, g_del_idx;
static int g_mail_fresh;
static char g_mail_state[16] = "pending";
static ULONGLONG g_mail_retry_at;
static volatile int g_mail_refresh = 1;
static volatile int g_mail_raw_new;
static int g_mail_fails;
static BYTE g_push_q[8][0x48];
static volatile LONG g_push_n;

/* body "subject 0x07 text 0x00 [attachment]". Group invitations and their answers add a type-10 {u64 group id,
   char[0x14] name} (svc 0x440 appends it raw). */
static void mail_parse(mailrec *m, const char *body, int len)
{
    int i, bel = -1, end;
    for (i = 0; i < len; i++) if (body[i] == 0x07) { bel = i; break; }
    if (bel < 0) m->subj[0] = 0;
    else sjis_to_utf8(body, bel > 0x80 ? 0x80 : bel, m->subj, sizeof m->subj);
    for (end = bel + 1; end < len && body[end]; end++) ;
    sjis_to_utf8(body + bel + 1, end - bel - 1, m->text, sizeof m->text);
    if (bel >= 0 && (*(WORD *)(m->hdr + 0x3e) & 0x7f) == 10 && len - (end + 1) >= 0x20) {
        memcpy(m->att, body + end + 1, 0x20);
        m->has_att = 1;
    }
}
/* the viewer's message identity (app.dll 0x1005245d): ids, seq, time and slots. Other bytes can differ in a pushed
   copy. */
static int mail_same(const BYTE *a, const BYTE *c)
{
    return memcmp(a, c, 0x10) == 0 && memcmp(a + 0x30, c + 0x30, 8) == 0 && a[0x3c] == c[0x3c] && a[0x3d] == c[0x3d];
}
static void mail_ident(const BYTE *h, BYTE out[0x1a])
{
    memcpy(out, h, 0x10);
    memcpy(out + 0x10, h + 0x30, 8);
    out[0x18] = h[0x3c];
    out[0x19] = h[0x3d];
}
/* the slot with the lowest mid above after and a state in mask (a bit per state), or -1. Mids only go up, so it's
   arrival order */
static int mail_next(int after, unsigned mask)
{
    int best = -1;
    for (int i = 0; i < g_mail_n; i++)
        if (g_mail[i].mid > after && ((mask >> g_mail[i].state) & 1) && (best < 0 || g_mail[i].mid < g_mail[best].mid))
            best = i;
    return best;
}
static mailrec *mail_by_mid(int mid)
{
    for (int i = 0; i < g_mail_n; i++) if (g_mail[i].mid == mid) return &g_mail[i];
    return NULL;
}
static ULONGLONG mail_order(const BYTE *h)
{
    return (ULONGLONG)*(const DWORD *)(h + 0x34) << 32 | *(const DWORD *)(h + 0x30);
}
/* a chat line waits for an earlier one from the same handle that's still to be read, two sends or two reads at once
   can finish in either order */
static int mail_held(const mailrec *m)
{
    const BYTE *e = m->hdr, *o;
    if (m->state != 1 || ((*(const WORD *)(e + 0x3e) >> 7) & 0x1f) != 0) return 0;
    for (int i = 0; i < g_mail_n; i++) {
        o = g_mail[i].hdr;
        if (g_mail[i].state == 0 && ((*(const WORD *)(o + 0x3e) >> 7) & 0x1f) == 0 && !memcmp(o, e, 8) &&
            o[0x3c] == e[0x3c] && mail_order(o) < mail_order(e))
            return 1;
    }
    return 0;
}
/* the oldest saved one whose delete hasn't started */
static int mail_saved_spare(void)
{
    for (int i = mail_next(0, 1 << 4); i >= 0; i = mail_next(g_mail[i].mid, 1 << 4))
        if (g_del_job < 0 || i != g_del_idx) return i;
    return -1;
}
static mailrec *mail_add(const BYTE *hdr)
{
    BYTE id[0x1a];
    int i;
    for (i = 0; i < g_mail_n; i++)
        if (mail_same(g_mail[i].hdr, hdr)) return NULL;
    mail_ident(hdr, id);
    for (i = 0; i < g_mail_seen_n; i++)
        if (memcmp(g_mail_seen[i], id, sizeof id) == 0) return NULL;
    if (g_mail_n < MAIL_MAX) {
        i = g_mail_n++;
    } else if ((i = mail_next(0, 1 << 2 | 1 << 5 | 1 << 6)) >= 0 || (i = mail_saved_spare()) >= 0) {
        /* the oldest failed, deleted or skipped one makes room, then a saved one whose delete hasn't started (the next
           login fetches it again). Its identity goes in the ring */
        mail_ident(g_mail[i].hdr, g_mail_seen[g_mail_seen_next]);
        g_mail_seen_next = (g_mail_seen_next + 1) % MAIL_SEEN;
        if (g_mail_seen_n < MAIL_SEEN) g_mail_seen_n++;
    } else {
        lg("mail: cache full (%d) - message not kept this session (still on the server)\r\n", MAIL_MAX);
        return NULL;
    }
    memset(&g_mail[i], 0, sizeof g_mail[0]);
    memcpy(g_mail[i].hdr, hdr, 0x48);
    g_mail[i].mid = ++g_mail_mid;
    return &g_mail[i];
}
static void mail_log_hdr(const char *what, const BYTE *h)
{
    char nm[64];
    sjis_json(nm, sizeof nm, h + 0x10, 0x0f);
    lg("mail: %s from=%s slot=%u kind=%u len=%u seq=%u time=%u 3e=%04x 42=%04x\r\n", what, nm,
       h[0x3c], (unsigned)((*(WORD *)(h + 0x3e) >> 7) & 0x1f), *(DWORD *)(h + 0x38),
       *(DWORD *)(h + 0x30), *(DWORD *)(h + 0x34), *(WORD *)(h + 0x3e), *(WORD *)(h + 0x42));
}
/* push callbacks run inside polcore's IRC processing on the hold loop's thread, H on its stack, so copy and get out */
static void __cdecl mail_evt(int code, void *arg)
{
    lg("mail: evt code=%d arg=%p\r\n", code, arg);
    /* 2 friend presence, 3 kind-9 notice, 4-6 groups: polcore already updated its arrays */
    if (code >= 2 && code <= 6) g_snap_dirty = 1;
    if (code == 0 && arg && !IsBadReadPtr(arg, 0x48)) {
        LONG n = g_push_n;
        if (n < 8) { memcpy(g_push_q[n], arg, 0x48); g_push_n = n + 1; }
    }
}
static void __cdecl mail_raw(void *arg)
{
    BYTE *h = (BYTE *)arg;
    if (!h || IsBadReadPtr(h, 0x50)) return;
    mail_log_hdr("real-time", h);
    if (((*(WORD *)(h + 0x3e) >> 12) & 3) == 1) return;   /* delivery receipt */
    mailrec *m = mail_add(h);
    if (m) {
        const char *body = *(const char **)(h + 0x4c);
        DWORD len = *(DWORD *)(h + 0x38);
        if (body && len <= 0x180 && !IsBadReadPtr(body, len)) mail_parse(m, body, (int)len);
        m->state = 3;
        g_snap_dirty = g_mail_fresh = g_mail_raw_new = 1;
    }
}

static int mail_mine(BYTE *b, const BYTE *h)
{
    return ((*(const WORD *)(h + 0x3e) >> 7) & 0x1f) == 0 && is_me(b, *(const DWORD *)h, *(const DWORD *)(h + 4));
}

/* every line this session sent to another POL ID: header time and seq and the slot it went from */
#define SENT_IDS 256
static struct { DWORD time, seq; BYTE from_slot; } g_sent_ids[SENT_IDS];
static int g_sent_ids_n, g_sent_ids_next;
static int sent_by_me(const BYTE *h)
{
    for (int i = 0; i < g_sent_ids_n; i++)
        if (g_sent_ids[i].time == *(const DWORD *)(h + 0x34) && g_sent_ids[i].seq == *(const DWORD *)(h + 0x30) &&
            g_sent_ids[i].from_slot == h[0x3c])
            return 1;
    return 0;
}
/* a zeroed or odd recipient field never counts as someone else's POL ID */
static int polid_ok(BYTE *b, DWORD lo, DWORD hi)
{
    char id[12];
    if (!lo && !hi) return 0;
    fmt_polid(b, lo, hi, id);
    return lstrlenA(id) == 8;
}
/* an echo of mine: a line this session sent to another POL ID (same time, seq and slot), or a handle writing to itself.
   Anything else from my POL ID is a real message */
static int mail_echo(BYTE *b, const BYTE *h)
{
    DWORD lo = *(const DWORD *)(h + 8), hi = *(const DWORD *)(h + 0xc);
    if (!mail_mine(b, h)) return 0;
    return sent_by_me(h) || (polid_ok(b, lo, hi) && is_me(b, lo, hi) && h[0x3c] == h[0x3d]);
}
/* the ones that came back, so the app can mark the line it sent */
#define RETURNED_MAX 16
static struct { DWORD time, seq; BYTE from_slot; } g_returned[RETURNED_MAX];
static int g_returned_n, g_returned_next;
static void mail_returned(const BYTE *h)
{
    DWORD t = *(const DWORD *)(h + 0x34), q = *(const DWORD *)(h + 0x30);
    for (int i = 0; i < g_returned_n; i++)
        if (g_returned[i].time == t && g_returned[i].seq == q && g_returned[i].from_slot == h[0x3c]) return;
    g_returned[g_returned_next].time = t;
    g_returned[g_returned_next].seq = q;
    g_returned[g_returned_next].from_slot = h[0x3c];
    g_returned_next = (g_returned_next + 1) % RETURNED_MAX;
    if (g_returned_n < RETURNED_MAX) g_returned_n++;
    g_snap_dirty = 1;
}
/* a line of mine can't be told from an echo while a send of mine is still going, so it waits, unread and out of the
   snapshot's bodies, till they've all been answered */
static int sends_going(void);
static int mail_undecided(BYTE *b, const mailrec *m)
{
    return sends_going() && (m->state == 0 || m->state == 3) && mail_mine(b, m->hdr);
}
/* looked at again whenever a line lands and whenever a send's answer comes in */
static void mail_recheck(BYTE *b)
{
    if (!g_mail_raw_new) return;
    g_mail_raw_new = 0;
    for (int i = 0; i < g_mail_n; i++)
        if (g_mail[i].state == 3 && mail_echo(b, g_mail[i].hdr)) {
            lg("mail: a chat line I sent came back real-time - not shown\r\n");
            mail_returned(g_mail[i].hdr);
            g_mail[i].state = 6;
        }
}
static int sender_ignored(BYTE *b, const BYTE *h)
{
    for (int bi = 0; bi < 100; bi++) {
        BYTE *a = b + 0xafc18 + bi * 0xb0;
        if (IsBadReadPtr(a, 0xb0)) break;
        if (!(*(unsigned *)(a + 0x98) & 1)) continue;
        if (*(DWORD *)(a + 0x00) != *(const DWORD *)(h + 0x00) || *(DWORD *)(a + 0x04) != *(const DWORD *)(h + 0x04))
            continue;
        if ((*(unsigned *)(a + 0x08) & 3) || ((*(unsigned *)(a + 0x9c) >> 13) & 0x3f) == h[0x3c]) return 1;
    }
    return 0;
}
static void mail_list_done(int r)
{
    int found = 0, added = 0;
    if (r != 1) {
        if (g_mail_fails++ == 0) {
            lg("mail: list failed %d - retry once in 30 s\r\n", r);
            g_mail_retry_at = GetTickCount64() + 30000;
            g_mail_refresh = 1;
        } else {
            lg("mail: list failed %d again - waiting for new mail\r\n", r);
            if (lstrcmpA(g_mail_state, "ok") != 0) lstrcpyA(g_mail_state, "failed");
            g_snap_dirty = 1;
        }
        return;
    }
    g_mail_fails = 0;
    lstrcpyA(g_mail_state, "ok");
    g_snap_dirty = 1;
    for (int i = 0; i < MAIL_MAX; i++) {
        BYTE *e = g_inbox + i * 0x50;
        if (!(*(WORD *)(e + 0x3e) & 0x8000)) continue;
        found++;
        mail_log_hdr("listed", e);
        if (mail_add(e)) added++;
    }
    lg("mail: list done, %d on server, %d new\r\n", found, added);
}
static void mail_read_done(int lane, int r)
{
    mailrec *m = &g_mail[g_rd[lane].idx];
    if (r == 1) {
        mail_parse(m, g_rd[lane].body, (int)*(DWORD *)(m->hdr + 0x38));
        m->state = 1;
        g_mail_fresh = 1;
        lg("mail: body read (%u bytes)\r\n", *(DWORD *)(m->hdr + 0x38));
    } else {
        m->state = 2;
        lg("mail: body read failed %d\r\n", r);
    }
    *(char **)(m->hdr + 0x4c) = NULL;
    HeapFree(GetProcessHeap(), 0, g_rd[lane].body);
    g_rd[lane].body = NULL;
    g_snap_dirty = 1;
}
static int msg_jobs(void);
static int msg_slot_ok(BYTE *b);

static int writes_waiting(void);
static int handle_switching(void);
static int mail_lane_busy(int kind, int idx)
{
    for (int l = 0; l < FETCH_LANES; l++)
        if (g_rd[l].job >= 0 && g_rd[l].kind == kind && (kind == 1 || g_rd[l].idx == idx)) return 1;
    return 0;
}
/* list or read, -0x100 (no free polcore slot, refused locally) tries again next turn. Only the list waits out the
   30 s after a failed one, reads still go */
static void mail_fetch_start(BYTE *b, int lane)
{
    int i, t;
    char *body;
    if (g_mail_refresh && !mail_lane_busy(1, 0) && GetTickCount64() >= g_mail_retry_at) {
        memset(g_inbox, 0, sizeof g_inbox);
        t = ((int (__cdecl *)(void *, int))SVC(0x524))(g_inbox, MAIL_MAX);
        if (t == NO_SLOT) return;
        lg("mail: list start -> %d\r\n", t);
        g_mail_refresh = 0;
        if (t < 0) { mail_list_done(t); return; }
        g_rd[lane].job = t; g_rd[lane].kind = 1;
        return;
    }
    for (i = mail_next(0, 1); i >= 0; i = mail_next(g_mail[i].mid, 1)) {
        mailrec *m = &g_mail[i];
        DWORD len = *(DWORD *)(m->hdr + 0x38);
        unsigned kd = (*(WORD *)(m->hdr + 0x3e) >> 7) & 0x1f;
        if (mail_lane_busy(2, i)) continue;
        if (len == 0 || len > 0x101100) { m->state = 2; continue; }
        if (kd == 0 && sender_ignored(b, m->hdr)) {
            lg("mail: chat message from an ignored sender - deleted unread\r\n");
            m->state = 4;
            continue;
        }
        /* the viewer reads one like any other and deletes it once it's saved. A line I sent is never shown as
           received, so it's deleted unread like an ignored sender's */
        if (mail_echo(b, m->hdr)) {
            lg("mail: a chat line I sent came back - deleted unread\r\n");
            mail_returned(m->hdr);
            m->state = 4;
            continue;
        }
        if (mail_undecided(b, m)) continue;
        /* chat 0, friend request 1, answers 9/10, group invitation 0xe and its notices, the rest stay unread */
        if (!(kd == 0 || kd == 1 || kd == 9 || kd == 10 || kd == 0xe || kd == 0xf || kd == 0x10 ||
              kd == 0x12 || kd == 0x13)) {
            lg("mail: kind %u is not shown by the launcher - not fetched\r\n", kd);
            m->state = 6;
            continue;
        }
        body = (char *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, len + 1);
        if (!body) return;
        *(char **)(m->hdr + 0x4c) = body;
        t = ((svc_p)SVC(0x518))(m->hdr);
        if (t != NO_SLOT) lg("mail: read start -> %d\r\n", t);
        if (t < 0) {
            *(char **)(m->hdr + 0x4c) = NULL;
            HeapFree(GetProcessHeap(), 0, body);
            if (t != NO_SLOT) m->state = 2;
            return;
        }
        g_rd[lane].job = t; g_rd[lane].kind = 2; g_rd[lane].idx = i; g_rd[lane].body = body;
        return;
    }
}
/* saved by the launcher: delete the server copy (svc 0x508, keyed like the GET). One to a slot this account lost
   is refused (-0x1540) and stays, as in the viewer. */
static void mail_delete_start(BYTE *b)
{
    int i, t;
    for (i = mail_next(0, 1 << 4); i >= 0; i = mail_next(g_mail[i].mid, 1 << 4)) {
        BYTE *h = g_mail[i].hdr;
        t = ((svc_p)SVC(0x508))(h);
        if (t == NO_SLOT) return;
        lg("mail: delete start -> %d (from slot %u to slot %u)\r\n", t, h[0x3c], h[0x3d]);
        if (t < 0) { g_mail[i].state = 5; continue; }
        g_del_job = t; g_del_idx = i;
        return;
    }
}
/* reads first, two at once, then deletes on a lane of their own */
static void mail_pump(BYTE *b)
{
    int i, r, done = 0;
    for (i = 0; i < g_push_n; i++) {   /* arrival order */
        mail_log_hdr("notice", g_push_q[i]);
        mail_add(g_push_q[i]);
    }
    g_push_n = 0;
    mail_recheck(b);
    /* never cancelled here, polcore fails a stuck job at 100 s itself and svc 0x520 stalls every job poll 10 s */
    for (i = 0; i < FETCH_LANES; i++)
        if (g_rd[i].job >= 0 && (r = ((svc_i)SVC(g_rd[i].kind == 1 ? 0x528 : 0x51c))(g_rd[i].job)) != 0) {
            g_rd[i].job = -1;
            if (g_rd[i].kind == 1) mail_list_done(r); else mail_read_done(i, r);
            done = 1;
        }
    if (g_del_job >= 0 && (r = ((svc_i)SVC(0x50c))(g_del_job)) != 0) {
        g_del_job = -1;
        g_mail[g_del_idx].state = 5;
        if (r == 1) lg("mail: deleted from the server after saving\r\n");
        else lg("mail: delete FAILED %d - the message stays on the server\r\n", r);
        done = 1;
    }
    /* a freed lane waits a turn, so a waiting send gets it first */
    if (done) return;
    if (!g_draining)
        for (i = 0; i < FETCH_LANES && msg_slot_ok(b); i++) {
            if (g_rd[i].job >= 0) continue;
            mail_fetch_start(b, i);
            if (g_rd[i].job < 0) break;   /* nothing to read, or polcore said no */
        }
    /* a delete keeps to the viewer's 2 jobs. In a drain it only goes with no write waiting for a slot, and nothing
       waits for it */
    if (g_del_job < 0 && msg_jobs() < 2 && (!g_draining || (!msg_jobs() && !writes_waiting())) && msg_slot_ok(b))
        mail_delete_start(b);
}
#define ACK_MAX (1024 * 1024)   /* about 140,000 mids, far more than a session ever has */
static void mail_ack_request(void)
{
    char *buf;
    int got, v = -1;
    if (!request_waiting("ack")) return;
    if ((got = take_request_ex("ack", NULL, 0, &buf, ACK_MAX)) < 0) return;
    for (int i = 0; i <= got; i++) {
        char c = buf[i];
        if (c >= '0' && c <= '9') v = v > 9999999 ? v : (v < 0 ? 0 : v * 10) + (c - '0');
        else {
            mailrec *m = v > 0 ? mail_by_mid(v) : NULL;
            if (m && (m->state == 1 || m->state == 3)) {
                m->state = m->state == 1 ? 4 : 5;
                m->text[0] = 0; m->subj[0] = 0;
                g_snap_dirty = 1;
            }
            v = -1;
        }
    }
    HeapFree(GetProcessHeap(), 0, buf);
}
