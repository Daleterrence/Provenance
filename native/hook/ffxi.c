enum {
    FX_LOOK_AFTER_MS = 20000, FX_RETRY_MS = 30000, FX_TRIES = 5,
    FX_RING = 16, FX_NAME = 48, FX_TEXT = 640, FX_SEEN = 1024, FX_GIVE_UP_MS = 30000, FX_MOVE_AFTER_MS = 3000,
    FX_MOVE_RETRY_MS = 2000, FX_MOVE_TRIES = 5, FX_SCAN_MS = 5000, FX_PRINTS = 3, FX_WORLD_MS = 5000,
};
enum { FX_FREE, FX_QUEUED, FX_PRINTED };
enum { FX_IN, FX_NOTE, FX_BOUNCE, FX_MOVE };
enum { FX_CMD_NONE, FX_CMD_M, FX_CMD_RM };
enum { FX_SENDS = 4, FX_SEND_GAP_MS = 5000, FX_LINE_IN = 150, FX_PLAIN = 0x800, FX_SENT_IDS = 64, FX_COPIES = 4 };

/* 0x1f and a chat mode gives the rest of the line that mode's colour. 0xdb's is blue, 0x7b's the red of the game's own refusals */
static const char g_fx_blue[] = "\x1f\xdb", g_fx_red[] = "\x1f\x7b";
static const char g_fx_not_sent[] = "This message was not sent. Please try again.";

typedef unsigned (__cdecl *fx_count_fn)(void);
typedef int (__cdecl *fx_mark_fn)(void *);
typedef const BYTE *(__cdecl *fx_lookup_fn)(const BYTE *, const char *);
typedef int (__cdecl *fx_filter_fn)(char *);
typedef int (__cdecl *fx_sound_on_fn)(int);
typedef void (__cdecl *fx_sound_fn)(void);
/* thiscall: the object in ECX and the callee pops, which __fastcall with EDX left unused matches */
typedef BYTE (__fastcall *fx_add_line_fn)(void *log, void *unused, const char *text, const BYTE *mode, int indent, int expand,
                                          int which);
typedef int (__fastcall *fx_expand_fn)(void *at, void *unused, char *dst, int cap, const char *src, int len, int a, int b);

typedef struct { DWORD lo, hi; int slot; char name[FX_NAME], text[FX_TEXT]; } fx_send;
typedef struct { DWORD time, seq; BYTE from_slot; } fx_sent_id;

static const int g_fx_core[] = { AT_FX_COUNT_CALL, AT_FX_MARK_CALL, AT_FX_ADD_LINE, AT_FX_CHAT_LOG };

typedef struct {
    volatile LONG state;
    volatile DWORD printed_at;
    int kind, sound, move, tries;
    DWORD next_try;
    char name[FX_NAME], text[FX_TEXT];
    BYTE hdr[sizeof(pol_mail)];
    char file[100], now[100];
} fx_line;

typedef struct { char name[100]; DWORD from_lo, from_hi, seq, time, first, gen; int slot, done; } fx_seen_rec;

static struct {
    volatile LONG found, first_poll, world, off, patched, watching, in_world, dirty, moved, nsent_ids, worker_dead;
    volatile DWORD netstat;
    BYTE *base;
    DWORD size, gen, old_time;
    int tries, print_dead, unsent, old_said;
    HANDLE find;
    ULONGLONG polled_at, next_look, next_scan;
    fx_count_fn count;
    fx_mark_fn mark;
    fx_lookup_fn lookup;
    DWORD count_was, mark_was, lookup_was;
    BYTE **unread_mgr;
    int commands;
    HANDLE change;
    char dir[MAX_PATH + 64], quiet[MAX_PATH + 64];
    fx_line ring[FX_RING];
    int tail, head;
    fx_seen_rec seen[FX_SEEN];
    int nseen, seen_full;
    DWORD last_lo, last_hi;
    int last_slot;
    char last_name[FX_NAME];
    fx_send sends[FX_SENDS];
    int nsends, job;
    BYTE *msg;
    ULONGLONG sent_at, started;
    fx_sent_id sent_ids[FX_SENT_IDS];
    void *volatile copies[FX_COPIES];
} g_fx;

static int fx_missing(void)
{
    int i, n = 0;
    for (i = 0; i < (int)(sizeof g_fx_core / sizeof g_fx_core[0]); i++)
        if (!pc_addr_found(g_fx_core[i])) n++;
    return n;
}

static int fx_lead(BYTE c) { return (c >= 0x81 && c <= 0x9f) || (c >= 0xe0 && c <= 0xfc); }

/* Shift-JIS pairs and printable bytes stay, anything that could recolour, split or expand the line becomes a space */
static int fx_clean(const BYTE *in, int n, char *out, int cap)
{
    int i = 0, o = 0;
    while (i < n && in[i] && o < cap - 1) {
        BYTE c = in[i];
        if (fx_lead(c)) {
            BYTE t = i + 1 < n ? in[i + 1] : 0;
            if ((t >= 0x40 && t <= 0x7e) || (t >= 0x80 && t <= 0xfc)) {
                if (o + 2 >= cap) break;
                out[o++] = (char)c;
                out[o++] = (char)t;
                i += 2;
                continue;
            }
            out[o++] = ' ';
            i++;
            continue;
        }
        out[o++] = (c >= 0x20 && c <= 0x7e) || (c >= 0xa1 && c <= 0xdf) ? (char)c : ' ';
        i++;
    }
    out[o] = 0;
    return o;
}

static void fx_append(char *out, int cap, const char *s)
{
    int n = lstrlenA(out);
    if (n < cap - 1) lstrcpynA(out + n, s, cap - n);
}

