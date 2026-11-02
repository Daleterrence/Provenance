/* a friend group's chat. polcore runs the lot (its own channel, line format and pump, app.dll FUN_100487d7 asks it
   the same way), the hook only asks for a join, a line or a leave and keeps the lines its callback hands back */
#define GC_LINES 256
#define GC_TEXT_MAX 174       /* NOTICE #XXL<16 hex> :<base64> has to fit polcore's 0x200 byte line, past that it drops it */
#define GC_GIVE_UP_MS 30000   /* the viewer's own, then svc 0x6c4 */
#define GC_ECHO_MS 5000

typedef struct { DWORD seq, time; int from, me; WCHAR name[0x20]; WCHAR text[0xc0]; } gc_line;
enum { GC_OUT, GC_STARTING, GC_JOINING, GC_IN, GC_LEAVING };
enum { GS_NONE, GS_WAITING, GS_QUEUED };

static int g_gc_st = GC_OUT, g_gc_used, g_gc_my = -1, g_gc_send, g_gc_leave_want, g_gc_lines_dirty;
static DWORD g_gc_lo, g_gc_hi, g_gc_seq, g_gc_written_seq;
static char g_gc_tok[48], g_gc_state[24] = "", g_gc_stok[48], g_gc_sstate[24] = "", g_gc_cancel_tok[48];
static ULONGLONG g_gc_t0, g_gc_sent_at, g_gc_written_at;
static gc_line g_gc_line[GC_LINES];
static int g_gc_line_n, g_gc_line_next;
static volatile LONG g_gc_echo;
static WCHAR g_gc_out[GC_TEXT_MAX + 1];

typedef int (__cdecl *gc_start_t)(DWORD, DWORD, const WCHAR *, const WCHAR *, const WCHAR *, void *, int, int, int, int,
                                  int);

static void gc_state(const char *s)
{
    lstrcpynA(g_gc_state, s, sizeof g_gc_state);
    lg("groupchat: %s\r\n", s);
    g_snap_dirty = 1;
}
static void gc_send_state(const char *s)
{
    lstrcpynA(g_gc_sstate, s, sizeof g_gc_sstate);
    g_snap_dirty = 1;
}
static void gc_out(const char *state)
{
    if (g_gc_send) gc_send_state("failed:left");
    g_gc_send = GS_NONE;
    g_gc_leave_want = 0;
    g_gc_st = GC_OUT;
    gc_state(state);
}
static int gc_open(void) { return g_gc_st != GC_OUT; }
/* joining and leaving read the link's one command answer, which a chat room's steps read too */
static int gc_busy(void) { return g_gc_st == GC_STARTING || g_gc_st == GC_JOINING || g_gc_st == GC_LEAVING; }

/* polcore calls this from its pump on the hold thread, outside its lock. name's its cache and text's on its stack, so
   both get copied and nothing else happens in here */
