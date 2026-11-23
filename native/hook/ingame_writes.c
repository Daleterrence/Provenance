/* the app's sends, status changes and friend list changes whilst the game runs. The worker takes the request files and
   hands them over, the game's thread does every polcore and FFXiMain call from the POLL callback */
enum { IW_SEND, IW_STATUS, IW_FRIEND, IW_COMMENT, IW_ANSWER, IW_SEARCH, IW_BOXES };
enum { IW_AREA_SEND, IW_AREA_STATUS, IW_AREA_EDITS };
enum { IW_UP_NONE, IW_UP_ARMED, IW_UP_LATE };
enum { IW_THEN_NONE, IW_THEN_SAVED, IW_THEN_REQUEST, IW_THEN_DECLINE };
enum { IW_TEXT = 0x4000, IW_WAIT_MS = 45000, IW_UPLOAD_MS = 60000, IW_STATUS_MS = 15000 };

typedef BYTE (__cdecl *iw_upload_cb_fn)(int result, int extra);
typedef int (__cdecl *iw_arm_fn)(iw_upload_cb_fn cb);
typedef void (__cdecl *iw_status_fn)(int status);
typedef int (__cdecl *iw_away_fn)(int which, int on);
typedef struct { char token[48], state[48]; } iw_job;

static struct {
    volatile LONG lock, full[IW_BOXES];
    char box[IW_BOXES][IW_TEXT];
    ULONGLONG wait_at[IW_BOXES];
    iw_job send, edit;
    send_hist hist[SEND_HIST];
    int hist_next, status_want, comment_job, up, then, dead;
    ULONGLONG status_at, up_at;
    volatile LONG cb_done, cb_result;
    char up_token[48];
    fx_send then_send;
    fx_request then_decline;
} g_iw = { .status_want = -1, .comment_job = -1 };
static const char *g_iw_step = "";

static void iw_reset(void)
{
    memset(&g_iw, 0, sizeof g_iw);
    g_iw.status_want = -1;
    g_iw.comment_job = -1;
}

/* the game's thread never waits for it, a frame that finds it taken does nothing and the next one tries again */
static int iw_try_lock(void) { return !InterlockedCompareExchange(&g_iw.lock, 1, 0); }
static int iw_lock(void)
{
    int i;
    for (i = 0; i < 2000; i++) {
        if (iw_try_lock()) return 1;
        Sleep(i < 50 ? 0 : 1);
    }
    return 0;
}
static void iw_unlock(void) { InterlockedExchange(&g_iw.lock, 0); }

static void iw_changed(void)
{
    g_snap_dirty = 1;
    if (g_ig.wake) SetEvent(g_ig.wake);
}
static void iw_set(iw_job *j, const char *token, const char *state)
{
    lstrcpynA(j->token, token, sizeof j->token);
    lstrcpynA(j->state, state, sizeof j->state);
    iw_changed();
}
static int iw_final(const char *state) { return !lstrcmpA(state, "sent") || !strncmp(state, "failed", 6); }

static void iw_send_result(const char *token, const char *state, const BYTE *msg)
{
    send_hist *h;
    iw_set(&g_iw.send, token, state);
    if (!iw_final(state)) return;
    h = &g_iw.hist[g_iw.hist_next];
    memset(h, 0, sizeof *h);
    lstrcpynA(h->token, token, sizeof h->token);
    lstrcpynA(h->state, state, sizeof h->state);
    h->from_slot = -1;
    if (msg) {
        h->time = MAIL(msg)->time;
        h->seq = MAIL(msg)->seq;
        h->to_lo = MAIL(msg)->to_lo;
        h->to_hi = MAIL(msg)->to_hi;
        h->from_slot = MAIL(msg)->from_slot;
    }
    g_iw.hist_next = (g_iw.hist_next + 1) % SEND_HIST;
}
static void iw_answer_result(const char *token, const char *state) { iw_send_result(token, state, NULL); }