static DWORD g_fx_code, g_fx_at;
static const char *g_fx_step = "";
static int fx_caught(EXCEPTION_POINTERS *ep)
{
    g_fx_code = ep->ExceptionRecord->ExceptionCode;
    g_fx_at = (DWORD)(ULONG_PTR)ep->ExceptionRecord->ExceptionAddress;
    return EXCEPTION_EXECUTE_HANDLER;
}
static void fx_fault(const char *where, const char *step)
{
    g_fx.off = 1;
    /* a fault inside the game's own chat or sound code could leave it half done, so nothing more gets printed */
    if (!lstrcmpA(step, "add-line") || !lstrcmpA(step, "word filter") || !lstrcmpA(step, "message sound")) g_fx.print_dead = 1;
    if (g_fx.base && g_fx_at >= (DWORD)(ULONG_PTR)g_fx.base && g_fx_at < (DWORD)(ULONG_PTR)g_fx.base + g_fx.size)
        base_log("ffxi: fault %08x at FFXiMain+%x %s (%s), messages go back to the game's own way\r\n", g_fx_code,
                 g_fx_at - (DWORD)(ULONG_PTR)g_fx.base, where, step);
    else
        base_log("ffxi: fault %08x at %08x %s (%s), messages go back to the game's own way\r\n", g_fx_code, g_fx_at, where, step);
}

static int fx_print(const char *colour, const char *a, const char *b, const char *c)
{
    static char line[FX_TEXT + FX_NAME + 32];
    /* a writable block, the way the game's own callers pass it. A read-only one faulted */
    BYTE mode[4] = { 0x12, 0, 0, 0 };
    void *log = *(void **)pc_addr_of(g_fx.base, AT_FX_CHAT_LOG);
    if (!log || g_fx.print_dead) return 0;
    lstrcpynA(line, colour, sizeof line);
    fx_append(line, sizeof line, a);
    fx_append(line, sizeof line, b);
    fx_append(line, sizeof line, c);
    g_fx_step = "add-line";
    ((fx_add_line_fn)pc_addr_of(g_fx.base, AT_FX_ADD_LINE))(log, NULL, line, mode, 0, 1, 0);
    g_fx_step = "";
    return 1;
}

static int fx_print_line(fx_line *l)
{
    static char text[FX_TEXT], name[FX_NAME + 4];
    lstrcpynA(text, l->text, sizeof text);
    if (l->kind == FX_BOUNCE) {
        if (!fx_print(g_fx_red, g_fx_not_sent, "", "")) return 0;
    } else if (l->kind == FX_NOTE) {
        if (!fx_print(g_fx_blue, text, "", "")) return 0;
    } else {
        g_fx_step = "word filter";
        if (pc_addr_found(AT_FX_WORD_FILTER)) ((fx_filter_fn)pc_addr_of(g_fx.base, AT_FX_WORD_FILTER))(text);
        g_fx_step = "";
        wsprintfA(name, "[%s] ", l->name);
        if (!fx_print(g_fx_blue, name, ">> ", text)) return 0;
        g_fx.last_lo = MAIL(l->hdr)->from_lo;
        g_fx.last_hi = MAIL(l->hdr)->from_hi;
        g_fx.last_slot = MAIL(l->hdr)->from_slot;
        lstrcpynA(g_fx.last_name, l->name, sizeof g_fx.last_name);
    }
    g_fx_step = "message sound";
    if (l->sound && pc_addr_found(AT_FX_SOUND_ON) && pc_addr_found(AT_FX_SOUND) &&
        ((fx_sound_on_fn)pc_addr_of(g_fx.base, AT_FX_SOUND_ON))(0x48))
        ((fx_sound_fn)pc_addr_of(g_fx.base, AT_FX_SOUND))();
    g_fx_step = "";
    return 1;
}

/* the game's own netstat update is the only caller, every frame the box is drawn, and it's only drawn in the world */
static unsigned __cdecl fx_count(void)
{
    g_fx.netstat = GetTickCount() | 1;
    if (g_fx.watching && !g_fx.off) return 0;
    return g_fx.count();
}
static int __cdecl fx_mark(void *entry)
{
    if (g_fx.watching && !g_fx.off) return 0;
    return g_fx.mark(entry);
}

static int fx_in_image(const BYTE *p) { return p >= g_fx.base && p < g_fx.base + g_fx.size; }

/* game thread only, so nothing's running the call being changed */
static int fx_repoint(int id, void *to, DWORD *was, void **orig)
{
    BYTE *site = pc_addr_of(g_fx.base, id);
    DWORD old, rel = (DWORD)((BYTE *)to - (site + 5));
    if (site[0] != 0xe8) return 0;
    *was = *(DWORD *)(site + 1);
    *orig = site + 5 + (int)*was;
    if (!fx_in_image((BYTE *)*orig) || !VirtualProtect(site + 1, 4, PAGE_EXECUTE_READWRITE, &old)) return 0;
    *(DWORD *)(site + 1) = rel;
    VirtualProtect(site + 1, 4, old, &old);
    FlushInstructionCache(GetCurrentProcess(), site, 5);
    return 1;
}
static void fx_restore(int id, void *ours, DWORD was)
{
    BYTE *site = pc_addr_of(g_fx.base, id);
    DWORD old;
    if (site + 5 + (int)*(DWORD *)(site + 1) != (BYTE *)ours || !VirtualProtect(site + 1, 4, PAGE_EXECUTE_READWRITE, &old)) return;
    *(DWORD *)(site + 1) = was;
    VirtualProtect(site + 1, 4, old, &old);
    FlushInstructionCache(GetCurrentProcess(), site, 5);
}

/* the getter is mov eax,[manager]; test; jz; mov ax,[eax+0xa]. Our moves bring that count down the way the game's own do,
   or it only ever rises and trips the game's 200 trim */
static void fx_find_unread(void)
{
    static const BYTE shape[] = { 0x85, 0xc0, 0x74, 0x06, 0x66, 0x8b, 0x40, 0x0a };
    const BYTE *p = (const BYTE *)g_fx.count;
    if (p[0] == 0xa1 && !memcmp(p + 5, shape, sizeof shape) && fx_in_image(*(BYTE **)(p + 1))) g_fx.unread_mgr = *(BYTE ***)(p + 1);
    else base_log("ffxi: the unread count getter isn't the shape the hook knows, its count won't come down for moved messages\r\n");
}
static void fx_count_down(void)
{
    BYTE *mgr;
    while (g_fx.moved > 0) {
        InterlockedDecrement(&g_fx.moved);
        if (!g_fx.unread_mgr || !(mgr = *g_fx.unread_mgr)) continue;
        if (*(WORD *)(mgr + 0xa)) (*(WORD *)(mgr + 0xa))--;
    }
}

