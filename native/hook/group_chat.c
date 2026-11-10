/* a friend group's chat, which polcore runs itself (channel, line format, pump). The hook asks for a join, a line or a
   leave like app.dll 0x100487d7 does, and keeps the lines */
#define GC_LINES 256
#define GC_TEXT_MAX 174       /* NOTICE #XXL<16 hex> :<base64> has to fit polcore's 0x200 byte line, past that it drops it */
#define GC_GIVE_UP_MS 30000   /* the viewer's own, then svc 0x6c4 */
#define GC_SEND_MS 30000      /* a line polcore's still holding after this is given up on, it might still go */

typedef struct { DWORD seq, time; int from, me; WCHAR name[0x20]; WCHAR text[0xc0]; } gc_line;
enum { GC_OUT, GC_STARTING, GC_JOINING, GC_IN, GC_LEAVING };
enum { GS_NONE, GS_WAITING, GS_QUEUED };

static int g_gc_st = GC_OUT, g_gc_used, g_gc_my = -1, g_gc_send, g_gc_leave_want, g_gc_lines_dirty, g_gc_have_row = 1;
static DWORD g_gc_lo, g_gc_hi, g_gc_seq, g_gc_written_seq;
static char g_gc_tok[48], g_gc_state[24] = "", g_gc_stok[48], g_gc_sstate[24] = "", g_gc_cancel_tok[48];
static ULONGLONG g_gc_t0, g_gc_sent_at, g_gc_written_at;
static gc_line g_gc_line[GC_LINES];
static int g_gc_line_n, g_gc_line_next;
static volatile LONG g_gc_echo;
static WCHAR g_gc_out[GC_TEXT_MAX + 1];

static void group_chat_state(const char *s)
{
    lstrcpynA(g_gc_state, s, sizeof g_gc_state);
    base_log("groupchat: %s\r\n", s);
    g_snap_dirty = 1;
}
static void group_chat_send_state(const char *s)
{
    lstrcpynA(g_gc_sstate, s, sizeof g_gc_sstate);
    g_snap_dirty = 1;
}
static void group_chat_write_lines(void);
static void group_chat_out(const char *state)
{
    /* the last second's lines go in before the app hears it's over */
    if (g_gc_lines_dirty) group_chat_write_lines();
    if (g_gc_send) group_chat_send_state("failed:left");
    g_gc_send = GS_NONE;
    g_gc_leave_want = 0;
    g_gc_st = GC_OUT;
    group_chat_state(state);
}
static int group_chat_open(void) { return g_gc_st != GC_OUT; }
/* joining and leaving read the link's one command answer, which a chat room's steps read too */
static int group_chat_busy(void) { return g_gc_st == GC_STARTING || g_gc_st == GC_JOINING || g_gc_st == GC_LEAVING; }

/* polcore calls this from its pump on the hold thread, outside its lock. name's its cache and text's on its stack, so
   both get copied and nothing else happens in here */
static void __cdecl group_chat_cb(int member, const WCHAR *name, int nameLen, WCHAR *text, int textLen)
{
    gc_line *l;
    int i;
    if ((g_gc_st != GC_IN && g_gc_st != GC_JOINING && g_gc_st != GC_LEAVING) || !name || !text || textLen < 1) return;
    l = &g_gc_line[g_gc_line_next];
    memset(l, 0, sizeof *l);
    l->seq = ++g_gc_seq;
    l->time = chat_frame_now();
    l->from = member;
    l->me = member == g_gc_my;
    for (i = 0; i < nameLen && i < 0x1f && name[i]; i++) l->name[i] = name[i] < 0x20 ? L' ' : name[i];
    for (i = 0; i < textLen && i < 0xbf && text[i]; i++) l->text[i] = text[i] < 0x20 ? L' ' : text[i];
    g_gc_line_next = (g_gc_line_next + 1) % GC_LINES;
    if (g_gc_line_n < GC_LINES) g_gc_line_n++;
    if (l->me && g_gc_send == GS_QUEUED) g_gc_echo = 1;
    g_gc_lines_dirty = 1;
    g_snap_dirty = 1;
}

static int group_chat_link(BYTE *b) { return pc_has(b, SV_CHAT_SESSION) && pc_chat_session(b) >= 0; }
static int group_chat_joined(BYTE *b)
{
    char ch[0x20];
    return pc_has(b, SV_GCHAT_CHANNEL) && pc_gchat_channel(b, ch) == 0;
}
static void group_chat_force_leave(BYTE *b) { if (pc_has(b, SV_GCHAT_FORCE_LEAVE)) pc_gchat_force_leave(b); }
static int group_chat_line_held(BYTE *b)
{
    return !IsBadReadPtr(pc_gchat_line_held(b), 1) && *pc_gchat_line_held(b) != 0;
}