/* undecided till FFXiMain's been looked at, so a request made early waits for it rather than being turned down */
static int iw_base(void)
{
    if (g_ig.off || g_fx.off || g_fx.worker_dead || g_iw.dead) return 0;
    return g_fx.found || g_fx.tries < FX_TRIES;
}
static int iw_area(int area)
{
    static const int status[] = { AT_FX_STATUS_SET, AT_FX_STATUS_OBJ, AT_FX_AWAY_FLAG };
    static const int edits[] = { AT_FX_FRIEND_UPLOAD, AT_FX_FRIEND_JOBS, AT_FX_FRIEND_STATE, AT_FX_MAIL_BUSY, AT_FX_FRIEND_DECLINE,
                                 AT_FX_FRIEND_ACCEPT };
    const int *ids = area == IW_AREA_STATUS ? status : edits;
    int i, n = area == IW_AREA_STATUS ? 3 : area == IW_AREA_EDITS ? 6 : 0;
    if (!iw_base()) return 0;
    for (i = 0; g_fx.found && i < n; i++)
        if (!pc_addr_found(ids[i])) return 0;
    return 1;
}
static int iw_quick(void) { return iw_base(); }

static int iw_world(BYTE *b) { return g_fx.in_world && !g_fx.at_select && pc_game_tag(b) >= 0; }
static int iw_slots(BYTE *b)
{
    int used = 0, total = 4;
    if (pc_has(b, SV_JOB_SLOTS)) pc_job_slots(b, &used, &total);
    return total - used >= 3;
}
static int iw_busy(void) { return g_iw.up != IW_UP_NONE || g_iw.comment_job >= 0 || g_search.step != 0; }
static int iw_mine_idle(void) { return !iw_busy() && g_fx.job < 0 && !g_fx.msg; }
/* the game's own friend request, accept and block find the upload taken and give up part way, so nothing starts beside
   them. The lock bits are 2 for the handle list's upload and 4 for the friend list's */
static int iw_game_idle(void)
{
    BYTE *jobs = *(BYTE **)pc_addr_of(g_fx.base, AT_FX_FRIEND_JOBS), *list = *(BYTE **)pc_addr_of(g_fx.base, AT_FX_FRIEND_STATE);
    if (!jobs || !list) return 0;
    return !(*(DWORD *)jobs & 6) && !*(int *)(list + 0x128) && !*pc_addr_of(g_fx.base, AT_FX_MAIL_BUSY);
}
static int iw_edit_ready(BYTE *b) { return iw_world(b) && iw_slots(b) && iw_mine_idle() && g_fx.ans_state == FX_ANS_NONE && iw_game_idle(); }
/* /fr worked before the game's friend jobs could be looked at, so a build without them still answers */
static int iw_answer_ready(BYTE *b)
{
    if (!pc_addr_found(AT_FX_FRIEND_JOBS) || !pc_addr_found(AT_FX_FRIEND_STATE) || !pc_addr_found(AT_FX_MAIL_BUSY))
        return iw_mine_idle();
    return iw_world(b) && iw_slots(b) && iw_mine_idle() && iw_game_idle();
}

static BYTE __cdecl iw_upload_cb(int result, int extra)
{
    (void)extra;
    if (g_iw.up != IW_UP_NONE && !g_iw.cb_done) {
        g_iw.cb_result = result;
        g_iw.cb_done = 1;
    }
    return 0;
}
/* 0 when it's armed. The edit has to go in straight after, before the friend manager next steps */
static int iw_arm(const char *token, int then)
{
    int r;
    g_iw_step = "upload";
    r = ((iw_arm_fn)pc_addr_of(g_fx.base, AT_FX_FRIEND_UPLOAD))(iw_upload_cb);
    g_iw_step = "";
    if (r) return r;
    g_iw.up = IW_UP_ARMED;
    g_iw.up_at = GetTickCount64();
    g_iw.cb_done = 0;
    g_iw.then = then;
    lstrcpynA(g_iw.up_token, token, sizeof g_iw.up_token);
    return 0;
}
/* an edit that fails after the arm leaves an upload with nothing to send, which ends on its own */
static void iw_edit_failed(int r)
{
    g_iw.then = IW_THEN_NONE;
    iw_set(&g_iw.edit, g_iw.up_token, requests_failed(r));
}