/* a Shift-JIS trail byte is never a space, so names in any language split cleanly */
static int fx_parse(char *line, char **who, char **text)
{
    char *p = line, *cmd, *e;
    int kind;
    *who = *text = NULL;
    while (*p == ' ') p++;
    cmd = p;
    while (*p && *p != ' ') p++;
    if (*p) *p++ = 0;
    while (*p == ' ') p++;
    if (!lstrcmpiA(cmd, "/m")) kind = FX_CMD_M;
    else if (!lstrcmpiA(cmd, "/rm")) kind = FX_CMD_RM;
    else return FX_CMD_NONE;
    if (kind == FX_CMD_M) {
        *who = p;
        while (*p && *p != ' ') p++;
        if (*p) *p++ = 0;
        while (*p == ' ') p++;
    }
    *text = p;
    for (e = p + lstrlenA(p); e > p && e[-1] == ' '; e--) e[-1] = 0;
    return kind;
}

/* case only counts for single bytes, a trail byte in 0x41-0x5a is part of another character */
static int fx_same_name(const char *typed, const char *row, int cap)
{
    int i = 0;
    while (i < cap && row[i]) {
        char a = typed[i], c = row[i];
        if (fx_lead((BYTE)c)) {
            if (a != c || i + 1 >= cap || typed[i + 1] != row[i + 1]) return 0;
            i += 2;
            continue;
        }
        if (a >= 'A' && a <= 'Z') a += 32;
        if (c >= 'A' && c <= 'Z') c += 32;
        if (a != c) return 0;
        i++;
    }
    return i > 0 && !typed[i];
}

/* auto-translate phrases go as their plain text, a viewer can't show them */
static void fx_plain(const char *raw, char *out, int cap)
{
    void *at;
    out[0] = 0;
    if (!strchr(raw, 0xfd) || !pc_addr_found(AT_FX_EXPANDER) || !pc_addr_found(AT_FX_AT_OBJECT) ||
        !(at = *(void **)pc_addr_of(g_fx.base, AT_FX_AT_OBJECT))) {
        lstrcpynA(out, raw, cap);
        return;
    }
    if (((fx_expand_fn)pc_addr_of(g_fx.base, AT_FX_EXPANDER))(at, NULL, out, cap, raw, lstrlenA(raw), 0, 0) < 0) out[0] = 0;
    out[cap - 1] = 0;
}

static int fx_friend(BYTE *b, const char *typed, fx_send *s)
{
    int i;
    for (i = 0; i < IG_FRIENDS; i++) {
        const pol_friend *f = pc_friend_row(b, i);
        if (IsBadReadPtr(f, sizeof *f)) break;
        if (!pc_friend_valid(f) || (f->flags & FRIEND_PENDING) || !fx_same_name(typed, f->name, sizeof f->name)) continue;
        if (peers_is_me(b, f->id_lo, f->id_hi)) continue;
        s->lo = f->id_lo;
        s->hi = f->id_hi;
        s->slot = pc_friend_slot(f);
        fx_clean((const BYTE *)f->name, sizeof f->name, s->name, sizeof s->name);
        return 1;
    }
    return 0;
}

static void fx_command(BYTE *b)
{
    static char line[FX_LINE_IN + 1], plain[FX_PLAIN];
    char *who, *raw, name[FX_NAME + 4];
    fx_send *s;
    int kind;
    lstrcpynA(line, (const char *)pc_addr_of(g_fx.base, AT_FX_EXPANDED), sizeof line);
    if ((kind = fx_parse(line, &who, &raw)) == FX_CMD_NONE) return;
    if (g_fx.nsends >= FX_SENDS) {
        fx_print(g_fx_red, g_fx_not_sent, "", "");
        base_log("ffxi: a %s didn't go, four are already waiting\r\n", kind == FX_CMD_M ? "/m" : "/rm");
        return;
    }
    s = &g_fx.sends[g_fx.nsends];
    memset(s, 0, sizeof *s);
    g_fx_step = "auto-translate";
    fx_plain(raw, plain, sizeof plain);
    g_fx_step = "";
    fx_clean((const BYTE *)plain, lstrlenA(plain), s->text, TEXT_MAX_BYTES + 1);
    if (kind == FX_CMD_M && !fx_friend(b, who, s)) s->name[0] = 0;
    if (kind == FX_CMD_RM && g_fx.last_name[0]) {
        s->lo = g_fx.last_lo;
        s->hi = g_fx.last_hi;
        s->slot = g_fx.last_slot;
        lstrcpynA(s->name, g_fx.last_name, sizeof s->name);
    }
    for (raw = s->text; *raw == ' '; raw++) {}
    if (!s->name[0] || !*raw) {
        fx_print(g_fx_red, g_fx_not_sent, "", "");
        base_log("ffxi: a %s didn't go, %s\r\n", kind == FX_CMD_M ? "/m" : "/rm",
                 !s->name[0] ? (kind == FX_CMD_M ? "no friend by that name" : "nobody to reply to yet") : "nothing to send");
        return;
    }
    g_fx.nsends++;
    wsprintfA(name, "[%s] ", s->name);
    fx_print(g_fx_blue, ">> ", name, s->text);
}

/* the game's own commands always win, this only sees one it didn't know */
static const BYTE *__cdecl fx_lookup(const BYTE *table, const char *token)
{
    const BYTE *r = g_fx.lookup(table, token);
    if (r || g_fx.off || !g_fx.watching || !token) return r;
    __try {
        if (!lstrcmpiA(token, "/m") || !lstrcmpiA(token, "/rm")) fx_command(g_ig.b);
    } __except (fx_caught(GetExceptionInformation())) {
        fx_fault("in a chat command", g_fx_step);
        g_fx_step = "";
    }
    return r;
}

