enum {
    FX_LOOK_AFTER_MS = 20000, FX_RETRY_MS = 30000, FX_TRIES = 5, FX_LOBBY_LOOK_MS = 3000, FX_LOBBY_RETRY_MS = 5000,
    FX_RING = 16, FX_NAME = 48, FX_TEXT = 640, FX_SEEN = 1024, FX_GIVE_UP_MS = 30000, FX_MOVE_AFTER_MS = 3000,
    FX_MOVE_RETRY_MS = 2000, FX_MOVE_TRIES = 5, FX_SCAN_MS = 5000, FX_PRINTS = 3, FX_WORLD_MS = 5000,
};
enum { FX_FREE, FX_QUEUED, FX_PRINTED };
enum { FX_IN, FX_NOTE, FX_BOUNCE, FX_MOVE, FX_FRIEND, FX_PLAYING };
enum { FX_ICON_NONE, FX_ICON_POL, FX_ICON_AWAY, FX_ICON_FFXI, FX_ICON_INFO };
enum { FX_CMD_NONE, FX_CMD_M, FX_CMD_RM };
enum { FX_SENDS = 4, FX_SEND_GAP_MS = 5000, FX_LINE_IN = 150, FX_PLAIN = 0x800, FX_SENT_IDS = 64, FX_COPIES = 4, FX_FRIEND_LINES = 4, FX_FRIEND_RING = 8, FX_FRIEND_BURST = 4 };
enum { FX_CMD_FR = FX_CMD_RM + 1, FX_WAITING = 16, FX_ANSWER_MS = 30000, FX_NAMES_MAX = 0x40000, FX_STOP_GRACE_MS = 10000,
       FX_ICON_OFF_MS = 30000, FX_ICON_FAULTS = 3, FX_LIST_MAX = 4096, FX_PIECES_MAX = 16 };
enum { FX_ANS_NONE, FX_ANS_WANTED, FX_ANS_RUNNING };
enum { FX_REQ_NO, FX_REQ_NEW, FX_REQ_DONE, FX_REQ_KEPT };

/* 0x1e and a number colours what follows and lasts past a wrap. 0x1f and a chat mode's colour stops at the first one */
static const char g_fx_blue[] = "\x1e\x70", g_fx_red[] = "\x1e\x44", g_fx_name[] = "\x1e\x01", g_fx_on[] = "\x1e\x02",
                  g_fx_off[] = "\x1e\x09", g_fx_note[] = "\x1e\x06";
static const char g_fx_not_sent[] = "This message was not sent. Please try again.";
static const char g_fx_no_friend[] = " wasn't found on your friend list, check the spelling and try again.";
static const char g_fx_no_reply[] = "Nobody has sent you a message yet to reply to.";
static const char g_fx_no_request[] = "No friend request for that name was found.";
static const char g_fx_not_answered[] = "Your answer was not sent. Please try again.";

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
typedef int (__cdecl *fx_decline_fn)(DWORD seq, DWORD time, DWORD lo, DWORD hi, const char *title, const char *typed, void *cb, int tag);
typedef int (__cdecl *fx_accept_fn)(DWORD seq, DWORD time, DWORD lo, DWORD hi, const char *typed, const char *title, const char *title_1,
                                    void *cb, int tag);

typedef struct { DWORD lo, hi; int slot; char name[FX_NAME], text[FX_TEXT]; } fx_send;
typedef struct { DWORD time, seq; BYTE from_slot; } fx_sent_id;

static const int g_fx_core[] = { AT_FX_COUNT_CALL, AT_FX_MARK_CALL, AT_FX_ADD_LINE, AT_FX_CHAT_LOG };

typedef struct {
    volatile LONG state;
    volatile DWORD printed_at;
    int kind, sound, move, tries, away;
    DWORD next_try;
    char name[FX_NAME], text[FX_TEXT];
    BYTE hdr[sizeof(pol_mail)];
    char file[100], now[100];
} fx_line;

typedef struct { DWORD seq, time, lo, hi; int slot, used; char name[FX_NAME]; } fx_request;
typedef struct { char name[100]; DWORD from_lo, from_hi, seq, time, first, gen; int slot, done, req; } fx_seen_rec;
typedef struct { DWORD lo, hi, ctsid, ct_lo, ct_hi; int slot, on, row, changed, game, game_changed; ULONGLONG stop_at; } fx_presence;

static struct {
    volatile LONG found, resolved, first_poll, world, off, patched, watching, in_world, dirty, moved, nsent_ids, worker_dead, at_select;
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
    char dir[MAX_PATH + 64], quiet[MAX_PATH + 64], no_friend_lines[MAX_PATH + 64], no_playing_lines[MAX_PATH + 64],
         names[MAX_PATH + 64];
    int icons_dead, icons_said, icons_ok, icons_faults;
    ULONGLONG icons_off_until;
    fx_line ring[FX_RING];
    int tail, head;
    fx_seen_rec seen[FX_SEEN];
    int nseen, seen_full;
    fx_presence presence[IG_FRIENDS];
    int npresence, presence_known;
    DWORD last_lo, last_hi;
    int last_slot;
    char last_name[FX_NAME];
    fx_send sends[FX_SENDS];
    int nsends, job;
    BYTE *msg;
    ULONGLONG sent_at, started;
    fx_sent_id sent_ids[FX_SENT_IDS];
    void *volatile copies[FX_COPIES];
    fx_request waiting[FX_WAITING], want;
    volatile LONG waiting_lock, ans_state, ans_done, ans_code;
    int want_yes;
    ULONGLONG ans_at;
} g_fx;

