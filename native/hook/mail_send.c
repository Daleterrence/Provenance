static char g_send_token[48];      /* the snapshot's "send", the newest message and its state */
static char g_send_state[48] = "";
/* built messages in the order they were built, job -1 until the PUT starts. Two at once, the viewer's own most */
#define SEND_MAX 2
typedef struct { BYTE *msg; int job; ULONGLONG started; char token[48]; } sendrec;
static sendrec g_sq[SEND_MAX];
static int g_sq_n;
static int sends_going(void) { return g_sq_n > 0; }
static int msg_jobs(void)
{
    int n = g_del_job >= 0, i;
    for (i = 0; i < FETCH_LANES; i++) n += g_rd[i].job >= 0;
    for (i = 0; i < g_sq_n; i++) n += g_sq[i].job >= 0;
    return n;
}
static int slots_free(BYTE *b)
{
    int used = 0, total = 4;
    if (SVC(0xff4)) ((void (__cdecl *)(int *, int *))SVC(0xff4))(&used, &total);
    return total - used;
}
/* the viewer's rule (app.dll 0x100512cf) plus a job. 2 of 4 polcore slots free (svc 0xff4) means a third only
   starts with nothing else running */
#define MSG_JOBS_MAX 3
static int msg_slot_ok(BYTE *b) { return msg_jobs() < MSG_JOBS_MAX && slots_free(b) >= 2; }
static int send_built(void)
{
    for (int i = 0; i < g_sq_n; i++) if (g_sq[i].job < 0) return 1;
    return 0;
}

/* sends are capped, so nothing (a stuck app included) can hammer the server with them */
#define PACE_PER_MIN 40
#define PACE_PER_HOUR 600
enum { PACE_OK, PACE_MIN, PACE_HOUR, PACE_HELD, PACE_STOPPED };
typedef struct {
    ULONGLONG take[PACE_PER_HOUR];   /* ring of PUT start times */
    int next, count;
    ULONGLONG hold, failed;          /* no PUT before hold, when the last failure landed */
    int fails, stopped;              /* failed sends in a row, no more sends this session */
} send_pace;
static send_pace g_pace;
static int pace_check(const send_pace *p, ULONGLONG now)
{
    if (p->stopped) return PACE_STOPPED;
    if (now < p->hold) return PACE_HELD;
    if (p->count >= PACE_PER_MIN &&
        now - p->take[(p->next + PACE_PER_HOUR - PACE_PER_MIN) % PACE_PER_HOUR] < 60000) return PACE_MIN;
    if (p->count >= PACE_PER_HOUR && now - p->take[p->next] < 3600000) return PACE_HOUR;
    return PACE_OK;
}
static void pace_take(send_pace *p, ULONGLONG now)
{
    p->take[p->next] = now;
    p->next = (p->next + 1) % PACE_PER_HOUR;
    if (p->count < PACE_PER_HOUR) p->count++;
}
/* r is a send's result (1 = stored), ms how long its PUT took. Only sends come here, a delete's -0x1540 (a slot
   this account lost) never does */
static void pace_done(send_pace *p, ULONGLONG now, int r, ULONGLONG ms)
{
    ULONGLONG h;
    if (r == NO_SLOT) return;
    if (r == 1) p->fails = 0;
    /* one already on the wire when the last failure landed is the same blip, not another strike */
    else if (!(p->fails && now - ms <= p->failed) && ++p->fails >= 3) p->stopped = 1;
    if (r != 1 && !p->stopped) {
        p->failed = now;
        h = now + (p->fails == 1 ? 10000 : 60000);
        if (p->hold < h) p->hold = h;
    }
    if (ms > 5000 && p->hold < now + 5000) p->hold = now + 5000;
}
static void send_paced(int r, ULONGLONG ms)
{
    int was = g_pace.stopped;
    pace_done(&g_pace, GetTickCount64(), r, ms);
    if (g_pace.stopped) {
        if (!was) lg("mail: send failed %d - no more sends this session\r\n", r);
    } else if (r != 1) {
        lg("mail: send failed %d - sends wait %d s\r\n", r, g_pace.fails == 1 ? 10 : 60);
    } else if (ms > 5000) {
        lg("mail: slow send (%u ms) - the next one waits 5 s\r\n", (unsigned)ms);
    }
}
/* a message that can't go this session or this hour is refused, not kept waiting */
static const char *pace_refusal(void)
{
    int why = pace_check(&g_pace, GetTickCount64());
    return why == PACE_STOPPED ? "failed:stopped" : why == PACE_HOUR ? "failed:hourly limit" : NULL;
}

