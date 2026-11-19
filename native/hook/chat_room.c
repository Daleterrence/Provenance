
static int chat_room_new_key_ok(const char *k);
static int group_chat_busy(void);

static void chat_room_raw_pump(BYTE *b)
{
    static WCHAR w[0x220];
    LONG n = g_cr_raw_n;
    for (LONG i = 0; i < n; i++) {
        if (!g_s.cr_open || (!g_cr_raw[i].whisper && lstrcmpiA(g_cr_raw[i].chan, g_cr_room) != 0)) continue;
        chat_frame_wide(b, g_cr_raw[i].text, w, 0x220);
        chat_frame_parse(w, g_cr_raw[i].lo, g_cr_raw[i].hi, g_cr_raw[i].whisper);
    }
    g_cr_raw_n = 0;
    if (g_cr_raw_lost) {
        base_log("chat rooms: %d room line(s) dropped, too many at once\r\n", g_cr_raw_lost);
        g_cr_raw_lost = 0;
    }
}

static int chat_room_members(BYTE *b)
{
    int s = chat_frame_session(b), seen[CR_PEOPLE] = {0};
    if (s < 0 || !pc_has(b, SV_CHAT_MEMBERS)) return 0;
    memset(g_cr_slot, 0, sizeof g_cr_slot);
    memset(g_cr_mem, 0, sizeof g_cr_mem);
    if (!pc_chat_members(b, s, g_cr_room, g_cr_slot, g_cr_mem)) return 0;
    for (int i = 0; i < 0x40; i++) {
        BYTE *m = g_cr_mem + i * 16;
        cr_person *p;
        if (!(m[0xc] & 1) || !(p = chat_frame_person_for(*(DWORD *)m, *(DWORD *)(m + 4), 1))) continue;
        seen[p - g_cr_people] = 1;
        p->member = 1;
        p->master = (m[0xc] & 2) != 0;
    }
    for (int i = 0; i < g_cr_people_n; i++) {
        cr_person *p = &g_cr_people[i];
        if (seen[i] || !p->member) continue;
        p->member = p->master = p->answered = 0;
        if (!chat_frame_is_me(p->lo, p->hi)) chat_frame_add_line(CRK_LEFT, 0, p, p->name, NULL);
    }
    g_snap_dirty = 1;
    return 1;
}

static void chat_room_clear(void)
{
    g_s.cr_open = g_s.cr_hello = g_s.cr_replies = g_cr_reread = g_cr_skip_topic = g_cr_kicked_me = 0;
    g_cr_people_n = g_cr_next_key = 0;
    g_cr_raw_n = 0;
    memset(g_cr_slot, 0, sizeof g_cr_slot);
}
static void chat_room_state(const char *state)
{
    lstrcpynA(g_cr_state, state, sizeof g_cr_state);
    base_log("chat rooms: room %s\r\n", state);
    g_snap_dirty = 1;
}
static void chat_room_send_state(const char *state)
{
    lstrcpynA(g_cr_sstate, state, sizeof g_cr_sstate);
    g_snap_dirty = 1;
}

#define CR_NOTHING_TO_LEAVE (-0x7e01)
static int chat_room_part(BYTE *b)
{
    int s = chat_frame_session(b), r;
    if (!g_s.cr_joined) return CR_NOTHING_TO_LEAVE;
    if (s < 0) return NO_CHAT_LINK;
    r = pc_chat_part(b, s, g_cr_room);
    if (r > 0) g_s.cr_joined = g_s.cr_open = 0;
    return r;
}
/* MODE: 0xb 'n', 8 'k' (arg password), 9 'l' (arg limit as text), 4 'b' and 0xc 'o' on a member (lo, hi).
   polcore answers a no-change one at once */
