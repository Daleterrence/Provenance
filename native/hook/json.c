static void json_str(char *out, int cap, const char *s)
{
    int o = 0;
    if (cap < 3) { if (cap > 0) out[0] = 0; return; }
    out[o++] = '"';
    for (int i = 0; s && s[i] && o < cap - 2; i++) {
        unsigned char c = (unsigned char)s[i];
        if (c == '"' || c == '\\') { if (o < cap - 3) { out[o++] = '\\'; out[o++] = (char)c; } }
        else if (c >= 0x20) out[o++] = (char)c;
        else if (o < cap - 3) { out[o++] = ' '; }
    }
    out[o++] = '"';
    out[o] = 0;
}
/* keeps newlines as \n where json_str makes them spaces, message text needs them */
static int json_text(char *out, int cap, const char *s)
{
    int o = 0;
    if (cap < 3) return 0;
    out[o++] = '"';
    for (int i = 0; s[i] && o < cap - 3; i++) {
        unsigned char c = (unsigned char)s[i];
        if (c == '"' || c == '\\') { out[o++] = '\\'; out[o++] = (char)c; }
        else if (c == '\n') { out[o++] = '\\'; out[o++] = 'n'; }
        else if (c == '\r') ;
        else if (c < 0x20) out[o++] = ' ';
        else out[o++] = (char)c;
    }
    out[o++] = '"';
    out[o] = 0;
    return o;
}
/* the longest prefix of w (n chars) whose converted length is at most max.
   len(k) is how long its first k come out, and only ever grows with k */
static int json_cut(WCHAR *w, int n, int max, int (*len)(WCHAR *w, int k, BYTE *b), BYTE *b)
{
    int lo = 0, hi = n;
    if (len(w, n, b) <= max) return n;
    while (lo < hi) {
        int mid = (lo + hi + 1) / 2;
        if (len(w, mid, b) <= max) lo = mid; else hi = mid - 1;
    }
    /* never between the halves of a surrogate pair */
    if (lo > 0 && lo < n && w[lo - 1] >= 0xd800 && w[lo - 1] < 0xdc00 && w[lo] >= 0xdc00 && w[lo] < 0xe000) lo--;
    return lo;
}
static int json_utf8_len(WCHAR *w, int k, BYTE *b)
{
    (void)b;
    return k > 0 ? WideCharToMultiByte(CP_UTF8, 0, w, k, NULL, 0, NULL, NULL) : 0;
}
/* POL's Shift-JIS (EU Latin extension too) to UTF-8 via svc 0xa38. Names and the 0x180 byte mail body fit the stack buffers,
   anything longer goes to the heap */
static void pol_utf8(BYTE *b, const char *src, int len, char *out, int cap)
{
    char stk[0x182], *tmp = stk;
    WCHAR wstk[0x182], *w = wstk;
    int n, m;
    out[0] = 0;
    if (!src || len <= 0 || cap < 1) return;
    if (len + 2 > (int)(sizeof stk / sizeof stk[0])) {
        tmp = (char *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, (SIZE_T)len + 2);
        w = (WCHAR *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, ((SIZE_T)len + 2) * sizeof(WCHAR));
    }
    else {
        memset(stk, 0, (size_t)len + 2);
        memset(wstk, 0, ((size_t)len + 2) * sizeof(WCHAR));
    }
    if (tmp && w) {
        memcpy(tmp, src, (size_t)len);                  /* svc 0xa38 has no bound, so the copy ends in two NULs */
        pc_sjis_to_wide(b, w, tmp, WIDE_GETA);
        w[len + 1] = 0;
        n = json_cut(w, lstrlenW(w), cap - 1, json_utf8_len, NULL);
        m = n > 0 ? WideCharToMultiByte(CP_UTF8, 0, w, n, out, cap - 1, NULL, NULL) : 0;
        out[m > 0 ? m : 0] = 0;
    }
    if (tmp && tmp != stk) HeapFree(GetProcessHeap(), 0, tmp);
    if (w && w != wstk) HeapFree(GetProcessHeap(), 0, w);
}
static int json_sjis_len(WCHAR *w, int k, BYTE *b)
{
    WCHAR keep = w[k];
    int n;
    w[k] = 0;
    n = pc_wide_to_sjis(b, NULL, w, SJIS_GETA);
    w[k] = keep;
    return n;
}
/* UTF-8 to POL's Shift-JIS through svc 0xa40, cut at whole characters to cap. Gives back how many bytes it came to, 0 when
   there's nothing or no room */