static void iw_upload_step(void)
{
    ULONGLONG now = GetTickCount64();
    int r, then;
    char why[64];
    if (g_iw.up == IW_UP_LATE && (g_iw.cb_done || now - g_iw.up_at >= IW_UPLOAD_MS)) {
        g_iw.up = IW_UP_NONE;
        g_iw.cb_done = 0;
    }
    if (g_iw.up != IW_UP_ARMED) return;
    then = g_iw.then;
    if (!g_iw.cb_done) {
        if (now - g_iw.up_at < IW_UPLOAD_MS) return;
        /* the callback has no tag, so nothing new is armed till this one's had its say */
        base_log("ingame: a friend list upload got no answer in 60s\r\n");
        g_iw.up = IW_UP_LATE;
        g_iw.up_at = now;
        g_iw.then = IW_THEN_NONE;
        if (then == IW_THEN_SAVED) iw_set(&g_iw.edit, g_iw.up_token, "failed:no reply");
        else if (then != IW_THEN_NONE) iw_send_result(g_iw.up_token, "failed:list failed:no reply", NULL);
        return;
    }
    r = (int)g_iw.cb_result;
    g_iw.up = IW_UP_NONE;
    g_iw.cb_done = 0;
    g_iw.then = IW_THEN_NONE;
    base_log("ingame: friend list upload done -> %d\r\n", r);
    if (then == IW_THEN_SAVED) {
        iw_set(&g_iw.edit, g_iw.up_token, r > 0 ? "saved" : requests_failed(r));
    } else if (then != IW_THEN_NONE && r <= 0) {
        wsprintfA(why, "failed:list %s", requests_failed(r));
        iw_send_result(g_iw.up_token, why, NULL);
    } else if (then == IW_THEN_REQUEST) {
        if (g_fx.nsends >= FX_SENDS) {
            iw_send_result(g_iw.up_token, "failed:busy", NULL);
            return;
        }
        g_fx.sends[g_fx.nsends++] = g_iw.then_send;
        iw_send_result(g_iw.up_token, "queued", NULL);
    } else if (then == IW_THEN_DECLINE) {
        if (g_fx.ans_state != FX_ANS_NONE) {
            iw_send_result(g_iw.up_token, "failed:busy", NULL);
            return;
        }
        fx_answer_want(&g_iw.then_decline, 0, g_iw.up_token);
    }
}

static void iw_comment_step(BYTE *b)
{
    int r;
    if (g_iw.comment_job < 0) return;
    g_iw_step = "comment";
    r = pc_comment_save_check(b, g_iw.comment_job);
    g_iw_step = "";
    if (r == 0) return;
    g_iw.comment_job = -1;
    base_log("ingame: my comment done -> %d\r\n", r);
    iw_set(&g_iw.edit, g_iw.up_token, r == 1 ? "saved" : requests_failed(r));
}

/* polcore's own status, svc 0x338, moves once the game's two changes have gone */
static void iw_status_step(BYTE *b)
{
    if (g_iw.status_want < 0) return;
    if (pc_my_status_or(b, -1) == g_iw.status_want || GetTickCount64() - g_iw.status_at >= IW_STATUS_MS) {
        if (pc_my_status_or(b, -1) != g_iw.status_want) base_log("ingame: the status change didn't show in 15s\r\n");
        g_iw.status_want = -1;
    }
}

static int iw_status(BYTE *b, char *buf)
{
    BYTE *obj, *entry;
    int want = buf[0] - '0';
    (void)b;
    if (want == 2) want = 0;
    if (want == 3) want = 4;
    if (want != 0 && want != 1 && want != 4) return 1;
    if (!(obj = *(BYTE **)pc_addr_of(g_fx.base, AT_FX_STATUS_OBJ)) || !(entry = *(BYTE **)(obj + 0x30)) || entry[1]) return 0;
    g_iw_step = "status";
    /* the game's own pair, as /away does it. The first is 0 with no game connection, and then the game changes nothing */
    if (!((iw_away_fn)pc_addr_of(g_fx.base, AT_FX_AWAY_FLAG))(1, want == 1)) {
        g_iw_step = "";
        return 0;
    }
    ((iw_status_fn)pc_addr_of(g_fx.base, AT_FX_STATUS_SET))(want);
    g_iw_step = "";
    g_iw.status_want = want;
    g_iw.status_at = GetTickCount64();
    base_log("ingame: status %d through the game's own setter\r\n", want);
    return 1;
}

