/* POL chat rooms: the zone and room lists are database searches, a room is IRC on the link polcore already holds.
   One command in flight and every step waits for the last, like the viewer */

static int cr_new_key_ok(const char *k);
static int gc_busy(void);

static void cr_raw_pump(BYTE *b)
{
    static WCHAR w[0x220];
    LONG n = g_cr_raw_n;
    for (LONG i = 0; i < n; i++) {
        if (!g_cr_open || (!g_cr_raw[i].whisper && lstrcmpiA(g_cr_raw[i].chan, g_cr_room) != 0)) continue;
        cr_wide(b, g_cr_raw[i].text, w, 0x220);
        cr_parse(b, w, g_cr_raw[i].lo, g_cr_raw[i].hi, g_cr_raw[i].whisper);
    }
    g_cr_raw_n = 0;
    if (g_cr_raw_lost) {
        lg("chat: %d room line(s) dropped, too many at once\r\n", g_cr_raw_lost);
        g_cr_raw_lost = 0;
    }
}

/* polcore's member array for the room, local only. 0 when it isn't in the room any more */
static int cr_members(BYTE *b)
{
    int s = cr_session(b), seen[CR_PEOPLE] = {0};
    if (s < 0 || !SVC(0x280)) return 0;
    memset(g_cr_slot, 0, sizeof g_cr_slot);
    memset(g_cr_mem, 0, sizeof g_cr_mem);
    if (!((int (__cdecl *)(int, const char *, BYTE *, BYTE *))SVC(0x280))(s, g_cr_room, g_cr_slot, g_cr_mem)) return 0;
    for (int i = 0; i < 0x40; i++) {
        BYTE *m = g_cr_mem + i * 16;
        cr_person *p;
        if (!(m[0xc] & 1) || !(p = cr_person_for(*(DWORD *)m, *(DWORD *)(m + 4), 1))) continue;
        seen[p - g_cr_people] = 1;
        p->member = 1;
        p->master = (m[0xc] & 2) != 0;
    }
    for (int i = 0; i < g_cr_people_n; i++) {
        cr_person *p = &g_cr_people[i];
        if (seen[i] || !p->member) continue;
        p->member = p->master = p->answered = 0;
        if (!cr_is_me(p->lo, p->hi)) cr_add_line(CRK_LEFT, 0, p, p->name, NULL);
    }
    g_snap_dirty = 1;
    return 1;
}

static void cr_room_clear(void)
{
    g_cr_open = g_cr_hello = g_cr_replies = g_cr_reread = g_cr_skip_topic = g_cr_kicked_me = 0;
    g_cr_people_n = g_cr_next_key = 0;
    g_cr_raw_n = 0;
    memset(g_cr_slot, 0, sizeof g_cr_slot);
}
static void cr_room_state(const char *state)
{
    lstrcpynA(g_cr_state, state, sizeof g_cr_state);
    lg("chat: room %s\r\n", state);
    g_snap_dirty = 1;
}
static void cr_send_state(const char *state)
{
    lstrcpynA(g_cr_sstate, state, sizeof g_cr_sstate);
    g_snap_dirty = 1;
}

#define CR_NOTHING_TO_LEAVE (-0x7e01)
/* the only place a PART goes from. The window's leave and Play's both come through here, and the PART that goes
   clears g_cr_joined, so whichever's second finds nothing to leave */
static int cr_part(BYTE *b)
{
    int s = cr_session(b), r;
    if (!g_cr_joined) return CR_NOTHING_TO_LEAVE;
    if (s < 0) return -0x41f;
    r = ((int (__cdecl *)(int, const char *))SVC(0x260))(s, g_cr_room);
    if (r > 0) g_cr_joined = g_cr_open = 0;
    return r;
}
/* MODE: 0xb 'n', 8 'k' (arg the password), 9 'l' (arg the limit as text), 4 'b' and 0xc 'o' on a member (lo, hi).
   polcore answers one that'd change nothing straight away */
