/* built messages in the order they were built, job -1 until the PUT starts. Two at once, the viewer's own most */
#define SEND_MAX 2
typedef struct { BYTE *msg; int job; ULONGLONG started; char token[48]; } sendrec;
static sendrec g_sq[SEND_MAX];
static int mail_send_going(void) { return g_s.send_queue_n > 0; }
static int mail_send_msg_jobs(void)
{
    int n = g_s.del_job >= 0, i;
    for (i = 0; i < FETCH_LANES; i++) n += g_s.mail_lanes[i].job >= 0;
    for (i = 0; i < g_s.send_queue_n; i++) n += g_sq[i].job >= 0;
    return n;
}
static int mail_send_slots_free(BYTE *b)
{
    int used = 0, total = 4;
    if (pc_has(b, SV_JOB_SLOTS)) pc_job_slots(b, &used, &total);
    return total - used;
}
/* the viewer's rule (app.dll 0x100512cf) plus a job. 2 of 4 polcore slots free (svc 0xff4) means a third only
   starts with nothing else running */
#define MSG_JOBS_MAX 3
static int mail_send_msg_slot_ok(BYTE *b)
{
    return mail_send_msg_jobs() < MSG_JOBS_MAX && mail_send_slots_free(b) >= 2;
}
static int mail_send_built(void)
{
    for (int i = 0; i < g_s.send_queue_n; i++) if (g_sq[i].job < 0) return 1;
    return 0;
}

/* sends are capped, so nothing (a stuck app included) can hammer the server with them */
static int mail_send_pace_check(const send_pace *p, ULONGLONG now)
{
    if (p->stopped) return PACE_STOPPED;
    if (now < p->hold) return PACE_HELD;
    if (p->count >= PACE_PER_MIN &&
        now - p->take[(p->next + PACE_PER_HOUR - PACE_PER_MIN) % PACE_PER_HOUR] < 60000) return PACE_MIN;
    if (p->count >= PACE_PER_HOUR && now - p->take[p->next] < 3600000) return PACE_HOUR;
    return PACE_OK;
}
static void mail_send_pace_take(send_pace *p, ULONGLONG now)
{
    p->take[p->next] = now;
    p->next = (p->next + 1) % PACE_PER_HOUR;
    if (p->count < PACE_PER_HOUR) p->count++;
}
/* r is a send's result (1 = stored), ms how long its PUT took. Only sends come here, a delete's -0x1540 (a slot
   this account lost) never does */
static void mail_send_pace_done(send_pace *p, ULONGLONG now, int r, ULONGLONG ms)
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
static void mail_send_paced(int r, ULONGLONG ms)
{
    int was = g_s.pace.stopped;
    mail_send_pace_done(&g_s.pace, GetTickCount64(), r, ms);
    if (g_s.pace.stopped) {
        if (!was) base_log("messages: send failed %d - no more sends this session\r\n", r);
    } else if (r != 1) {
        base_log("messages: send failed %d - sends wait %ds\r\n", r, g_s.pace.fails == 1 ? 10 : 60);
    } else if (ms > 5000) {
        base_log("messages: slow send (%ums) - the next one waits 5s\r\n", (unsigned)ms);
    }
}
static const char *mail_send_pace_refusal(void)
{
    int why = mail_send_pace_check(&g_s.pace, GetTickCount64());
    return why == PACE_STOPPED ? "failed:stopped" : why == PACE_HOUR ? "failed:hourly limit" : NULL;
}

