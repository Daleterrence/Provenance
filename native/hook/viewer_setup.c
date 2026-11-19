static const struct pol_region {
    const char *name, *polcore, *hive, *maker, *strings;
    int selector, jp_table, lang;   /* lang: JP 0 always, -1 Settings\Language or 1, -2 it or the UI language */
} g_regions[3] = {
    { POL_REGION_JP, "polcore.dll",   POL_HIVE_JP, "Square",     "viewer\\data\\common", 0, 1, 0 },
    { POL_REGION_US, "polcore.dll",   POL_HIVE_US, "SquareEnix", "viewer\\data\\common", 1, 0, -1 },
    { POL_REGION_EU, "polcoreEU.dll", POL_HIVE_EU, "SquareEnix", "EU\\EN",               1, 0, -2 },
};

static const struct pol_region *g_region;

static const struct pol_region *viewer_region_of(int idx)
{
    if (idx >= 0 && idx <= 2) return &g_regions[idx];
    return !GetModuleHandleA("polcore.dll") && GetModuleHandleA("polcoreEU.dll") ? &g_regions[2] : &g_regions[1];
}

static const struct pol_region *viewer_region(void)
{
    if (!g_region) g_region = viewer_region_of(*(int *)pc_addr_of((BYTE *)GetModuleHandleW(NULL), AT_REGION_INDEX));
    return g_region;
}
static HMODULE viewer_polcore_module(void) { return GetModuleHandleA(viewer_region()->polcore); }
static const char *viewer_pol_hive(void) { return viewer_region()->hive; }

/* app.dll 0x102afa80's EU answer when Settings has no Language: German 4, French 3, anything else 2 */
static DWORD viewer_eu_lang(LANGID id)
{
    switch (id) {
    case 0x407: case 0x807: case 0xc07: case 0x1007: case 0x1407: return 4;
    case 0x40c: case 0x80c: case 0xc0c: case 0x100c: case 0x140c: case 0x180c: return 3;
    default: return 2;
    }
}

static DWORD viewer_lang(void)
{
    const struct pol_region *r = viewer_region();
    HKEY hk;
    char sub[96];
    DWORD fallback, lang, sz = sizeof lang;
    if (r->lang >= 0) return (DWORD)r->lang;
    fallback = r->lang == -2 ? viewer_eu_lang(GetUserDefaultLangID()) : 1;
    lang = fallback;
    wsprintfA(sub, "SOFTWARE\\%s\\%s\\PlayOnlineViewer\\Settings", r->hive, r->maker);
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, sub, 0, KEY_QUERY_VALUE | KEY_WOW64_32KEY, &hk) == ERROR_SUCCESS) {
        if (RegQueryValueExA(hk, "Language", NULL, NULL, (LPBYTE)&lang, &sz) != ERROR_SUCCESS || lang > 8)
            lang = fallback;
        RegCloseKey(hk);
    }
    return lang;
}

static void viewer_region_init(BYTE *b, DWORD lang)
{
    const struct pol_region *r = viewer_region();
    pc_set_region(b, r->selector);
    if (r->jp_table) pc_set_jp_table(b, 1);
    pc_set_language(b, (int)lang);
    if (r->jp_table) pc_set_jp_language(b, (int)lang);
    base_log("viewer_init: region %s, selector %d, conversion table %d\r\n", r->name, r->selector, r->jp_table);
}

static int viewer_pex_bits(const BYTE *d, DWORD bits, DWORD *bit, int k)
{
    int v = 0, i;
    for (i = 0; i < k; i++, (*bit)++) {
        if (*bit >= bits) return -1;
        v |= ((d[0x10 + (*bit >> 3)] >> (*bit & 7)) & 1) << i;
    }
    return v;
}