static int cr_mode(BYTE *b, int mode, DWORD lo, DWORD hi, const char *arg, int on)
{
    int s = cr_session(b);
    if (s < 0) return -0x41f;
    return ((int (__cdecl *)(int, int, const char *, DWORD, DWORD, const char *, int))SVC(0x258))(s, mode, g_cr_room, lo,
                                                                                                  hi, arg, on);
}
static int cr_command(BYTE *b, int step)
{
    int s = cr_session(b), r = 0;
    if (s < 0) return -0x41f;
    switch (step) {
    case CR_AWAY: r = ((svc_i)SVC(0x234))(s); break;
    case CR_JOIN:
        r = ((int (__cdecl *)(int, const char *, const char *))SVC(0x240))(s, g_cr_room, g_cr_key);
        if (r > 0) g_cr_joined = 1;
        break;
    case CR_WHO: r = ((int (__cdecl *)(int, const char *))SVC(0x228))(s, g_cr_room); break;
    case CR_TOPIC: r = ((int (__cdecl *)(int, const char *, const char *))SVC(0x254))(s, g_cr_room, NULL); break;
    case CR_PART: r = cr_part(b); break;
    case CR_SETTOPIC: r = ((int (__cdecl *)(int, const char *, const char *))SVC(0x254))(s, g_cr_room, g_cr_new_topic); break;
    case CR_MODE_N: r = cr_mode(b, 0xb, 0, 0, NULL, 1); break;
    case CR_MODE_K: r = cr_mode(b, 8, 0, 0, g_cr_key, 1); break;
    case CR_MODE_L: r = cr_mode(b, 9, 0, 0, g_cr_new_limit, 1); break;
    }
    return r;
}
static const char *cr_join_failed(int r)
{
    switch (r) {
    case -0x11db: return "failed:refused";
    case -0x11dc: return "failed:password";
    case -0x11d8: return "failed:full";
    case -0x1194: return "failed:closed";
    }
    return failed(r);
}
static void cr_go(int step)
{
    g_cr_st = step;
    g_cr_tries = 0;
}
/* a new room's AWAY and JOIN only get the 5 */
static int cr_tries_for(int step)
{
    return step == CR_PART || (!g_cr_making && (step == CR_AWAY || step == CR_JOIN)) ? CR_TRIES_ENTER : CR_TRIES;
}
static void cr_master_end(const char *state)
{
    /* the old password came off and the new one didn't go on, so the room's open now */
    if (lstrcmpA(state, "done") && lstrcmpA(state, "failed:left room") && g_cr_mop == MO_KEY && g_cr_mi > 0 &&
        g_cr_msteps[0] == MS_UNKEY && g_cr_msteps[g_cr_mi] == MS_KEY) {
        SecureZeroMemory(g_cr_key, sizeof g_cr_key);
        state = "failed:unlocked";
    }
    lstrcpynA(g_cr_mstate, state, sizeof g_cr_mstate);
    lg("chat: master job %d %s\r\n", g_cr_mop, state);
    g_cr_mop = MO_NONE;
    g_cr_mn = g_cr_mi = 0;
    SecureZeroMemory(g_cr_newkey, sizeof g_cr_newkey);
    g_snap_dirty = 1;
}
static char g_cr_part_state[32];   /* what a PART that's already been reported ends in, "left" when empty */
static void cr_left(const char *state)
{
    if (lstrcmpA(g_cr_sstate, "sending") == 0) cr_send_state("failed:left");
    g_cr_msg_kind = -1;
    g_cr_wait = CRW_NONE;
    g_cr_joined = 0;
    g_cr_making = 0;
    if (g_cr_mop) cr_master_end("failed:left room");
    cr_room_clear();
    SecureZeroMemory(g_cr_key, sizeof g_cr_key);
    SecureZeroMemory(g_cr_newkey, sizeof g_cr_newkey);
    g_cr_leave = 0;
    cr_go(CR_OUT);
    cr_room_state(state);
    g_cr_part_state[0] = 0;
}
/* a step's answer: >0 done, <0 the server's error (or the link's), from svc 0x244 */
static void cr_step_done(BYTE *b, int r)
{
    int st = g_cr_st;
    g_cr_wait = CRW_NONE;
    if (st == CR_PART) { cr_left(g_cr_part_state[0] ? g_cr_part_state : "left"); return; }
    if (st == CR_AWAY) {
        if (r < 0) lg("chat: AWAY answered %d, entering anyway\r\n", r);
        if (g_cr_leave) { cr_left("left"); return; }
        cr_go(CR_JOIN);
        return;
    }
    if (st == CR_JOIN) {
        if (r < 0) { cr_left(cr_join_failed(r)); return; }
        g_cr_open = 1;
        cr_go(g_cr_leave ? CR_PART : CR_WHO);
        return;
    }
    if (st >= CR_SETTOPIC) {
        /* a room that didn't get its settings isn't the room that was asked for, so it's left */
        if (r < 0) {
            lg("chat: new room step %d answered %d, leaving it\r\n", st, r);
            wsprintfA(g_cr_part_state, "failed:create %d", r);
            cr_room_state(g_cr_part_state);
            g_cr_leave = 1;
            cr_go(CR_PART);
            return;
        }
        cr_go(g_cr_leave ? CR_PART : st == CR_SETTOPIC ? CR_MODE_N : st == CR_MODE_N && g_cr_key[0] ? CR_MODE_K
              : st == CR_MODE_L ? CR_IN : CR_MODE_L);
        if (g_cr_st == CR_IN) cr_room_state("in");
        return;
    }
    if (st == CR_WHO) {
        if (r < 0) lg("chat: WHO answered %d\r\n", r);
        cr_members(b);
        if (g_cr_making) {
            /* the viewer's hello goes before the new room's settings (0x31, then 0x32 on) */
            g_cr_hello = !g_cr_leave;
            cr_go(g_cr_leave ? CR_PART : CR_SETTOPIC);
            return;
        }
        /* alone and room master: a room nobody else is in, so the viewer skips the TOPIC query */
        {
            int n = 0, master = 0;
            for (int i = 0; i < g_cr_people_n; i++)
                if (g_cr_people[i].member) {
                    n++;
                    if (g_cr_people[i].master && cr_is_me(g_cr_people[i].lo, g_cr_people[i].hi)) master = 1;
                }
            g_cr_skip_topic = n < 2 && master;
        }
        cr_go(g_cr_leave ? CR_PART : g_cr_skip_topic ? CR_IN : CR_TOPIC);
    } else if (st == CR_TOPIC) {
        if (r < 0) lg("chat: TOPIC answered %d\r\n", r);
        cr_go(g_cr_leave ? CR_PART : CR_IN);
    }
    if (g_cr_st == CR_IN) {
        g_cr_hello = 1;
        cr_room_state("in");
    }
}

static void cr_master_next(void);
static int cr_privmsg(BYTE *b, const char *frame)
{
    int s = cr_session(b);
    if (s < 0) return -0x41f;
    return ((int (__cdecl *)(int, DWORD, DWORD, const char *, const char *))SVC(0x22c))(s, 0, 0, g_cr_room, frame);
}

/* one PRIVMSG in flight */
static int cr_msg_ready(void)
{
    return (g_cr_st == CR_IN || g_cr_st == CR_SETTOPIC) && g_cr_wait == CRW_NONE && !g_cr_leave;
}
static void cr_msg_start(BYTE *b, int kind)
{
    int r = cr_privmsg(b, g_cr_frame);
    if (r == 0) {
        if (++g_cr_tries < (kind == CRM_SAY ? CR_TRIES : CR_TRIES_FRAME)) { g_cr_msg_kind = kind; return; }
        r = -1;
    }
    g_cr_tries = 0;
    if (r < 0) {
        g_cr_msg_kind = -1;
        lg("chat: PRIVMSG (%d) refused %d\r\n", kind, r);
        if (kind == CRM_SAY) cr_send_state("failed:busy");
        if (kind >= CRM_KEY) cr_master_next();
        return;
    }
    g_cr_msg_kind = kind;
    g_cr_wait = CRW_MSG;
    g_cr_sent_at = GetTickCount64();
    if (kind == CRM_SAY) {
        static WCHAR w[0x220];
        cr_wide(b, g_cr_frame, w, 0x220);
        {
            int tab = cr_tab(w, lstrlenW(w), 6);
            cr_line *l = cr_add_line(CRK_SAY, 1, NULL, g_cr_nick, tab >= 0 ? w + tab + 3 : L"");
            l->colour = g_cr_colour < 0 ? 0 : g_cr_colour + 5;
            l->emote = g_cr_emote;
        }
    }
}
static void cr_msg_done(int r, const char *why)
{
    int kind = g_cr_msg_kind;
    g_cr_wait = CRW_NONE;
    g_cr_msg_kind = -1;
    if (kind == CRM_SAY) cr_send_state(why ? why : r > 0 ? "sent" : failed(r));
    else if (why || r < 0) lg("chat: frame %d answered %d\r\n", kind, r);
    /* like the viewer, a frame that didn't go doesn't stop the job */
    if (kind >= CRM_KEY) cr_master_next();
}

