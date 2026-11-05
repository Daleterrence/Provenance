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
static void sjis_json(char *out, int cap, const BYTE *src, int srcMax)
{
    WCHAR w[64];
    char u[192], tmp[64];
    int i, n;
    for (i = 0; i < srcMax && i < (int)sizeof tmp - 1 && src[i]; i++) tmp[i] = (char)src[i];
    tmp[i] = 0;
    n = MultiByteToWideChar(932, 0, tmp, -1, w, 64);
    if (n <= 0) { out[0] = '"'; out[1] = '"'; out[2] = 0; return; }
    WideCharToMultiByte(CP_UTF8, 0, w, -1, u, sizeof u - 1, NULL, NULL);
    json_str(out, cap, u);
}
static void sjis_to_utf8(const char *src, int srcLen, char *out, int cap)
{
    out[0] = 0;
    if (srcLen <= 0) return;
    WCHAR *w = (WCHAR *)HeapAlloc(GetProcessHeap(), 0, (srcLen + 1) * sizeof(WCHAR));
    if (!w) return;
    int n = MultiByteToWideChar(932, 0, src, srcLen, w, srcLen);
    if (n > 0) {
        int u = WideCharToMultiByte(CP_UTF8, 0, w, n, out, cap - 1, NULL, NULL);
        out[u > 0 ? u : 0] = 0;
    }
    HeapFree(GetProcessHeap(), 0, w);
}
static int utf8_to_sjis(const char *src, char *out, int cap)
{
    int n = MultiByteToWideChar(CP_UTF8, 0, src, -1, NULL, 0), k, m = 0;
    out[0] = 0;
    if (n <= 1) return 0;
    WCHAR *w = (WCHAR *)HeapAlloc(GetProcessHeap(), 0, n * sizeof(WCHAR));
    if (!w) return 0;
    MultiByteToWideChar(CP_UTF8, 0, src, -1, w, n);
    k = n - 1;
    if (k > cap) k = cap;
    while (k > 0 && WideCharToMultiByte(932, 0, w, k, NULL, 0, NULL, NULL) > cap - 1) k--;
    if (k > 0) m = WideCharToMultiByte(932, 0, w, k, out, cap - 1, NULL, NULL);
    out[m > 0 ? m : 0] = 0;
    HeapFree(GetProcessHeap(), 0, w);
    return m > 0 ? m : 0;
}

/* cut at whole SJIS characters, never between a lead byte and its trail */
static void sjis_copy(char *out, const char *src, int cap)
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
static void pol_json(BYTE *b, const char *sj, int u8cap, char *js, int jscap)
{
    static WCHAR w[0x420];
    static char u8[0x600];
    char tmp[0x400];
    u8[0] = 0;
    if (sj && sj[0]) {
        lstrcpynA(tmp, sj, sizeof tmp);                 /* svc 0xa38 has no bound, so the input gets capped */
        memset(w, 0, sizeof w);
        ((int (__cdecl *)(WCHAR *, const char *, int))SVC(0xa38))(w, tmp, 0x3013);
        w[0x41f] = 0;
        if (WideCharToMultiByte(CP_UTF8, 0, w, -1, u8, u8cap - 1, NULL, NULL) <= 0) u8[0] = 0;
        u8[u8cap - 1] = 0;
    }
    json_text(js, jscap, u8);
}

static void wr(HANDLE h, const char *s)
{
    DWORD n;
    WriteFile(h, s, lstrlenA(s), &n, NULL);
}
static void wr_job(HANDLE h, const char *fmt, const char *token, const char *state)
{
    char tk[100], line[400];
    json_str(tk, sizeof tk, token);
    wsprintfA(line, fmt, tk, state);
    wr(h, line);
}