static int group_chat_my_row(BYTE *b, DWORD lo, DWORD hi, grp_mem *m)
{
    for (int i = 0; i < 64; i++)
        if (groups_get_member(b, lo, hi, i, m) && peers_is_me(b, m->idLo, m->idHi)) return i;
    return -1;
}
/* still in the group, as a member (3-5). 0 it's gone, -1 I've been removed */
static int group_chat_still_in(BYTE *b)
{
    grp_hdr gh;
    grp_mem m;
    int cls, row;
    if (!groups_get_group(b, g_gc_lo, g_gc_hi, &gh)) return 0;
    /* the viewer reads myIndex for every line, polcore can move my row while I'm in */
    g_gc_my = gh.myIndex;
    cls = (gh.flags >> 1) & 7;
    row = group_chat_my_row(b, g_gc_lo, g_gc_hi, &m);
    /* polcore's echo needs my id and the group's handle slot */
    g_gc_have_row = row >= 0 && ((m.hHi >> 13) & 0x3f) == ((gh.flags >> 4) & 0x3f);
    if (row >= 0 && ((m.flags >> 18) & 7) == 1) return -1;
    return cls >= 3 && cls <= 5;
}

static void group_chat_try_start(BYTE *b)
{
    int r = pc_gchat_start(b, g_gc_lo, g_gc_hi, NULL, NULL, NULL, (void *)group_chat_cb, 3, -1, 4, 0, 0);
    (void)b;
    if (r == 0) return;   /* polcore's still leaving or joining something, the viewer tries again next frame */
    if (r < 0) { base_log("groupchat: start refused %d\r\n", r); group_chat_out("failed:join"); return; }
    g_gc_st = GC_JOINING;   /* the app's had "joining" since the request */
}

static void group_chat_join_refused(const char *tok, const char *state)
{
    g_gc_used = 1;
    if (group_chat_open()) return;   /* the one that's open keeps its own token */
    lstrcpynA(g_gc_tok, tok, sizeof g_gc_tok);
    group_chat_state(state);
}
/* <token>, <group id as the snapshot has it, 16 hex>, <my handle's slot>, <its key>. The app's already asked the viewer's
   handle question, this only checks nothing's changed under it */
static void group_chat_join_request(BYTE *b)
{
    char buf[256], *p, *tok, *gid, *sl, *hk, k[17];
    DWORD lo, hi;
    int slot, cls;
    grp_hdr gh;
    grp_mem m;
    if (requests_take(REQ_GROUPCHATJOIN, buf, sizeof buf) < 0) return;
    g_gc_used = 1;
    p = buf;
    tok = requests_line(&p); gid = requests_line(&p); sl = requests_line(&p); hk = requests_line(&p);
    if (g_gc_cancel_tok[0] && !lstrcmpA(g_gc_cancel_tok, tok)) {
        g_gc_cancel_tok[0] = 0;
        base_log("groupchat: join called off before it was taken\r\n");
        return;
    }
    g_gc_cancel_tok[0] = 0;
    if (lstrlenA(gid) != 16 || !groups_hex8(gid, &hi) || !groups_hex8(gid + 8, &lo) || !(lo | hi) ||
        !requests_parse_num(sl, 2, &slot) ||
        slot > 63) {
        group_chat_join_refused(tok, "failed:bad request");
        return;
    }
    if (!pc_has(b, SV_GCHAT_START) || !pc_has(b, SV_GCHAT_JOINED_CHECK) || !group_chat_link(b)) {
        group_chat_join_refused(tok, "failed:no chat");
        return;
    }
    if (!groups_get_group(b, lo, hi, &gh)) { group_chat_join_refused(tok, "failed:no such group"); return; }
    cls = (gh.flags >> 1) & 7;
    if (cls < 3 || cls > 5) { group_chat_join_refused(tok, "failed:not a member"); return; }
    /* the viewer joins straight away when it can't find my row, and only asks about the handle when it can */
    if (group_chat_my_row(b, lo, hi, &m) >= 0) {
        peers_hid_key(hid_of(m.hLo, m.hHi), k);
        if ((int)((m.hHi >> 13) & 0x3f) != slot || lstrcmpA(k, hk)) { group_chat_join_refused(tok, "failed:list changed"); return; }
        if (!pc_has(b, SV_CURRENT_HANDLE) || pc_current_handle(b) != slot) { group_chat_join_refused(tok, "failed:handle"); return; }
    }
    g_gc_leave_want = 0;
    if (g_gc_st == GC_IN && lo == g_gc_lo && hi == g_gc_hi) {
        lstrcpynA(g_gc_tok, tok, sizeof g_gc_tok);
        group_chat_state("in");
        return;
    }
    if (g_gc_send) group_chat_send_state("failed:left");
    g_gc_send = GS_NONE;
    lstrcpynA(g_gc_tok, tok, sizeof g_gc_tok);
    /* the last group's tail goes out under its own id, and the file stays that group's till this one has a line */
    if (g_gc_lines_dirty) group_chat_write_lines();
    g_gc_lo = lo;
    g_gc_hi = hi;
    g_gc_my = gh.myIndex;
    g_gc_line_n = g_gc_line_next = 0;
    g_gc_t0 = GetTickCount64();
    /* in another group's chat already: polcore PARTs it and JOINs this one, the viewer's Switch Group */
    g_gc_st = GC_STARTING;
    group_chat_state("joining");
    if (!chat_room_cmd_busy()) group_chat_try_start(b);
}