static void cr_send_request(BYTE *b)
{
    char buf[0x800], *p, *tok, *text, *colour, *emote;
    static WCHAR w[0x220], t[0x220];
    int n;
    if (take_request(REQ_CHATSEND, buf, sizeof buf) < 0) return;
    g_cr_used = 1;
    p = buf;
    tok = req_line(&p);
    text = req_line(&p);
    colour = req_line(&p);
    emote = req_line(&p);
    /* the viewer's 12 colours and the 23 faces of its first page, anything else goes as none */
    g_cr_colour = parse_num(colour, 2, &n) && n < 12 ? n : -1;
    g_cr_emote = parse_num(emote, 2, &n) && n < 24 ? n : 0;
    lstrcpynA(g_cr_stok, tok, sizeof g_cr_stok);
    if (g_cr_st != CR_IN || !g_cr_open || g_cr_leave) { cr_send_state("failed:not in a room"); return; }
    if (!text[0]) { cr_send_state("failed:empty"); return; }
    if ((n = MultiByteToWideChar(CP_UTF8, 0, text, -1, t, 0x200)) <= 1 || n - 1 > CR_TEXT_MAX) {
        cr_send_state("failed:too long");
        return;
    }
    for (int i = 0; t[i]; i++) if (t[i] < 0x20) t[i] = L' ';
    cr_frame_say(b, t, w, 0x220);
    if ((n = cr_sjis(b, w, g_cr_frame, sizeof g_cr_frame)) < 0 || !cr_fits(n)) { cr_send_state("failed:too long"); return; }
    cr_send_state("sending");
    g_cr_tries = 0;
    cr_msg_start(b, CRM_SAY);
}
static void cr_frame_out(BYTE *b, int type)
{
    static WCHAR w[0x100];
    cr_frame_hello(b, type, w);
    if (cr_sjis(b, w, g_cr_frame, sizeof g_cr_frame) < 0) { lg("chat: frame %d doesn't convert\r\n", type); return; }
    g_cr_tries = 0;
    cr_msg_start(b, type);
}

/* 7, a and b: type, "00000", the language and the rest (a name or a password) */
static void cr_frame_tail(BYTE *b, int type, const WCHAR *tail)
{
    static WCHAR w[0x80];
    int lang = SVC(0xf18) ? ((svc_v)SVC(0xf18))() : 0;
    wsprintfW(w, L"%x00000%02x", type, lang & 0xff);
    lstrcpynW(w + 8, tail, 0x40);
    if (cr_sjis(b, w, g_cr_frame, sizeof g_cr_frame) < 0) {
        lg("chat: frame %d doesn't convert\r\n", type);
        cr_master_next();
        return;
    }
    g_cr_tries = 0;
    cr_msg_start(b, type);
}

/* the room master's kick, designate and password change (app.dll 0x1009ba31). Each step is one command or one frame,
   and each waits for the last */