static void __cdecl gc_cb(int member, const WCHAR *name, int nameLen, WCHAR *text, int textLen)
{
    gc_line *l;
    int i;
    if ((g_gc_st != GC_IN && g_gc_st != GC_JOINING && g_gc_st != GC_LEAVING) || !name || !text || textLen < 1) return;
    l = &g_gc_line[g_gc_line_next];
    memset(l, 0, sizeof *l);
    l->seq = ++g_gc_seq;
    l->time = cr_now();
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

static int gc_link(BYTE *b) { return SVC(0xcd0) && ((svc_v)SVC(0xcd0))() >= 0; }
static int gc_joined(BYTE *b)
{
    char ch[0x20];
    return SVC(0x6cc) && ((int (__cdecl *)(char *))SVC(0x6cc))(ch) == 0;
}
static void gc_force_leave(BYTE *b) { if (SVC(0x6c4)) ((svc_v)SVC(0x6c4))(); }

/* my row in the group, -1 when polcore hasn't got one for me */
static int gc_my_row(BYTE *b, DWORD lo, DWORD hi, grp_mem *m)
{
    for (int i = 0; i < 64; i++)
        if (get_member(b, lo, hi, i, m) && is_me(b, m->idLo, m->idHi)) return i;
    return -1;
}
/* still in the group, as a member (3-5). 0 it's gone, -1 I've been removed */
static int gc_still_in(BYTE *b)
{
    grp_hdr gh;
    grp_mem m;
    int cls;
    if (!get_group(b, g_gc_lo, g_gc_hi, &gh)) return 0;
    cls = (gh.flags >> 1) & 7;
    if (gc_my_row(b, g_gc_lo, g_gc_hi, &m) >= 0 && ((m.flags >> 18) & 7) == 1) return -1;
    return cls >= 3 && cls <= 5;
}

static void gc_try_start(BYTE *b)
{
    int r = ((gc_start_t)SVC(0x6a8))(g_gc_lo, g_gc_hi, NULL, NULL, NULL, (void *)gc_cb, 3, -1, 4, 0, 0);
    (void)b;
    if (r == 0) return;   /* polcore's still leaving or joining something, the viewer tries again next frame */
    if (r < 0) { lg("groupchat: start refused %d\r\n", r); gc_out("failed:join"); return; }
    g_gc_st = GC_JOINING;
    gc_state("joining");
}

static void gc_join_refused(const char *tok, const char *state)
{
    g_gc_used = 1;
    if (gc_open()) return;   /* the one that's open keeps its own token */
    lstrcpynA(g_gc_tok, tok, sizeof g_gc_tok);
    gc_state(state);
}
/* <token>, <group id as the snapshot has it, 16 hex>, <my handle's slot>, <its key>. The app's already asked the viewer's
   handle question, this only checks nothing's changed under it */
static void gc_join_request(BYTE *b)
{
    char buf[256], *p, *tok, *gid, *sl, *hk, k[17];
    DWORD lo, hi;
    int slot, cls;
    grp_hdr gh;
    grp_mem m;
    if (take_request("groupchatjoin", buf, sizeof buf) < 0) return;
    g_gc_used = 1;
    p = buf;
    tok = req_line(&p); gid = req_line(&p); sl = req_line(&p); hk = req_line(&p);
    if (g_gc_cancel_tok[0] && !lstrcmpA(g_gc_cancel_tok, tok)) {
        g_gc_cancel_tok[0] = 0;
        lg("groupchat: join called off before it was taken\r\n");
        return;
    }
    g_gc_cancel_tok[0] = 0;
    if (gc_busy()) { gc_join_refused(tok, "failed:busy"); return; }
    if (notice_blocks(g_notice)) { gc_join_refused(tok, "failed:account notice"); return; }
    if (lstrlenA(gid) != 16 || !hex8(gid, &hi) || !hex8(gid + 8, &lo) || !(lo | hi) || !parse_num(sl, 2, &slot) ||
        slot > 63) {
        gc_join_refused(tok, "failed:bad request");
        return;
    }
    if (!SVC(0x6a8) || !SVC(0x6b0) || !gc_link(b)) { gc_join_refused(tok, "failed:no chat"); return; }
    if (!get_group(b, lo, hi, &gh)) { gc_join_refused(tok, "failed:no such group"); return; }
    cls = (gh.flags >> 1) & 7;
    if (cls < 3 || cls > 5) { gc_join_refused(tok, "failed:not a member"); return; }
    /* the viewer joins straight away when it can't find my row, and only asks about the handle when it can */
    if (gc_my_row(b, lo, hi, &m) >= 0) {
        handle_key((const BYTE *)&m.hLo, k);
        if ((int)((m.hHi >> 13) & 0x3f) != slot || lstrcmpA(k, hk)) { gc_join_refused(tok, "failed:list changed"); return; }
        if (!SVC(0x2f8) || ((svc_v)SVC(0x2f8))() != slot) { gc_join_refused(tok, "failed:handle"); return; }
    }
    if (g_gc_st == GC_IN && lo == g_gc_lo && hi == g_gc_hi) {
        lstrcpynA(g_gc_tok, tok, sizeof g_gc_tok);
        gc_state("in");
        return;
    }
    if (g_gc_send) gc_send_state("failed:left");
    g_gc_send = GS_NONE;
    lstrcpynA(g_gc_tok, tok, sizeof g_gc_tok);
    g_gc_lo = lo;
    g_gc_hi = hi;
    g_gc_my = gh.myIndex;
    g_gc_line_n = g_gc_line_next = 0;
    g_gc_lines_dirty = 1;
    g_gc_t0 = GetTickCount64();
    /* in another group's chat already: polcore PARTs it and JOINs this one, the viewer's Switch Group */
    g_gc_st = GC_STARTING;
    gc_state("joining");
    if (!cr_cmd_busy()) gc_try_start(b);
}

static void gc_leave(BYTE *b)
{
    int r;
    (void)b;
    g_gc_leave_want = 0;
    if (g_gc_st == GC_STARTING) { gc_out("left"); return; }
    if (g_gc_st == GC_JOINING) { gc_force_leave(b); gc_out("left"); return; }
    if (g_gc_st != GC_IN) return;
    if (cr_cmd_busy()) { g_gc_leave_want = 1; return; }
    r = ((svc_v)SVC(0x6b4))();
    if (r == 0) { g_gc_leave_want = 1; return; }
    if (r < 0) { gc_out("left"); return; }
    g_gc_st = GC_LEAVING;
    g_gc_t0 = GetTickCount64();
    gc_state("leaving");
}
/* <token>, <the join it calls off when the app couldn't withdraw that> */
static void gc_leave_request(BYTE *b)
{
    char buf[128], *p, *join;
    if (take_request("groupchatleave", buf, sizeof buf) < 0) return;
    p = buf;
    req_line(&p);
    join = req_line(&p);
    if (join[0] && lstrcmpA(join, g_gc_tok)) lstrcpynA(g_gc_cancel_tok, join, sizeof g_gc_cancel_tok);
    gc_leave(b);
}

static void gc_send_refused(const char *tok, const char *state)
{
    g_gc_used = 1;
    if (g_gc_send) return;
    lstrcpynA(g_gc_stok, tok, sizeof g_gc_stok);
    gc_send_state(state);
}
/* <token>, <the line in UTF-8>. One at a time, like the viewer's Send */
static void gc_send_request(BYTE *b)
{
    char buf[0x400], *p, *tok, *text, sj[0x200];
    static WCHAR w[0x200];
    int n;
    if (take_request("groupchatsend", buf, sizeof buf) < 0) return;
    g_gc_used = 1;
    p = buf;
    tok = req_line(&p);
    text = req_line(&p);
    if (g_gc_send) { lg("groupchat: line ignored, the last one's still going\r\n"); return; }
    lstrcpynA(g_gc_stok, tok, sizeof g_gc_stok);
    if (g_gc_st != GC_IN) { gc_send_state("failed:not in"); return; }
    n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text, -1, w, 0x200);
    if (n <= 1) { gc_send_state(n == 1 ? "failed:empty" : "failed:too long"); return; }
    if (n - 1 > GC_TEXT_MAX) { gc_send_state("failed:too long"); return; }
    for (int i = 0; w[i]; i++) if (w[i] < 0x20) w[i] = L' ';
    /* the wire takes UTF-16, but the other side's viewer only shows what Shift-JIS can */
    if (cr_sjis_exact(b, w, sj, sizeof sj) < 0) { gc_send_state("failed:characters"); return; }
    lstrcpynW(g_gc_out, w, GC_TEXT_MAX + 1);
    g_gc_send = GS_WAITING;
    gc_send_state("sending");
}