static BYTE *fx_build(BYTE *b, const fx_send *s)
{
    char me_name[16];
    unsigned __int64 me = pc_my_id(b);
    int my_slot = peers_my_handle(b, me_name), n;
    BYTE *msg = (BYTE *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, MAIL_BUILT_SIZE + lstrlenA(s->text) + 16);
    if (!msg || ((ULONG_PTR)msg & 7)) {
        if (msg) HeapFree(GetProcessHeap(), 0, msg);
        return NULL;
    }
    n = pc_mail_body(b, "", s->text, 0, NULL, (char *)msg + MAIL_BUILT_SIZE);
    pc_mail_header(b, msg, 0, (unsigned)me, (unsigned)(me >> 32), s->lo, s->hi, me_name, "", my_slot, s->slot, 1000, 0, n);
    return msg;
}

static void fx_send_done(int ok)
{
    int i;
    if (ok) {
        fx_sent_id *id = &g_fx.sent_ids[g_fx.nsent_ids % FX_SENT_IDS];
        id->time = MAIL(g_fx.msg)->time;
        id->seq = MAIL(g_fx.msg)->seq;
        id->from_slot = MAIL(g_fx.msg)->from_slot;
        InterlockedIncrement(&g_fx.nsent_ids);
    }
    for (i = 0; ok && g_fx.msg && i < FX_COPIES; i++)
        if (!InterlockedCompareExchangePointer(&g_fx.copies[i], g_fx.msg, NULL)) {
            g_fx.msg = NULL;
            SetEvent(g_ig.wake);
        }
    if (ok && g_fx.msg) base_log("ffxi: no room for a sent message's s\\a copy, it isn't kept\r\n");
    if (g_fx.msg) HeapFree(GetProcessHeap(), 0, g_fx.msg);
    g_fx.msg = NULL;
    g_fx.job = -1;
    if (!ok) fx_print(g_fx_red, g_fx_not_sent, "", "");
}

/* a job's state machine only moves inside its poll, so it's polled every callback while it's going */
static void fx_send_pump(BYTE *b)
{
    ULONGLONG now = GetTickCount64();
    int used = 0, total = 4, r, why;
    if (g_fx.job >= 0) {
        if ((r = pc_mail_send_check(b, g_fx.job)) == 0) return;
        if (r == NO_SLOT) {
            g_fx.job = -1;
            return;
        }
        base_log("ffxi: a chat line message %s (%d)\r\n", r == 1 ? "went" : "didn't go", r);
        mail_send_paced(r, now - g_fx.started);
        fx_send_done(r == 1);
        return;
    }
    if (!g_fx.msg) {
        if (!g_fx.nsends || now - g_fx.sent_at < FX_SEND_GAP_MS) return;
        why = mail_send_pace_check(&g_s.pace, now);
        if (why == PACE_HELD || why == PACE_MIN) return;
        g_fx.msg = why == PACE_OK ? fx_build(b, &g_fx.sends[0]) : NULL;
        memmove(&g_fx.sends[0], &g_fx.sends[1], (FX_SENDS - 1) * sizeof g_fx.sends[0]);
        g_fx.nsends--;
        if (!g_fx.msg) {
            if (why != PACE_OK) base_log("ffxi: a chat line message didn't go, %s\r\n", mail_send_pace_refusal());
            fx_send_done(0);
            return;
        }
    }
    if (pc_has(b, SV_JOB_SLOTS)) pc_job_slots(b, &used, &total);
    if (total - used < 3) return;
    g_fx.sent_at = now;
    r = pc_mail_send(b, g_fx.msg);
    if (r == NO_SLOT) return;
    if (r < 0) {
        base_log("ffxi: a chat line message didn't start (%d)\r\n", r);
        if (r == -0x153a || r == -0x1540) mail_send_paced(r, 0);
        fx_send_done(0);
        return;
    }
    g_fx.job = r;
    g_fx.started = now;
    mail_send_pace_take(&g_s.pace, now);
}

/* after a fault: a send already started is still polled to its end, and ones waiting say they didn't go */
static void fx_send_wind_down(BYTE *b)
{
    int r;
    if (g_fx.job >= 0 && (r = pc_mail_send_check(b, g_fx.job)) != 0) {
        base_log("ffxi: a chat line message %s after the fault (%d)\r\n", r == 1 ? "went" : "didn't go", r);
        mail_send_paced(r, GetTickCount64() - g_fx.started);
        fx_send_done(r == 1);
    }
    if (g_fx.job < 0 && g_fx.msg) fx_send_done(0);
    for (; g_fx.nsends; g_fx.nsends--) fx_print(g_fx_red, g_fx_not_sent, "", "");
    for (; g_fx.unsent; g_fx.unsent--) fx_print(g_fx_red, g_fx_not_sent, "", "");
}

/* game thread */
static void fx_poll(BYTE *b)
{
    if (!g_fx.watching || !g_fx.commands) return;
    __try {
        if (g_fx.off) {
            fx_send_wind_down(b);
            return;
        }
        g_fx_step = "send";
        fx_send_pump(b);
        g_fx_step = "";
    } __except (fx_caught(GetExceptionInformation())) {
        /* one already going is left for the wind-down to poll, unless the wind-down's what faulted. A dropped buffer stays,
           polcore may still have it */
        if (g_fx.job < 0 || g_fx.off) {
            if (g_fx.msg && g_fx.job < 0 && !g_fx.off) g_fx.unsent++;
            g_fx.job = -1;
            g_fx.msg = NULL;
        }
        fx_fault("sending a chat line message", g_fx_step);
        g_fx_step = "";
    }
}