static int iw_send(BYTE *b, char *buf)
{
    static char sj_text[0x1004];
    pol_friend_copy fr;
    fx_send *s;
    char *p = buf, *row_s, *tok, *who, *subj, *text, *q, frName[64];
    int row, n, wl;
    if (g_fx.nsends >= FX_SENDS) return 0;
    row_s = requests_line(&p); tok = requests_line(&p); who = requests_line(&p); subj = requests_line(&p);
    text = p;
    if (!requests_parse_num(row_s, 4, &row)) { iw_send_result(tok, "failed:bad request", NULL); return 1; }
    if (mail_send_pace_refusal()) { iw_send_result(tok, mail_send_pace_refusal(), NULL); return 1; }
    if (row >= 200 || !peers_get_friend(b, row, &fr)) { iw_send_result(tok, "failed:no such friend", NULL); return 1; }
    n = lstrlenA(fr.f.name);
    pol_utf8(b, fr.f.name, n > 15 ? 15 : n, frName, sizeof frName);
    if (lstrcmpA(frName, who)) { iw_send_result(tok, "failed:friend list changed", NULL); return 1; }
    if (fr.f.flags & FRIEND_PENDING) { iw_send_result(tok, "failed:pending", NULL); return 1; }
    if (peers_is_me(b, fr.f.id_lo, fr.f.id_hi)) { iw_send_result(tok, "failed:self", NULL); return 1; }
    wl = MultiByteToWideChar(CP_UTF8, 0, text, -1, NULL, 0);
    if (wl <= 0 || wl - 1 > TEXT_MAX_BYTES) { iw_send_result(tok, "failed:too long", NULL); return 1; }
    for (q = text; *q; q++) if ((BYTE)*q < 0x20 && *q != '\n') *q = ' ';
    for (q = subj; *q; q++) if ((BYTE)*q < 0x20) *q = ' ';
    n = pol_sjis(b, text, sj_text, 0x1000);
    if (!sj_text[0]) { iw_send_result(tok, "failed:empty", NULL); return 1; }
    if (n > TEXT_MAX_BYTES) { iw_send_result(tok, "failed:too long", NULL); return 1; }
    s = &g_fx.sends[g_fx.nsends++];
    memset(s, 0, sizeof *s);
    s->lo = fr.f.id_lo;
    s->hi = fr.f.id_hi;
    s->slot = pc_friend_slot(&fr.f);
    lstrcpynA(s->name, frName, sizeof s->name);
    lstrcpynA(s->text, sj_text, sizeof s->text);
    pol_sjis(b, subj, s->subj, 0x80);
    lstrcpynA(s->token, tok, sizeof s->token);
    base_log("ingame: a message from Provenance to row %d waits its turn\r\n", row);
    iw_send_result(tok, "queued", NULL);
    return 1;
}

static int iw_friend(BYTE *b, char *buf)
{
    char *p = buf, *op, *tok, *rows, *key, *arg, k[17], nm[16];
    int row, r, unignore, del, rename, ignore;
    pol_friend_copy fr;
    op = requests_line(&p); tok = requests_line(&p); rows = requests_line(&p); key = requests_line(&p); arg = requests_line(&p);
    unignore = !lstrcmpA(op, "unignore");
    del = !lstrcmpA(op, "delete");
    rename = !lstrcmpA(op, "rename");
    ignore = !lstrcmpA(op, "ignore");
    if (!requests_parse_num(rows, 4, &row) || !(unignore || del || rename || ignore)) {
        iw_set(&g_iw.edit, tok, "failed:bad request");
        return 1;
    }
    memset(&fr, 0, sizeof fr);
    if (unignore) {
        if (row >= 100 || pc_ignore_copy(b, row, &fr) < 0 || !pc_friend_valid(&fr.f)) { iw_set(&g_iw.edit, tok, "failed:no such entry"); return 1; }
        peers_friend_key(b, &fr.f, k);
        if (lstrcmpA(k, key)) { iw_set(&g_iw.edit, tok, "failed:list changed"); return 1; }
    } else {
        if (row >= 200 || !peers_get_friend(b, row, &fr)) { iw_set(&g_iw.edit, tok, "failed:no such friend"); return 1; }
        peers_friend_key(b, &fr.f, k);
        if (lstrcmpA(k, key)) { iw_set(&g_iw.edit, tok, "failed:friend list changed"); return 1; }
        if ((fr.f.flags & FRIEND_PENDING) && !del) { iw_set(&g_iw.edit, tok, "failed:pending"); return 1; }
        if (rename && !peers_ascii_name_ok(arg)) { iw_set(&g_iw.edit, tok, "failed:bad name"); return 1; }
        if (rename && !lstrcmpA(fr.f.name, arg)) { iw_set(&g_iw.edit, tok, "saved"); return 1; }
        if (ignore && peers_is_me(b, fr.f.id_lo, fr.f.id_hi)) { iw_set(&g_iw.edit, tok, "failed:self"); return 1; }
    }
    if (!iw_edit_ready(b) || iw_arm(tok, IW_THEN_SAVED)) return 0;
    iw_set(&g_iw.edit, tok, "saving");
    g_iw_step = "friend edit";
    if (unignore) {
        r = pc_ignore_remove(b, row);
    } else if (del) {
        r = pc_friend_delete(b, row);
    } else if (rename) {
        /* read and written back in the one callback, so no presence push lands between */
        memset(fr.f.name, 0, sizeof fr.f.name);
        lstrcpynA(fr.f.name, arg, 16);
        pc_set_friend_row(b, row, &fr);
        r = 0;
    } else {
        memcpy(nm, fr.f.name, 15);
        nm[15] = 0;
        r = pc_ignore_add(b, -1, arg[0] == '1' ? 1u : 0u, fr.f.id_lo, fr.f.id_hi, (unsigned)pc_friend_slot(&fr.f), nm);
    }
    g_iw_step = "";
    base_log("ingame: %s row %d -> %d\r\n", op, row, r);
    if (r < 0) iw_edit_failed(r);
    return 1;
}

