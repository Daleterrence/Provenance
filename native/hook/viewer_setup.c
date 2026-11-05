#define SVC(off) (*(void **)(b + 0x6fbe8 + (off)))   /* polcore's service table, byte offset off */
#define PSLOT(n) SVC((n) * 4)
typedef int (__cdecl *svc_v)(void);
typedef int (__cdecl *svc_i)(int);
typedef int (__cdecl *svc_p)(void *);
typedef int (__cdecl *svc_ip)(int, void *);
#define CANCEL_JOB(j) ((svc_i)SVC(0x520))(j)
#define NO_SLOT (-0x100)   /* polcore has no free job slot, try next turn */
#define POLCON_STEP(b) ((svc_v)((b) + 0x44a50))()
#define POLCON_IDLE(b) ((svc_v)((b) + 0x45480))()

/* each region as its app.dll sets polcore up. svc 0x17dc(1) swaps in the JP half of polcore's conversion table, and
   svc 0x690's language is what FFXI reads back through 0x694 as its area code */
static const struct pol_region {
    const char *name, *polcore, *hive, *maker, *strings;
    int selector, jp_table, lang;   /* lang: JP 0 always, -1 Settings\Language or 1, -2 it or the UI language */
} g_regions[3] = {
    { "JP", "polcore.dll",   "PlayOnline",   "Square",     "viewer\\data\\common", 0, 1, 0 },
    { "US", "polcore.dll",   "PlayOnlineUS", "SquareEnix", "viewer\\data\\common", 1, 0, -1 },
    { "EU", "polcoreEU.dll", "PlayOnlineEU", "SquareEnix", "EU\\EN",               1, 0, -2 },
};

static const struct pol_region *g_region;

/* pol.exe's index (0 JP, 1 US, 2 EU), or the loaded polcore when it isn't one */
static const struct pol_region *region_of(int idx)
{
    if (idx >= 0 && idx <= 2) return &g_regions[idx];
    return !GetModuleHandleA("polcore.dll") && GetModuleHandleA("polcoreEU.dll") ? &g_regions[2] : &g_regions[1];
}

/* pol.exe's region index (0x0045cd90), set from env.dat before it loads a polcore */
static const struct pol_region *region(void)
{
    if (!g_region) g_region = region_of(*(int *)((BYTE *)GetModuleHandleW(NULL) + 0x5cd90));
    return g_region;
}
static HMODULE polcore_module(void) { return GetModuleHandleA(region()->polcore); }
static const char *pol_hive(void) { return region()->hive; }

/* app.dll 0x102afa80's EU answer when Settings has no Language: German 4, French 3, anything else 2 */
static DWORD eu_lang(LANGID id)
{
    switch (id) {
    case 0x407: case 0x807: case 0xc07: case 0x1007: case 0x1407: return 4;
    case 0x40c: case 0x80c: case 0xc0c: case 0x100c: case 0x140c: case 0x180c: return 3;
    default: return 2;
    }
}

static DWORD viewer_lang(void)
{
    const struct pol_region *r = region();
    HKEY hk;
    char sub[96];
    DWORD fallback, lang, sz = sizeof lang;
    if (r->lang >= 0) return (DWORD)r->lang;
    fallback = r->lang == -2 ? eu_lang(GetUserDefaultLangID()) : 1;
    lang = fallback;
    wsprintfA(sub, "SOFTWARE\\%s\\%s\\PlayOnlineViewer\\Settings", r->hive, r->maker);
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, sub, 0, KEY_QUERY_VALUE | KEY_WOW64_32KEY, &hk) == ERROR_SUCCESS) {
        if (RegQueryValueExA(hk, "Language", NULL, NULL, (LPBYTE)&lang, &sz) != ERROR_SUCCESS || lang > 8)
            lang = fallback;
        RegCloseKey(hk);
    }
    return lang;
}

/* svc 0xf14 and 0x690 in app.dll's order, with JP's 0x17dc in between and its 0x17e0 after */
static void region_init(BYTE *b, DWORD lang)
{
    const struct pol_region *r = region();
    ((int (__cdecl *)(int))PSLOT(0x3C5))(r->selector);
    if (r->jp_table) ((int (__cdecl *)(int))PSLOT(0x5F7))(1);
    ((int (__cdecl *)(int))PSLOT(0x1A4))((int)lang);
    if (r->jp_table) ((int (__cdecl *)(int))PSLOT(0x5F8))((int)lang);
    lg("viewer_init: region %s, selector %d, conversion table %d\r\n", r->name, r->selector, r->jp_table);
}

/* StringTable.bin's bits, LSB first from +0x10. -1 when it runs out */
static int pex_bits(const BYTE *d, DWORD bits, DWORD *bit, int k)
{
    int v = 0, i;
    for (i = 0; i < k; i++, (*bit)++) {
        if (*bit >= bits) return -1;
        v |= ((d[0x10 + (*bit >> 3)] >> (*bit & 7)) & 1) << i;
    }
    return v;
}