static void fx_patch(void)
{
    void *count = NULL, *mark = NULL, *lookup = NULL;
    if (!fx_repoint(AT_FX_COUNT_CALL, (void *)fx_count, &g_fx.count_was, &count)) {
        g_fx.off = 1;
        base_log("ffxi: the unread count call isn't a call into FFXiMain, messages stay as the game has them\r\n");
        return;
    }
    g_fx.count = (fx_count_fn)count;
    if (!fx_repoint(AT_FX_MARK_CALL, (void *)fx_mark, &g_fx.mark_was, &mark)) {
        fx_restore(AT_FX_COUNT_CALL, (void *)fx_count, g_fx.count_was);
        g_fx.off = 1;
        base_log("ffxi: the friend mark call isn't a call into FFXiMain, messages stay as the game has them\r\n");
        return;
    }
    g_fx.mark = (fx_mark_fn)mark;
    g_fx.patched = 1;
    fx_find_unread();
    base_log("ffxi: unread count and friend mark repointed, they pass through till the player's in the world\r\n");
    if (!pc_addr_found(AT_FX_LOOKUP_CALL) || !pc_addr_found(AT_FX_EXPANDED)) {
        base_log("ffxi: no command lookup in this build, no /m or /rm this time\r\n");
    } else if (fx_repoint(AT_FX_LOOKUP_CALL, (void *)fx_lookup, &g_fx.lookup_was, &lookup)) {
        g_fx.lookup = (fx_lookup_fn)lookup;
        g_fx.commands = 1;
        base_log("ffxi: /m and /rm are caught after the game's own commands\r\n");
    } else {
        base_log("ffxi: the command lookup isn't a call into FFXiMain, no /m or /rm this time\r\n");
    }
}
static void fx_unpatch(void)
{
    if (!g_fx.patched) return;
    if (g_fx.commands && g_fx.job < 0 && !g_fx.msg && !g_fx.nsends && !g_fx.unsent) {
        fx_restore(AT_FX_LOOKUP_CALL, (void *)fx_lookup, g_fx.lookup_was);
        g_fx.commands = 0;
    }
    if (g_fx.commands) return;
    fx_restore(AT_FX_MARK_CALL, (void *)fx_mark, g_fx.mark_was);
    fx_restore(AT_FX_COUNT_CALL, (void *)fx_count, g_fx.count_was);
    g_fx.patched = 0;
    base_log("ffxi: unread count and friend mark back to the game's own\r\n");
}

static void fx_print_queued(BYTE *b)
{
    int n;
    for (n = 0; n < FX_PRINTS; n++) {
        fx_line *l = &g_fx.ring[g_fx.head];
        if (l->state != FX_QUEUED || (l->kind != FX_MOVE && !fx_print_line(l))) return;
        if (l->move && !message_store_move_name(b, l->hdr, l->file, g_home, 1, l->now)) l->move = 0;
        l->printed_at = GetTickCount();
        InterlockedExchange(&l->state, l->move ? FX_PRINTED : FX_FREE);
        g_fx.head = (g_fx.head + 1) % FX_RING;
    }
}

/* game thread */
static void fx_game_tick(BYTE *b)
{
    LONG was;
    DWORD seen;
    if (!g_fx.found) return;
    __try {
        if (!g_fx.off) {
            if (!g_fx.patched) fx_patch();
            was = g_fx.in_world;
            seen = g_fx.netstat;
            g_fx.in_world = seen && (LONG)(GetTickCount() - seen) < FX_WORLD_MS && pc_game_tag(b) >= 0;
            if (g_fx.in_world && !was) {
                g_fx.dirty = 1;
                SetEvent(g_ig.wake);
            }
        }
        /* moves the worker finishes after a fault still count */
        fx_count_down();
        if (!g_fx.off && g_fx.in_world && g_fx.watching) fx_print_queued(b);
    } __except (fx_caught(GetExceptionInformation())) {
        g_fx.unread_mgr = NULL;
        fx_fault("on the game's thread", g_fx_step);
        g_fx_step = "";
    }
    if (g_fx.off) fx_unpatch();
}

static void fx_look(void)
{
    HMODULE m = GetModuleHandleA("FFXiMain.dll");
    const IMAGE_NT_HEADERS32 *nt;
    const BYTE *add;
    int missing;
    if (!m) {
        base_log("ffxi: FFXiMain.dll isn't loaded yet\r\n");
        return;
    }
    g_fx.base = (BYTE *)m;
    nt = (const IMAGE_NT_HEADERS32 *)(g_fx.base + ((const IMAGE_DOS_HEADER *)m)->e_lfanew);
    g_fx.size = nt->OptionalHeader.SizeOfImage;
    addr_resolve(IN_FFXI, "FFXiMain.dll", g_fx.base, (DWORD)(ULONG_PTR)m);
    missing = fx_missing();
    if (pc_addr_found(AT_FX_ADD_LINE)) {
        /* 81 ec 44 08 00 00 is its own start, an e9 is Windower or Ashita's detour. Anything else isn't called */
        add = pc_addr_of(g_fx.base, AT_FX_ADD_LINE);
        base_log("ffxi: the add-line starts %02x %02x %02x %02x %02x %02x\r\n", add[0], add[1], add[2], add[3], add[4], add[5]);
        if (add[0] != 0xe9 && memcmp(add, "\x81\xec\x44\x08\x00\x00", 6)) missing++;
    }
    if (missing) {
        base_log("ffxi: %d of FFXiMain's addresses not found or not usable (try %d of %d)\r\n", missing, g_fx.tries, FX_TRIES);
        return;
    }
    if (!pc_addr_found(AT_FX_WORD_FILTER) || !pc_addr_found(AT_FX_SOUND) || !pc_addr_found(AT_FX_SOUND_ON) ||
        !pc_addr_found(AT_FX_EXPANDER) || !pc_addr_found(AT_FX_AT_OBJECT))
        base_log("ffxi: some extras aren't there (word filter %d, sound %d, auto-translate %d), the rest goes on without them\r\n",
                 pc_addr_found(AT_FX_WORD_FILTER), pc_addr_found(AT_FX_SOUND) && pc_addr_found(AT_FX_SOUND_ON),
                 pc_addr_found(AT_FX_EXPANDER) && pc_addr_found(AT_FX_AT_OBJECT));
    InterlockedExchange(&g_fx.found, 1);
    base_log("ffxi: every FFXiMain address the messages need is there\r\n");
}

