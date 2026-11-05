#define CR_LINES 256
#define CR_RAW 128            /* the viewer's ring, a busy room's 3s all land in one pump */
#define CR_PEOPLE 128

#define CR_REPLIES 8
/* the viewer's give-ups (app.dll 0x10095141, 0x10097c61): 18000 frames for an answer, 6 min at 50 a second. A try is a
   hold loop turn polcore wouldn't take the command on, with another one out */
#define CR_WAIT_MS 360000
#define CR_TRIES_ENTER 3600   /* entering's AWAY and JOIN, and the PART, which the viewer never gives up on */
#define CR_TRIES 5            /* any other command, and the typed line */
#define CR_TRIES_FRAME 20     /* a queued frame: the hello, its replies and the master's */
#define CR_TEXT_MAX 400       /* characters, the viewer's box. cr_fits is polcore's real limit */
#define CR_NAME_MAX 15

typedef struct { char text[0x200]; char chan[0x40]; DWORD lo, hi; int whisper; } cr_raw;
typedef struct { DWORD lo, hi; int key, member, away, master, answered; WCHAR name[0x21]; } cr_person;
typedef struct { DWORD seq, time; int kind, me, from, colour, emote; WCHAR name[0x21]; WCHAR text[0x201]; } cr_line;

enum { CRK_SAY, CRK_ENTERED, CRK_LEFT, CRK_AWAY, CRK_BACK, CRK_MASTER, CRK_KICKED, CRK_WHISPER };
static const char *const g_cr_kind[] = { "say", "entered", "left", "away", "back", "master", "kicked", "whisper" };

/* in-room steps: AWAY (back), JOIN, WHO+MODE, TOPIC, then in. PART on the way out. A new room sets its TOPIC and
   MODEs after the WHO instead of asking for the TOPIC */
enum { CR_OUT, CR_AWAY, CR_JOIN, CR_WHO, CR_TOPIC, CR_IN, CR_PART, CR_SETTOPIC, CR_MODE_N, CR_MODE_K, CR_MODE_L };
/* what's in flight */
enum { CRW_NONE, CRW_CMD, CRW_MSG };
/* PRIVMSG kinds, the frame's type where there is one */
enum { CRM_SAY, CRM_HELLO = 2, CRM_REPLY = 3, CRM_KEY = 7, CRM_MASTER = 10, CRM_KICK = 11 };
/* the room master's jobs, one step at a time like everything else in the room */
enum { MO_NONE, MO_KICK, MO_MASTER, MO_KEY };
enum { MS_BAN, MS_KICKFRAME, MS_KICK, MS_OP, MS_OPFRAME, MS_UNKEY, MS_KEY, MS_KEYFRAME };

static int g_cr_used;
static void *g_cr_644;

static cr_raw g_cr_raw[CR_RAW];
static volatile LONG g_cr_raw_n;
static int g_cr_raw_lost;

static int g_cr_st = CR_OUT, g_cr_wait, g_cr_tries, g_cr_open, g_cr_leave, g_cr_hello, g_cr_replies;
static int g_cr_msg_kind = -1, g_cr_skip_topic, g_cr_reread, g_cr_kicked_me;
static int g_cr_joined;   /* a JOIN's gone and no PART since: the server might have me in the room */
static char g_cr_cancel_tok[48];
static DWORD g_cr_written_seq;
static ULONGLONG g_cr_written_at;
static ULONGLONG g_cr_sent_at;
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
static int g_cr_making;                      /* this enter's a create */
static char g_cr_new_topic[0x80], g_cr_new_limit[4];
static int g_cr_colour = -1, g_cr_emote;     /* the line being sent's */
static int g_cr_mop, g_cr_msteps[4], g_cr_mn, g_cr_mi;
static DWORD g_cr_mlo, g_cr_mhi;
static WCHAR g_cr_mname[0x21];
static char g_cr_mtok[48], g_cr_mstate[32] = "", g_cr_newkey[32];

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
/* svc 0xa40 never fails, it puts its default in for anything Shift-JIS hasn't got, so it's checked on the way back */
static int cr_sjis_exact(BYTE *b, const WCHAR *w, char *out, int cap)
{
    static WCHAR back[0x202];
    int n = cr_sjis(b, w, out, cap);
    if (n < 0) return -1;
    cr_wide(b, out, back, 0x202);
    return lstrcmpW(back, w) ? -1 : n;
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

static cr_line *cr_add_line(int kind, int me, const cr_person *p, const WCHAR *name, const WCHAR *text)
{
    cr_line *l = &g_cr_line[g_cr_line_next];
    memset(l, 0, sizeof *l);
    l->colour = -1;
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
    return l;
}

/* one character of a frame's colour, emoticon or colour-index field (app.dll 0x1009664e): 0-9 and a-n either case,
   a space is 0, 'z' is -1 and anything else 0 */
static int cr_hex(WCHAR c)
{
    if (c >= L'0' && c <= L'9') return c - L'0';
    if (c >= L'a' && c <= L'n') return c - L'a' + 10;
    if (c >= L'A' && c <= L'N') return c - L'A' + 10;
    return c == L'z' ? -1 : 0;
}
static int cr_key_ok(const char *s);

/* the name runs from just past the header (6 chars, 8 with the language) to the tab */
static int cr_tab(const WCHAR *w, int n, int from)
{
    for (int i = from; i < n; i++) if (w[i] == L'\t') return i;
    return -1;
}
/* one room line, already in UTF-16, read like app.dll 0x10099d8e does minus whispers, passwords and profile switches */
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
        {
            cr_line *l = cr_add_line(CRK_SAY, 0, p, name, text);
            int page = cr_hex(w[1]) * 16 + cr_hex(w[2]);
            l->colour = cr_hex(w[5]);
            /* the viewer only draws one when the page isn't -1 ('z') and the face is 1 or more */
            l->emote = w[1] == L'z' || w[2] == L'z' || page < 0 ? 0 : cr_hex(w[3]) * 16 + cr_hex(w[4]);
        }
        return;
    case L'7':
        /* the master's new password, which the viewer keeps for its own next password change */
        if (p && p->master && n > 8) {
            char k[32];
            int m = WideCharToMultiByte(CP_ACP, 0, w + 8, -1, k, sizeof k, NULL, NULL);
            if (m > 1 && cr_key_ok(k)) lstrcpynA(g_cr_key, k, sizeof g_cr_key);
        }
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
/* a 0: type, emoticon page and face as space-padded %2x, colour char, name, tab, language, text. A colour's char is 5
   on from its place in the viewer's list (app.dll 0x100984df), '0' for none */
static void cr_frame_say(BYTE *b, const WCHAR *text, WCHAR *w, int cap)
{
    static const WCHAR cc[] = L"0123456789abcdefghijklmn";
    int lang = SVC(0xf18) ? ((svc_v)SVC(0xf18))() : 0, n;
    wsprintfW(w, L"%x%2x%2x%c", 0, 0, g_cr_emote, g_cr_colour < 0 ? L'0' : cc[g_cr_colour + 5]);
    lstrcatW(w, g_cr_nick);
    n = lstrlenW(w);
    wsprintfW(w + n, L"\t%02x", lang & 0xff);
    n = lstrlenW(w);
    lstrcpynW(w + n, text, cap - n);
}
/* PRIVMSG #room :frame has to fit polcore's 0x200 with its checksum and the line end */
static int cr_fits(int frameBytes) { return 8 + lstrlenA(g_cr_room) + 2 + frameBytes + 2 + 8 <= 0x200; }