static int chat_room_mode(BYTE *b, int mode, DWORD lo, DWORD hi, const char *arg, int on)
{
    int s = chat_frame_session(b);
    if (s < 0) return NO_CHAT_LINK;
    return pc_chat_mode(b, s, mode, g_cr_room, lo,
                                                                                                  hi, arg, on);
}
static int chat_room_command(BYTE *b, int step)
{
    int s = chat_frame_session(b), r = 0;
    if (s < 0) return NO_CHAT_LINK;
    switch (step) {
    case CR_AWAY: r = pc_chat_away(b, s); break;
    case CR_JOIN:
        r = pc_chat_join(b, s, g_cr_room, g_cr_key);
        if (r > 0) g_s.cr_joined = 1;
        break;
    case CR_WHO: r = pc_chat_who(b, s, g_cr_room); break;
    case CR_TOPIC: r = pc_chat_topic(b, s, g_cr_room, NULL); break;
    case CR_PART: r = chat_room_part(b); break;
    case CR_SETTOPIC: r = pc_chat_topic(b, s, g_cr_room, g_cr_new_topic); break;
    case CR_MODE_N: r = chat_room_mode(b, 0xb, 0, 0, NULL, 1); break;
    case CR_MODE_K: r = chat_room_mode(b, 8, 0, 0, g_cr_key, 1); break;
    case CR_MODE_L: r = chat_room_mode(b, 9, 0, 0, g_cr_new_limit, 1); break;
    }
    return r;
}
static const char *chat_room_join_failed(int r)
{
    switch (r) {
    case -0x11db: return "failed:refused";
    case -0x11dc: return "failed:password";
    case -0x11d8: return "failed:full";
    case -0x1194: return "failed:closed";
    }
    return requests_failed(r);
}
static void chat_room_go(int step)
{
    g_s.cr_st = step;
    g_s.cr_tries = 0;
}
static int chat_room_tries_for(int step)
{
    return step == CR_PART || (!g_cr_making && (step == CR_AWAY || step == CR_JOIN)) ? CR_TRIES_ENTER : CR_TRIES;
}
static void chat_room_master_end(const char *state)
{
    if (lstrcmpA(state, "done") && lstrcmpA(state, "failed:left room") && g_cr_mop == MO_KEY && g_cr_mi > 0 &&
        g_cr_msteps[0] == MS_UNKEY && g_cr_msteps[g_cr_mi] == MS_KEY) {
        SecureZeroMemory(g_cr_key, sizeof g_cr_key);
        state = "failed:unlocked";
    }
    lstrcpynA(g_cr_mstate, state, sizeof g_cr_mstate);
    base_log("chat rooms: master job %d %s\r\n", g_cr_mop, state);
    g_cr_mop = MO_NONE;
    g_cr_mn = g_cr_mi = 0;
    SecureZeroMemory(g_cr_newkey, sizeof g_cr_newkey);
    g_snap_dirty = 1;
}
static char g_cr_part_state[32];
static void chat_room_left(const char *state)
{
    if (lstrcmpA(g_cr_sstate, "sending") == 0) chat_room_send_state("failed:left");
    g_cr_msg_kind = -1;
    g_s.cr_wait = CRW_NONE;
    g_s.cr_joined = 0;
    g_cr_making = 0;
    if (g_cr_mop) chat_room_master_end("failed:left room");
    chat_room_clear();
    SecureZeroMemory(g_cr_key, sizeof g_cr_key);
    SecureZeroMemory(g_cr_newkey, sizeof g_cr_newkey);
    g_s.cr_leave = 0;
    chat_room_go(CR_OUT);
    chat_room_state(state);
    g_cr_part_state[0] = 0;
}
static void chat_room_step_done(BYTE *b, int r)
{
    int st = g_s.cr_st;
    g_s.cr_wait = CRW_NONE;
    if (st == CR_PART) { chat_room_left(g_cr_part_state[0] ? g_cr_part_state : "left"); return; }
    if (st == CR_AWAY) {
        if (r < 0) base_log("chat rooms: AWAY answered %d, entering anyway\r\n", r);
        if (g_s.cr_leave) { chat_room_left("left"); return; }
        chat_room_go(CR_JOIN);
        return;
    }
    if (st == CR_JOIN) {
        if (r < 0) { chat_room_left(chat_room_join_failed(r)); return; }
        g_s.cr_open = 1;
        chat_room_go(g_s.cr_leave ? CR_PART : CR_WHO);
        return;
    }
    if (st >= CR_SETTOPIC) {
        if (r < 0) {
            base_log("chat rooms: new room step %d answered %d, leaving it\r\n", st, r);
            wsprintfA(g_cr_part_state, "failed:create %d", r);
            chat_room_state(g_cr_part_state);
            g_s.cr_leave = 1;
            chat_room_go(CR_PART);
            return;
        }
        chat_room_go(g_s.cr_leave ? CR_PART : st == CR_SETTOPIC ? CR_MODE_N : st == CR_MODE_N && g_cr_key[0] ? CR_MODE_K
              : st == CR_MODE_L ? CR_IN : CR_MODE_L);
        if (g_s.cr_st == CR_IN) chat_room_state("in");
        return;
    }
    if (st == CR_WHO) {
        if (r < 0) base_log("chat rooms: WHO answered %d\r\n", r);
        chat_room_members(b);
        if (g_cr_making) {
            g_s.cr_hello = !g_s.cr_leave;
            chat_room_go(g_s.cr_leave ? CR_PART : CR_SETTOPIC);
            return;
        }
        {
            int n = 0, master = 0;
            for (int i = 0; i < g_cr_people_n; i++)
                if (g_cr_people[i].member) {
                    n++;
                    if (g_cr_people[i].master && chat_frame_is_me(g_cr_people[i].lo, g_cr_people[i].hi)) master = 1;
                }
            g_cr_skip_topic = n < 2 && master;
        }
        chat_room_go(g_s.cr_leave ? CR_PART : g_cr_skip_topic ? CR_IN : CR_TOPIC);
    } else if (st == CR_TOPIC) {
        if (r < 0) base_log("chat rooms: TOPIC answered %d\r\n", r);
        chat_room_go(g_s.cr_leave ? CR_PART : CR_IN);
    }
    if (g_s.cr_st == CR_IN) {
        g_s.cr_hello = 1;
        chat_room_state("in");
    }
}

