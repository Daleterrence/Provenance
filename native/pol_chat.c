/* POL chat rooms: the zone and room lists are database searches, a room is IRC on the link polcore already holds.
   One command in flight and every step waits for the last, like the viewer */
#define CR_LINES 256
#define CR_RAW 128            /* the viewer's ring, a busy room's 3s all land in one pump */
#define CR_PEOPLE 128
#define CR_ZONES 32
#define CR_ROOMS 100
#define CR_REPLIES 8
#define CR_GAP_MS 3000        /* between the user's own actions that reach the server */
#define CR_SEND_GAP_MS 1000   /* between PRIVMSGs */
#define CR_WAIT_MS 120000
#define CR_TRIES 3600         /* turns a command can't be queued (another one's waiting), about the viewer's minute */
#define CR_TEXT_MAX 300       /* Shift-JIS bytes in a line, the app holds to the same */
#define CR_NAME_MAX 15

typedef struct { char text[0x200]; char chan[0x40]; DWORD lo, hi; int whisper; } cr_raw;
typedef struct { DWORD lo, hi; int key, member, away, master, answered; WCHAR name[0x21]; } cr_person;
typedef struct { DWORD seq, time; int kind, me, from; WCHAR name[0x21]; WCHAR text[0x201]; } cr_line;
typedef struct { char irc[64]; char topic[80]; int official, locked; DWORD people, capa, ctime; WORD cate[3]; } cr_room;
typedef struct { BYTE order, field; WORD pad; void *next; } cr_sort;

enum { CRK_SAY, CRK_ENTERED, CRK_LEFT, CRK_AWAY, CRK_BACK, CRK_MASTER, CRK_KICKED, CRK_WHISPER };
static const char *const g_cr_kind[] = { "say", "entered", "left", "away", "back", "master", "kicked", "whisper" };

/* in-room steps: AWAY (back), JOIN, WHO+MODE, TOPIC, then in. PART on the way out */
enum { CR_OUT, CR_AWAY, CR_JOIN, CR_WHO, CR_TOPIC, CR_IN, CR_PART };
/* what's in flight */
enum { CRW_NONE, CRW_CMD, CRW_MSG };
/* PRIVMSG kinds */
enum { CRM_SAY, CRM_HELLO = 2, CRM_REPLY = 3 };

static int g_cr_used;
static void *g_cr_644;

static cr_raw g_cr_raw[CR_RAW];
static volatile LONG g_cr_raw_n;
static int g_cr_raw_lost;

static pdb_schema *g_cr_cpib, *g_cr_rpib;
static pfb_hdr *g_cr_cpfb, *g_cr_rpfb;
static int g_cr_job = -1;
static int g_cr_set, g_cr_count;
static BYTE *g_cr_buf;
static ULONGLONG g_cr_db_t0;
static char g_cr_ztok[48], g_cr_zstate[32] = "";
static char g_cr_rtok[48], g_cr_rstate[32] = "";
static struct { WORD code; DWORD rooms, people; } g_cr_zone[CR_ZONES];
static int g_cr_zone_n;
static cr_room g_cr_rooms[CR_ROOMS];
static int g_cr_rooms_n, g_cr_rzone, g_cr_roff, g_cr_rtotal;
static __declspec(align(8)) pdb_cond g_cr_cond[2];
static pdb_cond *g_cr_where;
static cr_sort g_cr_sortby[2];
static DWORD g_cr_vzone, g_cr_vcapa;

static int g_cr_st = CR_OUT, g_cr_wait, g_cr_tries, g_cr_open, g_cr_leave, g_cr_hello, g_cr_replies;
static int g_cr_msg_kind = -1, g_cr_skip_topic, g_cr_reread, g_cr_kicked_me;
static int g_cr_joined;   /* a JOIN's gone and no PART since: the server might have me in the room */
static char g_cr_cancel_tok[48];
static DWORD g_cr_written_seq;
static ULONGLONG g_cr_written_at;
static ULONGLONG g_cr_sent_at, g_cr_msg_at;
static char g_cr_room[64], g_cr_key[32];
static WCHAR g_cr_nick[CR_NAME_MAX + 1];
static char g_cr_frame[0x200];
static char g_cr_tok[48], g_cr_state[32] = "";
static char g_cr_stok[48], g_cr_sstate[32] = "";
static unsigned __int64 g_cr_me;
static BYTE g_cr_slot[0x98];
static BYTE g_cr_mem[0x40 * 16];
static cr_person g_cr_people[CR_PEOPLE];
static int g_cr_people_n, g_cr_next_key;
static cr_line g_cr_line[CR_LINES];
static int g_cr_line_n, g_cr_line_next, g_cr_lines_dirty;
static DWORD g_cr_seq;

static int cr_session(BYTE *b) { return SVC(0xcd0) ? ((svc_v)SVC(0xcd0))() : -0x41f; }

static DWORD cr_now(void)
{
    ULARGE_INTEGER t;
    GetSystemTimeAsFileTime((FILETIME *)&t);
    return (DWORD)(t.QuadPart / 10000000ull - 11644473600ull);
}

/* polcore's room callbacks run inside its line handling on the hold loop's thread, so copy and get out. With no room
   open a line's dropped */