static int fx_seen_find(const char *name)
{
    int i;
    for (i = 0; i < g_fx.nseen; i++)
        if (!lstrcmpA(g_fx.seen[i].name, name)) return i;
    return -1;
}
/* the game renames an r\b file when its state bits change, so a file is known by its header too */
static int fx_seen_same(const BYTE *hdr)
{
    int i;
    for (i = 0; i < g_fx.nseen; i++) {
        const fx_seen_rec *r = &g_fx.seen[i];
        if (r->from_lo == MAIL(hdr)->from_lo && r->from_hi == MAIL(hdr)->from_hi && r->slot == MAIL(hdr)->from_slot &&
            r->seq == MAIL(hdr)->seq && r->time == MAIL(hdr)->time)
            return i;
    }
    return -1;
}
/* a printed line still waiting to be marked read needs its record to follow a rename */
static int fx_in_ring(const char *name)
{
    int i;
    for (i = 0; i < FX_RING; i++)
        if (g_fx.ring[i].state != FX_FREE && !lstrcmpA(g_fx.ring[i].file, name)) return 1;
    return 0;
}

/* when it's full the oldest one already dealt with makes room, and anything that old is left alone after */
static int fx_seen_add(const char *name, const BYTE *hdr, int done)
{
    fx_seen_rec *r;
    int i, k = -1;
    if (g_fx.nseen < FX_SEEN) {
        k = g_fx.nseen++;
    } else {
        for (i = 0; i < g_fx.nseen; i++)
            if (g_fx.seen[i].done && (k < 0 || g_fx.seen[i].time < g_fx.seen[k].time) && !fx_in_ring(g_fx.seen[i].name)) k = i;
        if (!g_fx.seen_full) base_log("ffxi: over %d messages in r\\b, the oldest are left as they are\r\n", FX_SEEN);
        g_fx.seen_full = 1;
        if (k < 0) return -1;
        if (g_fx.seen[k].time > g_fx.old_time) g_fx.old_time = g_fx.seen[k].time;
    }
    r = &g_fx.seen[k];
    memset(r, 0, sizeof *r);
    lstrcpynA(r->name, name, sizeof r->name);
    if (hdr) {
        r->from_lo = MAIL(hdr)->from_lo;
        r->from_hi = MAIL(hdr)->from_hi;
        r->slot = MAIL(hdr)->from_slot;
        r->seq = MAIL(hdr)->seq;
        r->time = MAIL(hdr)->time;
    }
    r->first = GetTickCount();
    r->gen = g_fx.gen;
    r->done = done;
    return k;
}
/* a file renamed mid-scan can be missed by that scan, so it takes two misses to drop one */
static void fx_seen_prune(void)
{
    int i, k = 0;
    for (i = 0; i < g_fx.nseen; i++)
        if (g_fx.gen - g_fx.seen[i].gen <= 1) g_fx.seen[k++] = g_fx.seen[i];
    if (k < g_fx.nseen) g_fx.seen_full = 0;
    g_fx.nseen = k;
}

static void fx_sender(const BYTE *hdr, char *out)
{
    int i;
    for (i = 0; i < IG_FRIENDS; i++) {
        const pol_friend *f = &g_ig.written.friends[i];
        if (!pc_friend_valid(f) || f->id_lo != MAIL(hdr)->from_lo || f->id_hi != MAIL(hdr)->from_hi ||
            pc_friend_slot(f) != MAIL(hdr)->from_slot)
            continue;
        fx_clean((const BYTE *)f->name, sizeof f->name, out, FX_NAME);
        if (out[0]) return;
    }
    fx_clean((const BYTE *)MAIL(hdr)->name, sizeof MAIL(hdr)->name, out, FX_NAME);
}
static int fx_ignored(const BYTE *hdr)
{
    int i;
    for (i = 0; i < IG_IGNORES; i++) {
        const pol_friend *a = &g_ig.written.ignores[i];
        if (!pc_friend_valid(a) || a->id_lo != MAIL(hdr)->from_lo || a->id_hi != MAIL(hdr)->from_hi) continue;
        if ((a->flags & 3) || pc_friend_slot(a) == MAIL(hdr)->from_slot) return 1;
    }
    return 0;
}
/* the same test the hold uses: from me, and either one I sent or addressed back to the handle it came from */
static int fx_bounced(const BYTE *hdr)
{
    unsigned __int64 me = g_ig.written.me;
    LONG i, n = g_fx.nsent_ids;
    if (MAIL(hdr)->from_lo != (DWORD)me || MAIL(hdr)->from_hi != (DWORD)(me >> 32)) return 0;
    if (n > FX_SENT_IDS) n = FX_SENT_IDS;
    for (i = 0; i < n; i++)
        if (g_fx.sent_ids[i].time == MAIL(hdr)->time && g_fx.sent_ids[i].seq == MAIL(hdr)->seq &&
            g_fx.sent_ids[i].from_slot == MAIL(hdr)->from_slot)
            return 1;
    return MAIL(hdr)->to_lo == (DWORD)me && MAIL(hdr)->to_hi == (DWORD)(me >> 32) && MAIL(hdr)->from_slot == MAIL(hdr)->to_slot;
}

static int fx_queue(int kind, const char *name, const char *text, const BYTE *hdr, const char *file, int tries)
{
    fx_line *l = &g_fx.ring[g_fx.tail];
    if (l->state != FX_FREE) return 0;
    l->kind = kind;
    l->sound = (kind == FX_IN || kind == FX_NOTE) && (!g_fx.quiet[0] || GetFileAttributesA(g_fx.quiet) == INVALID_FILE_ATTRIBUTES);
    l->move = kind == FX_IN || kind == FX_MOVE;
    l->tries = tries;
    l->next_try = GetTickCount();
    lstrcpynA(l->name, name, sizeof l->name);
    lstrcpynA(l->text, text, sizeof l->text);
    if (hdr) memcpy(l->hdr, hdr, sizeof l->hdr);
    lstrcpynA(l->file, file ? file : "", sizeof l->file);
    l->now[0] = 0;
    InterlockedExchange(&l->state, FX_QUEUED);
    g_fx.tail = (g_fx.tail + 1) % FX_RING;
    return 1;
}