static void chat_room_master_next(void);
static void chat_room_master_step(BYTE *b);
static void chat_room_master_done(int r);
static void chat_room_master_refused(const char *state);
static void chat_room_master_request(void);
static int chat_room_privmsg(BYTE *b, const char *frame)
{
    int s = chat_frame_session(b);
    if (s < 0) return NO_CHAT_LINK;
    return pc_chat_privmsg(b, s, 0, 0, g_cr_room, frame);
}

static int chat_room_msg_ready(void)
{
    return (g_s.cr_st == CR_IN || g_s.cr_st == CR_SETTOPIC) && g_s.cr_wait == CRW_NONE && !g_s.cr_leave;
}
static void chat_room_msg_start(BYTE *b, int kind)
{
    int r = chat_room_privmsg(b, g_cr_frame);
    if (r == 0) {
        if (++g_s.cr_tries < (kind == CRM_SAY ? CR_TRIES : CR_TRIES_FRAME)) { g_cr_msg_kind = kind; return; }
        r = -1;
    }
    g_s.cr_tries = 0;
    if (r < 0) {
        g_cr_msg_kind = -1;
        base_log("chat rooms: PRIVMSG (%d) refused %d\r\n", kind, r);
        if (kind == CRM_SAY) chat_room_send_state("failed:busy");
        if (kind >= CRM_KEY) chat_room_master_next();
        return;
    }
    g_cr_msg_kind = kind;
    g_s.cr_wait = CRW_MSG;
    g_cr_sent_at = GetTickCount64();
    if (kind == CRM_SAY) {
        static WCHAR w[0x220];
        chat_frame_wide(b, g_cr_frame, w, 0x220);
        {
            int tab = chat_frame_tab(w, lstrlenW(w), 6);
            cr_line *l = chat_frame_add_line(CRK_SAY, 1, NULL, g_cr_nick, tab >= 0 ? w + tab + 3 : L"");
            l->colour = g_cr_colour < 0 ? 0 : g_cr_colour + 5;
            l->emote = g_cr_emote;
        }
    }
}
static void chat_room_msg_done(int r, const char *why)
{
    int kind = g_cr_msg_kind;
    g_s.cr_wait = CRW_NONE;
    g_cr_msg_kind = -1;
    if (kind == CRM_SAY) chat_room_send_state(why ? why : r > 0 ? "sent" : requests_failed(r));
    else if (why || r < 0) base_log("chat rooms: frame %d answered %d\r\n", kind, r);
    if (kind >= CRM_KEY) chat_room_master_next();
}

static void chat_room_send_request(BYTE *b)
{
    char buf[0x800], *p, *tok, *text, *colour, *emote;
    static WCHAR w[0x220], t[0x220];
    int n;
    if (requests_take(REQ_CHATSEND, buf, sizeof buf) < 0) return;
    g_cr_used = 1;
    p = buf;
    tok = requests_line(&p);
    text = requests_line(&p);
    colour = requests_line(&p);
    emote = requests_line(&p);
    g_cr_colour = requests_parse_num(colour, 2, &n) && n < 12 ? n : -1;
    g_cr_emote = requests_parse_num(emote, 2, &n) && n < 24 ? n : 0;
    lstrcpynA(g_cr_stok, tok, sizeof g_cr_stok);
    if (g_s.cr_st != CR_IN || !g_s.cr_open || g_s.cr_leave) { chat_room_send_state("failed:not in a room"); return; }
    if (!text[0]) { chat_room_send_state("failed:empty"); return; }
    if ((n = MultiByteToWideChar(CP_UTF8, 0, text, -1, t, 0x200)) <= 1 || n - 1 > CR_TEXT_MAX) {
        chat_room_send_state("failed:too long");
        return;
    }
    for (int i = 0; t[i]; i++) if (t[i] < 0x20) t[i] = L' ';
    chat_frame_say(b, t, w, 0x220);
    if ((n = chat_frame_sjis(b, w, g_cr_frame, sizeof g_cr_frame)) < 0 || !chat_frame_fits(n)) { chat_room_send_state("failed:too long"); return; }
    chat_room_send_state("sending");
    g_s.cr_tries = 0;
    chat_room_msg_start(b, CRM_SAY);
}
static void chat_room_frame_out(BYTE *b, int type)
{
    static WCHAR w[0x100];
    chat_frame_hello(type, w);
    if (chat_frame_sjis(b, w, g_cr_frame, sizeof g_cr_frame) < 0) {
        base_log("chat rooms: frame %d doesn't convert\r\n", type); return;
    }
    g_s.cr_tries = 0;
    chat_room_msg_start(b, type);
}