static void gc_send_pump(BYTE *b)
{
    int r;
    if (g_gc_send == GS_WAITING) {
        r = ((int (__cdecl *)(const WCHAR *))SVC(0x6bc))(g_gc_out);
        if (r == 0) return;   /* the last line hasn't gone yet */
        if (r < 0) {
            g_gc_send = GS_NONE;
            lg("groupchat: line refused %d\r\n", r);
            gc_send_state(failed(r));
            return;
        }
        g_gc_echo = 0;
        g_gc_send = GS_QUEUED;
        g_gc_sent_at = GetTickCount64();
        return;
    }
    if (g_gc_send != GS_QUEUED) return;
    /* NOTICE never gets an answer, so my own copy coming back is the only sign it went */
    if (g_gc_echo) { g_gc_send = GS_NONE; gc_send_state("sent"); }
    else if (GetTickCount64() - g_gc_sent_at >= GC_ECHO_MS) { g_gc_send = GS_NONE; gc_send_state("failed:not sent"); }
}

/* every hold loop turn. Requests on control turns only */
static void gc_pump(BYTE *b, int ctl)
{
    if (g_draining) return;
    if (g_gc_st == GC_STARTING) {
        if (GetTickCount64() - g_gc_t0 >= GC_GIVE_UP_MS) { gc_out("failed:busy"); return; }
        if (!cr_cmd_busy()) gc_try_start(b);
    } else if (g_gc_st == GC_JOINING) {
        int r = ((svc_v)SVC(0x6b0))();
        if (r > 0) { g_gc_st = GC_IN; gc_state("in"); }
        else if (r < 0) gc_out("failed:join");
        else if (GetTickCount64() - g_gc_t0 >= GC_GIVE_UP_MS) { gc_force_leave(b); gc_out("failed:busy"); }
    } else if (g_gc_st == GC_LEAVING) {
        if (((svc_v)SVC(0x6b8))() != 0) gc_out("left");
        else if (GetTickCount64() - g_gc_t0 >= GC_GIVE_UP_MS) { gc_force_leave(b); gc_out("left"); }
    } else if (g_gc_st == GC_IN) {
        int still;
        if (!gc_link(b)) { gc_out("lost"); return; }
        /* polcore leaves on its own when the group goes or I'm taken out of it, and says nothing */
        if ((still = gc_still_in(b)) <= 0) {
            gc_force_leave(b);
            gc_out(still < 0 ? "removed" : "gone");
            return;
        }
        if (!gc_joined(b)) { gc_out("lost"); return; }
        /* the viewer's once-a-second "in chat" refresh, which is local */
        if (ctl && SVC(0x680) && ((svc_i)SVC(0x680))(0) > 0) g_snap_dirty = 1;
        if (g_gc_leave_want && !cr_cmd_busy()) { gc_leave(b); return; }
        gc_send_pump(b);
    }
    if (!ctl) return;
    if (request_waiting("groupchatleave")) { gc_leave_request(b); return; }
    if (request_waiting("groupchatjoin")) { gc_join_request(b); return; }
    if (request_waiting("groupchatsend")) gc_send_request(b);
}