static void mail_send_note(const char *token, const char *state)
{
    if (!lstrcmpA(token, g_s.send_token)) lstrcpynA(g_s.send_state, state, sizeof g_s.send_state);
}
/* a line that went to another POL ID, remembered so the same line coming back can be told from a real one */
static void mail_send_sent_record(BYTE *b, const BYTE *hdr)
{
    DWORD lo = MAIL(hdr)->to_lo, hi = MAIL(hdr)->to_hi;
    if (!mail_inbox_polid_ok(b, lo, hi) || peers_is_me(b, lo, hi)) return;
    g_sent_ids[g_s.sent_ids_next].time = MAIL(hdr)->time;
    g_sent_ids[g_s.sent_ids_next].seq = MAIL(hdr)->seq;
    g_sent_ids[g_s.sent_ids_next].from_slot = MAIL(hdr)->from_slot;
    g_s.sent_ids_next = (g_s.sent_ids_next + 1) % SENT_IDS;
    if (g_s.sent_ids_n < SENT_IDS) g_s.sent_ids_n++;
    g_mail_raw_new = 1;
}
static void mail_send_result(const char *token, const char *state, const BYTE *hdr)
{
    mail_send_note(token, state);
    lstrcpynA(g_s.send_hist[g_s.send_hist_next].token, token, sizeof g_s.send_hist[0].token);
    lstrcpynA(g_s.send_hist[g_s.send_hist_next].state, state, sizeof g_s.send_hist[0].state);
    g_s.send_hist[g_s.send_hist_next].time = hdr ? MAIL(hdr)->time : 0;
    g_s.send_hist[g_s.send_hist_next].seq = hdr ? MAIL(hdr)->seq : 0;
    g_s.send_hist[g_s.send_hist_next].to_lo = hdr ? MAIL(hdr)->to_lo : 0;
    g_s.send_hist[g_s.send_hist_next].to_hi = hdr ? MAIL(hdr)->to_hi : 0;
    g_s.send_hist[g_s.send_hist_next].from_slot = hdr ? MAIL(hdr)->from_slot : -1;
    g_s.send_hist_next = (g_s.send_hist_next + 1) % SEND_HIST;
    g_snap_dirty = 1;
}
static void mail_send_fail(const char *why)
{
    base_log("messages: send refused - %s\r\n", why);
    mail_send_result(g_s.send_token, why, NULL);
}
static void mail_send_drop(int i)
{
    HeapFree(GetProcessHeap(), 0, g_sq[i].msg);
    for (; i < g_s.send_queue_n - 1; i++) g_sq[i] = g_sq[i + 1];
    g_s.send_queue_n--;
}
/* chat lines, answers and group messages all get paced here (a chat take only checks first), oldest built first */
static void mail_send_start(BYTE *b)
{
    ULONGLONG now = GetTickCount64();
    int why = mail_send_pace_check(&g_s.pace, now), i, t;
    const char *no = mail_send_pace_refusal();
    char st[24];
    sendrec *s;
    for (i = 0; i < g_s.send_queue_n && g_sq[i].job >= 0; i++) ;
    if (i == g_s.send_queue_n) return;
    s = &g_sq[i];
    if (no) {
        base_log("messages: send refused - %s\r\n", no);
        mail_send_result(s->token, no, NULL);
        mail_send_drop(i);
        return;
    }
    /* in a drain (Play, log out) a built message goes once a message job's free, the pace's waits don't hold it */
    t = (g_s.draining ? mail_send_msg_jobs() < MSG_JOBS_MAX : why == PACE_OK && mail_send_msg_slot_ok(b))
        ? pc_mail_send(b, s->msg)
        : NO_SLOT;
    if (t == NO_SLOT) { mail_send_note(s->token, "queued"); return; }
    base_log("messages: send start -> %d\r\n", t);
    if (t < 0) {
        lstrcpyA(st, requests_failed(t));
        mail_send_result(s->token, st, NULL);
        /* polcore refusing it locally (-0x1551 for a bad header) sent nothing, only the account codes count */
        if (t == -0x153a || t == -0x1540) mail_send_paced(t, 0);
        mail_send_drop(i);
    } else {
        mail_send_pace_take(&g_s.pace, now);
        s->job = t;
        s->started = now;
        mail_send_note(s->token, "sending");
        g_snap_dirty = 1;   /* the app hands over its next line once it sees this one's been taken */
    }
}
/* FFXI only prints a message's text when it's 300 bytes or less, counted in the Shift-JIS that goes */
#define TEXT_MAX_BYTES 300