static void chat_room_frame_tail(BYTE *b, int type, const WCHAR *tail)
{
    static WCHAR w[0x80];
    int lang = pc_viewer_language_or(b, 0);
    wsprintfW(w, L"%x00000%02x", type, lang & 0xff);
    lstrcpynW(w + 8, tail, 0x40);
    if (chat_frame_sjis(b, w, g_cr_frame, sizeof g_cr_frame) < 0) {
        base_log("chat rooms: frame %d doesn't convert\r\n", type);
        chat_room_master_next();
        return;
    }
    g_s.cr_tries = 0;
    chat_room_msg_start(b, type);
}

/* chat names go out in every frame: 1-15 Shift-JIS bytes, no tab or control characters, not all blanks */
static int chat_room_nick_ok(BYTE *b, const char *u8)
{
    WCHAR w[64];
    char sj[64];
    int any = 0, n;
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, u8, -1, w, 64) <= 1) return 0;
    for (int i = 0; w[i]; i++) {
        if (w[i] < 0x20 || w[i] == 0x7f) return 0;
        if (w[i] != L' ' && w[i] != 0x3000) any = 1;
    }
    if (!any || (n = chat_frame_sjis(b, w, sj, sizeof sj)) < 1 || n > CR_NAME_MAX) return 0;
    lstrcpynW(g_cr_nick, w, CR_NAME_MAX + 1);
    return 1;
}

static int chat_room_key_ok(const char *s)
{
    int n = lstrlenA(s);
    if (n > 23) return 0;
    for (int i = 0; i < n; i++) if ((BYTE)s[i] <= 0x20 || (BYTE)s[i] >= 0x7f || s[i] == ',') return 0;
    return 1;
}
static int chat_room_password(const char *tok, char *key, int cap)
{
    WCHAR tbuf[256];
    char user[64];
    int rc;
    const WCHAR *t = cred_target(ENV_CRED_CHAT, tbuf, 256);
    key[0] = 0;
    if (!t || !cred_launch_target(t)) return 0;
    rc = cred_read(t, user, sizeof user, key, cap);
    if (rc != PC_NOT_FOUND) CredDeleteW(t, CRED_TYPE_GENERIC, 0);
    if (rc == PC_OK && !lstrcmpA(user, tok)) return 1;
    SecureZeroMemory(key, cap);
    if (rc != PC_NOT_FOUND) base_log("chat rooms: room password entry unusable (%s)\r\n", rc == PC_OK ? "another enter's" : cred_why(rc));
    return 0;
}
static void chat_room_join_refused(const char *state)
{
    SecureZeroMemory(g_cr_key, sizeof g_cr_key);
    if (state) chat_room_state(state);
}
static int chat_room_called_off(const char *tok)
{
    int off = g_cr_cancel_tok[0] && !lstrcmpA(g_cr_cancel_tok, tok);
    g_cr_cancel_tok[0] = 0;
    return off;
}
static void chat_room_enter(BYTE *b)
{
    /* a line the hook renamed but never read belongs to the last room, it never goes into this one */
    char path[MAX_PATH + 64];
    for (int i = 0; i < 2; i++) {
        base_ipc_file(path, REQ_CHATSEND);
        lstrcatA(path, i ? IPC_TAKING : "");
        DeleteFileA(path);
    }
    chat_room_clear();
    g_cr_line_n = g_cr_line_next = 0;
    g_cr_lines_dirty = 1;
    g_s.cr_leave = 0;
    g_cr_me = pc_has(b, SV_CHAT_MY_ID) ? pc_chat_my_id(b, chat_frame_session(b)) : 0;
    base_log("chat rooms: %s %s%s\r\n", g_cr_making ? "creating" : "entering", g_cr_room, g_cr_key[0] ? " (password)" : "");
    chat_room_state("entering");
    chat_room_go(CR_AWAY);
}
static void chat_room_join_request(BYTE *b)
{
    char buf[512], *p, *tok, *irc, *haskey, *nick;
    int pw = 0;
    if (requests_take(REQ_CHATJOIN, buf, sizeof buf) < 0) return;
    g_cr_used = 1;
    p = buf;
    tok = requests_line(&p);
    irc = requests_line(&p);
    haskey = requests_line(&p);
    nick = requests_line(&p);
    if (haskey[0]) pw = chat_room_password(tok, g_cr_key, sizeof g_cr_key);
    if (chat_room_called_off(tok)) {
        base_log("chat rooms: enter called off before it was taken\r\n");
        chat_room_join_refused(NULL);
        return;
    }
    lstrcpynA(g_cr_tok, tok, sizeof g_cr_tok);
    if (!chat_list_irc_ok(irc) || (haskey[0] && lstrcmpA(haskey, "1")) || !chat_room_nick_ok(b, nick)) {
        chat_room_join_refused("failed:bad request");
        return;
    }
    if (chat_frame_session(b) < 0) { chat_room_join_refused("failed:no chat"); return; }
    if (haskey[0] && (!pw || !chat_room_key_ok(g_cr_key))) { chat_room_join_refused("failed:no password"); return; }
    if (!haskey[0]) g_cr_key[0] = 0;
    lstrcpynA(g_cr_room, irc, sizeof g_cr_room);
    g_cr_making = 0;
    chat_room_enter(b);
}