/* <folder>\<sub>\StringTable.bin unpacked: "PEX\0", the plain size at +4, then LZ77 bits over a zeroed 0x1000 window
   (0 literal, 1 a 12-bit offset and a 4-bit length + 2) */
static BYTE *string_table(const char *folder, const char *sub, DWORD *len)
{
    char path[MAX_PATH + 48];
    HANDLE f;
    DWORD size, got, n, bit = 0, bits, pos = 0x1000;
    BYTE *d = NULL, *buf = NULL;
    int t, off, cnt, ok = 0;
    wsprintfA(path, "%s\\%s\\StringTable.bin", folder, sub);
    f = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return NULL;
    size = GetFileSize(f, NULL);
    if (size >= 0x10 && size < (8u << 20) && (d = HeapAlloc(GetProcessHeap(), 0, size)) != NULL &&
        ReadFile(f, d, size, &got, NULL) && got == size && !memcmp(d, "PEX", 4) &&
        (n = *(DWORD *)(d + 4)) > 0x10 && n < (16u << 20) &&
        (buf = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 0x1000 + n)) != NULL) {
        bits = (size - 0x10) * 8;
        ok = 1;
        while (ok && pos < 0x1000 + n) {
            if ((t = pex_bits(d, bits, &bit, 1)) < 0) ok = 0;
            else if (t == 0) {
                if ((t = pex_bits(d, bits, &bit, 8)) < 0) ok = 0;
                else buf[pos++] = (BYTE)t;
            } else if ((off = pex_bits(d, bits, &bit, 12)) < 0 || (cnt = pex_bits(d, bits, &bit, 4)) < 0) ok = 0;
            else
                for (cnt += 2; cnt > 0 && pos < 0x1000 + n; cnt--, pos++) buf[pos] = buf[pos - 0x1000 + off];
        }
    }
    CloseHandle(f);
    if (d) HeapFree(GetProcessHeap(), 0, d);
    if (!ok) {
        if (buf) HeapFree(GetProcessHeap(), 0, buf);
        return NULL;
    }
    *len = n;
    MoveMemory(buf, buf + 0x1000, n);
    return buf;
}

/* records from +0x10: u16 size, u16 id, NUL-ended UTF-16. Copied out, at most cap - 1 characters */
static int table_text(const BYTE *raw, DWORD n, int id, WCHAR *out, int cap, int drop_cr)
{
    DWORD o = 0x10, i;
    int k = 0;
    while (o + 4 <= n) {
        WORD size = *(const WORD *)(raw + o), sid = *(const WORD *)(raw + o + 2);
        if (size < 4 || o + size > n) return 0;
        if (sid == id) {
            for (i = o + 4; i + 1 < o + size && k < cap - 1; i += 2) {
                WCHAR c = (WCHAR)(raw[i] | raw[i + 1] << 8);
                if (!c) break;
                if (c != L'\r' || !drop_cr) out[k++] = c;
            }
            out[k] = 0;
            return k > 0;
        }
        o += size;
    }
    return 0;
}

typedef int (__cdecl *to_sjis)(char *, const WCHAR *, int);

/* message #i (0-9 titles, 10-19 texts) as app.dll (0x10057900) makes it: the table's string, CRs dropped from the
   texts, through conv (svc 0xa40). english falls back to the US table's bytes. Its length, or -1 */
static int message_bytes(const BYTE *raw, DWORD n, int i, int english, to_sjis conv, char out[0x100])
{
    static const WORD ids[20] = { 0x46c9, 0x46d0, 0x46d1, 0x46d2, 0x46d3, 0x46d4, 0x46d6, 0x46d8, 0x46dc, 0x46da,
                                  0x46e6, 0x46ea, 0x46ec, 0x4ab8, 0x3bad, 0x46d7, 0x46d9, 0x46dd, 0x46db, 0x46d5 };
    static const char *const en[20] = {
        "Let's be friends!", "Friend registration accepted", "Friend registration declined", "Deleted",
        "Please delete.", "Would you like to join a friend group?", "Group registration accepted",
        "Group registration declined", "Removed from friend group", "Friend group disbanded",
        "Would you like to be friends?", "%s accepted friend registration.\nWill be added to Friend List.",
        "%s declined friend registration.", "You don't have access to %s's away message.",
        "The name you entered is unavailable.\nPlease choose another name.",
        "%s has agreed to join\nthe group \"%s.\"", "%s has declined to join\nthe group \"%s.\"",
        "You have been removed from\nthe group \"%s.\"", "The group \"%s\"\nhas been disbanded.",
        "Would you like to join the group \"%s\"?" };
    WCHAR w[0x200];
    int cap = i < 10 ? 0x80 : 0x100;
    if (raw && table_text(raw, n, ids[i], w, 0x200, i >= 10) && conv(NULL, w, 0x81ac) < cap)
        return conv(out, w, 0x81ac);
    if (!english) return -1;
    lstrcpyA(out, en[i]);
    return lstrlenA(out);
}