/* 1 when it's dealt with, 0 to look again on the next scan, 2 when the ring's full */
static int fx_take(const char *name, const BYTE *hdr)
{
    static char body[STORE_BODY_MAX];
    char who[FX_NAME], text[FX_TEXT];
    unsigned kind = pc_mail_kind(hdr);
    storefile f;
    int got, start, end;
    if (!(MAIL(hdr)->flags & MAIL_VALID)) return 1;
    if (!g_ig.written.me) return 0;
    /* a bounce can be addressed to the friend it was for, so this goes before the owned check */
    if (kind == 0 && !pc_mail_receipt(hdr) && fx_bounced(hdr)) return fx_queue(FX_BOUNCE, "", "", NULL, NULL, 0) ? 1 : 2;
    if (!message_store_owned(hdr, 1, g_ig.written.me) || pc_mail_receipt(hdr) || fx_ignored(hdr)) return 1;
    /* requests and invitations stay in r\b for Provenance to answer, they just get a line */
    if (kind == 1 || (kind == 0xe && pc_mail_att_type(hdr) == 10)) {
        fx_sender(hdr, who);
        lstrcpynA(text, who, sizeof text);
        fx_append(text, sizeof text, kind == 1 ? " sent you a friend request." : " invited you to a friend group.");
        return fx_queue(FX_NOTE, who, text, NULL, NULL, 0) ? 1 : 2;
    }
    if (kind != 0) return 1;
    memset(&f, 0, sizeof f);
    memcpy(f.hdr, hdr, sizeof f.hdr);
    lstrcpyA(f.name, name);
    f.home = g_home;
    f.sub = 1;
    f.alt_home = -1;
    got = message_store_body(&f, body, sizeof body);
    if (got < (int)MAIL(hdr)->len) return 0;
    for (start = 0; start < got && body[start] != 0x07; start++) {}
    start = start < got ? start + 1 : 0;
    for (end = start; end < got && body[end]; end++) {}
    fx_clean((const BYTE *)body + start, end - start, text, sizeof text);
    SecureZeroMemory(body, sizeof body);
    fx_sender(hdr, who);
    return fx_queue(FX_IN, who, text, hdr, name, 0) ? 1 : 2;
}

static void fx_scan(BYTE *b, int seeding)
{
    __declspec(align(8)) BYTE hdr[sizeof(pol_mail)];
    WIN32_FIND_DATAA fd;
    char glob[MAX_PATH + 80];
    HANDLE h;
    int k, n = 0, have, took, full = 0;
    lstrcpyA(glob, g_fx.dir);
    lstrcatA(glob, "\\*");
    if ((h = FindFirstFileA(glob, &fd)) == INVALID_HANDLE_VALUE) return;
    g_fx.find = h;
    g_fx.gen++;
    do {
        if ((fd.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) || !message_store_name_ok(fd.cFileName))
            continue;
        have = 0;
        if ((k = fx_seen_find(fd.cFileName)) >= 0) {
            g_fx.seen[k].gen = g_fx.gen;
            if (g_fx.seen[k].done) continue;
        } else {
            memset(hdr, 0, sizeof hdr);
            if (pc_mail_decode_header(b, hdr, fd.cFileName) != 0) continue;
            have = 1;
            if ((k = fx_seen_same(hdr)) >= 0) {
                lstrcpynA(g_fx.seen[k].name, fd.cFileName, sizeof g_fx.seen[k].name);
                g_fx.seen[k].gen = g_fx.gen;
                if (g_fx.seen[k].done) continue;
            } else if (g_fx.old_time && MAIL(hdr)->time <= g_fx.old_time) {
                if (!g_fx.old_said) base_log("ffxi: a message dated before ones the full set let go of is left unread\r\n");
                g_fx.old_said = 1;
                continue;
            /* already there before the player first got in the world, so it stays unread like it always has */
            } else if (seeding) {
                if (fx_seen_add(fd.cFileName, hdr, 1) >= 0) n++;
                continue;
            } else if ((k = fx_seen_add(fd.cFileName, hdr, 0)) < 0) {
                continue;
            }
        }
        /* zoning, a cutscene or the character select: it waits and prints once they're back */
        if (!g_fx.in_world) {
            g_fx.seen[k].first = GetTickCount();
            continue;
        }
        if (!have) {
            memset(hdr, 0, sizeof hdr);
            have = pc_mail_decode_header(b, hdr, fd.cFileName) == 0;
        }
        took = !have ? 1 : full ? 2 : fx_take(fd.cFileName, hdr);
        /* the give-up is for a file that never finishes being written, a full ring just means wait */
        if (took == 2) {
            full = 1;
            g_fx.seen[k].first = GetTickCount();
        } else if (took || GetTickCount() - g_fx.seen[k].first > FX_GIVE_UP_MS) {
            g_fx.seen[k].done = 1;
        }
    } while (FindNextFileA(h, &fd));
    g_fx.find = NULL;
    FindClose(h);
    fx_seen_prune();
    if (seeding) base_log("ffxi: in the world, %d message(s) already in r\\b stay unread, new ones go to the chat log\r\n", n);
}

static void fx_watch_start(BYTE *b)
{
    int n;
    if (g_home < 0 || !g_viewer[0] || !pc_has(b, SV_MAIL_DECODE_HEADER) || !pc_has(b, SV_MAIL_PATH_UNREAD) ||
        !pc_has(b, SV_MAIL_PATH_READ)) {
        g_fx.off = 1;
        base_log("ffxi: no message store to watch, messages stay as the game has them\r\n");
        return;
    }
    message_store_dir(g_fx.dir, g_home, 1);
    if (!message_store_plain_path(g_fx.dir)) {
        g_fx.off = 1;
        base_log("ffxi: r\\b isn't a plain folder, messages stay as the game has them\r\n");
        return;
    }
    n = (int)GetEnvironmentVariableA(ENV_IPC_DIR, g_fx.quiet, MAX_PATH);
    if (n > 0 && n < MAX_PATH - 32) {
        if (g_fx.quiet[n - 1] != '\\') lstrcatA(g_fx.quiet, "\\");
        lstrcatA(g_fx.quiet, IPC_QUIET);
    } else {
        g_fx.quiet[0] = 0;
    }
    fx_scan(b, 1);
    g_fx.change = FindFirstChangeNotificationA(g_fx.dir, FALSE, FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_SIZE |
                                                               FILE_NOTIFY_CHANGE_LAST_WRITE);
    if (g_fx.change == INVALID_HANDLE_VALUE) g_fx.change = NULL;
    InterlockedExchange(&g_fx.watching, 1);
}