static void cr_master_next(void)
{
    if (!g_cr_mop) return;
    if (++g_cr_mi >= g_cr_mn) {
        if (g_cr_mop == MO_KEY) lstrcpynA(g_cr_key, g_cr_newkey, sizeof g_cr_key);
        cr_master_end("done");
    }
}
static void cr_master_step(BYTE *b)
{
    WCHAR k[32];
    int r = 0;
    switch (g_cr_msteps[g_cr_mi]) {
    case MS_BAN: r = cr_mode(b, 4, g_cr_mlo, g_cr_mhi, NULL, 1); break;
    case MS_KICK: {
        int s = cr_session(b);
        r = s < 0 ? -0x41f : ((int (__cdecl *)(int, const char *, DWORD, DWORD))SVC(0x238))(s, g_cr_room, g_cr_mlo, g_cr_mhi);
        break;
    }
    case MS_OP: r = cr_mode(b, 0xc, g_cr_mlo, g_cr_mhi, NULL, 1); break;
    case MS_UNKEY: r = cr_mode(b, 8, 0, 0, g_cr_key, 0); break;
    case MS_KEY: r = cr_mode(b, 8, 0, 0, g_cr_newkey, 1); break;
    /* the viewer shows its own line for the kick and the new master as the frame goes */
    case MS_KICKFRAME:
        cr_add_line(CRK_KICKED, 0, NULL, g_cr_mname, NULL);
        cr_frame_tail(b, CRM_KICK, g_cr_mname);
        return;
    case MS_OPFRAME:
        cr_add_line(CRK_MASTER, 0, NULL, g_cr_mname, NULL);
        cr_frame_tail(b, CRM_MASTER, g_cr_mname);
        return;
    case MS_KEYFRAME:
        MultiByteToWideChar(CP_ACP, 0, g_cr_newkey, -1, k, 32);
        cr_frame_tail(b, CRM_KEY, k);
        SecureZeroMemory(k, sizeof k);
        return;
    }
    if (r == 0) {
        if (++g_cr_tries < CR_TRIES) return;
        r = -1;
    }
    g_cr_tries = 0;
    if (r < 0) { cr_master_end(failed(r)); return; }
    g_cr_wait = CRW_CMD;
    g_cr_sent_at = GetTickCount64();
}
/* 401 (no such nick): the member's already gone */
static void cr_master_done(BYTE *b, int r)
{
    g_cr_wait = CRW_NONE;
    if (r < 0) { cr_master_end(r == -0x1192 ? "failed:left" : failed(r)); return; }
    cr_master_next();
}
static void cr_master_refused(const char *state)
{
    SecureZeroMemory(g_cr_newkey, sizeof g_cr_newkey);
    lstrcpynA(g_cr_mstate, state, sizeof g_cr_mstate);
    lg("chat: master request refused - %s\r\n", state);
    g_snap_dirty = 1;
}
static int cr_password(const char *tok, char *key, int cap);
/* <token>, <1 when there's a new password in Credential Manager>, kick|master|password, <member key> */
static void cr_master_request(BYTE *b)
{
    char buf[256], *p, *tok, *haskey, *op, *key, nk[sizeof g_cr_newkey];
    cr_person *me = NULL, *who = NULL;
    int k = -1, pw = 0;
    if (take_request(REQ_CHATMASTER, buf, sizeof buf) < 0) return;
    p = buf;
    tok = req_line(&p); haskey = req_line(&p); op = req_line(&p); key = req_line(&p);
    nk[0] = 0;
    if (haskey[0]) pw = cr_password(tok, nk, sizeof nk);
    if (g_cr_mop) {
        SecureZeroMemory(nk, sizeof nk);
        lg("chat: master request ignored, one's still going\r\n");
        return;
    }
    lstrcpynA(g_cr_mtok, tok, sizeof g_cr_mtok);
    lstrcpynA(g_cr_newkey, nk, sizeof g_cr_newkey);
    SecureZeroMemory(nk, sizeof nk);
    if (g_cr_st != CR_IN || g_cr_leave) { cr_master_refused("failed:not in a room"); return; }
    for (int i = 0; i < g_cr_people_n; i++) {
        cr_person *q = &g_cr_people[i];
        if (!q->member) continue;
        if (cr_is_me(q->lo, q->hi)) me = q;
        else if (parse_num(key, 9, &k) && q->key == k) who = q;
    }
    if (!me || !me->master) { cr_master_refused("failed:not master"); return; }
    g_cr_mn = g_cr_mi = 0;
    if (!lstrcmpA(op, "password")) {
        if (haskey[0] && (!pw || !cr_new_key_ok(g_cr_newkey))) { cr_master_refused("failed:no password"); return; }
        if (*(DWORD *)(g_cr_slot + 0x8c) & (1u << 8)) g_cr_msteps[g_cr_mn++] = MS_UNKEY;
        if (haskey[0]) { g_cr_msteps[g_cr_mn++] = MS_KEY; g_cr_msteps[g_cr_mn++] = MS_KEYFRAME; }
        if (!g_cr_mn) { cr_master_refused("done"); return; }
        g_cr_mop = MO_KEY;
    } else if (!lstrcmpA(op, "kick") || !lstrcmpA(op, "master")) {
        BYTE *m = NULL;
        if (!who) { cr_master_refused("failed:left"); return; }
        g_cr_mlo = who->lo;
        g_cr_mhi = who->hi;
        lstrcpynW(g_cr_mname, who->name, 0x21);
        if (op[0] == 'k') {
            for (int i = 0; i < 0x40; i++)
                if ((g_cr_mem[i * 16 + 0xc] & 1) && *(DWORD *)(g_cr_mem + i * 16) == who->lo &&
                    *(DWORD *)(g_cr_mem + i * 16 + 4) == who->hi)
                    m = g_cr_mem + i * 16;
            /* banned first so a kicked member can't just come back, unless they're banned already */
            if (!m || !(*(DWORD *)(m + 8) & (1u << 4))) g_cr_msteps[g_cr_mn++] = MS_BAN;
            g_cr_msteps[g_cr_mn++] = MS_KICKFRAME;
            g_cr_msteps[g_cr_mn++] = MS_KICK;
            g_cr_mop = MO_KICK;
        } else {
            g_cr_msteps[g_cr_mn++] = MS_OP;
            g_cr_msteps[g_cr_mn++] = MS_OPFRAME;
            g_cr_mop = MO_MASTER;
        }
    } else {
        cr_master_refused("failed:bad request");
        return;
    }
    g_cr_tries = 0;
    lstrcpyA(g_cr_mstate, "working");
    lg("chat: master job %d, %d step(s)\r\n", g_cr_mop, g_cr_mn);
    g_snap_dirty = 1;
}

/* chat names go out in every frame: 1-15 Shift-JIS bytes, no tab or control characters, not all blanks */
static int cr_nick_ok(BYTE *b, const char *u8)
{
    WCHAR w[64];
    char sj[64];
    int any = 0, n;
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, u8, -1, w, 64) <= 1) return 0;
    for (int i = 0; w[i]; i++) {
        if (w[i] < 0x20 || w[i] == 0x7f) return 0;
        if (w[i] != L' ' && w[i] != 0x3000) any = 1;
    }
    if (!any || (n = cr_sjis(b, w, sj, sizeof sj)) < 1 || n > CR_NAME_MAX) return 0;
    lstrcpynW(g_cr_nick, w, CR_NAME_MAX + 1);
    return 1;
}

static int cr_key_ok(const char *s)
{
    int n = lstrlenA(s);
    if (n > 23) return 0;
    for (int i = 0; i < n; i++) if ((BYTE)s[i] <= 0x20 || (BYTE)s[i] >= 0x7f || s[i] == ',') return 0;
    return 1;
}
/* the room password is the one-shot POL_CRED_CHAT entry (user = the join's token), wrapped like the account details,
   so it never touches a file. 0 when it isn't there or isn't this join's */