#define SEND_HIST 8
static struct { char token[48]; char state[48]; DWORD time, seq, to_lo, to_hi; int from_slot; } g_send_hist[SEND_HIST];
static int g_send_hist_next;
static void send_note(const char *token, const char *state)
{
    if (!lstrcmpA(token, g_send_token)) lstrcpynA(g_send_state, state, sizeof g_send_state);
}
/* a line that went to another POL ID, remembered so the same line coming back can be told from a real one */
static void sent_record(BYTE *b, const BYTE *hdr)
{
    DWORD lo = *(const DWORD *)(hdr + 8), hi = *(const DWORD *)(hdr + 0xc);
    if (!polid_ok(b, lo, hi) || is_me(b, lo, hi)) return;
    g_sent_ids[g_sent_ids_next].time = *(const DWORD *)(hdr + 0x34);
    g_sent_ids[g_sent_ids_next].seq = *(const DWORD *)(hdr + 0x30);
    g_sent_ids[g_sent_ids_next].from_slot = hdr[0x3c];
    g_sent_ids_next = (g_sent_ids_next + 1) % SENT_IDS;
    if (g_sent_ids_n < SENT_IDS) g_sent_ids_n++;
    g_mail_raw_new = 1;
}
/* hdr is the message that went, its header time and seq let the app put my line where the friend sees it */
static void send_result(const char *token, const char *state, const BYTE *hdr)
{
    send_note(token, state);
    lstrcpynA(g_send_hist[g_send_hist_next].token, token, sizeof g_send_hist[0].token);
    lstrcpynA(g_send_hist[g_send_hist_next].state, state, sizeof g_send_hist[0].state);
    g_send_hist[g_send_hist_next].time = hdr ? *(const DWORD *)(hdr + 0x34) : 0;
    g_send_hist[g_send_hist_next].seq = hdr ? *(const DWORD *)(hdr + 0x30) : 0;
    g_send_hist[g_send_hist_next].to_lo = hdr ? *(const DWORD *)(hdr + 0x08) : 0;
    g_send_hist[g_send_hist_next].to_hi = hdr ? *(const DWORD *)(hdr + 0x0c) : 0;
    g_send_hist[g_send_hist_next].from_slot = hdr ? hdr[0x3c] : -1;
    g_send_hist_next = (g_send_hist_next + 1) % SEND_HIST;
    g_snap_dirty = 1;
}
static void send_fail(const char *why)
{
    lg("mail: send refused - %s\r\n", why);
    send_result(g_send_token, why, NULL);
}
static void send_drop(int i)
{
    HeapFree(GetProcessHeap(), 0, g_sq[i].msg);
    for (; i < g_sq_n - 1; i++) g_sq[i] = g_sq[i + 1];
    g_sq_n--;
}
/* chat lines, answers and group messages all get paced here (a chat take only checks first), oldest built first */
static void mail_send_start(BYTE *b)
{
    ULONGLONG now = GetTickCount64();
    int why = pace_check(&g_pace, now), i, t;
    const char *no = pace_refusal();
    char st[24];
    sendrec *s;
    for (i = 0; i < g_sq_n && g_sq[i].job >= 0; i++) ;
    if (i == g_sq_n) return;
    s = &g_sq[i];
    if (no) {
        lg("mail: send refused - %s\r\n", no);
        send_result(s->token, no, NULL);
        send_drop(i);
        return;
    }
    /* in a drain (Play, log out) a built message goes once a message job's free, the pace's waits don't hold it */
    t = (g_draining ? msg_jobs() < MSG_JOBS_MAX : why == PACE_OK && msg_slot_ok(b)) ? ((svc_p)SVC(0x510))(s->msg)
                                                                                    : NO_SLOT;
    if (t == NO_SLOT) { send_note(s->token, "queued"); return; }
    lg("mail: send start -> %d\r\n", t);
    if (t < 0) {
        wsprintfA(st, "failed:%d", t);
        send_result(s->token, st, NULL);
        /* polcore refusing it locally (-0x1551 for a bad header) sent nothing, only the account codes count */
        if (t == -0x153a || t == -0x1540) send_paced(t, 0);
        send_drop(i);
    } else {
        pace_take(&g_pace, now);
        s->job = t;
        s->started = now;
        send_note(s->token, "sending");
        g_snap_dirty = 1;   /* the app hands over its next line once it sees this one's been taken */
    }
}
/* FFXI only prints a message's text when it's 300 bytes or less, counted in the Shift-JIS that goes */
#define TEXT_MAX_BYTES 300