static int chat_room_new_key_ok(const char *k)
{
    int n = lstrlenA(k);
    if (n < 1 || n > 16) return 0;
    for (int i = 0; i < n; i++)
        if (!((k[i] >= '0' && k[i] <= '9') || (k[i] >= 'a' && k[i] <= 'z') || (k[i] >= 'A' && k[i] <= 'Z'))) return 0;
    return 1;
}
/* a category is unset (0) or one of the viewer's own: 100-103, 200-204, 300-303 */
static int chat_room_cate(const char *x, int base, int count, int *out)
{
    if (!requests_parse_num(x, 3, out)) return 0;
    return *out == 0 || (*out >= base && *out < base + count);
}
static void chat_room_create_request(BYTE *b)
{
    char buf[512], *p, *tok, *haskey, *zone, *limit, *c0, *c1, *c2, *nick, *name, sj[0x80], id[0x40];
    static WCHAR w[0x40];
    WORD v[6];
    int pw = 0, z, l, k0, k1, k2, n, any = 0, s, lang;
    if (requests_take(REQ_CHATCREATE, buf, sizeof buf) < 0) return;
    g_cr_used = 1;
    p = buf;
    tok = requests_line(&p); haskey = requests_line(&p); zone = requests_line(&p); limit = requests_line(&p);
    c0 = requests_line(&p); c1 = requests_line(&p); c2 = requests_line(&p); nick = requests_line(&p);
    name = requests_line(&p);
    if (haskey[0]) pw = chat_room_password(tok, g_cr_key, sizeof g_cr_key);
    if (chat_room_called_off(tok)) {
        base_log("chat rooms: create called off before it was taken\r\n");
        chat_room_join_refused(NULL);
        return;
    }
    lstrcpynA(g_cr_tok, tok, sizeof g_cr_tok);
    if (!requests_parse_num(zone, 5, &z) || !chat_list_zone_known((DWORD)z) || !requests_parse_num(limit, 2, &l) ||
        l < 2 || l > 20 ||
        !chat_room_cate(c0, 100, 4, &k0) || !chat_room_cate(c1, 200, 5, &k1) || !chat_room_cate(c2, 300, 4, &k2) ||
        (haskey[0] && lstrcmpA(haskey, "1")) || !chat_room_nick_ok(b, nick)) {
        chat_room_join_refused("failed:bad request");
        return;
    }
    n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, name, -1, w, 0x40);
    for (int i = 0; n > 0 && w[i]; i++) {
        if (w[i] < 0x20 || w[i] == 0x7f) n = 0;
        else if (w[i] != L' ' && w[i] != 0x3000) any = 1;
    }
    if (n < 2 || n > 31 || !any || (n = chat_frame_sjis_exact(b, w, sj, 61)) < 1) { chat_room_join_refused("failed:bad name"); return; }
    if ((s = chat_frame_session(b)) < 0) { chat_room_join_refused("failed:no chat"); return; }
    if (haskey[0] && (!pw || !chat_room_new_key_ok(g_cr_key))) { chat_room_join_refused("failed:no password"); return; }
    if (!haskey[0]) g_cr_key[0] = 0;
    memset(id, 0, sizeof id);
    pc_chat_unique_text(b, s, id);
    id[0x34] = 0;
    lang = pc_viewer_language_or(b, 0);
    wsprintfA(g_cr_room, "#%02xCU%s", lang & 0xff, id);
    if (!chat_list_irc_ok(g_cr_room)) { chat_room_join_refused("failed:bad request"); return; }
    /* svc 0x360 turns the zone and categories into 12 characters with no end, and reads a byte past them */
    memset(v, 0, sizeof v);
    v[0] = (WORD)z; v[1] = (WORD)k0; v[2] = (WORD)k1; v[3] = (WORD)k2;
    memset(g_cr_new_topic, 0, sizeof g_cr_new_topic);
    pc_chat_zone_text(b, v, g_cr_new_topic, 8, 0);
    lstrcpynA(g_cr_new_topic + 12, sj, sizeof g_cr_new_topic - 12);
    wsprintfA(g_cr_new_limit, "%d", l);
    g_cr_making = 1;
    chat_room_enter(b);
}
static void chat_room_leave_request(void)
{
    char buf[128];
    char *p = buf, *join;
    if (requests_take(REQ_CHATLEAVE, buf, sizeof buf) < 0) return;
    requests_line(&p);
    join = requests_line(&p);
    if (join[0] && lstrcmpA(join, g_cr_tok)) lstrcpynA(g_cr_cancel_tok, join, sizeof g_cr_cancel_tok);
    if (g_s.cr_st == CR_OUT || g_s.cr_leave) return;
    base_log("chat rooms: leaving %s\r\n", g_cr_room);
    g_s.cr_leave = 1;
}