static int iw_comment(BYTE *b, char *buf)
{
    WCHAR want[0x33];
    char *p = buf, *tok, *e;
    int t;
    tok = requests_line(&p);
    for (e = p + lstrlenA(p); e > p && (e[-1] == '\n' || e[-1] == '\r'); ) *--e = 0;
    memset(want, 0, sizeof want);
    if (!tok[0] || (*p && !friends_comment_text(p, want))) {
        iw_set(&g_iw.edit, tok, "failed:bad text");
        return 1;
    }
    if (!iw_world(b) || !iw_slots(b) || !iw_mine_idle()) return 0;
    g_iw_step = "comment";
    t = pc_comment_save(b, -2, want[0] ? want : NULL);
    g_iw_step = "";
    if (t == NO_SLOT) return 0;
    base_log("ingame: my comment start -> %d\r\n", t);
    lstrcpynA(g_iw.up_token, tok, sizeof g_iw.up_token);
    if (t < 0) {
        iw_set(&g_iw.edit, tok, requests_failed(t));
        return 1;
    }
    g_iw.comment_job = t;
    iw_set(&g_iw.edit, tok, "saving");
    return 1;
}

static int iw_waiting(const BYTE *hdr, fx_request *out)
{
    int i, found = 0;
    if (!fx_req_lock()) return 0;
    for (i = 0; i < FX_WAITING && !found; i++) {
        const fx_request *w = &g_fx.waiting[i];
        if (!w->used || w->lo != MAIL(hdr)->from_lo || w->hi != MAIL(hdr)->from_hi || w->slot != MAIL(hdr)->from_slot ||
            w->seq != MAIL(hdr)->seq || w->time != MAIL(hdr)->time)
            continue;
        *out = *w;
        found = 1;
    }
    fx_req_unlock();
    return found;
}

/* a request from the hold's list, by its mid. Answers go through the game's own accept and decline, so its file moves to
   r\a the way the game's menu would do it, and the menu can't answer it again */