static int fx_in_rb(const char *name)
{
    char path[MAX_PATH + 160];
    message_store_dir(path, g_home, 1);
    if (lstrlenA(path) + 1 + lstrlenA(name) >= (int)sizeof path) return 0;
    lstrcatA(path, "\\");
    lstrcatA(path, name);
    return GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES;
}

/* a printed one that's gone from r\b may just have been renamed */
static int fx_follow(BYTE *b, fx_line *l)
{
    __declspec(align(8)) BYTE hdr[sizeof(pol_mail)];
    char name[100];
    int k = fx_seen_same(l->hdr), tries = l->tries + 1;
    if (k < 0 || !lstrcmpA(g_fx.seen[k].name, l->file)) return 0;
    lstrcpynA(name, g_fx.seen[k].name, sizeof name);
    memset(hdr, 0, sizeof hdr);
    if (pc_mail_decode_header(b, hdr, name) != 0) return 0;
    InterlockedExchange(&l->state, FX_FREE);
    if (fx_queue(FX_MOVE, "", "", hdr, name, tries)) return 1;
    InterlockedExchange(&l->state, FX_PRINTED);
    return 0;
}

static void fx_moves(BYTE *b)
{
    DWORD now = GetTickCount();
    int i;
    for (i = 0; i < FX_RING; i++) {
        fx_line *l = &g_fx.ring[i];
        if (l->state != FX_PRINTED || (LONG)(now - l->printed_at) < FX_MOVE_AFTER_MS || (LONG)(now - l->next_try) < 0)
            continue;
        if (!fx_in_rb(l->file)) {
            if (l->tries < FX_MOVE_TRIES && fx_follow(b, l)) continue;
            if (++l->tries < FX_MOVE_TRIES) {
                l->next_try = now + FX_MOVE_RETRY_MS;
                continue;
            }
            base_log("ffxi: a printed message's file had gone from r\\b before it was marked read\r\n");
        } else if (message_store_move_file(l->file, l->now, g_home, 1)) {
            InterlockedIncrement(&g_fx.moved);
            base_log("ffxi: a message printed in the chat log is read\r\n");
        } else if (++l->tries < FX_MOVE_TRIES) {
            l->next_try = now + FX_MOVE_RETRY_MS;
            continue;
        } else {
            base_log("ffxi: a printed message couldn't be marked read, it stays unread\r\n");
        }
        InterlockedExchange(&l->state, FX_FREE);
    }
}

static void fx_stop(void) { g_fx.off = 1; }
static void fx_reset(void)
{
    memset(&g_fx, 0, sizeof g_fx);
    g_fx.job = -1;
}

/* the hold's done with the store by the time the game's up, so the worker can open it for the s\a copy */
static void fx_sent_copies(BYTE *b, int write)
{
    int i;
    for (i = 0; i < FX_COPIES; i++) {
        BYTE *msg = (BYTE *)InterlockedExchangePointer(&g_fx.copies[i], NULL);
        if (!msg) continue;
        if (write) {
            g_store_open = 1;
            message_store_sent(b, msg);
            g_store_open = 0;
        }
        HeapFree(GetProcessHeap(), 0, msg);
    }
}

/* game thread */
static void fx_polled(void)
{
    if (g_fx.first_poll) return;
    g_fx.polled_at = GetTickCount64();
    InterlockedExchange(&g_fx.first_poll, 1);
}
static void fx_session_up(void)
{
    InterlockedExchange(&g_fx.world, 1);
}

/* 1 when it was r\b changing that woke it */
static int fx_wait(HANDLE wake, DWORD ms)
{
    HANDLE hs[2];
    if (!g_fx.change) {
        WaitForSingleObject(wake, ms);
        return 0;
    }
    hs[0] = wake;
    hs[1] = g_fx.change;
    if (WaitForMultipleObjects(2, hs, FALSE, ms) != WAIT_OBJECT_0 + 1) return 0;
    g_fx.dirty = 1;
    FindNextChangeNotification(g_fx.change);
    return 1;
}

static void fx_worker(BYTE *b)
{
    ULONGLONG now = GetTickCount64();
    if (g_fx.worker_dead) {
        fx_sent_copies(b, 0);
        return;
    }
    __try {
        if (!g_fx.found) {
            if (g_fx.tries >= FX_TRIES || !g_fx.first_poll) return;
            if (g_fx.world == 1) {
                g_fx.world = 2;
                g_fx.next_look = 0;
            }
            if (!g_fx.world && now - g_fx.polled_at < FX_LOOK_AFTER_MS) return;
            if (now < g_fx.next_look) return;
            g_fx.tries++;
            g_fx.next_look = now + FX_RETRY_MS;
            fx_look();
            return;
        }
        fx_moves(b);
        fx_sent_copies(b, 1);
        if (g_fx.off) return;
        if (!g_fx.watching) {
            if (g_fx.in_world && g_fx.patched) fx_watch_start(b);
            return;
        }
        if (g_fx.dirty || now >= g_fx.next_scan) {
            g_fx.dirty = 0;
            g_fx.next_scan = now + FX_SCAN_MS;
            fx_scan(b, 0);
        }
    } __except (fx_caught(GetExceptionInformation())) {
        g_fx.tries = FX_TRIES;
        g_fx.worker_dead = 1;
        g_store_open = 0;
        if (g_fx.find) FindClose(g_fx.find);
        g_fx.find = NULL;
        fx_fault("on the worker", "");
    }
}