static void cr_take(const char *text, const char *chan, DWORD lo, DWORD hi, int whisper)
{
    LONG n = g_cr_raw_n;
    if (!g_cr_open || !text || !chan || IsBadStringPtrA(text, 0x200) || IsBadStringPtrA(chan, 0x40)) return;
    if (n >= CR_RAW) { g_cr_raw_lost++; return; }
    lstrcpynA(g_cr_raw[n].text, text, sizeof g_cr_raw[n].text);
    lstrcpynA(g_cr_raw[n].chan, chan, sizeof g_cr_raw[n].chan);
    g_cr_raw[n].lo = lo;
    g_cr_raw[n].hi = hi;
    g_cr_raw[n].whisper = whisper;
    g_cr_raw_n = n + 1;
}
static void __cdecl chat_line(const char *text, const char *chan, DWORD lo, DWORD hi) { cr_take(text, chan, lo, hi, 0); }
/* a PRIVMSG to me rather than a room, which in chat is a whisper. Kept only while a room's open, same as room lines */
static void __cdecl chat_private(const char *text, DWORD myLo, DWORD myHi, DWORD lo, DWORD hi)
{
    (void)myLo; (void)myHi;
    cr_take(text, "", lo, hi, 1);
}
/* svc 0x644 gets a room NOTICE first, same as the viewer, and only one it says isn't its own is a room line */
static int __cdecl chat_notice(const char *text, const char *chan, DWORD lo, DWORD hi)
{
    int r = ((int (__cdecl *)(const char *, const char *, DWORD, DWORD))g_cr_644)(text, chan, lo, hi);
    if (r == 0) cr_take(text, chan, lo, hi, 0);
    return r;
}
/* the svc 0xd9c array, registered once at the start of the hold */
static void push_callbacks(BYTE *b, void *cb[6])
{
    g_cr_644 = SVC(0x644);
    cb[0] = (void *)chat_private;
    cb[1] = SVC(0x538);
    cb[2] = (void *)chat_line;
    cb[3] = g_cr_644 ? (void *)chat_notice : NULL;
    cb[4] = (void *)mail_raw;
    cb[5] = (void *)mail_evt;
}

static void cr_wide(BYTE *b, const char *sj, WCHAR *w, int cap)
{
    static char tmp[0x201];
    memset(tmp, 0, sizeof tmp);
    lstrcpynA(tmp, sj, sizeof tmp);
    memset(w, 0, cap * sizeof(WCHAR));
    if (!tmp[0] || cap < 0x202) return;
    ((int (__cdecl *)(WCHAR *, const char *, int))SVC(0xa38))(w, tmp, 0x3013);
    w[cap - 1] = 0;
}
static int cr_sjis(BYTE *b, const WCHAR *w, char *out, int cap)
{
    int n = ((int (__cdecl *)(char *, const WCHAR *, int))SVC(0xa40))(NULL, w, 0x81ac);
    out[0] = 0;
    if (n < 0 || n >= cap) return -1;
    ((int (__cdecl *)(char *, const WCHAR *, int))SVC(0xa40))(out, w, 0x81ac);
    out[n] = 0;
    return n;
}
static void cr_u8(const WCHAR *w, char *out, int cap)
{
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, out, cap - 1, NULL, NULL);
    out[n > 0 ? n - 1 : 0] = 0;
    out[cap - 1] = 0;
}
static void cr_copy(WCHAR *out, int cap, const WCHAR *src, int n)
{
    int i;
    for (i = 0; i < n && i < cap - 1 && src[i]; i++) out[i] = src[i] < 0x20 ? L' ' : src[i];
    out[i] = 0;
}

static cr_person *cr_person_for(DWORD lo, DWORD hi, int add)
{
    int i, drop = -1;
    for (i = 0; i < g_cr_people_n; i++)
        if (g_cr_people[i].lo == lo && g_cr_people[i].hi == hi) return &g_cr_people[i];
    if (!add) return NULL;
    if (g_cr_people_n < CR_PEOPLE) i = g_cr_people_n++;
    else {
        for (i = 0; i < CR_PEOPLE; i++)
            if (!g_cr_people[i].member && (drop < 0 || g_cr_people[i].key < g_cr_people[drop].key)) drop = i;
        if (drop < 0) return NULL;
        i = drop;
    }
    memset(&g_cr_people[i], 0, sizeof g_cr_people[i]);
    g_cr_people[i].lo = lo;
    g_cr_people[i].hi = hi;
    g_cr_people[i].key = ++g_cr_next_key;
    return &g_cr_people[i];
}
static int cr_is_me(DWORD lo, DWORD hi) { return g_cr_me && lo == (DWORD)g_cr_me && hi == (DWORD)(g_cr_me >> 32); }

static void cr_add_line(int kind, int me, const cr_person *p, const WCHAR *name, const WCHAR *text)
{
    cr_line *l = &g_cr_line[g_cr_line_next];
    memset(l, 0, sizeof *l);
    l->seq = ++g_cr_seq;
    l->time = cr_now();
    l->kind = kind;
    l->me = me;
    l->from = p ? p->key : 0;
    cr_copy(l->name, 0x21, name ? name : L"", 0x20);
    cr_copy(l->text, 0x201, text ? text : L"", 0x200);
    g_cr_line_next = (g_cr_line_next + 1) % CR_LINES;
    if (g_cr_line_n < CR_LINES) g_cr_line_n++;
    g_cr_lines_dirty = 1;
    g_snap_dirty = 1;
}