/* like the viewer (app.dll 0x100556e0), a 0xa8 header (svc 0x438) then the body (svc 0x440). It joins the send
   queue under g_send_token */
static int msg_build(BYTE *b, int kind, unsigned toLo, unsigned toHi, int fromSlot, int toSlot,
                     const char *myName, const char *sj_subj, const char *sj_text, const BYTE *att, int attType)
{
    unsigned __int64 me = my_id(b);
    int attLen = att ? 0x20 : 0;
    int need = lstrlenA(sj_subj) + 1 + lstrlenA(sj_text) + 1 + attLen, n;
    BYTE *msg;
    if (g_sq_n >= SEND_MAX) return -1;
    msg = (BYTE *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 0xa8 + need + 8);
    if (!msg || ((ULONG_PTR)msg & 7)) {
        if (msg) HeapFree(GetProcessHeap(), 0, msg);
        return -1;
    }
    n = ((int (__cdecl *)(const char *, const char *, int, const void *, char *))SVC(0x440))(
            sj_subj, sj_text, attLen, att, (char *)msg + 0xa8);
    /* 0x438's 8th argument is the subject line the recipient's list shows, not a name */
    ((int (__cdecl *)(void *, int, unsigned, unsigned, unsigned, unsigned, const char *, const char *,
                      int, int, int, int, int))SVC(0x438))(
            msg, kind, (unsigned)me, (unsigned)(me >> 32), toLo, toHi, myName, sj_subj,
            fromSlot, toSlot, 1000, att ? attType : 0, n);
    mail_log_hdr("send", msg);
    g_sq[g_sq_n].msg = msg;
    g_sq[g_sq_n].job = -1;
    g_sq[g_sq_n].started = 0;
    lstrcpynA(g_sq[g_sq_n].token, g_send_token, sizeof g_sq[0].token);
    g_sq_n++;
    g_snap_dirty = 1;
    return n;
}
static void mail_send_parse(BYTE *b, char *buf)
{
    static char sj_subj[0x84], sj_text[0x1004];
    BYTE fr[0x140];
    char *p = buf, *tok, *who, *subj, *text, *q, myName[16], frName[64];
    int row = 0, digits = 0, mySlot, n, wl;
    g_send_token[0] = 0;   /* the token first, so a refusal reports under its own token */
    if ((q = strchr(buf, '\n')) != NULL) {
        int tl = (int)strcspn(++q, "\r\n");
        lstrcpynA(g_send_token, q, (tl < (int)sizeof g_send_token - 1 ? tl : (int)sizeof g_send_token - 1) + 1);
    }
    if (pace_refusal()) { send_fail(pace_refusal()); return; }
    while (*p >= '0' && *p <= '9' && digits < 4) { row = row * 10 + (*p++ - '0'); digits++; }
    if (*p == '\r') p++;
    if (!digits || *p != '\n') { send_fail("failed:bad request"); return; }
    p++;
    tok = p; while (*p && *p != '\n') p++;
    if (*p) *p++ = 0;
    who = p; while (*p && *p != '\n') p++;
    if (*p) *p++ = 0;
    subj = p; while (*p && *p != '\n') p++;
    if (*p) *p++ = 0;
    text = p;
    lstrcpynA(g_send_token, tok, sizeof g_send_token);
    if (row >= 200 || !get_friend(b, row, fr)) { send_fail("failed:no such friend"); return; }
    n = lstrlenA((const char *)fr + 0xa0);
    sjis_to_utf8((const char *)fr + 0xa0, n > 15 ? 15 : n, frName, sizeof frName);
    if (lstrcmpA(frName, who) != 0) { send_fail("failed:friend list changed"); return; }
    if (*(DWORD *)(fr + 0x08) & 0x10000000) { send_fail("failed:pending"); return; }
    if (is_me(b, *(DWORD *)(fr + 0x00), *(DWORD *)(fr + 0x04))) { send_fail("failed:self"); return; }
    wl = MultiByteToWideChar(CP_UTF8, 0, text, -1, NULL, 0);
    if (wl <= 0 || wl - 1 > TEXT_MAX_BYTES) { send_fail("failed:too long"); return; }
    for (q = text; *q; q++) if ((BYTE)*q < 0x20 && *q != '\n') *q = ' ';
    for (q = subj; *q; q++) if ((BYTE)*q < 0x20) *q = ' ';
    mySlot = my_handle(b, myName);
    utf8_to_sjis(subj, sj_subj, 0x80);
    n = utf8_to_sjis(text, sj_text, 0x1000);
    if (!sj_text[0]) { send_fail("failed:empty"); return; }
    if (n > TEXT_MAX_BYTES) { send_fail("failed:too long"); return; }
    n = msg_build(b, 0, *(DWORD *)(fr + 0x00), *(DWORD *)(fr + 0x04), mySlot, (int)FR_SLOT(fr), myName,
                  sj_subj, sj_text, NULL, 0);
    if (n < 0) { send_fail("failed:alloc"); return; }
    lg("mail: send to row %d (my slot %d, body %d bytes)\r\n", row, mySlot, n);
    mail_send_start(b);
}
static void mail_send_pump(BYTE *b)
{
    static int said;
    char *buf, st[24];
    int i, r, why, again = 0;
    ULONGLONG ms;
    for (i = 0; i < g_sq_n; i++) {
        sendrec *s = &g_sq[i];
        if (s->job < 0 || (r = ((svc_i)SVC(0x514))(s->job)) == 0) continue;
        s->job = -1;
        /* nothing went, the viewer starts it again next frame (its send state 4 -> 2) */
        if (r == NO_SLOT) { send_note(s->token, "queued"); again = 1; continue; }
        if (r == 1) lstrcpyA(st, "sent"); else wsprintfA(st, "failed:%d", r);
        send_result(s->token, st, s->msg);
        if (r == 1) sent_record(b, s->msg);
        ms = GetTickCount64() - s->started;
        lg("mail: send done -> %d (%u ms)\r\n", r, (unsigned)ms);
        send_paced(r, ms);
        send_drop(i--);   /* only now, the upload read it during the polls */
    }
    /* a built message is a write the user asked for, so it still starts during a drain (Play, log out), which waits
       for it. No new requests then though */
    if (send_built()) { if (!again) mail_send_start(b); return; }
    /* an answer owns the send slots from its request to its message. A waiting answer or group request goes
       before the next line, they only start with nothing sending */
    if (g_sq_n >= SEND_MAX || g_draining || g_answer_kind || handle_switching() || !request_waiting(REQ_SEND) ||
        request_new(REQ_ANSWER) || request_new(REQ_GROUP))
        return;
    /* stopped or at the hourly cap, a request is still taken so the app hears it wasn't sent */
    why = pace_check(&g_pace, GetTickCount64());
    if (why != PACE_OK && why != PACE_STOPPED && why != PACE_HOUR) {
        if (why == PACE_MIN && said != why)
            lg("mail: %d sends in a minute - the next chat line waits\r\n", PACE_PER_MIN);
        said = why;
        return;
    }
    if (why == PACE_OK && !msg_slot_ok(b)) return;
    buf = (char *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 0x4000);
    if (!buf) return;
    if (take_request(REQ_SEND, buf, 0x4000) >= 0) {
        said = 0;
        mail_send_parse(b, buf);
    }
    HeapFree(GetProcessHeap(), 0, buf);
}