static int cr_password(const char *tok, char *key, int cap)
{
    WCHAR tbuf[256];
    char user[64];
    int rc;
    const WCHAR *t = cred_target(ENV_CRED_CHAT, tbuf, 256);
    key[0] = 0;
    if (!t || !launch_target(t)) return 0;
    rc = cred_read(t, user, sizeof user, key, cap);
    if (rc != PC_NOT_FOUND) CredDeleteW(t, CRED_TYPE_GENERIC, 0);
    if (rc == PC_OK && !lstrcmpA(user, tok)) return 1;
    SecureZeroMemory(key, cap);
    if (rc != PC_NOT_FOUND) lg("chat: room password entry unusable (%s)\r\n", rc == PC_OK ? "another enter's" : cred_why(rc));
    return 0;
}
static void cr_join_refused(const char *state)
{
    SecureZeroMemory(g_cr_key, sizeof g_cr_key);
    if (state) cr_room_state(state);
}
/* the app couldn't withdraw its enter or create, so its leave got here first */
static int cr_called_off(const char *tok)
{
    int off = g_cr_cancel_tok[0] && !lstrcmpA(g_cr_cancel_tok, tok);
    g_cr_cancel_tok[0] = 0;
    return off;
}
/* the start of an enter or a create, once g_cr_room's set */
static void cr_enter(BYTE *b)
{
    /* a line the hook renamed but never read belongs to the last room, it never goes into this one */
    char path[MAX_PATH + 64];
    for (int i = 0; i < 2; i++) {
        ipc_file(path, REQ_CHATSEND);
        lstrcatA(path, i ? IPC_TAKING : "");
        DeleteFileA(path);
    }
    cr_room_clear();
    g_cr_line_n = g_cr_line_next = 0;
    g_cr_lines_dirty = 1;
    g_cr_leave = 0;
    g_cr_me = SVC(0x270) ? ((unsigned __int64 (__cdecl *)(int))SVC(0x270))(cr_session(b)) : 0;
    lg("chat: %s %s%s\r\n", g_cr_making ? "creating" : "entering", g_cr_room, g_cr_key[0] ? " (password)" : "");
    cr_room_state("entering");
    cr_go(CR_AWAY);
}
static void cr_join_request(BYTE *b)
{
    char buf[512], *p, *tok, *irc, *haskey, *nick;
    int pw = 0;
    if (take_request(REQ_CHATJOIN, buf, sizeof buf) < 0) return;
    g_cr_used = 1;
    p = buf;
    tok = req_line(&p);
    irc = req_line(&p);
    haskey = req_line(&p);
    nick = req_line(&p);
    /* the password entry's read, and so deleted, before anything can turn the enter down */
    if (haskey[0]) pw = cr_password(tok, g_cr_key, sizeof g_cr_key);
    if (g_cr_st != CR_OUT) {
        lg("chat: enter ignored, already in or entering a room\r\n");
        cr_join_refused(NULL);
        return;
    }
    if (cr_called_off(tok)) {
        lg("chat: enter called off before it was taken\r\n");
        cr_join_refused(NULL);
        return;
    }
    lstrcpynA(g_cr_tok, tok, sizeof g_cr_tok);
    if (!cr_irc_ok(irc) || (haskey[0] && lstrcmpA(haskey, "1")) || !cr_nick_ok(b, nick)) {
        cr_join_refused("failed:bad request");
        return;
    }
    if (cr_session(b) < 0) { cr_join_refused("failed:no chat"); return; }
    if (haskey[0] && (!pw || !cr_key_ok(g_cr_key))) { cr_join_refused("failed:no password"); return; }
    if (!haskey[0]) g_cr_key[0] = 0;
    lstrcpynA(g_cr_room, irc, sizeof g_cr_room);
    g_cr_making = 0;
    cr_enter(b);
}

/* the viewer's new room password box: up to 16, letters and numbers */
static int cr_new_key_ok(const char *k)
{
    int n = lstrlenA(k);
    if (n < 1 || n > 16) return 0;
    for (int i = 0; i < n; i++)
        if (!((k[i] >= '0' && k[i] <= '9') || (k[i] >= 'a' && k[i] <= 'z') || (k[i] >= 'A' && k[i] <= 'Z'))) return 0;
    return 1;
}
/* a category is unset (0) or one of the viewer's own: 100-103, 200-204, 300-303 */
static int cr_cate(const char *x, int base, int count, int *out)
{
    if (!parse_num(x, 3, out)) return 0;
    return *out == 0 || (*out >= base && *out < base + count);
}
/* a new room (app.dll 0x100a188d): <token>, <1 with a password in Credential Manager>, <zone>,
   <occupancy>, <the three categories>, <chat name>, <room name>. The JOIN makes it and makes me its master */