/* a handle switch or Play: out now, through the one PART, before anything else touches the link */
static void gc_end(BYTE *b, const char *state, int reset)
{
    ULONGLONG until = GetTickCount64() + 3000;
    if (g_gc_st == GC_OUT) {
        if (reset && SVC(0x6c8)) ((svc_v)SVC(0x6c8))();
        return;
    }
    if (g_gc_st == GC_IN && SVC(0x6b4) && ((svc_v)SVC(0x6b4))() > 0)
        while (GetTickCount64() < until && ((svc_v)SVC(0x6b8))() == 0) {
            POLCON_STEP(b);
            POLCON_IDLE(b);
            Sleep(16);
        }
    if (gc_joined(b) || g_gc_st == GC_JOINING) {
        gc_force_leave(b);
        while (GetTickCount64() < until && gc_joined(b)) {
            POLCON_STEP(b);
            POLCON_IDLE(b);
            Sleep(16);
        }
    }
    /* polcore keeps a pointer to gc_cb till the PART goes or this, and FFXI's about to own the link */
    if (reset && SVC(0x6c8)) ((svc_v)SVC(0x6c8))();
    gc_out(state);
}
/* the sign-out's svc 0x6c8 has already dropped it */
static void gc_signed_out(void)
{
    if (g_gc_st != GC_OUT) gc_out("lost");
}

static void gc_write_lines(void)
{
    char path[MAX_PATH + 64], tmp[MAX_PATH + 80], line[0x200], nm[0x100], tx[0x400], u8[0x300];
    HANDLE h;
    g_gc_lines_dirty = 0;
    g_gc_written_seq = g_gc_seq;
    g_gc_written_at = GetTickCount64();
    ipc_file(path, "groupchat.json");
    lstrcpyA(tmp, path); lstrcatA(tmp, ".tmp");
    h = CreateFileA(tmp, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    wsprintfA(line, "{\"group\":\"%08x%08x\",\"lines\":[", g_gc_hi, g_gc_lo);
    wr(h, line);
    for (int i = 0; i < g_gc_line_n; i++) {
        const gc_line *l = &g_gc_line[(g_gc_line_next - g_gc_line_n + i + GC_LINES) % GC_LINES];
        cr_u8(l->name, u8, sizeof u8);
        json_str(nm, sizeof nm, u8);
        cr_u8(l->text, u8, sizeof u8);
        json_text(tx, sizeof tx, u8);
        wsprintfA(line, "%s{\"seq\":%lu,\"time\":%lu,\"from\":%d,\"me\":%s,\"name\":%s,\"text\":", i ? "," : "", l->seq,
                  l->time, l->from, l->me ? "true" : "false", nm);
        wr(h, line);
        wr(h, tx);
        wr(h, "}");
    }
    wr(h, "]}");
    CloseHandle(h);
    MoveFileExA(tmp, path, MOVEFILE_REPLACE_EXISTING);
}
static void gc_snapshot(BYTE *b, HANDLE h)
{
    char line[0x200], tk[100], st[100];
    (void)b;
    if (!g_gc_used) return;
    if (g_gc_lines_dirty && GetTickCount64() - g_gc_written_at >= 1000) gc_write_lines();
    json_str(tk, sizeof tk, g_gc_tok);
    wsprintfA(line, ",\"groupChat\":{\"token\":%s,\"state\":\"%s\",\"group\":\"%08x%08x\",\"lineSeq\":%lu", tk, g_gc_state,
              g_gc_hi, g_gc_lo, g_gc_written_seq);
    wr(h, line);
    json_str(st, sizeof st, g_gc_stok);
    wsprintfA(line, ",\"send\":{\"token\":%s,\"state\":\"%s\"}}", st, g_gc_sstate);
    wr(h, line);
}