/* like the viewer (app.dll 0x100556e0), a header (svc 0x438) then the body (svc 0x440). It joins the send
   queue under g_s.send_token */
static int mail_send_msg_build(BYTE *b, int kind, unsigned toLo, unsigned toHi, int fromSlot, int toSlot,
                     const char *myName, const char *sj_subj, const char *sj_text, const BYTE *att, int attType)
{
    unsigned __int64 me = pc_my_id(b);
    int attLen = att ? 0x20 : 0;
    int need = lstrlenA(sj_subj) + 1 + lstrlenA(sj_text) + 1 + attLen, n;
    BYTE *msg;
    if (g_s.send_queue_n >= SEND_MAX) return -1;
    msg = (BYTE *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAIL_BUILT_SIZE + need + 8);
    if (!msg || ((ULONG_PTR)msg & 7)) {
        if (msg) HeapFree(GetProcessHeap(), 0, msg);
        return -1;
    }
    n = pc_mail_body(b, sj_subj, sj_text, attLen, att, (char *)msg + MAIL_BUILT_SIZE);
    /* 0x438's 8th argument is the subject line the recipient's list shows, not a name */
    pc_mail_header(b, msg, kind, (unsigned)me, (unsigned)(me >> 32), toLo, toHi, myName, sj_subj,
                   fromSlot, toSlot, 1000, att ? attType : 0, n);
    mail_inbox_log_hdr("send", msg);
    g_sq[g_s.send_queue_n].msg = msg;
    g_sq[g_s.send_queue_n].job = -1;
    g_sq[g_s.send_queue_n].started = 0;
    lstrcpynA(g_sq[g_s.send_queue_n].token, g_s.send_token, sizeof g_sq[0].token);
    g_s.send_queue_n++;
    g_snap_dirty = 1;
    return n;
}
static void mail_send_parse(BYTE *b, char *buf)
{
    static char sj_subj[0x84], sj_text[0x1004];
    pol_friend_copy fr;
    char *p = buf, *tok, *who, *subj, *text, *q, myName[16], frName[64];
    int row = 0, digits = 0, mySlot, n, wl;
    g_s.send_token[0] = 0;   /* the token first, so a refusal reports under its own token */
    if ((q = strchr(buf, '\n')) != NULL) {
        int tl = (int)strcspn(++q, "\r\n");
        lstrcpynA(g_s.send_token, q, (tl < (int)sizeof g_s.send_token - 1 ? tl : (int)sizeof g_s.send_token - 1) + 1);
    }
    if (mail_send_pace_refusal()) { mail_send_fail(mail_send_pace_refusal()); return; }
    while (*p >= '0' && *p <= '9' && digits < 4) { row = row * 10 + (*p++ - '0'); digits++; }
    if (*p == '\r') p++;
    if (!digits || *p != '\n') { mail_send_fail("failed:bad request"); return; }
    p++;
    tok = p; while (*p && *p != '\n') p++;
    if (*p) *p++ = 0;
    who = p; while (*p && *p != '\n') p++;
    if (*p) *p++ = 0;
    subj = p; while (*p && *p != '\n') p++;
    if (*p) *p++ = 0;
    text = p;
    lstrcpynA(g_s.send_token, tok, sizeof g_s.send_token);
    if (row >= 200 || !peers_get_friend(b, row, &fr)) { mail_send_fail("failed:no such friend"); return; }
    n = lstrlenA(fr.f.name);
    pol_utf8(b, fr.f.name, n > 15 ? 15 : n, frName, sizeof frName);
    if (lstrcmpA(frName, who) != 0) { mail_send_fail("failed:friend list changed"); return; }
    if (fr.f.flags & FRIEND_PENDING) { mail_send_fail("failed:pending"); return; }
    if (peers_is_me(b, fr.f.id_lo, fr.f.id_hi)) { mail_send_fail("failed:self"); return; }
    wl = MultiByteToWideChar(CP_UTF8, 0, text, -1, NULL, 0);
    if (wl <= 0 || wl - 1 > TEXT_MAX_BYTES) { mail_send_fail("failed:too long"); return; }
    for (q = text; *q; q++) if ((BYTE)*q < 0x20 && *q != '\n') *q = ' ';
    for (q = subj; *q; q++) if ((BYTE)*q < 0x20) *q = ' ';
    mySlot = peers_my_handle(b, myName);
    pol_sjis(b, subj, sj_subj, 0x80);
    n = pol_sjis(b, text, sj_text, 0x1000);
    if (!sj_text[0]) { mail_send_fail("failed:empty"); return; }
    if (n > TEXT_MAX_BYTES) { mail_send_fail("failed:too long"); return; }
    n = mail_send_msg_build(b, 0, fr.f.id_lo, fr.f.id_hi, mySlot, pc_friend_slot(&fr.f), myName,
                  sj_subj, sj_text, NULL, 0);
    if (n < 0) { mail_send_fail("failed:alloc"); return; }
    base_log("messages: send to row %d (my slot %d, body %d bytes)\r\n", row, mySlot, n);
    mail_send_start(b);
}
static void mail_send_pump(BYTE *b)
{
    static int said;
    char *buf, st[24];
    int i, r, why, again = 0;
    ULONGLONG ms;
    for (i = 0; i < g_s.send_queue_n; i++) {
        sendrec *s = &g_sq[i];
        if (s->job < 0 || (r = pc_mail_send_check(b, s->job)) == 0) continue;
        s->job = -1;
        /* nothing went, the viewer starts it again next frame (its send state 4 -> 2) */
        if (r == NO_SLOT) { mail_send_note(s->token, "queued"); again = 1; continue; }
        if (r == 1) lstrcpyA(st, "sent"); else lstrcpyA(st, requests_failed(r));
        mail_send_result(s->token, st, s->msg);
        if (r == 1) mail_send_sent_record(b, s->msg);
        ms = GetTickCount64() - s->started;
        base_log("messages: send done -> %d (%ums)\r\n", r, (unsigned)ms);
        mail_send_paced(r, ms);
        mail_send_drop(i--);   /* only now, the upload read it during the polls */
    }
    /* a built message is a write the user asked for, so it still starts during a drain (Play, log out), which waits
       for it. No new requests then though */
    if (mail_send_built()) { if (!again) mail_send_start(b); return; }
    /* an answer owns the send slots from its request to its message. A waiting answer or group request goes
       before the next line, they only start with nothing sending */
    if (g_s.send_queue_n >= SEND_MAX || g_s.draining || g_s.answer_kind || handles_switching() || !requests_waiting(REQ_SEND) ||
        requests_new(REQ_ANSWER) || requests_new(REQ_GROUP))
        return;
    /* stopped or at the hourly cap, a request is still taken so the app hears it wasn't sent */
    why = mail_send_pace_check(&g_s.pace, GetTickCount64());
    if (why != PACE_OK && why != PACE_STOPPED && why != PACE_HOUR) {
        if (why == PACE_MIN && said != why)
            base_log("messages: %d sends in a minute - the next chat line waits\r\n", PACE_PER_MIN);
        said = why;
        return;
    }
    if (why == PACE_OK && !mail_send_msg_slot_ok(b)) return;
    buf = (char *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 0x4000);
    if (!buf) return;
    if (requests_take(REQ_SEND, buf, 0x4000) >= 0) {
        said = 0;
        mail_send_parse(b, buf);
    }
    HeapFree(GetProcessHeap(), 0, buf);
}