static void cr_create_request(BYTE *b)
{
    char buf[512], *p, *tok, *haskey, *zone, *limit, *c0, *c1, *c2, *nick, *name, sj[0x80], id[0x40];
    static WCHAR w[0x40];
    WORD v[6];
    int pw = 0, z, l, k0, k1, k2, n, any = 0, s, lang;
    if (take_request(REQ_CHATCREATE, buf, sizeof buf) < 0) return;
    g_cr_used = 1;
    p = buf;
    tok = req_line(&p); haskey = req_line(&p); zone = req_line(&p); limit = req_line(&p);
    c0 = req_line(&p); c1 = req_line(&p); c2 = req_line(&p); nick = req_line(&p); name = req_line(&p);
    if (haskey[0]) pw = cr_password(tok, g_cr_key, sizeof g_cr_key);
    if (g_cr_st != CR_OUT) {
        lg("chat: create ignored, already in or entering a room\r\n");
        cr_join_refused(NULL);
        return;
    }
    if (cr_called_off(tok)) {
        lg("chat: create called off before it was taken\r\n");
        cr_join_refused(NULL);
        return;
    }
    lstrcpynA(g_cr_tok, tok, sizeof g_cr_tok);
    if (!parse_num(zone, 5, &z) || !cr_zone_known((DWORD)z) || !parse_num(limit, 2, &l) || l < 2 || l > 20 ||
        !cr_cate(c0, 100, 4, &k0) || !cr_cate(c1, 200, 5, &k1) || !cr_cate(c2, 300, 4, &k2) ||
        (haskey[0] && lstrcmpA(haskey, "1")) || !cr_nick_ok(b, nick)) {
        cr_join_refused("failed:bad request");
        return;
    }
    /* the viewer's room name box holds 30 */
    n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, name, -1, w, 0x40);
    for (int i = 0; n > 0 && w[i]; i++) {
        if (w[i] < 0x20 || w[i] == 0x7f) n = 0;
        else if (w[i] != L' ' && w[i] != 0x3000) any = 1;
    }
    if (n < 2 || n > 31 || !any || (n = cr_sjis_exact(b, w, sj, 61)) < 1) { cr_join_refused("failed:bad name"); return; }
    if ((s = cr_session(b)) < 0) { cr_join_refused("failed:no chat"); return; }
    if (haskey[0] && (!pw || !cr_new_key_ok(g_cr_key))) { cr_join_refused("failed:no password"); return; }
    if (!haskey[0]) g_cr_key[0] = 0;
    /* '#', the viewer's language, "CU" and svc 0x224's text, which is unique to now and this login */
    memset(id, 0, sizeof id);
    ((void (__cdecl *)(int, char *))SVC(0x224))(s, id);
    id[0x34] = 0;
    lang = SVC(0xf18) ? ((svc_v)SVC(0xf18))() : 0;
    wsprintfA(g_cr_room, "#%02xCU%s", lang & 0xff, id);
    if (!cr_irc_ok(g_cr_room)) { cr_join_refused("failed:bad request"); return; }
    /* svc 0x360 turns the zone and categories into 12 characters with no end, and reads a byte past them */
    memset(v, 0, sizeof v);
    v[0] = (WORD)z; v[1] = (WORD)k0; v[2] = (WORD)k1; v[3] = (WORD)k2;
    memset(g_cr_new_topic, 0, sizeof g_cr_new_topic);
    ((void (__cdecl *)(const void *, char *, int, int))SVC(0x360))(v, g_cr_new_topic, 8, 0);
    lstrcpynA(g_cr_new_topic + 12, sj, sizeof g_cr_new_topic - 12);
    wsprintfA(g_cr_new_limit, "%d", l);
    g_cr_making = 1;
    cr_enter(b);
}
static void cr_leave_request(BYTE *b)
{
    char buf[128];
    (void)b;
    char *p = buf, *join;
    if (take_request(REQ_CHATLEAVE, buf, sizeof buf) < 0) return;
    req_line(&p);
    join = req_line(&p);
    /* the app couldn't withdraw its enter, so it's called off here instead, even with a PART still going */
    if (join[0] && lstrcmpA(join, g_cr_tok)) lstrcpynA(g_cr_cancel_tok, join, sizeof g_cr_cancel_tok);
    if (g_cr_st == CR_OUT || g_cr_leave) return;
    lg("chat: leaving %s\r\n", g_cr_room);
    g_cr_leave = 1;
}

/* the in-room side, every turn: callbacks' lines, the member list when polcore says something came in, then the one
   step that's due */
static void cr_room_pump(BYTE *b, int ctl)
{
    int s, r;
    cr_raw_pump(b);
    if (g_cr_st == CR_OUT) return;
    s = cr_session(b);
    if (s < 0) {
        if (g_cr_wait != CRW_NONE && g_cr_msg_kind == CRM_SAY) cr_send_state("failed:link");
        g_cr_wait = CRW_NONE;
        cr_left(g_cr_st == CR_IN || g_cr_st == CR_PART ? "lost" : "failed:link");
        return;
    }
    if (g_cr_wait == CRW_CMD || g_cr_wait == CRW_MSG) {
        int out[1 + 0x14] = {0};
        r = g_cr_wait == CRW_MSG ? ((int (__cdecl *)(int, int *))SVC(0x24c))(s, out) : ((svc_i)SVC(0x244))(s);
        if (r == 0 && GetTickCount64() - g_cr_sent_at < CR_WAIT_MS) return;
        if (r == 0) {
            ((svc_i)SVC(0x248))(s);   /* forget it, the viewer's own timeout. Never sent again without a new click */
            lg("chat: no answer in %u s (step %d)\r\n", CR_WAIT_MS / 1000, g_cr_st);
            if (g_cr_wait == CRW_MSG) { cr_msg_done(0, "failed:timeout"); return; }
            /* a JOIN that timed out but got there anyway counts */
            if (g_cr_st == CR_JOIN && cr_members(b)) { cr_step_done(b, 1); return; }
            if (g_cr_st == CR_AWAY) { cr_left("failed:timeout"); return; }
            if (g_cr_st == CR_JOIN) {
                /* it could still get there, so its PART goes now and waits like any step. The window's back in the
                   lobby already */
                g_cr_wait = CRW_NONE;
                lstrcpyA(g_cr_part_state, "failed:timeout");
                cr_room_state(g_cr_part_state);
                g_cr_leave = 1;
                cr_go(CR_PART);
                return;
            }
            if (g_cr_st == CR_IN && g_cr_mop) {
                g_cr_wait = CRW_NONE;
                cr_master_end("failed:timeout");
                return;
            }
            /* a new room's setting with no answer might not be there, so it's left like a refusal */
            if (g_cr_st >= CR_SETTOPIC) {
                g_cr_wait = CRW_NONE;
                cr_step_done(b, -1);
                return;
            }
            r = 1;
        }
        if (g_cr_wait == CRW_MSG) cr_msg_done(r, NULL);
        else if (g_cr_st == CR_IN && g_cr_mop) cr_master_done(b, r);
        else cr_step_done(b, r);
        return;
    }
    if (g_cr_leave) {
        if (g_cr_st == CR_AWAY || g_cr_st == CR_JOIN) { cr_left("left"); return; }
        if (g_cr_st != CR_PART) cr_go(CR_PART);
    }
    if (g_cr_st == CR_IN) {
        if (SVC(0x146c) && ((svc_i)SVC(0x146c))(s)) g_cr_reread = 1;
        if (g_cr_reread) {
            g_cr_reread = 0;
            if (!cr_members(b)) { cr_left(g_cr_kicked_me ? "kicked" : "gone"); return; }
        }
        /* a PRIVMSG reads the same answer as the group chat's JOIN, WHO and PART, so nothing goes while one's out */
        if (gc_busy()) return;
        if (g_cr_msg_kind >= 0) {
            cr_msg_start(b, g_cr_msg_kind);
            return;
        }
        if (!cr_msg_ready()) return;
        if (g_cr_hello) { g_cr_hello = 0; cr_frame_out(b, CRM_HELLO); return; }
        if (g_cr_mop) { cr_master_step(b); return; }
        if (ctl && request_new(REQ_CHATMASTER)) { cr_master_request(b); return; }
        if (ctl && request_new(REQ_CHATSEND)) { cr_send_request(b); return; }
        if (g_cr_replies > 0) { g_cr_replies--; cr_frame_out(b, CRM_REPLY); }
        return;
    }
    if (g_cr_st == CR_SETTOPIC && (g_cr_hello || g_cr_msg_kind >= 0)) {
        if (gc_busy()) return;
        if (g_cr_msg_kind >= 0) {
            cr_msg_start(b, g_cr_msg_kind);
            return;
        }
        if (!cr_msg_ready()) return;
        g_cr_hello = 0;
        cr_frame_out(b, CRM_HELLO);
        return;
    }
    /* polcore's group chat reads the same one command answer for its JOIN and PART, so they never overlap */
    if (gc_busy()) return;
    r = cr_command(b, g_cr_st);
    if (r == 0) {
        if (++g_cr_tries < cr_tries_for(g_cr_st)) return;
        r = -1;
    }
    if (r < 0) {
        if (r != CR_NOTHING_TO_LEAVE) lg("chat: step %d couldn't be sent (%d)\r\n", g_cr_st, r);
        if (g_cr_st == CR_PART) { cr_left(g_cr_part_state[0] ? g_cr_part_state : "left"); return; }
        if (g_cr_st == CR_AWAY) { cr_go(CR_JOIN); return; }
        if (g_cr_st == CR_JOIN) { cr_left(failed(r)); return; }
        cr_step_done(b, r);
        return;
    }
    g_cr_wait = CRW_CMD;
    g_cr_sent_at = GetTickCount64();
}