static int pol_sjis(BYTE *b, const char *src, char *out, int cap)
{
    WCHAR *w;
    int n, k, m = 0;
    out[0] = 0;
    n = MultiByteToWideChar(CP_UTF8, 0, src, -1, NULL, 0);
    if (n <= 1 || cap < 2) return 0;
    w = (WCHAR *)HeapAlloc(GetProcessHeap(), 0, (SIZE_T)n * sizeof(WCHAR));
    if (!w) return 0;
    MultiByteToWideChar(CP_UTF8, 0, src, -1, w, n);
    k = json_cut(w, n - 1, cap - 1, json_sjis_len, b);
    if (k > 0) {
        w[k] = 0;
        m = pc_wide_to_sjis(b, out, w, SJIS_GETA);
    }
    out[m > 0 ? m : 0] = 0;
    HeapFree(GetProcessHeap(), 0, w);
    return m > 0 ? m : 0;
}
static void json_sjis(BYTE *b, char *out, int cap, const BYTE *src, int srcMax)
{
    char sj[64], u[192];
    int i;
    for (i = 0; i < srcMax && i < (int)sizeof sj - 1 && src[i]; i++) sj[i] = (char)src[i];
    sj[i] = 0;
    pol_utf8(b, sj, i, u, sizeof u);
    json_str(out, cap, u);
}
static void json_sjis_copy(char *out, const char *src, int cap)
{
    int i = 0, n;
    while (src[i]) {
        BYTE c = (BYTE)src[i];
        n = ((c >= 0x81 && c <= 0x9f) || (c >= 0xe0 && c <= 0xfc)) && src[i + 1] ? 2 : 1;
        if (i + n > cap - 1) break;
        i += n;
    }
    memcpy(out, src, (size_t)i);
    out[i] = 0;
}

/* POL's Shift-JIS (with the Latin extension the EU tables use) -> UTF-8 through svc 0xa38, cut at u8cap -> JSON */
static void json_pol(BYTE *b, const char *sj, int u8cap, char *js, int jscap)
{
    static WCHAR w[0x420];
    static char u8[0x600];
    char tmp[0x400] = {0};
    u8[0] = 0;
    if (sj && sj[0]) {
        lstrcpynA(tmp, sj, sizeof tmp - 2);             /* svc 0xa38 has no bound, so the input gets capped */
        memset(w, 0, sizeof w);
        pc_sjis_to_wide(b, w, tmp, WIDE_GETA);
        w[0x41f] = 0;
        if (WideCharToMultiByte(CP_UTF8, 0, w, -1, u8, u8cap - 1, NULL, NULL) <= 0) u8[0] = 0;
        u8[u8cap - 1] = 0;
    }
    json_text(js, jscap, u8);
}

/* compact JSON writer over a growing buffer. A level never opened with jw_obj or jw_arr is a bare list of members,
   which is how pieces spliced into a bigger object are built */
#define JW_DEPTH 12
#define JW_MAX 0x4000000
typedef struct {
    char *p;
    int n, cap, depth, fail;
    BYTE seen[JW_DEPTH];     /* this level's had something written to it */
    char close[JW_DEPTH];
} jw;