static void chat_room_in_room_pump(BYTE *b, int ctl)
{
    int s, r;
    chat_room_raw_pump(b);
    if (g_s.cr_st == CR_OUT) return;
    s = chat_frame_session(b);
    if (s < 0) {
        if (g_s.cr_wait != CRW_NONE && g_cr_msg_kind == CRM_SAY) chat_room_send_state("failed:link");
        g_s.cr_wait = CRW_NONE;
        chat_room_left(g_s.cr_st == CR_IN || g_s.cr_st == CR_PART ? "lost" : "failed:link");
        return;
    }
    if (g_s.cr_wait == CRW_CMD || g_s.cr_wait == CRW_MSG) {
        int out[1 + 0x14] = {0};
        r = g_s.cr_wait == CRW_MSG ? pc_chat_message_check(b, s, out) : pc_chat_command_check(b, s);
        if (r == 0 && GetTickCount64() - g_cr_sent_at < CR_WAIT_MS) return;
        if (r == 0) {
            pc_chat_command_forget(b, s);
            base_log("chat rooms: no answer in %us (step %d)\r\n", CR_WAIT_MS / 1000, g_s.cr_st);
            if (g_s.cr_wait == CRW_MSG) { chat_room_msg_done(0, "failed:timeout"); return; }
            if (g_s.cr_st == CR_JOIN && chat_room_members(b)) { chat_room_step_done(b, 1); return; }
            if (g_s.cr_st == CR_AWAY) { chat_room_left("failed:timeout"); return; }
            if (g_s.cr_st == CR_JOIN) {
                g_s.cr_wait = CRW_NONE;
                lstrcpyA(g_cr_part_state, "failed:timeout");
                chat_room_state(g_cr_part_state);
                g_s.cr_leave = 1;
                chat_room_go(CR_PART);
                return;
            }
            if (g_s.cr_st == CR_IN && g_cr_mop) {
                g_s.cr_wait = CRW_NONE;
                chat_room_master_end("failed:timeout");
                return;
            }
            if (g_s.cr_st >= CR_SETTOPIC) {
                g_s.cr_wait = CRW_NONE;
                chat_room_step_done(b, -1);
                return;
            }
            r = 1;
        }
        if (g_s.cr_wait == CRW_MSG) chat_room_msg_done(r, NULL);
        else if (g_s.cr_st == CR_IN && g_cr_mop) chat_room_master_done(r);
        else chat_room_step_done(b, r);
        return;
    }
    if (g_s.cr_leave) {
        if (g_s.cr_st == CR_AWAY || g_s.cr_st == CR_JOIN) { chat_room_left("left"); return; }
        if (g_s.cr_st != CR_PART) chat_room_go(CR_PART);
    }
    if (g_s.cr_st == CR_IN) {
        if (pc_has(b, SV_CHAT_CHANGED) && pc_chat_changed(b, s)) g_cr_reread = 1;
        if (g_cr_reread) {
            g_cr_reread = 0;
            if (!chat_room_members(b)) { chat_room_left(g_cr_kicked_me ? "kicked" : "gone"); return; }
        }
        if (group_chat_busy()) return;
        if (g_cr_msg_kind >= 0) {
            chat_room_msg_start(b, g_cr_msg_kind);
            return;
        }
        if (!chat_room_msg_ready()) return;
        if (g_s.cr_hello) { g_s.cr_hello = 0; chat_room_frame_out(b, CRM_HELLO); return; }
        if (g_cr_mop) { chat_room_master_step(b); return; }
        if (ctl && requests_new(REQ_CHATMASTER)) { chat_room_master_request(); return; }
        if (ctl && requests_new(REQ_CHATSEND)) { chat_room_send_request(b); return; }
        if (g_s.cr_replies > 0) { g_s.cr_replies--; chat_room_frame_out(b, CRM_REPLY); }
        return;
    }
    if (g_s.cr_st == CR_SETTOPIC && (g_s.cr_hello || g_cr_msg_kind >= 0)) {
        if (group_chat_busy()) return;
        if (g_cr_msg_kind >= 0) {
            chat_room_msg_start(b, g_cr_msg_kind);
            return;
        }
        if (!chat_room_msg_ready()) return;
        g_s.cr_hello = 0;
        chat_room_frame_out(b, CRM_HELLO);
        return;
    }
    if (group_chat_busy()) return;
    r = chat_room_command(b, g_s.cr_st);
    if (r == 0) {
        if (++g_s.cr_tries < chat_room_tries_for(g_s.cr_st)) return;
        r = -1;
    }
    if (r < 0) {
        if (r != CR_NOTHING_TO_LEAVE) base_log("chat rooms: step %d couldn't be sent (%d)\r\n", g_s.cr_st, r);
        if (g_s.cr_st == CR_PART) { chat_room_left(g_cr_part_state[0] ? g_cr_part_state : "left"); return; }
        if (g_s.cr_st == CR_AWAY) { chat_room_go(CR_JOIN); return; }
        if (g_s.cr_st == CR_JOIN) { chat_room_left(requests_failed(r)); return; }
        chat_room_step_done(b, r);
        return;
    }
    g_s.cr_wait = CRW_CMD;
    g_cr_sent_at = GetTickCount64();
}