static int iw_answer(BYTE *b, char *buf)
{
    char *p = buf, *op, *tok, *mids, *subj, *arg, *hkey, k[17], nm[16];
    int mid, r, yes;
    mailrec *m;
    fx_request req;
    const srchrec *h;
    fx_send *s = &g_iw.then_send;
    op = requests_line(&p); tok = requests_line(&p); mids = requests_line(&p); subj = requests_line(&p);
    arg = requests_line(&p); hkey = requests_line(&p);
    if (mail_send_pace_refusal()) { iw_answer_result(tok, mail_send_pace_refusal()); return 1; }
    if (!requests_parse_num(mids, 6, &mid)) { iw_answer_result(tok, "failed:bad request"); return 1; }
    if (!lstrcmpA(op, "request")) {
        if (mid >= g_s.search_n || g_search.step != 0) { iw_answer_result(tok, "failed:no such result"); return 1; }
        h = &g_srch[mid];
        peers_key(b, h->lo, h->hi, (unsigned)h->slot, k);
        if (lstrcmpA(k, hkey)) { iw_answer_result(tok, "failed:no such result"); return 1; }
        if (peers_is_me(b, h->lo, h->hi)) { iw_answer_result(tok, "failed:self"); return 1; }
        if (peers_find_friend_row(b, h->lo, h->hi, h->slot) >= 0) { iw_answer_result(tok, "failed:already a friend"); return 1; }
        if (!iw_edit_ready(b) || g_fx.nsends >= FX_SENDS || iw_arm(tok, IW_THEN_REQUEST)) return 0;
        memset(s, 0, sizeof *s);
        s->lo = h->lo;
        s->hi = h->hi;
        s->slot = h->slot;
        s->kind = 1;
        memcpy(s->name, h->name, 15);
        pol_sjis(b, subj, s->subj, 0x80);
        pol_sjis(b, arg, s->text, 0x80);
        lstrcpynA(s->token, tok, sizeof s->token);
        iw_answer_result(tok, "saving");
        g_iw_step = "friend add";
        r = pc_friend_add(b, -1, 0, h->lo, h->hi, (unsigned)h->slot, h->name);
        g_iw_step = "";
        base_log("ingame: request to search hit %d -> AddFriend %d\r\n", mid, r);
        if (r < 0) {
            g_iw.then = IW_THEN_NONE;
            iw_answer_result(tok, requests_failed(r));
        }
        return 1;
    }
    yes = !lstrcmpA(op, "accept");
    if (!yes && lstrcmpA(op, "decline") && lstrcmpA(op, "ignore")) { iw_answer_result(tok, "failed:bad request"); return 1; }
    if (!(m = mail_inbox_by_mid(mid)) || pc_mail_kind(m->hdr) != 1 || !iw_waiting(m->hdr, &req)) {
        iw_answer_result(tok, "failed:no such request");
        return 1;
    }
    if (lstrcmpA(op, "decline") && peers_is_me(b, MAIL(m->hdr)->from_lo, MAIL(m->hdr)->from_hi)) {
        iw_answer_result(tok, "failed:self");
        return 1;
    }
    if (g_fx.ans_state != FX_ANS_NONE) return 0;
    if (lstrcmpA(op, "ignore")) {
        fx_answer_want(&req, yes, tok);
        iw_answer_result(tok, "sending");
        return 1;
    }
    if (!iw_edit_ready(b) || iw_arm(tok, IW_THEN_DECLINE)) return 0;
    g_iw.then_decline = req;
    memcpy(nm, MAIL(m->hdr)->name, 15);
    nm[15] = 0;
    iw_answer_result(tok, "saving");
    g_iw_step = "ignore";
    r = pc_ignore_add(b, -1, 0u, MAIL(m->hdr)->from_lo, MAIL(m->hdr)->from_hi, MAIL(m->hdr)->from_slot, nm);
    g_iw_step = "";
    base_log("ingame: ignore requester mid %d -> AddBlock %d\r\n", mid, r);
    if (r < 0) {
        g_iw.then = IW_THEN_NONE;
        iw_answer_result(tok, r == ALREADY_LISTED ? "failed:already ignored" : requests_failed(r));
    }
    return 1;
}

static int iw_search(BYTE *b, char *buf)
{
    (void)buf;
    if (!iw_world(b) || !iw_slots(b) || !iw_mine_idle()) return 0;
    g_iw_step = "search";
    profiles_search_start(b, &g_search);
    g_iw_step = "";
    iw_changed();
    return 1;
}

static void iw_refuse(int k, char *buf, const char *why)
{
    char *p = buf, *l1 = requests_line(&p), *l2 = requests_line(&p);
    if (k == IW_SEND || k == IW_ANSWER) iw_send_result(l2, why, NULL);
    else if (k == IW_FRIEND) iw_set(&g_iw.edit, l2, why);
    else if (k == IW_COMMENT) iw_set(&g_iw.edit, l1, why);
    else if (k == IW_SEARCH) {
        lstrcpynA(g_s.search_token, l1, sizeof g_s.search_token);
        lstrcpynA(g_s.search_state, why, sizeof g_s.search_state);
        iw_changed();
    }
}

static const int g_iw_areas[IW_BOXES] = { IW_AREA_SEND, IW_AREA_STATUS, IW_AREA_EDITS, IW_AREA_EDITS, IW_AREA_EDITS, IW_AREA_EDITS };