static void group_chat_leave(BYTE *b)
{
    int r;
    (void)b;
    g_gc_leave_want = 0;
    /* still in the last group's chat while this one waits to start (a switch), so that one's left */
    if (g_gc_st == GC_STARTING) {
        if (group_chat_joined(b)) group_chat_force_leave(b);
        group_chat_out("left");
        return;
    }
    if (g_gc_st == GC_JOINING) { group_chat_force_leave(b); group_chat_out("left"); return; }
    if (g_gc_st != GC_IN) return;
    if (chat_room_cmd_busy()) { g_gc_leave_want = 1; return; }
    r = pc_gchat_leave(b);
    if (r == 0) { g_gc_leave_want = 1; return; }
    if (r < 0) { group_chat_out("left"); return; }
    g_gc_st = GC_LEAVING;
    g_gc_t0 = GetTickCount64();
    group_chat_state("leaving");
}
/* <token>, <the join it calls off when the app couldn't withdraw that> */
static void group_chat_leave_request(BYTE *b)
{
    char buf[128], *p, *join;
    if (requests_take(REQ_GROUPCHATLEAVE, buf, sizeof buf) < 0) return;
    p = buf;
    requests_line(&p);
    join = requests_line(&p);
    if (join[0] && lstrcmpA(join, g_gc_tok)) lstrcpynA(g_gc_cancel_tok, join, sizeof g_gc_cancel_tok);
    group_chat_leave(b);
}

static void group_chat_send_refused(const char *tok, const char *state)
{
    g_gc_used = 1;
    if (g_gc_send) return;
    lstrcpynA(g_gc_stok, tok, sizeof g_gc_stok);
    group_chat_send_state(state);
}
/* <token>, <the line in UTF-8>. One at a time, like the viewer's Send */
static void group_chat_send_request(BYTE *b)
{
    char buf[0x400], *p, *tok, *text, sj[0x200];
    static WCHAR w[0x200];
    int n;
    if (requests_take(REQ_GROUPCHATSEND, buf, sizeof buf) < 0) return;
    g_gc_used = 1;
    p = buf;
    tok = requests_line(&p);
    text = requests_line(&p);
    if (g_gc_send) { base_log("groupchat: line ignored, the last one's still going\r\n"); return; }
    lstrcpynA(g_gc_stok, tok, sizeof g_gc_stok);
    if (g_gc_st != GC_IN) { group_chat_send_state("failed:not in"); return; }
    n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text, -1, w, 0x200);
    if (n <= 1) { group_chat_send_state(n == 1 ? "failed:empty" : "failed:too long"); return; }
    if (n - 1 > GC_TEXT_MAX) { group_chat_send_state("failed:too long"); return; }
    for (int i = 0; w[i]; i++) if (w[i] < 0x20) w[i] = L' ';
    /* the wire takes UTF-16, but the other side's viewer only shows what Shift-JIS can */
    if (chat_frame_sjis_exact(b, w, sj, sizeof sj) < 0) { group_chat_send_state("failed:characters"); return; }
    lstrcpynW(g_gc_out, w, GC_TEXT_MAX + 1);
    g_gc_send = GS_WAITING;
    g_gc_sent_at = GetTickCount64();
    group_chat_send_state("sending");
}