/* the name runs from just past the header (6 chars, 8 with the language) to the tab */
static int cr_tab(const WCHAR *w, int n, int from)
{
    for (int i = from; i < n; i++) if (w[i] == L'\t') return i;
    return -1;
}
/* one room line, already in UTF-16. The viewer's FUN_10099d8e, minus whispers, passwords and profile switches */
static void cr_parse(BYTE *b, const WCHAR *w, DWORD lo, DWORD hi, int whisper)
{
    WCHAR name[0x21], text[0x201];
    int n = lstrlenW(w), tab;
    cr_person *p;
    if (n < 1 || cr_is_me(lo, hi)) return;
    if (whisper) {
        /* the viewer only shows a whisper from someone in the room with me */
        if (w[0] != L'1' || n < 6 || (tab = cr_tab(w, n, 6)) < 0 || !(p = cr_person_for(lo, hi, 0)) || !p->member) return;
        cr_copy(name, 0x21, w + 6, tab - 6);
        cr_copy(text, 0x201, tab + 3 <= n ? w + tab + 3 : L"", 0x200);
        if (name[0]) lstrcpyW(p->name, name);
        cr_add_line(CRK_WHISPER, 0, p, name, text);
        return;
    }
    p = cr_person_for(lo, hi, 1);
    switch (w[0]) {
    case L'0':
        if (n < 6 || (tab = cr_tab(w, n, 6)) < 0) return;
        cr_copy(name, 0x21, w + 6, tab - 6);
        cr_copy(text, 0x201, tab + 3 <= n ? w + tab + 3 : L"", 0x200);
        if (p && name[0]) lstrcpyW(p->name, name);
        cr_add_line(CRK_SAY, 0, p, name, text);
        return;
    case L'2':
    case L'3':
        cr_copy(name, 0x21, n > 0x68 ? w + 0x68 : L"", 0x20);
        if (p) {
            if (name[0]) lstrcpyW(p->name, name);
            p->away = n > 0x67 && w[0x67] == L'0';
        }
        if (w[0] == L'3') return;
        cr_add_line(CRK_ENTERED, 0, p, name, NULL);
        /* everyone in the room answers an arrival with a 3, once per arrival here, never per frame */
        if (p && !p->answered && g_cr_open) {
            p->answered = 1;
            if (g_cr_replies < CR_REPLIES) g_cr_replies++;
        }
        return;
    case L'9':
        if (n < 6 || (tab = cr_tab(w, n, 6)) < 0) return;
        cr_copy(name, 0x21, w + 6, tab - 6);
        if (p && name[0]) lstrcpyW(p->name, name);
        if (tab + 3 < n && w[tab + 3] == L'0') {
            /* the viewer takes the message from past the name's length again, bounded here */
            int at = tab + 3 + (tab - 6) + 2;
            cr_copy(text, 0x201, at < n ? w + at : L"", 0x200);
            for (int i = 0; text[i]; i++) if (text[i] == L'\t') { text[i] = 0; break; }
            if (p) p->away = 1;
            cr_add_line(CRK_AWAY, 0, p, name, text);
        } else {
            if (p) p->away = 0;
            cr_add_line(CRK_BACK, 0, p, name, NULL);
        }
        return;
    case L'a':
    case L'b':
        cr_copy(name, 0x21, n > 8 ? w + 8 : L"", 0x20);
        if (w[0] == L'b' && p && p->master && !lstrcmpW(name, g_cr_nick)) g_cr_kicked_me = 1;
        cr_add_line(w[0] == L'a' ? CRK_MASTER : CRK_KICKED, 0, NULL, name, NULL);
        return;
    }
    /* 1 whisper (private only), 5/6 profile views, 7 a new password, 8 leaving: nothing to show */
}
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