static BYTE *viewer_string_table(const char *folder, const char *sub, DWORD *len)
{
    char path[MAX_PATH + 48];
    HANDLE f;
    DWORD size, got, n = 0, bit = 0, bits, pos = 0x1000;
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
            if ((t = viewer_pex_bits(d, bits, &bit, 1)) < 0) ok = 0;
            else if (t == 0) {
                if ((t = viewer_pex_bits(d, bits, &bit, 8)) < 0) ok = 0;
                else buf[pos++] = (BYTE)t;
            } else if ((off = viewer_pex_bits(d, bits, &bit, 12)) < 0 ||
                (cnt = viewer_pex_bits(d, bits, &bit, 4)) < 0) ok = 0;
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

/* records from +0x10: u16 size, u16 id, NUL-ended UTF-16 */
static int viewer_table_text(const BYTE *raw, DWORD n, int id, WCHAR *out, int cap, int drop_cr)
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

static int viewer_message_bytes(BYTE *b, const BYTE *raw, DWORD n, int i, int english, char out[0x100])
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
    if (raw && viewer_table_text(raw, n, ids[i], w, 0x200, i >= 10) && pc_wide_to_sjis(b, NULL, w, SJIS_GETA) < cap)
        return pc_wide_to_sjis(b, out, w, SJIS_GETA);
    if (!english) return -1;
    lstrcpyA(out, en[i]);
    return lstrlenA(out);
}

/* text #4 is the name dialog FFXI-3322 needs */
static void viewer_set_messages(BYTE *b, const char *folder)
{
    const struct pol_region *r = viewer_region();
    DWORD n = 0;
    BYTE *raw = viewer_string_table(folder, r->strings, &n);
    char s[0x100];
    int i, set = 0;
    for (i = 0; i < 20; i++) {
        if (viewer_message_bytes(b, raw, n, i, !r->jp_table, s) < 0) continue;
        if (i < 10) pc_set_message_title(b, i, s);
        else pc_set_message_text(b, i - 10, s);
        set++;
    }
    base_log("viewer_init: %d message texts set, %s, FILE_INIT done\r\n", set,
       raw ? "from StringTable.bin" : r->jp_table ? "StringTable.bin unreadable, polcore's own kept" : "StringTable.bin unreadable, English");
    if (raw) HeapFree(GetProcessHeap(), 0, raw);
}

static void viewer_trim_slash(char *s)
{
    int n = lstrlenA(s);
    while (n > 3 && s[n - 1] == '\\') s[--n] = 0;
}

static void viewer_folder(char folder[MAX_PATH])
{
    HKEY hk;
    char sub[48], got[MAX_PATH];
    DWORD sz = sizeof got;
    wsprintfA(sub, "SOFTWARE\\%s\\InstallFolder", viewer_pol_hive());
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, sub, 0, KEY_QUERY_VALUE | KEY_WOW64_32KEY, &hk) != ERROR_SUCCESS) return;
    if (RegGetValueA(hk, NULL, "1000", RRF_RT_REG_SZ, NULL, got, &sz) == ERROR_SUCCESS) {
        viewer_trim_slash(got);
        if (got[0]) lstrcpyA(folder, got);
    }
    RegCloseKey(hk);
}

#define LEGACY_HOME 84
static char g_viewer[MAX_PATH];
static int g_home = -1, g_home_made;

static int message_store_plain_path(const char *path);
static int viewer_home_set(BYTE *b, int slot)
{
    static const char *const sub[4] = { "s\\a", "s\\b", "r\\a", "r\\b" };
    char full[MAX_PATH + 48], *p;
    int i;
    for (i = 0; i < 4; i++) {
        wsprintfA(full, "%s\\pub\\home%02d\\msg\\%s", g_viewer, slot, sub[i]);
        if (!message_store_plain_path(full)) {
            base_log("home: pub\\home%02d goes through a link - not used\r\n", slot);
            return 0;
        }
    }
    pc_set_user_slot(b, slot);
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

/* the menu detour skips the viewer's init before content, or character creation throws FFXI-3322 in Japanese */
static void viewer_init(BYTE *b)
{
    DWORD lang = viewer_lang();
    char key[32];
    char folder[MAX_PATH] = "C:\\Program Files (x86)\\PlayOnline\\SquareEnix\\PlayOnlineViewer";
    wsprintfA(key, "SOFTWARE\\%s", viewer_pol_hive());
    viewer_folder(folder);
    base_log(LOG_VIEWER_LANG "%u key='%s' folder='%s'\r\n", lang, key, folder);
    viewer_region_init(b, lang);
    pc_set_registry_key(b, key);
    pc_set_install_folder(b, folder);
    pc_file_init(b);
    lstrcpynA(g_viewer, folder, sizeof g_viewer);
    pc_set_user_slot(b, LEGACY_HOME);
    g_home = LEGACY_HOME;
    base_log("viewer_init: user slot %d for now\r\n", LEGACY_HOME);
    viewer_set_messages(b, folder);
}