static void chat_room_pump(BYTE *b, int ctl)
{
    if (g_s.draining) return;
    chat_room_in_room_pump(b, ctl);
    if (ctl && (g_s.cr_st != CR_IN || g_s.cr_leave) && requests_waiting(REQ_CHATSEND)) chat_room_send_request(b);
    if (ctl && (g_s.cr_st != CR_IN || g_s.cr_leave) && requests_waiting(REQ_CHATMASTER)) chat_room_master_request();
    if (g_cr.step) chat_list_db_pump(b);
    if (!ctl) return;
    if (requests_waiting(REQ_CHATLEAVE)) { chat_room_leave_request(); return; }
    if (g_s.cr_st == CR_OUT && requests_waiting(REQ_CHATJOIN)) { chat_room_join_request(b); return; }
    if (g_s.cr_st == CR_OUT && requests_waiting(REQ_CHATCREATE)) { chat_room_create_request(b); return; }
    if (g_cr.step || !hold_db_free()) return;
    if (requests_waiting(REQ_CHATZONES)) { chat_list_zones_request(b); return; }
    if (requests_waiting(REQ_CHATROOMS)) chat_list_rooms_request(b);
}

static int chat_room_open(void) { return g_s.cr_st != CR_OUT; }
static int chat_room_cmd_busy(void) { return g_s.cr_wait != CRW_NONE; }

static void chat_room_join_refused_switching(const char *tok, const char *haskey)
{
    char k[sizeof g_cr_key];
    g_cr_used = 1;
    k[0] = 0;
    if (haskey[0]) chat_room_password(tok, k, sizeof k);
    SecureZeroMemory(k, sizeof k);
    if (g_s.cr_st != CR_OUT) return;
    lstrcpynA(g_cr_tok, tok, sizeof g_cr_tok);
    chat_room_join_refused("failed:handle changing");
}
static void chat_room_master_refused_switching(const char *tok, const char *haskey)
{
    char k[sizeof g_cr_newkey];
    g_cr_used = 1;
    k[0] = 0;
    if (haskey[0]) chat_room_password(tok, k, sizeof k);
    SecureZeroMemory(k, sizeof k);
    if (g_cr_mop) return;
    lstrcpynA(g_cr_mtok, tok, sizeof g_cr_mtok);
    chat_room_master_refused("failed:handle changing");
}
static void chat_room_send_refused_switching(const char *tok)
{
    g_cr_used = 1;
    lstrcpynA(g_cr_stok, tok, sizeof g_cr_stok);
    chat_room_send_state("failed:handle changing");
}

static void chat_room_end(BYTE *b)
{
    int s = chat_frame_session(b), r;
    ULONGLONG until = GetTickCount64() + 3000;
    if (g_cr.step) chat_list_db_abandon(b);
    if (g_s.cr_st == CR_OUT) return;
    if (s >= 0 && g_s.cr_wait != CRW_NONE) pc_chat_command_forget(b, s);
    g_s.cr_wait = CRW_NONE;
    while ((r = chat_room_part(b)) == 0 && GetTickCount64() < until) {
        pc_pump_once(b);
        Sleep(16);
    }
    if (r > 0) {
        while (GetTickCount64() < until && pc_chat_command_check(b, s) == 0) {
            pc_pump_once(b);
            Sleep(16);
        }
    }
    chat_room_left("left");
}