static void group_chat_send_pump(BYTE *b)
{
    int r;
    if (g_gc_send == GS_WAITING) {
        r = pc_gchat_send(b, g_gc_out);
        if (r == 0) {   /* the last line hasn't gone yet */
            if (GetTickCount64() - g_gc_sent_at >= GC_SEND_MS) {
                g_gc_send = GS_NONE; group_chat_send_state("failed:busy");
            }
            return;
        }
        if (r < 0) {
            g_gc_send = GS_NONE;
            base_log("groupchat: line refused %d\r\n", r);
            group_chat_send_state(requests_failed(r));
            return;
        }
        g_gc_echo = 0;
        g_gc_send = GS_QUEUED;
        g_gc_sent_at = GetTickCount64();
        return;
    }
    if (g_gc_send != GS_QUEUED) return;
    /* NOTICE never gets an answer, so my own copy coming back is the only sign it went.
       polcore echoes as it lets go, so gone with no echo means not sent */
    if (g_gc_echo) { g_gc_send = GS_NONE; group_chat_send_state("sent"); }
    /* with no row for me in the group polcore can't echo (it looks me up first), so gone is all there is to go on,
       and the app shows the line itself */
    else if (!group_chat_line_held(b)) { g_gc_send = GS_NONE; group_chat_send_state(g_gc_have_row ? "failed:not sent" : "sent:unechoed"); }
    else if (GetTickCount64() - g_gc_sent_at >= GC_SEND_MS) {
        g_gc_send = GS_NONE; group_chat_send_state("failed:timeout");
    }
}

static void group_chat_pump(BYTE *b, int ctl)
{
    if (g_s.draining) return;
    if (g_gc_st == GC_STARTING) {
        if (GetTickCount64() - g_gc_t0 >= GC_GIVE_UP_MS) {
            if (group_chat_joined(b)) group_chat_force_leave(b);
            group_chat_out("failed:busy");
            return;
        }
        if (!chat_room_cmd_busy()) group_chat_try_start(b);
    } else if (g_gc_st == GC_JOINING) {
        int r = pc_gchat_joined_check(b);
        if (r > 0) { g_gc_st = GC_IN; group_chat_state("in"); }
        else if (r < 0) group_chat_out("failed:join");
        else if (GetTickCount64() - g_gc_t0 >= GC_GIVE_UP_MS) {
            group_chat_force_leave(b); group_chat_out("failed:busy");
        }
    } else if (g_gc_st == GC_LEAVING) {
        if (pc_gchat_left_check(b) != 0) group_chat_out("left");
        else if (GetTickCount64() - g_gc_t0 >= GC_GIVE_UP_MS) { group_chat_force_leave(b); group_chat_out("left"); }
    } else if (g_gc_st == GC_IN) {
        int still;
        if (!group_chat_link(b)) { group_chat_out("lost"); return; }
        /* polcore leaves on its own when the group goes or I'm taken out of it, and says nothing */
        if ((still = group_chat_still_in(b)) <= 0) {
            group_chat_force_leave(b);
            group_chat_out(still < 0 ? "removed" : "gone");
            return;
        }
        if (!group_chat_joined(b)) { group_chat_out("lost"); return; }
        /* the viewer's once-a-second "in chat" refresh, which is local */
        if (ctl && pc_has(b, SV_IN_CHAT_REFRESH) && pc_in_chat_refresh(b, 0) > 0) g_snap_dirty = 1;
        if (g_gc_leave_want && !chat_room_cmd_busy()) { group_chat_leave(b); return; }
        group_chat_send_pump(b);
    }
    if (!ctl) return;
    if (requests_waiting(REQ_GROUPCHATLEAVE)) { group_chat_leave_request(b); return; }
    /* a join waits for the last one, or a leave, to settle rather than being turned away */
    if (!group_chat_busy() && requests_waiting(REQ_GROUPCHATJOIN)) { group_chat_join_request(b); return; }
    if (requests_waiting(REQ_GROUPCHATSEND)) group_chat_send_request(b);
}

/* a handle switch, Play, or leaving the group: out now, through the one PART, before anything else touches the link.
   No room command can be waiting on its answer by now */