/* request and accept do AddFriend + a list upload before their message, decline replies at once, ignore does
   AddBlock first. The viewer's fixed subject, no text. */
static BYTE g_answer_hdr[MAIL_KEY_SIZE];
static char g_answer_subj[0x84];
static char g_answer_text[0x84];
static char g_answer_token[48];

static void mail_send_answer_fail(const char *why)
{
    lstrcpynA(g_s.send_token, g_answer_token, sizeof g_s.send_token);
    mail_send_fail(why);
    g_s.answer_kind = 0;
    g_s.answer_wait = 0;
}
static void mail_send_answer_send(BYTE *b)
{
    char myName[16];
    int mySlot = MAIL(g_answer_hdr)->to_slot, n;
    if ((g_s.answer_kind == 1 || g_s.answer_kind == 9) &&
        peers_find_friend_row(b, MAIL(g_answer_hdr)->from_lo, MAIL(g_answer_hdr)->from_hi, MAIL(g_answer_hdr)->from_slot) < 0) {
        mail_send_answer_fail("failed:no such friend");
        return;
    }
    if (!peers_slot_name(b, mySlot, myName)) { mail_send_answer_fail("failed:no handle"); return; }
    lstrcpynA(g_s.send_token, g_answer_token, sizeof g_s.send_token);
    n = mail_send_msg_build(b, g_s.answer_kind, MAIL(g_answer_hdr)->from_lo, MAIL(g_answer_hdr)->from_hi,
                  mySlot, MAIL(g_answer_hdr)->from_slot, myName, g_answer_subj, g_answer_text, NULL, 0);
    base_log("friends: answer kind %d from slot %d to slot %d (body %d)\r\n", g_s.answer_kind, mySlot, MAIL(g_answer_hdr)->from_slot, n);
    if (n < 0) { mail_send_answer_fail("failed:alloc"); return; }
    g_s.answer_kind = 0;
    mail_send_start(b);
    g_snap_dirty = 1;
}
static void mail_send_answer_request(BYTE *b)
{
    char buf[512], *p, *op, *tok, *mids, *subj, *arg, *hkey, nm[16], k[17];
    int mid, i, r, used = 0;
    pol_friend_copy row;
    const mailrec *m = NULL;
    const srchrec *h;
    if (requests_take(REQ_ANSWER, buf, sizeof buf) < 0) return;
    p = buf;
    op = requests_line(&p); tok = requests_line(&p); mids = requests_line(&p); subj = requests_line(&p);
    arg = requests_line(&p);
    hkey = requests_line(&p);
    lstrcpynA(g_answer_token, tok, sizeof g_answer_token);
    /* every answer ends in a message, so refuse it before AddFriend or a list upload */
    if (mail_send_pace_refusal()) { mail_send_answer_fail(mail_send_pace_refusal()); return; }
    if (!requests_parse_num(mids, 6, &mid)) { mail_send_answer_fail("failed:bad request"); return; }
    if (lstrcmpA(op, "request") == 0) {
        if (mid >= g_s.search_n || g_search.step != 0) { mail_send_answer_fail("failed:no such result"); return; }
        h = &g_srch[mid];
        /* the hit must be the one the launcher showed, a newer search replaces the list */
        peers_key(b, h->lo, h->hi, (unsigned)h->slot, k);
        if (lstrcmpA(k, hkey) != 0) { mail_send_answer_fail("failed:no such result"); return; }
        if (peers_is_me(b, h->lo, h->hi)) { mail_send_answer_fail("failed:self"); return; }
        if (peers_find_friend_row(b, h->lo, h->hi, h->slot) >= 0) {
            mail_send_answer_fail("failed:already a friend"); return;
        }
        r = pc_friend_add(b, -1, 0, h->lo, h->hi, (unsigned)h->slot, h->name);
        base_log("friends: request to search hit %d -> AddFriend %d\r\n", mid, r);
        if (r < 0) { mail_send_answer_fail(requests_failed(r)); return; }
        memset(g_answer_hdr, 0, sizeof g_answer_hdr);
        MAIL(g_answer_hdr)->from_lo = h->lo;
        MAIL(g_answer_hdr)->from_hi = h->hi;
        MAIL(g_answer_hdr)->from_slot = (BYTE)h->slot;
        MAIL(g_answer_hdr)->to_slot = (BYTE)g_my_slot;
        pol_sjis(b, subj, g_answer_subj, 0x80);
        pol_sjis(b, arg, g_answer_text, 0x80);
        g_s.answer_kind = 1;
        g_s.answer_wait = 1;
        lstrcpynA(g_s.edit_token, tok, sizeof g_s.edit_token);
        lstrcpynA(g_s.send_token, tok, sizeof g_s.send_token);
        lstrcpyA(g_s.send_state, "saving");
        friends_upload_start(b);
        return;
    }
    g_answer_text[0] = 0;
    for (i = 0; i < g_s.mail_n; i++) if (g_mail[i].mid == mid) { m = &g_mail[i]; break; }
    if (!m || pc_mail_kind(m->hdr) != 1) { mail_send_answer_fail("failed:no such request"); return; }
    memcpy(g_answer_hdr, m->hdr, MAIL_KEY_SIZE);
    pol_sjis(b, subj, g_answer_subj, 0x80);
    memcpy(nm, MAIL(m->hdr)->name, 15);
    nm[15] = 0;
    if (lstrcmpA(op, "decline") != 0 && peers_is_me(b, MAIL(m->hdr)->from_lo, MAIL(m->hdr)->from_hi)) {
        mail_send_answer_fail("failed:self"); return;
    }
    if (lstrcmpA(op, "decline") == 0) {
        g_s.answer_kind = 10;
        mail_send_answer_send(b);
        return;
    }
    if (lstrcmpA(op, "accept") == 0) {
        /* the viewer refuses an acceptance into a full list (200) first, even for someone already on it */
        for (i = 0; i < 200; i++) used += peers_get_friend(b, i, &row);
        if (used >= 200) { mail_send_answer_fail(requests_failed(FRIEND_LIST_FULL)); return; }
        r = pc_friend_add(b, -1, 0, MAIL(m->hdr)->from_lo, MAIL(m->hdr)->from_hi, MAIL(m->hdr)->from_slot, nm);
        base_log("friends: accept request mid %d -> AddFriend %d\r\n", mid, r);
        if (r == ALREADY_LISTED) {   /* already on my list: no upload, just the acceptance */
            g_s.answer_kind = 9;
            mail_send_answer_send(b);
            return;
        }
        if (r < 0) { mail_send_answer_fail(requests_failed(r)); return; }
        g_s.answer_kind = 9;
    } else if (lstrcmpA(op, "ignore") == 0) {
        /* this handle only (0u), the viewer's way from a request */
        r = pc_ignore_add(b, -1, 0u, MAIL(m->hdr)->from_lo, MAIL(m->hdr)->from_hi, MAIL(m->hdr)->from_slot, nm);
        base_log("friends: ignore requester mid %d -> AddBlock %d\r\n", mid, r);
        if (r == ALREADY_LISTED) { mail_send_answer_fail("failed:already ignored"); return; }
        if (r < 0) { mail_send_answer_fail(requests_failed(r)); return; }
        g_s.answer_kind = 10;
    } else {
        mail_send_answer_fail("failed:bad request");
        return;
    }
    g_s.answer_wait = 1;
    lstrcpynA(g_s.edit_token, tok, sizeof g_s.edit_token);
    lstrcpynA(g_s.send_token, tok, sizeof g_s.send_token);
    lstrcpyA(g_s.send_state, "saving");
    friends_upload_start(b);
}
static void mail_send_answer_pump(BYTE *b, int ctl)
{
    if (g_s.answer_kind && g_s.answer_wait) {
        if (g_s.edit_job >= 0 || g_s.edit_upload_want) return;
        g_s.answer_wait = 0;
        if (lstrcmpA(g_s.edit_state, "saved") != 0) {
            char why[48];
            wsprintfA(why, "failed:list %s", g_s.edit_state);
            mail_send_answer_fail(why);
            return;
        }
    }
    /* in a drain too, the upload with the new row is done so its message has to follow */
    if (g_s.answer_kind && !g_s.answer_wait) {
        if (g_s.send_queue_n) return;
        mail_send_answer_send(b);
        return;
    }
    if (ctl && !g_s.draining && g_s.edit_job < 0 && !g_s.edit_upload_want && !g_s.comment_wanted && !g_s.send_queue_n)
        mail_send_answer_request(b);
}