static void iw_take_boxes(BYTE *b)
{
    static int (*const take[IW_BOXES])(BYTE *, char *) = { iw_send, iw_status, iw_friend, iw_comment, iw_answer, iw_search };
    static const char *const steps[IW_BOXES] = { "send", "status", "friend edit", "comment", "answer", "search" };
    /* the lines are cut up as they're read, and one that has to wait is read again next frame */
    static char line[IW_TEXT];
    int k, world = -1;
    for (k = 0; k < IW_BOXES; k++) {
        if (!g_iw.full[k]) continue;
        if (world < 0) world = iw_world(b);
        g_iw_step = steps[k];
        lstrcpynA(line, g_iw.box[k], sizeof line);
        /* taken before FFXiMain was looked at, and what it needs isn't there */
        if (!iw_area(g_iw_areas[k])) {
            iw_refuse(k, line, "failed:ingame");
            InterlockedExchange(&g_iw.full[k], 0);
        } else if (world && take[k](b, line)) {
            InterlockedExchange(&g_iw.full[k], 0);
        } else if (GetTickCount64() - g_iw.wait_at[k] >= IW_WAIT_MS) {
            base_log("ingame: a %s from Provenance waited too long and was let go\r\n", steps[k]);
            lstrcpynA(line, g_iw.box[k], sizeof line);
            iw_refuse(k, line, "failed:busy");
            InterlockedExchange(&g_iw.full[k], 0);
        }
        g_iw_step = "";
    }
}

/* game thread. Jobs already going are seen through to the end, the slot's only given back by the poll that sees it end */
static void iw_game(BYTE *b)
{
    int k;
    if (g_fx.found) iw_upload_step();
    iw_comment_step(b);
    if (g_search.step) {
        g_iw_step = "search";
        profiles_search_pump(b, 0);
        g_iw_step = "";
        if (!g_search.step) iw_changed();
    }
    iw_status_step(b);
    if (!g_fx.found && iw_base()) return;
    if (g_fx.found && !g_fx.off && !g_ig.off && !g_iw.dead) {
        iw_take_boxes(b);
        return;
    }
    for (k = 0; k < IW_BOXES; k++)
        if (g_iw.full[k]) {
            iw_refuse(k, g_iw.box[k], "failed:ingame");
            InterlockedExchange(&g_iw.full[k], 0);
        }
}

static void iw_poll(BYTE *b)
{
    if (!iw_try_lock()) return;
    fx_poll(b);
    {
        __try {
            iw_game(b);
        } __except (fx_caught(GetExceptionInformation())) {
            /* a second fault means the wind-down's what faulted, so the jobs it was seeing through are let go */
            if (g_iw.dead || g_fx.off) {
                g_iw.comment_job = -1;
                g_search.step = 0;
                g_search.job = -1;
            }
            g_iw.dead = 1;
            fx_fault("on a request from Provenance", g_iw_step);
            g_iw_step = "";
        }
    }
    iw_unlock();
}

static void iw_worker(BYTE *b)
{
    static const struct { const char *ext; int area; } rows[] = {
        { REQ_SEND, IW_AREA_SEND }, { REQ_STATUS, IW_AREA_STATUS }, { REQ_FRIEND, IW_AREA_EDITS }, { REQ_COMMENT, IW_AREA_EDITS },
        { REQ_ANSWER, IW_AREA_EDITS }, { REQ_SEARCH, IW_AREA_EDITS },
    };
    static char buf[IW_TEXT];
    int k, ok;
    for (k = 0; k < IW_BOXES; k++) {
        if (g_iw.full[k] || !requests_waiting(rows[k].ext)) continue;
        ok = iw_area(rows[k].area);
        if (requests_take(rows[k].ext, ok ? g_iw.box[k] : buf, IW_TEXT) < 0) continue;
        if (!ok || k == IW_SEARCH) {
            if (!iw_lock()) {
                base_log("ingame: a %s request was let go, the game's thread held on\r\n", rows[k].ext);
                continue;
            }
            if (!ok) {
                base_log("ingame: a %s request was turned down, that can't be done in the game this time\r\n", rows[k].ext);
                iw_refuse(k, buf, "failed:ingame");
            } else {
                ok = !g_search.step && profiles_search_parse(b, g_iw.box[k]);
                if (!ok && g_search.step) iw_refuse(k, g_iw.box[k], "failed:busy");
                iw_changed();
            }
            iw_unlock();
            if (!ok) continue;
        }
        g_iw.wait_at[k] = GetTickCount64();
        InterlockedExchange(&g_iw.full[k], 1);
    }
}