/* a 2 or 3: type, the viewer's hidden profile, two face chars, '1' (not away) and the chat name */
static void cr_frame_hello(BYTE *b, int type, WCHAR *w)
{
    wsprintfW(w, L"%xz000000000000000z0z000z000z000z000z000z000z000z000z0000000z0000000z000000000000000z000000000000000z0",
              type);
    w[101] = L'0';
    w[102] = L'0';
    w[103] = L'1';
    lstrcpynW(w + 104, g_cr_nick, CR_NAME_MAX + 1);
    (void)b;
}
/* a 0: type, colour and emoticon as %2x (space padded), the face char, name, tab, language, text */
static void cr_frame_say(BYTE *b, const WCHAR *text, WCHAR *w, int cap)
{
    int lang = SVC(0xf18) ? ((svc_v)SVC(0xf18))() : 0, n;
    wsprintfW(w, L"%x%2x%2x%c", 0, 0, 0, L'0');
    lstrcatW(w, g_cr_nick);
    n = lstrlenW(w);
    wsprintfW(w + n, L"\t%02x", lang & 0xff);
    n = lstrlenW(w);
    lstrcpynW(w + n, text, cap - n);
}
/* PRIVMSG #room :frame has to fit polcore's 0x200 with its checksum and the line end */
static int cr_fits(int frameBytes) { return 8 + lstrlenA(g_cr_room) + 2 + frameBytes + 2 + 8 <= 0x200; }

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
static void cr_left(const char *state)
{
    if (lstrcmpA(g_cr_sstate, "sending") == 0) cr_send_state("failed:left");
    g_cr_msg_kind = -1;
    g_cr_wait = CRW_NONE;
    g_cr_joined = 0;
    cr_room_clear();
    SecureZeroMemory(g_cr_key, sizeof g_cr_key);
    g_cr_leave = 0;
    cr_go(CR_OUT);
    cr_room_state(state);
}
/* a step's answer: >0 done, <0 the server's error (or the link's), from svc 0x244 */
static void cr_step_done(BYTE *b, int r)
{
    int st = g_cr_st;
    g_cr_wait = CRW_NONE;
    if (st == CR_PART) { cr_left("left"); return; }
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
    if (st == CR_WHO) {
        if (r < 0) lg("chat: WHO answered %d\r\n", r);
        cr_members(b);
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

static int cr_privmsg(BYTE *b, const char *frame)
{
    int s = cr_session(b);
    if (s < 0) return -0x41f;
    return ((int (__cdecl *)(int, DWORD, DWORD, const char *, const char *))SVC(0x22c))(s, 0, 0, g_cr_room, frame);
}

/* one PRIVMSG in flight, never two inside a second */
static int cr_msg_ready(void)
{
    return g_cr_st == CR_IN && g_cr_wait == CRW_NONE && !g_cr_leave && GetTickCount64() - g_cr_msg_at >= CR_SEND_GAP_MS;
}
static void cr_msg_start(BYTE *b, int kind)
{
    int r = cr_privmsg(b, g_cr_frame);
    if (r == 0) {
        if (++g_cr_tries < CR_TRIES) { g_cr_msg_kind = kind; return; }
        r = -1;
    }
    g_cr_tries = 0;
    g_cr_msg_at = GetTickCount64();
    if (r < 0) {
        g_cr_msg_kind = -1;
        lg("chat: PRIVMSG (%d) refused %d\r\n", kind, r);
        if (kind == CRM_SAY) cr_send_state("failed:busy");
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
            cr_add_line(CRK_SAY, 1, NULL, g_cr_nick, tab >= 0 ? w + tab + 3 : L"");
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
}

static void cr_send_request(BYTE *b)
{
    char buf[0x800], *p, *tok, *text;
    static WCHAR w[0x220], t[0x220];
    char sj[0x200];
    int n;
    if (take_request("chatsend", buf, sizeof buf) < 0) return;
    g_cr_used = 1;
    p = buf;
    tok = req_line(&p);
    text = req_line(&p);
    lstrcpynA(g_cr_stok, tok, sizeof g_cr_stok);
    if (g_cr_st != CR_IN || !g_cr_open || g_cr_leave) { cr_send_state("failed:not in a room"); return; }
    if (!text[0]) { cr_send_state("failed:empty"); return; }
    if ((n = MultiByteToWideChar(CP_UTF8, 0, text, -1, t, 0x200)) <= 1) { cr_send_state("failed:too long"); return; }
    for (int i = 0; t[i]; i++) if (t[i] < 0x20) t[i] = L' ';
    if ((n = cr_sjis(b, t, sj, sizeof sj)) < 0 || n > CR_TEXT_MAX) { cr_send_state("failed:too long"); return; }
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
/* an IRC channel name as the room list builds it, nothing that'd change the JOIN line */
static int cr_irc_ok(const char *s)
{
    int n = lstrlenA(s);
    if (n < 2 || n > 63 || s[0] != '#') return 0;
    for (int i = 1; i < n; i++) if ((BYTE)s[i] <= 0x20 || (BYTE)s[i] >= 0x7f || s[i] == ',') return 0;
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
    int from_env = 0, rc;
    const WCHAR *t = cred_target("POL_CRED_CHAT", L"", tbuf, 256, &from_env);
    key[0] = 0;
    if (!t || !from_env || !launch_target(t)) return 0;
    rc = cred_read(t, user, sizeof user, key, cap);
    if (rc != PC_NOT_FOUND) CredDeleteW(t, CRED_TYPE_GENERIC, 0);
    if (rc == PC_OK && !lstrcmpA(user, tok)) return 1;
    SecureZeroMemory(key, cap);
    if (rc != PC_NOT_FOUND) lg("chat: room password entry unusable (%s)\r\n", rc == PC_OK ? "another enter's" : cred_why(rc));
    return 0;
}
/* a JOIN that timed out could still have got there, so it gets its one PART here */
static void cr_part_stale(BYTE *b)
{
    if (g_cr_st == CR_OUT && g_cr_joined && cr_part(b) > 0) lg("chat: PART for %s, its JOIN never answered\r\n", g_cr_room);
}
static void cr_join_refused(const char *state)
{
    SecureZeroMemory(g_cr_key, sizeof g_cr_key);
    if (state) cr_room_state(state);
}
static void cr_join_request(BYTE *b)
{
    char buf[512], *p, *tok, *irc, *haskey, *nick;
    int pw = 0;
    if (take_request("chatjoin", buf, sizeof buf) < 0) return;
    g_cr_used = 1;
    g_cr_last = GetTickCount64();
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
    if (g_cr_cancel_tok[0] && !lstrcmpA(g_cr_cancel_tok, tok)) {
        g_cr_cancel_tok[0] = 0;
        lg("chat: enter called off before it was taken\r\n");
        cr_join_refused(NULL);
        return;
    }
    g_cr_cancel_tok[0] = 0;
    lstrcpynA(g_cr_tok, tok, sizeof g_cr_tok);
    if (notice_blocks(g_notice)) { cr_join_refused("failed:account notice"); return; }
    if (!cr_irc_ok(irc) || (haskey[0] && lstrcmpA(haskey, "1")) || !cr_nick_ok(b, nick)) {
        cr_join_refused("failed:bad request");
        return;
    }
    if (cr_session(b) < 0) { cr_join_refused("failed:no chat"); return; }
    if (haskey[0] && (!pw || !cr_key_ok(g_cr_key))) { cr_join_refused("failed:no password"); return; }
    if (!haskey[0]) g_cr_key[0] = 0;
    cr_part_stale(b);
    /* a line the hook renamed but never read belongs to the last room, it never goes into this one */
    {
        char path[MAX_PATH + 64];
        for (int i = 0; i < 3; i++) {
            ipc_file(path, "chatsend");
            lstrcatA(path, i == 0 ? "" : i == 1 ? ".taking" : ".taking2");
            DeleteFileA(path);
        }
    }
    lstrcpynA(g_cr_room, irc, sizeof g_cr_room);
    cr_room_clear();
    g_cr_line_n = g_cr_line_next = 0;
    g_cr_lines_dirty = 1;
    g_cr_leave = 0;
    g_cr_me = SVC(0x270) ? ((unsigned __int64 (__cdecl *)(int))SVC(0x270))(cr_session(b)) : 0;
    lg("chat: entering %s%s\r\n", g_cr_room, g_cr_key[0] ? " (password)" : "");
    cr_room_state("entering");
    cr_go(CR_AWAY);
}
static void cr_leave_request(BYTE *b)
{
    char buf[128];
    (void)b;
    char *p = buf, *join;
    if (take_request("chatleave", buf, sizeof buf) < 0) return;
    g_cr_last = GetTickCount64();
    req_line(&p);
    join = req_line(&p);
    /* the app couldn't withdraw its enter, so it's called off here instead, even with a PART still going */
    if (join[0] && lstrcmpA(join, g_cr_tok)) lstrcpynA(g_cr_cancel_tok, join, sizeof g_cr_cancel_tok);
    cr_part_stale(b);
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
            if (g_cr_st == CR_JOIN || g_cr_st == CR_AWAY) {
                int sent = g_cr_st == CR_JOIN;
                cr_left("failed:timeout");
                g_cr_joined = sent;   /* it could still get there, so the next leave, Play or enter PARTs it */
                return;
            }
            r = 1;
        }
        if (g_cr_wait == CRW_MSG) cr_msg_done(r, NULL);
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
        if (g_cr_msg_kind >= 0) {
            if (GetTickCount64() - g_cr_msg_at >= CR_SEND_GAP_MS) cr_msg_start(b, g_cr_msg_kind);
            return;
        }
        if (!cr_msg_ready()) return;
        if (g_cr_hello) { g_cr_hello = 0; cr_frame_out(b, CRM_HELLO); return; }
        if (ctl && request_new("chatsend")) { cr_send_request(b); return; }
        if (g_cr_replies > 0) { g_cr_replies--; cr_frame_out(b, CRM_REPLY); }
        return;
    }
    r = cr_command(b, g_cr_st);
    if (r == 0) {
        if (++g_cr_tries < CR_TRIES) return;
        r = -1;
    }
    if (r < 0) {
        if (r != CR_NOTHING_TO_LEAVE) lg("chat: step %d couldn't be sent (%d)\r\n", g_cr_st, r);
        if (g_cr_st == CR_PART) { cr_left("left"); return; }
        if (g_cr_st == CR_AWAY) { cr_go(CR_JOIN); return; }
        if (g_cr_st == CR_JOIN) { cr_left(failed(r)); return; }
        cr_step_done(b, r);
        return;
    }
    g_cr_wait = CRW_CMD;
    g_cr_sent_at = GetTickCount64();
}

/* data\db\<name>.pfb, relocated by polcore, the same way pf_load reads prof_pol's */
static pfb_hdr *cr_pfb(BYTE *b, const WCHAR *file)
{
    WCHAR path[0x200 + 32];
    DWORD sz, got = 0;
    HANDLE f;
    BYTE *buf;
    pfb_hdr *p;
    memset(path, 0, sizeof path);
    if (((svc16c4_t)SVC(0x16c4))(0x14, path, 0x200, 0) <= 0) return NULL;
    path[0x200 - 1] = 0;
    lstrcatW(path, file);
    f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return NULL;
    sz = GetFileSize(f, NULL);
    buf = sz >= 0x20 && sz <= 0x100000 ? (BYTE *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sz + 16) : NULL;
    if (!buf || !ReadFile(f, buf, sz, &got, NULL) || got != sz || buf[3] != 0) {
        CloseHandle(f);
        if (buf) HeapFree(GetProcessHeap(), 0, buf);
        return NULL;
    }
    CloseHandle(f);
    p = ((pfb_hdr *(__cdecl *)(void *))SVC(0x498))(buf);
    if (!p) { HeapFree(GetProcessHeap(), 0, buf); return NULL; }
    lg("chat: %ls loaded, %u items, %u tables\r\n", file, p->nItems, p->nTabs);
    return p;
}
static int cr_check_field(BYTE *b, pdb_schema *s, const char *n, BYTE type, WORD size)
{
    int idx;
    const pdb_field *f = fs_field(s, n, &idx);
    return f && f->type == type && f->size == size && ((svc4bc_t)SVC(0x4bc))(s, n) == idx;
}
static int cr_check_count(BYTE *b, pdb_schema *s)
{
    return s->table_id == 1002 && cr_check_field(b, s, "z_zone", 4, 2) && cr_check_field(b, s, "z_count", 6, 4) &&
           cr_check_field(b, s, "z_spers", 5, 4) && 0x17 + (DWORD)s->nfields * 0x10 + s->rec_raw <= s->rec_size + 8;
}
static int cr_check_chan(BYTE *b, pdb_schema *s)
{
    static const struct { const char *n; BYTE type; WORD size; } want[] = {
        { "z_pf", 0, 1 }, { "z_lang", 2, 1 }, { "z_use", 0, 1 }, { "z_kind", 0, 1 }, { "z_idx", 1, 46 },
        { "z_npers", 5, 4 }, { "z_zone", 4, 2 }, { "z_cate0", 4, 2 }, { "z_cate1", 4, 2 }, { "z_cate2", 4, 2 },
        { "z_topic", 1, 73 }, { "z_capa", 5, 4 }, { "z_chlock", 2, 1 }, { "z_ctime", 6, 4 },
    };
    if (s->table_id != 1001) return 0;
    for (int i = 0; i < (int)(sizeof want / sizeof want[0]); i++)
        if (!cr_check_field(b, s, want[i].n, want[i].type, want[i].size)) return 0;
    return 0x17 + (DWORD)s->nfields * 0x10 + s->rec_raw <= s->rec_size + 8;
}
static const pfb_tab *cr_tab_of(const pfb_hdr *p, int field)
{
    const pfb_item *it = p ? pf_item(p, field) : NULL;
    return it ? it->tab : NULL;
}
static int cr_zone_known(DWORD code)
{
    const pfb_tab *t = cr_tab_of(g_cr_cpfb, 0);
    return pf_find(t, code) >= 0;
}

static void cr_db_done(int rooms, const char *state)
{
    lstrcpynA(rooms ? g_cr_rstate : g_cr_zstate, state, 32);
    if (g_cr_buf) { HeapFree(GetProcessHeap(), 0, g_cr_buf); g_cr_buf = NULL; }
    g_cr_db = 0;
    g_cr_job = -1;
    lg("chat: %s %s\r\n", rooms ? "room list" : "zone list", state);
    g_snap_dirty = 1;
}
/* a cancelled read can still be written into, so its buffer's never freed */
static void cr_db_abandon(BYTE *b, const char *state)
{
    int rooms = g_cr_db >= 10;
    if (g_cr_job >= 0 && (g_cr_db % 10 == 2 || g_cr_db % 10 == 4)) ((svc4dc_t)SVC(0x4dc))(g_cr_job);
    g_cr_buf = NULL;
    cr_db_done(rooms, state);
}
static void cr_db_start(BYTE *b)
{
    int rooms = g_cr_db >= 10;
    int t = rooms ? ((int (__cdecl *)(void *, void *, void *))SVC(0x4c0))(g_cr_rpib, g_cr_where, g_cr_sortby)
                  : ((int (__cdecl *)(void *, void *, void *))SVC(0x4c0))(g_cr_cpib, NULL, NULL);
    if (t == NO_SLOT) { g_cr_db = rooms ? 11 : 1; return; }
    if (t < 0) { cr_db_done(rooms, failed(t)); return; }
    g_cr_job = t;
    g_cr_db = rooms ? 12 : 2;
    g_cr_db_t0 = GetTickCount64();
}
static void cr_db_fetch(BYTE *b)
{
    int rooms = g_cr_db >= 10, r;
    if (rooms) r = ((int (__cdecl *)(int, void *, int, int, void *))SVC(0x4c8))(g_cr_set, g_cr_rpib, g_cr_roff,
                                                                               g_cr_roff + 100, g_cr_buf);
    else r = ((int (__cdecl *)(int, void *, int, int, void *))SVC(0x4c8))(g_cr_set, g_cr_cpib, 0, g_cr_count, g_cr_buf);
    if (r == NO_SLOT) { g_cr_db = rooms ? 13 : 3; return; }
    if (r < 0) { cr_db_done(rooms, failed(r)); return; }
    g_cr_job = r;
    g_cr_db = rooms ? 14 : 4;
    g_cr_db_t0 = GetTickCount64();
}
static DWORD cr_u(BYTE *b, pdb_schema *s, const char *n, pdb_rec *rec, int bytes)
{
    pdb_item *it = pdb_get(b, s, n, rec);
    if (!it || !(it->flags & 1)) return 0;
    if (bytes) return it->v.bytes.p && !IsBadReadPtr(it->v.bytes.p, 1) ? *(BYTE *)it->v.bytes.p : 0;
    return it->type == 3 || it->type == 4 ? it->v.u16 : it->v.u32;
}
static void cr_str(BYTE *b, pdb_schema *s, const char *n, pdb_rec *rec, char *out, int cap)
{
    pdb_item *it = pdb_get(b, s, n, rec);
    out[0] = 0;
    if (it && (it->flags & 1) && it->v.str && !IsBadStringPtrA(it->v.str, cap + 1)) sjis_copy(out, it->v.str, cap);
}
static void cr_parse_zones(BYTE *b)
{
    int guard = 0;
    for (int i = 0; i < g_cr_zone_n; i++) g_cr_zone[i].rooms = g_cr_zone[i].people = 0;
    for (BYTE *rec = g_cr_buf; rec && guard++ < CR_ZONES * 2; rec = *(BYTE **)rec) {
        DWORD z;
        if (!*(DWORD *)(rec + 0xc)) continue;
        z = cr_u(b, g_cr_cpib, "z_zone", (pdb_rec *)rec, 0);
        for (int i = 0; i < g_cr_zone_n; i++)
            if (g_cr_zone[i].code == z) {
                g_cr_zone[i].rooms = cr_u(b, g_cr_cpib, "z_count", (pdb_rec *)rec, 0);
                g_cr_zone[i].people = cr_u(b, g_cr_cpib, "z_spers", (pdb_rec *)rec, 0);
            }
    }
}
static void cr_parse_rooms(BYTE *b)
{
    int guard = 0;
    g_cr_rooms_n = 0;
    for (BYTE *rec = g_cr_buf; rec && g_cr_rooms_n < CR_ROOMS && guard++ < CR_ROOMS * 2; rec = *(BYTE **)rec) {
        cr_room *r = &g_cr_rooms[g_cr_rooms_n];
        pdb_rec *pr = (pdb_rec *)rec;
        char idx[64];
        BYTE pf, kind, use;
        if (!*(DWORD *)(rec + 0xc)) continue;
        memset(r, 0, sizeof *r);
        pf = (BYTE)cr_u(b, g_cr_rpib, "z_pf", pr, 1);
        use = (BYTE)cr_u(b, g_cr_rpib, "z_use", pr, 1);
        kind = (BYTE)cr_u(b, g_cr_rpib, "z_kind", pr, 1);
        cr_str(b, g_cr_rpib, "z_idx", pr, idx, 47);
        if (!pf || !use || !kind) continue;
        wsprintfA(r->irc, "%c%02X%c%c", pf, cr_u(b, g_cr_rpib, "z_lang", pr, 1), use, kind);
        lstrcpynA(r->irc + 5, idx, sizeof r->irc - 5);
        if (!cr_irc_ok(r->irc)) continue;
        cr_str(b, g_cr_rpib, "z_topic", pr, r->topic, 74);
        r->official = kind != 'U';
        r->locked = cr_u(b, g_cr_rpib, "z_chlock", pr, 1) != 0;
        r->people = cr_u(b, g_cr_rpib, "z_npers", pr, 0);
        r->capa = cr_u(b, g_cr_rpib, "z_capa", pr, 0);
        r->ctime = cr_u(b, g_cr_rpib, "z_ctime", pr, 0);
        r->cate[0] = (WORD)cr_u(b, g_cr_rpib, "z_cate0", pr, 0);
        r->cate[1] = (WORD)cr_u(b, g_cr_rpib, "z_cate1", pr, 0);
        r->cate[2] = (WORD)cr_u(b, g_cr_rpib, "z_cate2", pr, 0);
        g_cr_rooms_n++;
    }
}
static int cr_db_load(BYTE *b, int rooms)
{
    if (!g_cr_cpib) g_cr_cpib = pib_load(b, L"ch_count.pib", "ch_count", cr_check_count);
    if (!g_cr_cpfb) g_cr_cpfb = cr_pfb(b, L"ch_count.pfb");
    if (!g_cr_cpib || !cr_tab_of(g_cr_cpfb, 0)) return 0;
    if (!g_cr_zone_n) {
        const pfb_tab *t = cr_tab_of(g_cr_cpfb, 0);
        for (int i = 0; i < t->n && g_cr_zone_n < CR_ZONES; i++) g_cr_zone[g_cr_zone_n++].code = (WORD)t->e[i].code;
    }
    if (!rooms) return 1;
    if (!g_cr_rpib) g_cr_rpib = pib_load(b, L"c_chan.pib", "c_chan", cr_check_chan);
    if (!g_cr_rpfb) g_cr_rpfb = cr_pfb(b, L"c_chan.pfb");
    return g_cr_rpib != NULL;
}
static void cr_zones_request(BYTE *b)
{
    char buf[128], *p;
    if (take_request("chatzones", buf, sizeof buf) < 0) return;
    g_cr_used = 1;
    g_cr_last = g_db_last = GetTickCount64();
    p = buf;
    lstrcpynA(g_cr_ztok, req_line(&p), sizeof g_cr_ztok);
    if (notice_blocks(g_notice)) { cr_db_done(0, "failed:account notice"); return; }
    if (cr_session(b) < 0) { cr_db_done(0, "failed:no chat"); return; }
    if (!cr_db_load(b, 0)) { cr_db_done(0, "failed:no schema"); return; }
    lstrcpyA(g_cr_zstate, "searching");
    g_snap_dirty = 1;
    g_cr_db = 1;
    cr_db_start(b);
}
static void cr_rooms_request(BYTE *b)
{
    char buf[128], *p;
    int zone, off;
    if (take_request("chatrooms", buf, sizeof buf) < 0) return;
    g_cr_used = 1;
    g_cr_last = g_db_last = GetTickCount64();
    p = buf;
    lstrcpynA(g_cr_rtok, req_line(&p), sizeof g_cr_rtok);
    g_cr_rooms_n = g_cr_rtotal = 0;
    if (!parse_num(req_line(&p), 5, &zone) || !parse_num(req_line(&p), 5, &off) || off % 100 || off > 9900) {
        cr_db_done(1, "failed:bad request");
        return;
    }
    if (notice_blocks(g_notice)) { cr_db_done(1, "failed:account notice"); return; }
    if (cr_session(b) < 0) { cr_db_done(1, "failed:no chat"); return; }
    if (!cr_db_load(b, 1)) { cr_db_done(1, "failed:no schema"); return; }
    if (!cr_zone_known((DWORD)zone)) { cr_db_done(1, "failed:bad zone"); return; }
    g_cr_rzone = zone;
    g_cr_roff = off;
    /* the viewer's plain list: this zone, z_capa op 3 against 1, official rooms first then by name */
    memset(g_cr_cond, 0, sizeof g_cr_cond);
    g_cr_vzone = (DWORD)zone;
    g_cr_vcapa = 1;
    ((svc4a0_t)SVC(0x4a0))(g_cr_rpib, &g_cr_cond[0].item, "z_zone", &g_cr_vzone, 1);
    ((void (__cdecl *)(pdb_cond *, int, const char *))SVC(0x4a4))(&g_cr_cond[0], 1, NULL);
    ((svc4a0_t)SVC(0x4a0))(g_cr_rpib, &g_cr_cond[1].item, "z_capa", &g_cr_vcapa, 1);
    ((void (__cdecl *)(pdb_cond *, int, const char *))SVC(0x4a4))(&g_cr_cond[1], 3, NULL);
    if (!(g_cr_cond[0].item.flags & 1) || !(g_cr_cond[1].item.flags & 1)) { cr_db_done(1, "failed:schema"); return; }
    g_cr_where = ((svc4ac_t)SVC(0x4ac))(8, &g_cr_cond[0], &g_cr_cond[1], NULL, NULL, NULL, NULL, NULL, NULL);
    if (!g_cr_where) { cr_db_done(1, "failed:schema"); return; }
    g_cr_sortby[0].order = 1;
    g_cr_sortby[0].field = (BYTE)((svc4bc_t)SVC(0x4bc))(g_cr_rpib, "z_kind");
    g_cr_sortby[0].next = &g_cr_sortby[1];
    g_cr_sortby[1].order = 1;
    g_cr_sortby[1].field = (BYTE)((svc4bc_t)SVC(0x4bc))(g_cr_rpib, "z_topic");
    g_cr_sortby[1].next = NULL;
    lg("chat: room list for zone %d from %d\r\n", zone, off);
    lstrcpyA(g_cr_rstate, "searching");
    g_snap_dirty = 1;
    g_cr_db = 11;
    cr_db_start(b);
}
static void cr_db_pump(BYTE *b)
{
    int rooms = g_cr_db >= 10, step = g_cr_db % 10, r;
    pdb_schema *s = rooms ? g_cr_rpib : g_cr_cpib;
    if (step == 1) { if (!g_draining) cr_db_start(b); else cr_db_done(rooms, "cancelled"); return; }
    if (step == 3) { if (!g_draining) cr_db_fetch(b); else cr_db_done(rooms, "cancelled"); return; }
    if (step == 2) {
        int set = -1, count = 0;
        r = ((int (__cdecl *)(int, int *, int *))SVC(0x4c4))(g_cr_job, &set, &count);
        if (r == 0) {
            if (GetTickCount64() - g_cr_db_t0 >= CR_WAIT_MS) cr_db_abandon(b, "failed:timeout");
            return;
        }
        g_cr_job = -1;
        if (r == -0x14cc || (r > 0 && (set < 0 || count < 1))) {
            if (rooms) g_cr_rtotal = 0;
            else cr_parse_zones(b);
            cr_db_done(rooms, "done");
            return;
        }
        if (r < 0) { cr_db_done(rooms, failed(r)); return; }
        g_cr_set = set;
        g_cr_count = count;
        if (rooms) {
            g_cr_rtotal = count;
            while (g_cr_roff > count && g_cr_roff >= 100) g_cr_roff -= 100;
            if (g_cr_roff > count) g_cr_roff = 0;
        }
        if (!rooms && count > CR_ZONES) g_cr_count = count = CR_ZONES;
        g_cr_buf = (BYTE *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, (SIZE_T)s->rec_size * (count > 100 ? 100 : count) + 64);
        if (!g_cr_buf) { cr_db_done(rooms, "failed:alloc"); return; }
        g_cr_db = rooms ? 13 : 3;
        cr_db_fetch(b);
        return;
    }
    r = ((svc_i)SVC(0x4cc))(g_cr_job);
    if (r == 0) {
        if (GetTickCount64() - g_cr_db_t0 >= CR_WAIT_MS) cr_db_abandon(b, "failed:timeout");
        return;
    }
    g_cr_job = -1;
    if (r < 0) { cr_db_done(rooms, failed(r)); return; }
    if (rooms) cr_parse_rooms(b); else cr_parse_zones(b);
    cr_db_done(rooms, "done");
}

/* every hold loop turn. Requests only on control turns, and the user's own actions keep a 3 s gap */
static void chat_pump(BYTE *b, int ctl)
{
    if (g_draining) return;
    cr_room_pump(b, ctl);
    if (ctl && (g_cr_st != CR_IN || g_cr_leave) && request_waiting("chatsend")) cr_send_request(b);
    if (g_cr_db) cr_db_pump(b);
    if (!ctl || GetTickCount64() - g_cr_last < CR_GAP_MS) return;
    if (request_new("chatleave")) { cr_leave_request(b); return; }
    if (g_cr_st == CR_OUT && request_new("chatjoin")) { cr_join_request(b); return; }   /* a PART goes first */
    if (g_cr_db || !db_free()) return;
    if (request_new("chatzones")) { cr_zones_request(b); return; }
    if (request_new("chatrooms")) cr_rooms_request(b);
}

static int chat_room_open(void) { return g_cr_st != CR_OUT; }

/* Play: FFXI takes the link over, so a room gets a PART first (the user's leaving chat for the game) */
static void chat_end(BYTE *b)
{
    int s = cr_session(b);
    ULONGLONG until = GetTickCount64() + 3000;
    if (g_cr_db) cr_db_abandon(b, "cancelled");
    if (g_cr_st == CR_OUT && !g_cr_joined) return;
    if (s >= 0 && g_cr_wait != CRW_NONE) ((svc_i)SVC(0x248))(s);
    g_cr_wait = CRW_NONE;
    /* a JOIN with no answer yet still gets there, so it's PARTed like a room */
    if (cr_part(b) > 0) {
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
    ipc_file(path, "chatroom.json");
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
        wsprintfA(line, "%s{\"seq\":%lu,\"time\":%lu,\"kind\":\"%s\",\"from\":%d,\"me\":%s,\"name\":%s,\"text\":", i ? "," : "",
                  l->seq, l->time, g_cr_kind[l->kind], l->from, l->me ? "true" : "false", nm);
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
        wsprintfA(line, "%s{\"irc\":%s,\"name\":%s,\"official\":%s,\"locked\":%s,\"people\":%lu,\"capacity\":%lu,"
                        "\"categories\":[%u,%u,%u],\"created\":%lu}",
                  i ? "," : "", tk, js, r->official ? "true" : "false", r->locked ? "true" : "false", r->people, r->capa,
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
    wsprintfA(line, "]},\"send\":{\"token\":%s,\"state\":\"%s\"}}", tk, g_cr_sstate);
    wr(h, line);
}