/* the bytes are wiped, some of what goes through here is people's messages */
static void jw_free(jw *w)
{
    if (w->p) {
        SecureZeroMemory(w->p, w->cap);
        HeapFree(GetProcessHeap(), 0, w->p);
    }
    memset(w, 0, sizeof *w);
}
/* where the next need bytes (and the NUL) go, NULL when it's out of memory. After that nothing more gets written */
static char *jw_room(jw *w, int need)
{
    if (w->fail) return NULL;
    if (w->n + need + 1 > w->cap) {
        int cap = w->cap ? w->cap : 0x1000;
        char *q;
        while (cap < w->n + need + 1 && cap <= JW_MAX) cap *= 2;
        q = cap <= JW_MAX ? (char *)HeapAlloc(GetProcessHeap(), 0, cap) : NULL;
        if (!q) { w->fail = 1; return NULL; }
        if (w->p) {
            memcpy(q, w->p, (size_t)w->n);
            SecureZeroMemory(w->p, w->cap);
            HeapFree(GetProcessHeap(), 0, w->p);
        }
        w->p = q;
        w->cap = cap;
    }
    return w->p + w->n;
}
static void jw_put(jw *w, const char *s, int n)
{
    char *o = jw_room(w, n);
    if (!o) return;
    memcpy(o, s, (size_t)n);
    w->n += n;
    w->p[w->n] = 0;
}
static void jw_key(jw *w, const char *key)
{
    if (w->seen[w->depth]) jw_put(w, ",", 1);
    w->seen[w->depth] = 1;
    if (key) {
        jw_put(w, "\"", 1);
        jw_put(w, key, lstrlenA(key));
        jw_put(w, "\":", 2);
    }
}
static void jw_open(jw *w, const char *key, char open, char close)
{
    jw_key(w, key);
    jw_put(w, &open, 1);
    if (w->depth + 1 >= JW_DEPTH) { w->fail = 1; return; }
    w->depth++;
    w->seen[w->depth] = 0;
    w->close[w->depth] = close;
}
static void jw_obj(jw *w, const char *key) { jw_open(w, key, '{', '}'); }
static void jw_arr(jw *w, const char *key) { jw_open(w, key, '[', ']'); }
static void jw_end(jw *w)
{
    if (w->depth <= 0) return;
    jw_put(w, &w->close[w->depth], 1);
    w->depth--;
}
static void jw_int(jw *w, const char *key, int v)
{
    char t[16];
    jw_key(w, key);
    jw_put(w, t, wsprintfA(t, "%d", v));
}
static void jw_uint(jw *w, const char *key, DWORD v)
{
    char t[16];
    jw_key(w, key);
    jw_put(w, t, wsprintfA(t, "%u", v));
}
static void jw_bool(jw *w, const char *key, int v)
{
    jw_key(w, key);
    jw_put(w, v ? "true" : "false", v ? 4 : 5);
}
static void jw_lit(jw *w, const char *key, const char *s)
{
    jw_key(w, key);
    jw_put(w, "\"", 1);
    jw_put(w, s, lstrlenA(s));
    jw_put(w, "\"", 1);
}
static void jw_raw(jw *w, const char *key, const char *s)
{
    jw_key(w, key);
    jw_put(w, s, lstrlenA(s));
}
/* members or elements somebody else built, no-op when empty. The comma's mine */
static void jw_splice(jw *w, const char *s, int n)
{
    if (n <= 0) return;
    jw_key(w, NULL);
    jw_put(w, s, n);
}
/* cap is the most the string form can take up, 0 for all of it. Keys are never escaped */
static void jw_str(jw *w, const char *key, const char *s, int cap)
{
    char *o;
    if (cap <= 0) cap = 2 * (s ? lstrlenA(s) : 0) + 3;
    jw_key(w, key);
    if (!(o = jw_room(w, cap))) return;
    json_str(o, cap, s);
    w->n += lstrlenA(o);
}
static void jw_text(jw *w, const char *key, const char *s)
{
    int cap = 2 * lstrlenA(s) + 3;
    char *o;
    jw_key(w, key);
    if (!(o = jw_room(w, cap))) return;
    w->n += json_text(o, cap, s);
}
static void jw_sjis(jw *w, const char *key, BYTE *b, const BYTE *src, int srcMax, int cap)
{
    char *o;
    jw_key(w, key);
    if (!(o = jw_room(w, cap))) return;
    json_sjis(b, o, cap, src, srcMax);
    w->n += lstrlenA(o);
}
static void jw_pol(jw *w, const char *key, BYTE *b, const char *sj, int u8cap, int cap)
{
    char *o;
    jw_key(w, key);
    if (!(o = jw_room(w, cap))) return;
    json_pol(b, sj, u8cap, o, cap);
    w->n += lstrlenA(o);
}
/* copies the finished build into a fixed buffer. One that failed or doesn't fit leaves it empty and logs it,
   so a cut-off member can't get spliced into the snapshot */
static int jw_fit(const jw *w, char *dst, int cap, const char *what)
{
    dst[0] = 0;
    if (w->fail || w->n >= cap) {
        base_log("json: %s is %d bytes and has room for %d, left out\r\n", what, w->n, cap - 1);
        return 0;
    }
    if (w->n) memcpy(dst, w->p, (size_t)w->n + 1);
    return 1;
}
/* {"token":...,"state":"..."} left open for whatever else the job has to say */
static void jw_job(jw *w, const char *key, const char *token, const char *state)
{
    jw_obj(w, key);
    jw_str(w, "token", token, 100);
    jw_lit(w, "state", state);
}