/* request and accept do AddFriend + a list upload before their message, decline replies at once, ignore does
   AddBlock first. The viewer's fixed subject, no text. */
static int g_answer_wait;
static BYTE g_answer_hdr[0x48];
static char g_answer_subj[0x84];
static char g_answer_text[0x84];
static char g_answer_token[48];

static void answer_fail(const char *why)
{
    lstrcpynA(g_send_token, g_answer_token, sizeof g_send_token);
    send_fail(why);
    g_answer_kind = 0;
    g_answer_wait = 0;
}
static void answer_send(BYTE *b)
{
    char myName[16];
    int mySlot = g_answer_hdr[0x3d], n;
    if ((g_answer_kind == 1 || g_answer_kind == 9) &&
        find_friend_row(b, *(DWORD *)(g_answer_hdr + 0x00), *(DWORD *)(g_answer_hdr + 0x04), g_answer_hdr[0x3c]) < 0) {
        answer_fail("failed:no such friend");
        return;
    }
    if (!slot_name(b, mySlot, myName)) { answer_fail("failed:no handle"); return; }
    lstrcpynA(g_send_token, g_answer_token, sizeof g_send_token);
    n = msg_build(b, g_answer_kind, *(DWORD *)(g_answer_hdr + 0x00), *(DWORD *)(g_answer_hdr + 0x04),
                  mySlot, g_answer_hdr[0x3c], myName, g_answer_subj, g_answer_text, NULL, 0);
    lg("friends: answer kind %d from slot %d to slot %d (body %d)\r\n", g_answer_kind, mySlot, g_answer_hdr[0x3c], n);
    if (n < 0) { answer_fail("failed:alloc"); return; }
    g_answer_kind = 0;
    mail_send_start(b);
    g_snap_dirty = 1;
}
static void answer_request(BYTE *b)
{
    char buf[512], *p, *op, *tok, *mids, *subj, *arg, *hkey, nm[16], k[17];
    int mid, i, r, used = 0;
    BYTE row[0x140];
    const mailrec *m = NULL;
    const srchrec *h;
    if (take_request(REQ_ANSWER, buf, sizeof buf) < 0) return;
    p = buf;
    op = req_line(&p); tok = req_line(&p); mids = req_line(&p); subj = req_line(&p); arg = req_line(&p);
    hkey = req_line(&p);
    lstrcpynA(g_answer_token, tok, sizeof g_answer_token);
    /* every answer ends in a message, so refuse it before AddFriend or a list upload */
    if (pace_refusal()) { answer_fail(pace_refusal()); return; }
    if (!parse_num(mids, 6, &mid)) { answer_fail("failed:bad request"); return; }
    if (lstrcmpA(op, "request") == 0) {
        if (mid >= g_srch_n || g_srch_step != 0) { answer_fail("failed:no such result"); return; }
        h = &g_srch[mid];
        /* the hit must be the one the launcher showed, a newer search replaces the list */
        peer_key(b, h->lo, h->hi, (unsigned)h->slot, k);
        if (lstrcmpA(k, hkey) != 0) { answer_fail("failed:no such result"); return; }
        if (is_me(b, h->lo, h->hi)) { answer_fail("failed:self"); return; }
        if (find_friend_row(b, h->lo, h->hi, h->slot) >= 0) { answer_fail("failed:already a friend"); return; }
        r = ((svc_add)SVC(0x2ec))(-1, 0, h->lo, h->hi, (unsigned)h->slot, h->name);
        lg("friends: request to search hit %d -> AddFriend %d\r\n", mid, r);
        if (r < 0) { answer_fail(failed(r)); return; }
        memset(g_answer_hdr, 0, sizeof g_answer_hdr);
        *(DWORD *)(g_answer_hdr + 0x00) = h->lo;
        *(DWORD *)(g_answer_hdr + 0x04) = h->hi;
        g_answer_hdr[0x3c] = (BYTE)h->slot;
        g_answer_hdr[0x3d] = (BYTE)g_my_slot;
        utf8_to_sjis(subj, g_answer_subj, 0x80);
        utf8_to_sjis(arg, g_answer_text, 0x80);
        g_answer_kind = 1;
        g_answer_wait = 1;
        lstrcpynA(g_edit_token, tok, sizeof g_edit_token);
        lstrcpynA(g_send_token, tok, sizeof g_send_token);
        lstrcpyA(g_send_state, "saving");
        friend_upload_start(b);
        return;
    }
    g_answer_text[0] = 0;
    for (i = 0; i < g_mail_n; i++) if (g_mail[i].mid == mid) { m = &g_mail[i]; break; }
    if (!m || ((*(const WORD *)(m->hdr + 0x3e) >> 7) & 0x1f) != 1) { answer_fail("failed:no such request"); return; }
    memcpy(g_answer_hdr, m->hdr, 0x48);
    utf8_to_sjis(subj, g_answer_subj, 0x80);
    memcpy(nm, m->hdr + 0x10, 15);
    nm[15] = 0;
    if (lstrcmpA(op, "decline") != 0 && is_me(b, *(DWORD *)(m->hdr + 0x00), *(DWORD *)(m->hdr + 0x04))) {
        answer_fail("failed:self"); return;
    }
    if (lstrcmpA(op, "decline") == 0) {
        g_answer_kind = 10;
        answer_send(b);
        return;
    }
    if (lstrcmpA(op, "accept") == 0) {
        /* the viewer refuses an acceptance into a full list (200) first, even for someone already on it */
        for (i = 0; i < 200; i++) used += get_friend(b, i, row);
        if (used >= 200) { answer_fail("failed:-7188"); return; }
        r = ((svc_add)SVC(0x2ec))(-1, 0, *(DWORD *)(m->hdr + 0x00), *(DWORD *)(m->hdr + 0x04), m->hdr[0x3c], nm);
        lg("friends: accept request mid %d -> AddFriend %d\r\n", mid, r);
        if (r == -0x1c13) {   /* already on my list: no upload, just the acceptance */
            g_answer_kind = 9;
            answer_send(b);
            return;
        }
        if (r < 0) { answer_fail(failed(r)); return; }
        g_answer_kind = 9;
    } else if (lstrcmpA(op, "ignore") == 0) {
        /* this handle only (0u), the viewer's way from a request */
        r = ((svc_add)SVC(0x2e4))(-1, 0u, *(DWORD *)(m->hdr + 0x00), *(DWORD *)(m->hdr + 0x04), m->hdr[0x3c], nm);
        lg("friends: ignore requester mid %d -> AddBlock %d\r\n", mid, r);
        if (r == -0x1c13) { answer_fail("failed:already ignored"); return; }
        if (r < 0) { answer_fail(failed(r)); return; }
        g_answer_kind = 10;
    } else {
        answer_fail("failed:bad request");
        return;
    }
    g_answer_wait = 1;
    lstrcpynA(g_edit_token, tok, sizeof g_edit_token);
    lstrcpynA(g_send_token, tok, sizeof g_send_token);
    lstrcpyA(g_send_state, "saving");
    friend_upload_start(b);
}
static void answer_pump(BYTE *b, int ctl)
{
    if (g_answer_kind && g_answer_wait) {
        if (g_edit_job >= 0 || g_edit_upload_want) return;
        g_answer_wait = 0;
        if (lstrcmpA(g_edit_state, "saved") != 0) {
            char why[48];
            wsprintfA(why, "failed:list %s", g_edit_state);
            answer_fail(why);
            return;
        }
    }
    /* in a drain too, the upload with the new row is done so its message has to follow */
    if (g_answer_kind && !g_answer_wait) {
        if (g_sq_n) return;
        answer_send(b);
        return;
    }
    if (ctl && !g_draining && g_edit_job < 0 && !g_edit_upload_want && !g_comment_wanted && !g_sq_n)
        answer_request(b);
}