/* SET_MESSAGE_TITLE/TEXT from the region's own StringTable like its viewer (EU's English one), so a friend gets the
   same bytes. Text #4 is the name dialog FFXI-3322 needs */
static void set_messages(BYTE *b, const char *folder)
{
    const struct pol_region *r = region();
    DWORD n = 0;
    BYTE *raw = string_table(folder, r->strings, &n);
    char s[0x100];
    int i, set = 0;
    for (i = 0; i < 20; i++) {
        /* JP keeps polcore's own Japanese for anything its table hasn't got */
        if (message_bytes(raw, n, i, !r->jp_table, (to_sjis)PSLOT(0x290), s) < 0) continue;
        if (i < 10) ((int (__cdecl *)(int, const char *))PSLOT(0x430))(i, s);
        else ((int (__cdecl *)(int, const char *))PSLOT(0x516))(i - 10, s);
        set++;
    }
    lg("viewer_init: %d message texts set, %s, FILE_INIT done\r\n", set,
       raw ? "from StringTable.bin" : r->jp_table ? "StringTable.bin unreadable, polcore's own kept" : "StringTable.bin unreadable, English");
    if (raw) HeapFree(GetProcessHeap(), 0, raw);
}

/* drops trailing backslashes (Locate existing install writes one) but never a drive's own */
static void trim_slash(char *s)
{
    int n = lstrlenA(s);
    while (n > 3 && s[n - 1] == '\\') s[--n] = 0;
}

/* the region's InstallFolder\1000 like the app reads it, into its own buffer so a failed read keeps the default */
static void viewer_folder(char folder[MAX_PATH])
{
    HKEY hk;
    char sub[48], got[MAX_PATH];
    DWORD sz = sizeof got;
    wsprintfA(sub, "SOFTWARE\\%s\\InstallFolder", pol_hive());
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, sub, 0, KEY_QUERY_VALUE | KEY_WOW64_32KEY, &hk) != ERROR_SUCCESS) return;
    if (RegGetValueA(hk, NULL, "1000", RRF_RT_REG_SZ, NULL, got, &sz) == ERROR_SUCCESS) {
        trim_slash(got);
        if (got[0]) lstrcpyA(folder, got);
    }
    RegCloseKey(hk);
}

#define LEGACY_HOME 84   /* shared by every account without a slot of its own */
static char g_viewer[MAX_PATH];
static int g_home = -1, g_home_made;

static int plain_path(const char *path);
/* SET_USER_SLOT, then pub\homeNN\msg\{s/a,s/b,r/a,r/b}, folders polcore can't make itself. Never a slot that goes
   through a link, FFXI writes there as admin */
static int home_set(BYTE *b, int slot)
{
    static const char *const sub[4] = { "s\\a", "s\\b", "r\\a", "r\\b" };
    char full[MAX_PATH + 48], *p;
    int i;
    for (i = 0; i < 4; i++) {
        wsprintfA(full, "%s\\pub\\home%02d\\msg\\%s", g_viewer, slot, sub[i]);
        if (!plain_path(full)) {
            lg("home: pub\\home%02d goes through a link - not used\r\n", slot);
            return 0;
        }
    }
    ((int (__cdecl *)(int))PSLOT(0x366))(slot);
    g_home = slot;
    g_home_made = 1;
    for (i = 0; i < 4; i++) {
        wsprintfA(full, "%s\\pub\\home%02d\\msg\\%s", g_viewer, slot, sub[i]);
        for (p = full; *p; p++)
            if (*p == '\\') { *p = 0; CreateDirectoryA(full, NULL); *p = '\\'; }
        CreateDirectoryA(full, NULL);
    }
    return 1;
}

/* the viewer's init before content (REGISTRY_LANG, FFXI_LANG, REGISTRY_KEY, INSTALL_FOLDER, FILE_INIT), which the menu
   detour skips, or character creation throws FFXI-3322 in Japanese. Took me a whole night... solid work on my part */
static void viewer_init(BYTE *b)
{
    DWORD lang = viewer_lang();
    char key[32];
    char folder[MAX_PATH] = "C:\\Program Files (x86)\\PlayOnline\\SquareEnix\\PlayOnlineViewer";
    wsprintfA(key, "SOFTWARE\\%s", pol_hive());
    viewer_folder(folder);
    lg("viewer_init: lang=%u key='%s' folder='%s'\r\n", lang, key, folder);
    region_init(b, lang);
    ((int (__cdecl *)(const char *))PSLOT(0x16F))(key);
    ((int (__cdecl *)(const char *))PSLOT(0x07D))(folder);
    ((int (__cdecl *)(void))PSLOT(0x3A4))();
    lstrcpynA(g_viewer, folder, sizeof g_viewer);
    /* home84 till home_pick moves the account to its own after the login, then the folders get made */
    ((int (__cdecl *)(int))PSLOT(0x366))(LEGACY_HOME);
    g_home = LEGACY_HOME;
    lg("viewer_init: user slot %d for now\r\n", LEGACY_HOME);
    set_messages(b, folder);
}