static void group_chat_end_by(BYTE *b, const char *state, int reset, ULONGLONG until)
{
    int r = 0;
    if (g_gc_st == GC_OUT && !group_chat_joined(b)) {
        if (reset && pc_has(b, SV_GCHAT_SIGN_OUT)) pc_gchat_sign_out(b);
        return;
    }
    if (g_gc_st == GC_IN && pc_has(b, SV_GCHAT_LEAVE) && pc_gchat_leave(b) > 0) {
        base_log("groupchat: leaving\r\n");
        while (GetTickCount64() < until && (r = pc_gchat_left_check(b)) == 0) {
            pc_pump_once(b);
            Sleep(16);
        }
        base_log("groupchat: PART answered %d\r\n", r);
    }
    if (group_chat_joined(b) || g_gc_st == GC_JOINING) {
        group_chat_force_leave(b);
        while (GetTickCount64() < until && group_chat_joined(b)) {
            pc_pump_once(b);
            Sleep(16);
        }
    }
    /* polcore keeps a pointer to group_chat_cb till the PART goes or this, and FFXI's about to own the link */
    if (reset && pc_has(b, SV_GCHAT_SIGN_OUT)) pc_gchat_sign_out(b);
    group_chat_out(state);
}
static void group_chat_end(BYTE *b, const char *state, int reset)
{
    group_chat_end_by(b, state, reset, GetTickCount64() + 3000);
}

/* polcore only PARTs once I'm out of the group, too late for the others to see, so the PART goes first here */
static void group_chat_group_going(BYTE *b, DWORD lo, DWORD hi)
{
    ULONGLONG until = GetTickCount64() + 3000;
    if (!group_chat_open() || g_gc_lo != lo || g_gc_hi != hi) return;
    /* a room command's answer comes first, or the room would read the PART's. The room's pump returns once it's read
       one, so it can't start another in here */
    while (chat_room_cmd_busy() && GetTickCount64() < until) {
        pc_pump_once(b);
        chat_room_in_room_pump(b, 0);
        Sleep(16);
    }
    if (chat_room_cmd_busy()) {
        /* marked to go, it goes after the room's answer from the hold loop, so after the group change */
        base_log("groupchat: the room's still waiting on its answer, so the PART can't go first\r\n");
        group_chat_force_leave(b);
        group_chat_out("gone");
    } else group_chat_end_by(b, "gone", 0, until);
}
static void group_chat_signed_out(void)
{
    if (g_gc_st != GC_OUT) group_chat_out("lost");
}

static void group_chat_write_lines(void)
{
    char id[17], u8[0x300];
    ipc_json file;
    jw w = {0};
    g_gc_lines_dirty = 0;
    g_gc_written_seq = g_gc_seq;
    g_gc_written_at = GetTickCount64();
    if (!base_ipc_json_open(IPC_GROUPCHAT, &file)) return;
    wsprintfA(id, "%08x%08x", g_gc_hi, g_gc_lo);
    jw_obj(&w, NULL);
    jw_lit(&w, "group", id);
    jw_arr(&w, "lines");
    for (int i = 0; i < g_gc_line_n; i++) {
        const gc_line *l = &g_gc_line[(g_gc_line_next - g_gc_line_n + i + GC_LINES) % GC_LINES];
        jw_obj(&w, NULL);
        jw_uint(&w, "seq", l->seq);
        jw_uint(&w, "time", l->time);
        jw_int(&w, "from", l->from);
        jw_bool(&w, "me", l->me);
        chat_frame_u8(l->name, u8, sizeof u8);
        jw_str(&w, "name", u8, 0x100);
        chat_frame_u8(l->text, u8, sizeof u8);
        jw_text(&w, "text", u8);
        jw_end(&w);
    }
    jw_end(&w);
    jw_end(&w);
    base_ipc_json_commit(&file, !w.fail && base_ipc_json_write(&file, w.p, w.n));
    jw_free(&w);
}
static void group_chat_snapshot(BYTE *b, jw *w)
{
    char id[17];
    (void)b;
    if (!g_gc_used) return;
    if (g_gc_lines_dirty && GetTickCount64() - g_gc_written_at >= 1000) group_chat_write_lines();
    wsprintfA(id, "%08x%08x", g_gc_hi, g_gc_lo);
    jw_job(w, "groupChat", g_gc_tok, g_gc_state);
    jw_lit(w, "group", id);
    jw_uint(w, "lineSeq", g_gc_written_seq);
    jw_job(w, "send", g_gc_stok, g_gc_sstate);
    jw_end(w);
    jw_end(w);
}