/* every hold loop turn, requests only on control turns */
static void chat_pump(BYTE *b, int ctl)
{
    if (g_draining) return;
    cr_room_pump(b, ctl);
    if (ctl && (g_cr_st != CR_IN || g_cr_leave) && request_waiting(REQ_CHATSEND)) cr_send_request(b);
    if (ctl && (g_cr_st != CR_IN || g_cr_leave) && request_waiting(REQ_CHATMASTER)) cr_master_request(b);
    if (g_cr_db) cr_db_pump(b);
    if (!ctl) return;
    /* waiting, not new: a .taking left by a read that failed is tried again first */
    if (request_waiting(REQ_CHATLEAVE)) { cr_leave_request(b); return; }
    if (g_cr_st == CR_OUT && request_waiting(REQ_CHATJOIN)) { cr_join_request(b); return; }   /* a PART goes first */
    if (g_cr_st == CR_OUT && request_waiting(REQ_CHATCREATE)) { cr_create_request(b); return; }
    if (g_cr_db || !db_free()) return;
    if (request_waiting(REQ_CHATZONES)) { cr_zones_request(b); return; }
    if (request_waiting(REQ_CHATROOMS)) cr_rooms_request(b);
}

static int chat_room_open(void) { return g_cr_st != CR_OUT; }
/* a command or a PRIVMSG waiting on its answer. One that hasn't gone yet holds nothing, it waits for the group chat */
static int cr_cmd_busy(void) { return g_cr_wait != CRW_NONE; }

/* an enter or a line asked for while the handle's changing, answered as not done */
static void chat_join_refused(const char *tok, const char *haskey)
{
    char k[sizeof g_cr_key];
    g_cr_used = 1;
    k[0] = 0;
    if (haskey[0]) cr_password(tok, k, sizeof k);   /* only to delete it */
    SecureZeroMemory(k, sizeof k);
    if (g_cr_st != CR_OUT) return;   /* a room that's there keeps its own token and password */
    lstrcpynA(g_cr_tok, tok, sizeof g_cr_tok);
    cr_join_refused("failed:handle changing");
}
static void chat_create_refused(const char *tok, const char *haskey)
{
    chat_join_refused(tok, haskey);
}
static void chat_master_refused(const char *tok, const char *haskey)
{
    char k[sizeof g_cr_newkey];
    g_cr_used = 1;
    k[0] = 0;
    if (haskey[0]) cr_password(tok, k, sizeof k);   /* only to delete it */
    SecureZeroMemory(k, sizeof k);
    if (g_cr_mop) return;
    lstrcpynA(g_cr_mtok, tok, sizeof g_cr_mtok);
    cr_master_refused("failed:handle changing");
}
static void chat_send_refused(const char *tok)
{
    g_cr_used = 1;
    lstrcpynA(g_cr_stok, tok, sizeof g_cr_stok);
    cr_send_state("failed:handle changing");
}

/* Play: FFXI takes the link over, so a room gets a PART first (the user's leaving chat for the game) */
static void chat_end(BYTE *b)
{
    int s = cr_session(b), r;
    ULONGLONG until = GetTickCount64() + 3000;
    if (g_cr_db) cr_db_abandon(b);
    if (g_cr_st == CR_OUT) return;
    if (s >= 0 && g_cr_wait != CRW_NONE) ((svc_i)SVC(0x248))(s);
    g_cr_wait = CRW_NONE;
    /* a JOIN with no answer yet still gets there, so it's PARTed like a room. The group chat can have the link's one
       command out, so the PART's tried again till it goes */
    while ((r = cr_part(b)) == 0 && GetTickCount64() < until) {
        POLCON_STEP(b);
        POLCON_IDLE(b);
        Sleep(16);
    }
    if (r > 0) {
        while (GetTickCount64() < until && ((svc_i)SVC(0x244))(s) == 0) {
            POLCON_STEP(b);
            POLCON_IDLE(b);
            Sleep(16);
        }
    }
    cr_left("left");
}