static int fx_missing(void)
{
    int i, n = 0;
    for (i = 0; i < (int)(sizeof g_fx_core / sizeof g_fx_core[0]); i++)
        if (!pc_addr_found(g_fx_core[i])) n++;
    return n;
}

static int fx_answers(void)
{
    return g_fx.commands && pc_addr_found(AT_FX_FRIEND_DECLINE) && pc_addr_found(AT_FX_FRIEND_ACCEPT);
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

typedef BYTE (__fastcall *fx_bind_fn)(BYTE *piece);
typedef struct { char code[3], pic[17]; short u, v, w, h, qw, qh; } fx_icon_def;
/* EF and a byte draws slot byte - 0x1f of the chat's icon sheet. 2b to 2d are marks only the French and German clients
   print, so they're painted over. 4d is the game's own i */
static const fx_icon_def g_fx_icons[] = {
    [FX_ICON_POL] = { "\xef\x2b", "menu    ustatshd", 224, 32, 32, 32, 16, 16 },
    [FX_ICON_AWAY] = { "\xef\x2c", "menu    ustatshd", 96, 64, 32, 32, 16, 16 },
    [FX_ICON_FFXI] = { "\xef\x2d", "menu    cicon   ", 0, 0, 32, 16, 32, 16 },
    [FX_ICON_INFO] = { "\xef\x4d", "menu    ustatshd", 32, 160, 32, 32, 16, 16 },
};

/* the game's lists: the next node at +0, what it holds at +0x10, +0x14 set on one that's been dropped */
static BYTE *fx_icon_sheet(void)
{
    BYTE *node = *(BYTE **)pc_addr_of(g_fx.base, AT_FX_ICON_SHEETS), *sheet;
    int n;
    for (n = 0; node && n < FX_LIST_MAX; n++, node = *(BYTE **)node) {
        sheet = *(BYTE **)(node + 0x10);
        if (!node[0x14] && sheet && !memcmp(sheet + 0x24, "font    usgaiji ", 16)) return sheet + 0x20;
    }
    return NULL;
}

/* -1 for a slot with more pieces than any real one has, which is what a list gone wrong looks like */
static int fx_slot_pieces(const BYTE *slot, BYTE **out)
{
    BYTE *node = *(BYTE *const *)(slot + 4);
    int n = 0, walked;
    for (walked = 0; node; walked++, node = *(BYTE **)node) {
        if (walked == FX_PIECES_MAX) return -1;
        if (!node[0x14] && *(BYTE **)(node + 0x10)) out[n++] = *(BYTE **)(node + 0x10);
    }
    return n;
}

/* a piece: source size and place at +8, flip +0x1c, colours +0x1d, blend +0x2d, picture name +0x32, the picture +0x4c,
   four vertices of 0x1c from +0x50, corners at +0xc0 and +0xc8 */
static int fx_icon_cell(const BYTE *p, const fx_icon_def *d)
{
    const short *at = (const short *)(p + 8);
    return !memcmp(p + 0x32, d->pic, 16) && at[0] == d->w && at[1] == d->h && at[2] == d->u && at[3] == d->v;
}

static int fx_icon_paint(BYTE *slot, BYTE **pieces, int n, const fx_icon_def *d)
{
    BYTE *first = pieces[0], was[0xd0];
    short *at = (short *)(first + 8);
    int i, k;
    memcpy(was, first, sizeof was);
    memcpy(first + 0x32, d->pic, 16);
    at[0] = d->w;
    at[1] = d->h;
    at[2] = d->u;
    at[3] = d->v;
    first[0x1c] = 0;
    memset(first + 0x1d, 0x80, 16);
    memcpy(first + 0x2d, "\x01\x00\x01\x01", 4);
    for (i = 0; i < 4; i++) {
        *(DWORD *)(first + 0x60 + i * 0x1c) = 0x80808080;
        *(short *)(first + 0xc0 + i * 2) = (i & 1) ? d->qw : 0;
        *(short *)(first + 0xc8 + i * 2) = (i & 2) ? d->qh : 0;
    }
    *(void **)(first + 0x4c) = NULL;
    /* the game's own, finds the picture by its name and works out the UVs */
    if (!((fx_bind_fn)pc_addr_of(g_fx.base, AT_FX_ICON_BIND))(first)) {
        memcpy(first, was, sizeof was);
        return 0;
    }
    for (k = 1; k < n; k++)
        for (i = 0; i < 4; i++) *(DWORD *)(pieces[k] + 0x60 + i * 0x1c) = 0;
    at = (short *)(slot + 0x20);
    at[0] = 0;
    at[1] = d->qw;
    at[2] = 0;
    at[3] = d->qh;
    return 1;
}

/* a slot is its pieces' list at +4 and its bounds at +0x20, and the game can load the sheet afresh at any time */
static int fx_icon_ready(int which)
{
    const fx_icon_def *d = &g_fx_icons[which];
    int k = (BYTE)d->code[1] - 0x1f, n;
    BYTE *sheet = fx_icon_sheet(), **slots, *slot, *pieces[FX_PIECES_MAX];
    if (!sheet || *(WORD *)(sheet + 0x1c) <= k || !(slots = *(BYTE ***)sheet) || !(slot = slots[k])) return 0;
    if ((n = fx_slot_pieces(slot, pieces)) < 1) return 0;
    if (fx_icon_cell(pieces[0], d) && *(void **)(pieces[0] + 0x4c) && (which == FX_ICON_INFO || *(short *)(slot + 0x22) == d->qw)) return 1;
    if (which == FX_ICON_INFO || !pc_addr_found(AT_FX_ICON_BIND)) return 0;
    if (memcmp(pieces[0] + 0x32, "menu    colorbal", 16) && !fx_icon_cell(pieces[0], d)) return 0;
    return fx_icon_paint(slot, pieces, n, d);
}

/* game thread only */
static const char *fx_icon(int which)
{
    static const char *const names[] = { "", "POL", "away", "FFXI", "i" };
    int ok = 0, bit = 1 << which;
    if (!which || g_fx.icons_dead || !pc_addr_found(AT_FX_ICON_SHEETS) || GetTickCount64() < g_fx.icons_off_until) return "";
    __try {
        ok = fx_icon_ready(which);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        g_fx.icons_dead = ++g_fx.icons_faults >= FX_ICON_FAULTS;
        g_fx.icons_off_until = GetTickCount64() + FX_ICON_OFF_MS;
        base_log("ffxi: fault in the chat's icon sheet, lines go without icons for %s\r\n", g_fx.icons_dead ? "the rest of this game" : "a while");
        return "";
    }
    if (!(g_fx.icons_said & bit) || !(g_fx.icons_ok & bit) != !ok) {
        g_fx.icons_said |= bit;
        g_fx.icons_ok = ok ? g_fx.icons_ok | bit : g_fx.icons_ok & ~bit;
        base_log("ffxi: the %s icon %s\r\n", names[which], ok ? "is in the chat's sheet" : "can't be had, its lines go without");
    }
    return ok ? g_fx_icons[which].code : "";
}

static int fx_can_print(void) { return *(void **)pc_addr_of(g_fx.base, AT_FX_CHAT_LOG) && !g_fx.print_dead; }

static int fx_print(int icon, const char *colour, const char *a, const char *b, const char *c)
{
    static char line[FX_TEXT + FX_NAME + 40];
    /* a writable block, the way the game's own callers pass it. A read-only one faulted */
    BYTE mode[4] = { 0x12, 0, 0, 0 };
    if (!fx_can_print()) return 0;
    lstrcpynA(line, fx_icon(icon), sizeof line);
    if (line[0]) fx_append(line, sizeof line, " ");
    fx_append(line, sizeof line, colour);
    fx_append(line, sizeof line, a);
    fx_append(line, sizeof line, b);
    fx_append(line, sizeof line, c);
    g_fx_step = "add-line";
    ((fx_add_line_fn)pc_addr_of(g_fx.base, AT_FX_ADD_LINE))(*(void **)pc_addr_of(g_fx.base, AT_FX_CHAT_LOG), NULL, line, mode, 0, 1, 0);
    g_fx_step = "";
    return 1;
}

static int fx_print_line(fx_line *l)
{
    static char text[FX_TEXT], name[FX_NAME + 12];
    lstrcpynA(text, l->text, sizeof text);
    if (l->kind == FX_BOUNCE) {
        if (!fx_print(FX_ICON_NONE, g_fx_red, g_fx_not_sent, "", "")) return 0;
    } else if (l->kind == FX_NOTE || l->kind == FX_FRIEND || l->kind == FX_PLAYING) {
        if (!fx_print(l->kind == FX_PLAYING ? FX_ICON_FFXI : FX_ICON_INFO, "", text, "", "")) return 0;
    } else {
        if (!fx_can_print()) return 0;
        g_fx_step = "word filter";
        if (pc_addr_found(AT_FX_WORD_FILTER)) ((fx_filter_fn)pc_addr_of(g_fx.base, AT_FX_WORD_FILTER))(text);
        g_fx_step = "";
        wsprintfA(name, "[%s%s%s%s] ", l->away ? fx_icon(FX_ICON_AWAY) : "", g_fx_name, l->name, g_fx_blue);
        if (!fx_print(FX_ICON_POL, g_fx_blue, name, ">> ", text)) return 0;
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
    else if (!lstrcmpiA(cmd, "/fr")) kind = FX_CMD_FR;
    else return FX_CMD_NONE;
    if (kind == FX_CMD_M || kind == FX_CMD_FR) {
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

static int fx_req_lock(void)
{
    int i;
    for (i = 0; i < 200; i++) {
        if (!InterlockedCompareExchange(&g_fx.waiting_lock, 1, 0)) return 1;
        SwitchToThread();
    }
    return 0;
}
static void fx_req_unlock(void) { InterlockedExchange(&g_fx.waiting_lock, 0); }
static int fx_req_same(const fx_request *a, const fx_request *b)
{
    return a->lo == b->lo && a->hi == b->hi && a->slot == b->slot && a->seq == b->seq && a->time == b->time;
}
static int fx_req_older(const fx_request *a, const fx_request *b)
{
    return a->time < b->time || (a->time == b->time && a->seq < b->seq);
}

/* the worker adds and the game's thread finds and drops, a full table gives its oldest up for a new one */
static int fx_req_add(const fx_request *r)
{
    int i, k = -1;
    if (!fx_req_lock()) return 0;
    for (i = 0; i < FX_WAITING; i++)
        if (g_fx.waiting[i].used && fx_req_same(&g_fx.waiting[i], r)) {
            fx_req_unlock();
            return 1;
        }
    for (i = 0; i < FX_WAITING; i++) {
        if (!g_fx.waiting[i].used) {
            k = i;
            break;
        }
        if (k < 0 || fx_req_older(&g_fx.waiting[i], &g_fx.waiting[k])) k = i;
    }
    g_fx.waiting[k] = *r;
    g_fx.waiting[k].used = 1;
    fx_req_unlock();
    return 1;
}

static int fx_req_find(const char *typed, fx_request *out)
{
    int i, k = -1;
    if (!fx_req_lock()) return 0;
    for (i = 0; i < FX_WAITING; i++)
        if (g_fx.waiting[i].used && fx_same_name(typed, g_fx.waiting[i].name, FX_NAME) &&
            (k < 0 || fx_req_older(&g_fx.waiting[k], &g_fx.waiting[i])))
            k = i;
    if (k >= 0) *out = g_fx.waiting[k];
    fx_req_unlock();
    return k >= 0;
}
static void fx_req_drop(const fx_request *r)
{
    int i;
    if (!fx_req_lock()) return;
    for (i = 0; i < FX_WAITING; i++)
        if (g_fx.waiting[i].used && fx_req_same(&g_fx.waiting[i], r)) g_fx.waiting[i].used = 0;
    fx_req_unlock();
}

static int fx_seen_has(const fx_request *r)
{
    int i;
    for (i = 0; i < g_fx.nseen; i++) {
        const fx_seen_rec *s = &g_fx.seen[i];
        if (s->from_lo == r->lo && s->from_hi == r->hi && s->slot == r->slot && s->seq == r->seq && s->time == r->time) return 1;
    }
    return 0;
}
/* run after the seen set's pruned, a request whose file has left r\b is forgotten */
static void fx_req_sync(void)
{
    int i;
    if (!fx_req_lock()) return;
    for (i = 0; i < FX_WAITING; i++)
        if (g_fx.waiting[i].used && !fx_seen_has(&g_fx.waiting[i])) g_fx.waiting[i].used = 0;
    fx_req_unlock();
}

static BYTE __cdecl fx_answer_callback(int tag, int unused, int code, int extra)
{
    (void)tag; (void)unused; (void)extra;
    if (g_fx.ans_state == FX_ANS_RUNNING && !g_fx.ans_done) {
        g_fx.ans_code = code;
        g_fx.ans_done = 1;
    }
    return 1;
}

static void fx_answer_say(const char *colour, const char *text)
{
    if (!g_fx.at_select) fx_print(colour == g_fx_note ? FX_ICON_INFO : FX_ICON_NONE, colour, text, "", "");
}
static void fx_answer_end(void)
{
    g_fx.ans_state = FX_ANS_NONE;
    g_fx.ans_done = 0;
}

static void fx_answer_command(const char *who, const char *word)
{
    fx_request r;
    int yes;
    if (!lstrcmpiA(word, "yes")) yes = 1;
    else if (!lstrcmpiA(word, "no")) yes = 0;
    else return;
    if (!fx_req_find(who, &r)) {
        fx_print(FX_ICON_NONE, g_fx_red, g_fx_no_request, "", "");
        return;
    }
    if (g_fx.ans_state != FX_ANS_NONE) {
        fx_print(FX_ICON_NONE, g_fx_red, g_fx_not_answered, "", "");
        base_log("ffxi: a friend request answer didn't go, another is still waiting for its reply\r\n");
        return;
    }
    g_fx.want = r;
    g_fx.want_yes = yes;
    g_fx.ans_state = FX_ANS_WANTED;
}

static void fx_answer_start(BYTE *b)
{
    static char title[0x80], title_1[0x80], typed[16];
    const fx_request *w = &g_fx.want;
    int r;
    memset(title, 0, sizeof title);
    memset(title_1, 0, sizeof title_1);
    memset(typed, 0, sizeof typed);
    g_fx.ans_done = 0;
    g_fx.ans_at = GetTickCount64();
    g_fx.ans_state = FX_ANS_RUNNING;
    if (!pc_has(b, SV_GET_MESSAGE_TITLE)) {
        r = -1;
    } else if (g_fx.want_yes) {
        pc_get_message_title(b, 9, title, 0x7f);
        pc_get_message_title(b, 1, title_1, 0x7f);
        r = ((fx_accept_fn)pc_addr_of(g_fx.base, AT_FX_FRIEND_ACCEPT))(w->seq, w->time, w->lo, w->hi, typed, title, title_1,
                                                                        (void *)fx_answer_callback, 0x15);
    } else {
        pc_get_message_title(b, 10, title, 0x7f);
        r = ((fx_decline_fn)pc_addr_of(g_fx.base, AT_FX_FRIEND_DECLINE))(w->seq, w->time, w->lo, w->hi, title, typed,
                                                                          (void *)fx_answer_callback, 0x16);
    }
    if (!r) return;
    fx_answer_end();
    base_log("ffxi: a friend request answer didn't start (%d)\r\n", r);
    fx_answer_say(g_fx_red, g_fx_not_answered);
}

/* the callback only leaves its code, the lines are printed from here */
static void fx_answer_pump(BYTE *b)
{
    int code;
    if (g_fx.ans_state == FX_ANS_WANTED) {
        if (g_fx.off) {
            fx_answer_end();
            base_log("ffxi: a friend request answer didn't start, the hook's switched off\r\n");
            fx_answer_say(g_fx_red, g_fx_not_answered);
            return;
        }
        fx_answer_start(b);
    }
    if (g_fx.ans_state != FX_ANS_RUNNING) return;
    if (g_fx.ans_done) {
        code = (int)g_fx.ans_code;
        fx_answer_end();
        if (!code) {
            fx_req_drop(&g_fx.want);
            base_log("ffxi: a friend request was %s\r\n", g_fx.want_yes ? "accepted" : "declined");
            fx_answer_say(g_fx_note, g_fx.want_yes ? "You accepted the friend request." : "You declined the friend request.");
        } else {
            base_log("ffxi: a friend request answer failed (%d)\r\n", code);
            fx_answer_say(g_fx_red, g_fx_not_answered);
        }
    } else if (GetTickCount64() - g_fx.ans_at >= FX_ANSWER_MS) {
        fx_answer_end();
        base_log("ffxi: a friend request answer got no reply in 30s\r\n");
        fx_answer_say(g_fx_red, g_fx_not_answered);
    }
}

static void fx_command(BYTE *b)
{
    static char line[FX_LINE_IN + 1], plain[FX_PLAIN];
    char *who, *raw, name[FX_NAME + 8];
    fx_send *s;
    int kind;
    lstrcpynA(line, (const char *)pc_addr_of(g_fx.base, AT_FX_EXPANDED), sizeof line);
    if ((kind = fx_parse(line, &who, &raw)) == FX_CMD_NONE) return;
    if (kind == FX_CMD_FR) {
        if (fx_answers()) fx_answer_command(who, raw);
        return;
    }
    if (g_fx.nsends >= FX_SENDS) {
        fx_print(FX_ICON_NONE, g_fx_red, g_fx_not_sent, "", "");
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
        if (!s->name[0] && kind == FX_CMD_RM) fx_print(FX_ICON_NONE, g_fx_red, g_fx_no_reply, "", "");
        else if (!s->name[0] && *who) {
            fx_clean((const BYTE *)who, lstrlenA(who), name, FX_NAME);
            fx_print(FX_ICON_NONE, g_fx_red, name, g_fx_no_friend, "");
        } else fx_print(FX_ICON_NONE, g_fx_red, g_fx_not_sent, "", "");
        base_log("ffxi: a %s didn't go, %s\r\n", kind == FX_CMD_M ? "/m" : "/rm",
                 !s->name[0] ? (kind == FX_CMD_M ? "no friend by that name" : "nobody to reply to yet") : "nothing to send");
        return;
    }
    g_fx.nsends++;
    wsprintfA(name, "[%s%s%s] ", g_fx_name, s->name, g_fx_blue);
    fx_print(FX_ICON_POL, g_fx_blue, ">> ", name, s->text);
}

/* the game's own commands always win, this only sees one it didn't know */
static const BYTE *__cdecl fx_lookup(const BYTE *table, const char *token)
{
    const BYTE *r = g_fx.lookup(table, token);
    if (r || g_fx.off || !g_fx.watching || !token) return r;
    __try {
        if (!lstrcmpiA(token, "/m") || !lstrcmpiA(token, "/rm") || (fx_answers() && !lstrcmpiA(token, "/fr"))) fx_command(g_ig.b);
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
    if (!ok) fx_print(FX_ICON_NONE, g_fx_red, g_fx_not_sent, "", "");
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
    for (; g_fx.nsends; g_fx.nsends--) fx_print(FX_ICON_NONE, g_fx_red, g_fx_not_sent, "", "");
    for (; g_fx.unsent; g_fx.unsent--) fx_print(FX_ICON_NONE, g_fx_red, g_fx_not_sent, "", "");
}

/* game thread */
static void fx_poll(BYTE *b)
{
    if (!g_fx.watching || !g_fx.commands) return;
    __try {
        if (g_fx.off) {
            fx_send_wind_down(b);
            g_fx_step = "friend answer";
            fx_answer_pump(b);
            g_fx_step = "";
            return;
        }
        g_fx_step = "send";
        fx_send_pump(b);
        g_fx_step = "friend answer";
        fx_answer_pump(b);
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
    if (g_fx.commands && g_fx.job < 0 && !g_fx.msg && !g_fx.nsends && !g_fx.unsent && g_fx.ans_state == FX_ANS_NONE) {
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
        fx_line *l;
        while (g_fx.head != g_fx.tail && g_fx.ring[g_fx.head].state == FX_FREE) g_fx.head = (g_fx.head + 1) % FX_RING;
        l = &g_fx.ring[g_fx.head];
        if (l->state != FX_QUEUED || (l->kind != FX_MOVE && !fx_print_line(l))) return;
        if (l->move && !message_store_move_name(b, l->hdr, l->file, g_home, 1, l->now)) l->move = 0;
        l->printed_at = GetTickCount();
        InterlockedExchange(&l->state, l->move ? FX_PRINTED : FX_FREE);
        g_fx.head = (g_fx.head + 1) % FX_RING;
    }
}

/* [[menu global] + 4] + 0x46 is the name of the menu in front, 16 characters. Game thread only */
static int fx_menu_name(char *out)
{
    const BYTE *menu, *inner;
    if (!pc_addr_found(AT_FX_MENU_FRONT)) return 0;
    if (!(menu = *(const BYTE **)pc_addr_of(g_fx.base, AT_FX_MENU_FRONT))) return 0;
    if (!(inner = *(const BYTE **)(menu + 4))) return 0;
    memcpy(out, inner + 0x46, 16);
    out[16] = 0;
    return 1;
}
static int fx_lobby_menu(void)
{
    /* not the confirm box, a yes/no like it could be up in the world */
    static const char names[][17] = { "menu    ptc8lice", "menu    loby2win", "menu    dbnamese" };
    char name[17];
    int i;
    if (!fx_menu_name(name)) return 0;
    for (i = 0; i < (int)(sizeof names / sizeof names[0]); i++)
        if (!memcmp(name, names[i], 16)) return 1;
    return 0;
}

/* game thread */
static void fx_game_tick(BYTE *b)
{
    LONG was;
    DWORD seen;
    int lobby;
    if (!g_fx.found) return;
    __try {
        if (!g_fx.off) {
            if (!g_fx.patched) fx_patch();
            was = g_fx.in_world;
            lobby = fx_lobby_menu();
            if (lobby && !g_fx.at_select) {
                g_fx.at_select = 1;
                g_fx.netstat = 0;
                SetEvent(g_ig.wake);
                base_log("ffxi: at the character select, messages wait for Provenance till the player's in the world\r\n");
            }
            seen = g_fx.netstat;
            g_fx.in_world = !lobby && seen && (LONG)(GetTickCount() - seen) < FX_WORLD_MS && pc_game_tag(b) >= 0;
            if (g_fx.in_world && g_fx.at_select) {
                g_fx.at_select = 0;
                base_log("ffxi: in the world, messages print in the chat log again\r\n");
            }
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

/* finds FFXiMain and looks for every address in it, 0 when it isn't loaded yet */
static int fx_resolve(void)
{
    HMODULE m = GetModuleHandleA("FFXiMain.dll");
    const IMAGE_NT_HEADERS32 *nt;
    if (!m) {
        base_log("ffxi: FFXiMain.dll isn't loaded yet\r\n");
        return 0;
    }
    g_fx.base = (BYTE *)m;
    nt = (const IMAGE_NT_HEADERS32 *)(g_fx.base + ((const IMAGE_DOS_HEADER *)m)->e_lfanew);
    g_fx.size = nt->OptionalHeader.SizeOfImage;
    addr_resolve(IN_FFXI, "FFXiMain.dll", g_fx.base, (DWORD)(ULONG_PTR)m);
    InterlockedExchange(&g_fx.resolved, 1);
    return 1;
}

static void fx_look(void)
{
    const BYTE *add;
    int missing;
    if (!fx_resolve()) return;
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
    if (!pc_addr_found(AT_FX_FRIEND_DECLINE) || !pc_addr_found(AT_FX_FRIEND_ACCEPT))
        base_log("ffxi: no friend request answer in this build, no /fr this time\r\n");
    if (!pc_addr_found(AT_FX_MENU_FRONT))
        base_log("ffxi: no menu in front in this build, messages can't tell the character select from zoning\r\n");
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
            if (g_fx.seen[i].done && g_fx.seen[i].req != FX_REQ_NEW && g_fx.seen[i].req != FX_REQ_KEPT &&
                (k < 0 || g_fx.seen[i].time < g_fx.seen[k].time) && !fx_in_ring(g_fx.seen[i].name))
                k = i;
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
        if (pc_mail_kind(hdr) == 1 && (MAIL(hdr)->flags & MAIL_VALID)) r->req = FX_REQ_NEW;
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
static int fx_sender_away(const BYTE *hdr)
{
    int i;
    for (i = 0; i < IG_FRIENDS; i++) {
        const pol_friend *f = &g_ig.written.friends[i];
        if (pc_friend_valid(f) && f->id_lo == MAIL(hdr)->from_lo && f->id_hi == MAIL(hdr)->from_hi &&
            pc_friend_slot(f) == MAIL(hdr)->from_slot)
            return !(f->flags & FRIEND_PENDING) && peers_friend_online(f->flags) && ((f->flags >> 13) & 7) == 2;
    }
    return 0;
}
static int fx_switched_off(const char *file) { return file[0] && GetFileAttributesA(file) != INVALID_FILE_ATTRIBUTES; }
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
/* the seen set doesn't keep headers, so each new request is decoded again */
static void fx_requests(BYTE *b)
{
    __declspec(align(8)) BYTE hdr[sizeof(pol_mail)];
    int i;
    if (!fx_answers()) return;
    for (i = 0; g_ig.written.me && i < g_fx.nseen; i++) {
        fx_seen_rec *s = &g_fx.seen[i];
        fx_request r;
        if (s->req != FX_REQ_NEW) continue;
        memset(hdr, 0, sizeof hdr);
        if (pc_mail_decode_header(b, hdr, s->name) != 0) continue;
        if (!message_store_owned(hdr, 1, g_ig.written.me) || pc_mail_receipt(hdr) || fx_ignored(hdr)) {
            s->req = FX_REQ_DONE;
            continue;
        }
        memset(&r, 0, sizeof r);
        fx_sender(hdr, r.name);
        if (!r.name[0]) {
            s->req = FX_REQ_DONE;
            continue;
        }
        r.seq = MAIL(hdr)->seq;
        r.time = MAIL(hdr)->time;
        r.lo = MAIL(hdr)->from_lo;
        r.hi = MAIL(hdr)->from_hi;
        r.slot = MAIL(hdr)->from_slot;
        if (fx_req_add(&r)) s->req = FX_REQ_KEPT;
    }
    fx_req_sync();
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
    l->sound = (kind == FX_IN || kind == FX_NOTE) && !fx_switched_off(g_fx.quiet);
    l->away = kind == FX_IN && hdr && fx_sender_away(hdr);
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

/* the player's at the character select: what's waiting to print or be looked at stays unread, like at first entry */
static void fx_drop_waiting(void)
{
    int i;
    for (i = 0; i < FX_RING; i++)
        if (g_fx.ring[i].state == FX_QUEUED && g_fx.ring[i].kind != FX_MOVE) InterlockedExchange(&g_fx.ring[i].state, FX_FREE);
    for (i = 0; i < g_fx.nseen; i++) g_fx.seen[i].done = 1;
}

static int fx_friend_room(void)
{
    int i, used = 0, mine = 0;
    for (i = 0; i < FX_RING; i++) {
        used += g_fx.ring[i].state != FX_FREE;
        mine += g_fx.ring[i].state == FX_QUEUED && (g_fx.ring[i].kind == FX_FRIEND || g_fx.ring[i].kind == FX_PLAYING);
    }
    return used < FX_FRIEND_RING && mine < FX_FRIEND_LINES;
}

/* the app's list of the names it knows, a line each: "w <world id> <name>" and "c <ctsid> <ctid low> <ctid high> <name>" */
static int fx_known_name(const char *key, char *out, int cap)
{
    static char list[FX_NAMES_MAX + 2];
    DWORD got = 0;
    HANDLE h;
    const char *p;
    int n = 0, len = lstrlenA(key);
    out[0] = 0;
    if (!g_fx.names[0]) return 0;
    h = CreateFileA(g_fx.names, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING,
                    FILE_FLAG_OPEN_REPARSE_POINT, NULL);
    if (h == INVALID_HANDLE_VALUE) return 0;
    list[0] = '\n';
    if (!ReadFile(h, list + 1, FX_NAMES_MAX, &got, NULL)) got = 0;
    CloseHandle(h);
    list[1 + got] = 0;
    /* a file longer than what's read ends mid-line, and half a name is worse than none */
    if (got == FX_NAMES_MAX && strrchr(list, '\n')) *strrchr(list, '\n') = 0;
    for (p = list; (p = strstr(p, key)) != NULL; p += len) {
        if (p[-1] != '\n') continue;
        for (p += len; n < cap - 1 && ((*p >= 'A' && *p <= 'Z') || (*p >= 'a' && *p <= 'z')); p++) out[n++] = *p;
        out[n] = 0;
        if (*p && *p != '\r' && *p != '\n') out[0] = 0;
        return out[0] != 0;
    }
    return 0;
}

static void fx_playing_as(const fx_presence *p, char *text, int cap)
{
    char key[40], who[16], world[16];
    wsprintfA(key, "c %08x %08x %08x ", p->ctsid, p->ct_lo, p->ct_hi);
    fx_known_name(key, who, sizeof who);
    wsprintfA(key, "w %u ", (p->ctsid >> 16) & 0xff);
    fx_known_name(key, world, sizeof world);
    if (!who[0] && !world[0]) return;
    fx_append(text, cap, " (");
    fx_append(text, cap, who);
    if (who[0] && world[0]) fx_append(text, cap, " - ");
    fx_append(text, cap, world);
    fx_append(text, cap, ")");
}

/* only a friend seen before gets a line, a row new to the list is just noted. A burst in one copy is the list reloading or
   the connection dropping, not friends */
static void fx_friend_lines(void)
{
    enum { NONE, PRESENCE, STARTED, STOPPED };
    fx_presence now[IG_FRIENDS];
    char name[FX_NAME], text[FX_NAME + 128];
    ULONGLONG tick = GetTickCount64();
    int i, k, n = 0, changes = 0, live, show, on_lines, game_lines;
    if (!g_friends_loaded || !g_ig.written.me) return;
    for (i = 0; i < IG_FRIENDS; i++) {
        const pol_friend *f = &g_ig.written.friends[i];
        fx_presence *p = &now[n];
        if (!pc_friend_valid(f) || (f->flags & FRIEND_PENDING)) continue;
        memset(p, 0, sizeof *p);
        p->lo = f->id_lo;
        p->hi = f->id_hi;
        p->slot = pc_friend_slot(f);
        p->on = peers_friend_online(f->flags) != 0;
        p->game = peers_friend_ffxi(f, &p->ctsid, &p->ct_lo, &p->ct_hi);
        p->row = i;
        for (k = 0; g_fx.presence_known && k < g_fx.npresence; k++) {
            const fx_presence *was = &g_fx.presence[k];
            if (was->lo != p->lo || was->hi != p->hi || was->slot != p->slot) continue;
            p->changed = was->on != p->on;
            p->game_changed = was->game != p->game ||
                              (p->game && (was->ctsid != p->ctsid || was->ct_lo != p->ct_lo || was->ct_hi != p->ct_hi));
            p->stop_at = was->stop_at;
            break;
        }
        changes += p->changed || p->game_changed;
        n++;
    }
    live = g_fx.presence_known && g_fx.watching && !g_fx.at_select;
    show = live && changes && changes < FX_FRIEND_BURST;
    on_lines = show && !fx_switched_off(g_fx.no_friend_lines);
    game_lines = !fx_switched_off(g_fx.no_playing_lines);
    for (i = 0; i < n; i++) {
        fx_presence *p = &now[i];
        int kind = NONE;
        if (!p->on || p->game) p->stop_at = 0;
        /* straight from offline into the game is the one line. Leaving it waits a moment, they're usually going offline too */
        if (p->game_changed && p->on && p->game) kind = show && game_lines ? STARTED : NONE;
        else if (p->game_changed && p->on && !p->changed) p->stop_at = show && game_lines ? tick : 0;
        if (!kind && p->stop_at && tick - p->stop_at >= FX_STOP_GRACE_MS) {
            if (live && game_lines) kind = STOPPED;
            else p->stop_at = 0;
        }
        if (!kind && on_lines && p->changed) kind = PRESENCE;
        if (!kind || !fx_friend_room()) continue;
        if (kind == STOPPED) p->stop_at = 0;
        fx_clean((const BYTE *)g_ig.written.friends[p->row].name, 16, name, sizeof name);
        if (!name[0]) continue;
        lstrcpyA(text, g_fx_name);
        fx_append(text, sizeof text, name);
        if (kind == PRESENCE) {
            fx_append(text, sizeof text, p->on ? g_fx_on : g_fx_off);
            fx_append(text, sizeof text, p->on ? " has come online." : " has gone offline.");
        } else {
            fx_append(text, sizeof text, kind == STARTED ? g_fx_on : g_fx_off);
            fx_append(text, sizeof text, kind == STARTED ? " is now playing FINAL FANTASY XI." : " is no longer playing FINAL FANTASY XI.");
            if (kind == STARTED) fx_playing_as(p, text, sizeof text);
        }
        fx_queue(kind == PRESENCE ? FX_FRIEND : FX_PLAYING, name, text, NULL, NULL, 0);
    }
    memcpy(g_fx.presence, now, n * sizeof now[0]);
    g_fx.npresence = n;
    g_fx.presence_known = 1;
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
    /* a friend request, or the news that one was accepted, stays in r\b and just gets a line. Anything about groups gets none */
    if (kind == 1 || kind == 9) {
        fx_sender(hdr, who);
        if (kind == 1 && fx_answers() && who[0]) {
            lstrcpyA(text, g_fx_note);
            fx_append(text, sizeof text, "You've received a friend request from ");
            fx_append(text, sizeof text, g_fx_name);
            fx_append(text, sizeof text, who);
            fx_append(text, sizeof text, g_fx_note);
            fx_append(text, sizeof text, ". Respond with /fr ");
            fx_append(text, sizeof text, who);
            fx_append(text, sizeof text, " yes/no.");
        } else {
            lstrcpyA(text, g_fx_name);
            fx_append(text, sizeof text, who);
            fx_append(text, sizeof text, g_fx_note);
            fx_append(text, sizeof text, kind == 9 ? " accepted your friend request." : " sent you a friend request.");
        }
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
            /* already there before the player first got in the world, or it came at the character select: it stays unread */
            } else if (seeding || g_fx.at_select) {
                if (fx_seen_add(fd.cFileName, hdr, 1) >= 0) n++;
                continue;
            } else if ((k = fx_seen_add(fd.cFileName, hdr, 0)) < 0) {
                continue;
            }
        }
        /* zoning or a cutscene: it waits and prints once they're back. At the character select it's let go */
        if (!g_fx.in_world) {
            if (g_fx.at_select) g_fx.seen[k].done = 1;
            else g_fx.seen[k].first = GetTickCount();
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
    fx_requests(b);
    if (seeding) base_log("ffxi: in the world, %d message(s) already in r\\b stay unread, new ones go to the chat log\r\n", n);
}

static void fx_switch_file(char *out, const char *name)
{
    int n = (int)GetEnvironmentVariableA(ENV_IPC_DIR, out, MAX_PATH);
    if (n > 0 && n < MAX_PATH - 32) {
        if (out[n - 1] != '\\') lstrcatA(out, "\\");
        lstrcatA(out, name);
    } else {
        out[0] = 0;
    }
}

static void fx_watch_start(BYTE *b)
{
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
    fx_switch_file(g_fx.quiet, IPC_QUIET);
    fx_switch_file(g_fx.no_friend_lines, IPC_NO_FRIEND_LINES);
    fx_switch_file(g_fx.no_playing_lines, IPC_NO_PLAYING_LINES);
    fx_switch_file(g_fx.names, IPC_CHARACTERS);
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

static int lb_wanted(void);

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
            /* the licence window is up within seconds, so a game the lobby has work in is looked at sooner */
            if (!g_fx.world && now - g_fx.polled_at < (ULONGLONG)(lb_wanted() ? FX_LOBBY_LOOK_MS : FX_LOOK_AFTER_MS)) return;
            if (now < g_fx.next_look) return;
            g_fx.tries++;
            g_fx.next_look = now + (lb_wanted() && g_fx.tries <= 2 ? FX_LOBBY_RETRY_MS : FX_RETRY_MS);
            fx_look();
            return;
        }
        fx_moves(b);
        fx_sent_copies(b, 1);
        if (g_fx.off) return;
        fx_friend_lines();
        if (!g_fx.watching) {
            if (g_fx.in_world && g_fx.patched) fx_watch_start(b);
            return;
        }
        if (g_fx.at_select) fx_drop_waiting();
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