static void chat_room_write_lines(void)
{
    char u8[0x620];
    ipc_json file;
    jw w = {0};
    g_cr_written_at = GetTickCount64();
    if (!base_ipc_json_open(IPC_CHATROOM, &file)) return;
    jw_obj(&w, NULL);
    jw_str(&w, "room", g_cr_room, 0x100);
    jw_arr(&w, "lines");
    for (int i = 0; i < g_cr_line_n; i++) {
        const cr_line *l = &g_cr_line[(g_cr_line_next - g_cr_line_n + i + CR_LINES) % CR_LINES];
        jw_obj(&w, NULL);
        jw_uint(&w, "seq", l->seq);
        jw_uint(&w, "time", l->time);
        jw_lit(&w, "kind", g_cr_kind[l->kind]);
        jw_int(&w, "from", l->from);
        jw_bool(&w, "me", l->me);
        jw_int(&w, "colour", l->colour);
        jw_int(&w, "emote", l->emote);
        chat_frame_u8(l->name, u8, sizeof u8);
        jw_str(&w, "name", u8, 0x100);
        chat_frame_u8(l->text, u8, sizeof u8);
        jw_text(&w, "text", u8);
        jw_end(&w);
    }
    jw_end(&w);
    jw_end(&w);
    if (base_ipc_json_commit(&file, !w.fail && base_ipc_json_write(&file, w.p, w.n))) {
        g_cr_lines_dirty = 0;
        g_cr_written_seq = g_cr_seq;
    }
    jw_free(&w);
}
static void chat_room_snapshot(BYTE *b, jw *w)
{
    char part[0x80];
    const pfb_tab *zt, *ct;
    int i;
    if (!g_cr_used) return;
    if (g_cr_lines_dirty && GetTickCount64() - g_cr_written_at >= 1000) chat_room_write_lines();
    jw_obj(w, "chat");
    jw_uint(w, "lineSeq", g_cr_written_seq);
    jw_obj(w, "zones");
    jw_str(w, "token", g_cr_ztok, 100);
    jw_lit(w, "state", g_cr_zstate);
    jw_arr(w, "list");
    zt = chat_list_tab_of(g_cr_cpfb, 0);
    for (i = 0; zt && i < g_cr_zone_n; i++) {
        int e = profile_db_pfb_find(zt, g_cr_zone[i].code);
        if (e < 0) continue;
        jw_obj(w, NULL);
        jw_uint(w, "code", g_cr_zone[i].code);
        profile_db_pfb_part(zt, e, 0, part, sizeof part);
        jw_pol(w, "group", b, part, 0x100, 0x100);
        profile_db_pfb_part(zt, e, 2, part, sizeof part);
        jw_pol(w, "name", b, part, 0x100, 0x100);
        jw_uint(w, "rooms", g_cr_zone[i].rooms);
        jw_uint(w, "people", g_cr_zone[i].people);
        jw_end(w);
    }
    jw_end(w);
    jw_end(w);
    jw_arr(w, "categories");
    ct = chat_list_tab_of(g_cr_rpfb, 7);
    for (i = 0; ct && i < ct->n; i++) {
        jw_obj(w, NULL);
        jw_uint(w, "code", ct->e[i].code);
        profile_db_pfb_part(ct, i, 1, part, sizeof part);
        jw_pol(w, "group", b, part, 0x100, 0x100);
        profile_db_pfb_part(ct, i, 2, part, sizeof part);
        jw_pol(w, "name", b, part, 0x100, 0x100);
        jw_end(w);
    }
    jw_end(w);
    jw_obj(w, "rooms");
    jw_str(w, "token", g_cr_rtok, 100);
    jw_lit(w, "state", g_cr_rstate);
    jw_int(w, "zone", g_cr_rzone);
    jw_int(w, "offset", g_cr_roff);
    jw_int(w, "total", g_cr_rtotal);
    jw_arr(w, "list");
    for (i = 0; i < g_cr_rooms_n; i++) {
        const cr_room *r = &g_cr_rooms[i];
        jw_obj(w, NULL);
        jw_str(w, "irc", r->irc, 100);
        jw_pol(w, "name", b, r->topic, 0x100, 0x300);
        jw_bool(w, "locked", r->locked);
        jw_uint(w, "people", r->people);
        jw_uint(w, "capacity", r->capa);
        jw_arr(w, "categories");
        jw_uint(w, NULL, r->cate[0]);
        jw_uint(w, NULL, r->cate[1]);
        jw_uint(w, NULL, r->cate[2]);
        jw_end(w);
        jw_uint(w, "created", r->ctime);
        jw_end(w);
    }
    jw_end(w);
    jw_end(w);
    jw_obj(w, "room");
    jw_str(w, "token", g_cr_tok, 100);
    jw_lit(w, "state", g_cr_state);
    jw_str(w, "irc", g_s.cr_st == CR_OUT ? "" : g_cr_room, 0x300);
    jw_uint(w, "limit", g_s.cr_st == CR_IN ? *(DWORD *)(g_cr_slot + 0x94) : 0);
    jw_bool(w, "locked", g_s.cr_st == CR_IN && (*(DWORD *)(g_cr_slot + 0x8c) & (1u << 8)));
    jw_arr(w, "members");
    for (i = 0; g_s.cr_st == CR_IN && i < g_cr_people_n; i++) {
        const cr_person *p = &g_cr_people[i];
        char u8[0x80];
        int me = chat_frame_is_me(p->lo, p->hi);
        if (!p->member) continue;
        chat_frame_u8(me ? g_cr_nick : p->name, u8, sizeof u8);
        jw_obj(w, NULL);
        jw_int(w, "key", p->key);
        jw_str(w, "name", u8, 0x100);
        jw_bool(w, "me", me);
        jw_bool(w, "master", p->master);
        jw_bool(w, "away", !me && p->away);
        jw_end(w);
    }
    jw_end(w);
    jw_end(w);
    jw_job(w, "send", g_cr_stok, g_cr_sstate);
    jw_end(w);
    jw_job(w, "master", g_cr_mtok, g_cr_mstate);
    jw_end(w);
    jw_end(w);
}