static void cr_write_lines(BYTE *b)
{
    char path[MAX_PATH + 64], tmp[MAX_PATH + 80], line[0x400], nm[0x100], tx[0xd00], u8[0x620];
    HANDLE h;
    (void)b;
    g_cr_lines_dirty = 0;
    g_cr_written_seq = g_cr_seq;
    g_cr_written_at = GetTickCount64();
    ipc_file(path, IPC_CHATROOM);
    lstrcpyA(tmp, path); lstrcatA(tmp, ".tmp");
    h = CreateFileA(tmp, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    json_str(nm, sizeof nm, g_cr_room);
    wr(h, "{\"room\":"); wr(h, nm); wr(h, ",\"lines\":[");
    for (int i = 0; i < g_cr_line_n; i++) {
        const cr_line *l = &g_cr_line[(g_cr_line_next - g_cr_line_n + i + CR_LINES) % CR_LINES];
        cr_u8(l->name, u8, sizeof u8);
        json_str(nm, sizeof nm, u8);
        cr_u8(l->text, u8, sizeof u8);
        json_text(tx, sizeof tx, u8);
        wsprintfA(line, "%s{\"seq\":%lu,\"time\":%lu,\"kind\":\"%s\",\"from\":%d,\"me\":%s,\"colour\":%d,\"emote\":%d,"
                        "\"name\":%s,\"text\":", i ? "," : "", l->seq, l->time, g_cr_kind[l->kind], l->from,
                  l->me ? "true" : "false", l->colour, l->emote, nm);
        wr(h, line);
        wr(h, tx);
        wr(h, "}");
    }
    wr(h, "]}");
    CloseHandle(h);
    MoveFileExA(tmp, path, MOVEFILE_REPLACE_EXISTING);
}
/* the snapshot's "chat", only once chat's been used so the rest of the snapshot stays as it was */
static void chat_snapshot(BYTE *b, HANDLE h)
{
    char line[0x600], tk[100], js[0x300], g1[0x100], g2[0x100], part[0x80];
    const pfb_tab *zt, *ct;
    int i, first;
    if (!g_cr_used) return;
    if (g_cr_lines_dirty && GetTickCount64() - g_cr_written_at >= 1000) cr_write_lines(b);
    wsprintfA(line, ",\"chat\":{\"lineSeq\":%lu", g_cr_written_seq);
    wr(h, line);
    json_str(tk, sizeof tk, g_cr_ztok);
    wsprintfA(line, ",\"zones\":{\"token\":%s,\"state\":\"%s\",\"list\":[", tk, g_cr_zstate);
    wr(h, line);
    zt = cr_tab_of(g_cr_cpfb, 0);
    for (i = 0, first = 1; zt && i < g_cr_zone_n; i++) {
        int e = pf_find(zt, g_cr_zone[i].code);
        if (e < 0) continue;
        pf_part(zt, e, 0, part, sizeof part);
        pol_json(b, part, 0x100, g1, sizeof g1);
        pf_part(zt, e, 2, part, sizeof part);
        pol_json(b, part, 0x100, g2, sizeof g2);
        wsprintfA(line, "%s{\"code\":%u,\"group\":%s,\"name\":%s,\"rooms\":%lu,\"people\":%lu}", first ? "" : ",",
                  g_cr_zone[i].code, g1, g2, g_cr_zone[i].rooms, g_cr_zone[i].people);
        wr(h, line);
        first = 0;
    }
    wr(h, "]},\"categories\":[");
    ct = cr_tab_of(g_cr_rpfb, 7);
    for (i = 0; ct && i < ct->n; i++) {
        pf_part(ct, i, 1, part, sizeof part);
        pol_json(b, part, 0x100, g1, sizeof g1);
        pf_part(ct, i, 2, part, sizeof part);
        pol_json(b, part, 0x100, g2, sizeof g2);
        wsprintfA(line, "%s{\"code\":%lu,\"group\":%s,\"name\":%s}", i ? "," : "", ct->e[i].code, g1, g2);
        wr(h, line);
    }
    json_str(tk, sizeof tk, g_cr_rtok);
    wsprintfA(line, "],\"rooms\":{\"token\":%s,\"state\":\"%s\",\"zone\":%d,\"offset\":%d,\"total\":%d,\"list\":[", tk,
              g_cr_rstate, g_cr_rzone, g_cr_roff, g_cr_rtotal);
    wr(h, line);
    for (i = 0; i < g_cr_rooms_n; i++) {
        const cr_room *r = &g_cr_rooms[i];
        json_str(tk, sizeof tk, r->irc);
        pol_json(b, r->topic, 0x100, js, sizeof js);
        wsprintfA(line, "%s{\"irc\":%s,\"name\":%s,\"locked\":%s,\"people\":%lu,\"capacity\":%lu,"
                        "\"categories\":[%u,%u,%u],\"created\":%lu}",
                  i ? "," : "", tk, js, r->locked ? "true" : "false", r->people, r->capa,
                  r->cate[0], r->cate[1], r->cate[2], r->ctime);
        wr(h, line);
    }
    json_str(tk, sizeof tk, g_cr_tok);
    json_str(js, sizeof js, g_cr_st == CR_OUT ? "" : g_cr_room);
    wsprintfA(line, "]},\"room\":{\"token\":%s,\"state\":\"%s\",\"irc\":%s,\"limit\":%lu,\"locked\":%s,\"members\":[", tk,
              g_cr_state, js, g_cr_st == CR_IN ? *(DWORD *)(g_cr_slot + 0x94) : 0,
              g_cr_st == CR_IN && (*(DWORD *)(g_cr_slot + 0x8c) & (1u << 8)) ? "true" : "false");
    wr(h, line);
    for (i = 0, first = 1; g_cr_st == CR_IN && i < g_cr_people_n; i++) {
        const cr_person *p = &g_cr_people[i];
        char u8[0x80];
        int me = cr_is_me(p->lo, p->hi);
        if (!p->member) continue;
        cr_u8(me ? g_cr_nick : p->name, u8, sizeof u8);
        json_str(g1, sizeof g1, u8);
        wsprintfA(line, "%s{\"key\":%d,\"name\":%s,\"me\":%s,\"master\":%s,\"away\":%s}", first ? "" : ",", p->key, g1,
                  me ? "true" : "false", p->master ? "true" : "false", !me && p->away ? "true" : "false");
        wr(h, line);
        first = 0;
    }
    json_str(tk, sizeof tk, g_cr_stok);
    wsprintfA(line, "]},\"send\":{\"token\":%s,\"state\":\"%s\"}", tk, g_cr_sstate);
    wr(h, line);
    json_str(tk, sizeof tk, g_cr_mtok);
    wsprintfA(line, ",\"master\":{\"token\":%s,\"state\":\"%s\"}}", tk, g_cr_mstate);
    wr(h, line);
}