static void iw_snapshot(BYTE *b, jw *w, const ig_copy *c)
{
    static iw_job send, edit;
    static send_hist hist[SEND_HIST];
    static srchrec hits[SEARCH_MAX];
    static char stoken[48], sstate[32];
    char u8[0x33 * 3 + 1], mine[64];
    int i, k, next, busy, n, kind, total, many;
    if (!iw_lock()) return;
    send = g_iw.send;
    edit = g_iw.edit;
    memcpy(hist, g_iw.hist, sizeof hist);
    next = g_iw.hist_next;
    busy = g_iw.full[IW_STATUS] || g_iw.status_want >= 0;
    lstrcpynA(stoken, g_s.search_token, sizeof stoken);
    lstrcpynA(sstate, g_s.search_state, sizeof sstate);
    n = g_search.step ? 0 : g_s.search_n;
    kind = g_s.search_kind;
    total = g_s.search_total;
    many = g_s.search_many;
    memcpy(hits, g_srch, (size_t)n * sizeof hits[0]);
    iw_unlock();
    jw_obj(w, "gameWrites");
    jw_bool(w, "send", iw_area(IW_AREA_SEND));
    jw_bool(w, "status", iw_area(IW_AREA_STATUS));
    jw_bool(w, "edits", iw_area(IW_AREA_EDITS));
    jw_end(w);
    jw_bool(w, "statusBusy", busy);
    u8[0] = 0;
    WideCharToMultiByte(CP_UTF8, 0, c->comment, -1, u8, (int)sizeof u8 - 1, NULL, NULL);
    u8[sizeof u8 - 1] = 0;
    jw_str(w, "myComment", u8, 0x33 * 6 + 8);
    jw_job(w, "send", send.token, send.state);
    jw_end(w);
    jw_arr(w, "sends");
    for (i = 0; i < SEND_HIST; i++) {
        k = (next + i) % SEND_HIST;
        if (!hist[k].token[0]) continue;
        jw_obj(w, NULL);
        jw_str(w, "token", hist[k].token, 100);
        jw_str(w, "state", hist[k].state, SNAPSHOT_STATE_CAP);
        jw_uint(w, "time", hist[k].time);
        jw_uint(w, "seq", hist[k].seq);
        if (hist[k].from_slot >= 0) {
            jw_int(w, "fromSlot", hist[k].from_slot);
            jw_bool(w, "toOther", mail_inbox_polid_ok(b, hist[k].to_lo, hist[k].to_hi) && !peers_is_me(b, hist[k].to_lo, hist[k].to_hi));
        }
        jw_end(w);
    }
    jw_end(w);
    jw_job(w, "edit", edit.token, edit.state);
    jw_end(w);
    peers_polid_text(b, c->me, mine);
    jw_obj(w, "search");
    jw_str(w, "token", stoken, 100);
    jw_lit(w, "state", sstate);
    jw_lit(w, "kind", g_srch_kinds[kind]);
    jw_int(w, "total", total);
    jw_bool(w, "tooMany", many);
    jw_arr(w, "results");
    for (i = 0; i < n; i++) {
        const srchrec *h = &hits[i];
        int is_friend = 0, row;
        char key[17];
        for (row = 0; row < IG_FRIENDS && !is_friend; row++)
            is_friend = pc_friend_valid(&c->friends[row]) && c->friends[row].id_lo == h->lo && c->friends[row].id_hi == h->hi &&
                        pc_friend_slot(&c->friends[row]) == h->slot;
        peers_key(b, h->lo, h->hi, (unsigned)h->slot, key);
        jw_obj(w, NULL);
        jw_int(w, "index", i);
        jw_sjis(w, "name", b, (const BYTE *)(kind == SK_CHARACTER ? h->chname : h->name), 0x0f, SNAPSHOT_NAME_CAP);
        jw_sjis(w, "handle", b, (const BYTE *)h->name, 0x0f, SNAPSHOT_NAME_CAP);
        jw_sjis(w, "world", b, (const BYTE *)h->world, 0x0f, SNAPSHOT_NAME_CAP);
        jw_uint(w, "pic", h->pic);
        jw_lit(w, "key", key);
        jw_bool(w, "friend", is_friend);
        jw_bool(w, "me", peers_is_polid(b, mine, h->lo, h->hi));
        jw_end(w);
    }
    jw_end(w);
    jw_end(w);
}
