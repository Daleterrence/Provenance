/* addresses.c - where each of polcore.h's addresses is in 1.18.15e, the bytes of some code that uses it, and the
   resolver that finds them all again before anything's read or patched */
#define IN_EXE 0
#define IN_POLCORE 1
#define IS_DATA 0
#define IS_CODE 1          /* in .text: patched, called or compared */
#define SKIPPABLE 0        /* the hook can do without it, and skips what needs it */
#define REQUIRED 1
#define ADDR_MATCH (-1)    /* the address is in the match, as far in as 1.18.15e has it */
#define ADDR(name, module, code, need, usual, sig_at, operand, sig) \
    { name, sig, usual, sig_at, operand, module, code, need, usual }
#define ADDR_LAUNCHER(name, module, code, need, usual, sig_at, operand, sig, shows_op, shows, calls_op, calls) \
    { name, sig, usual, sig_at, operand, module, code, need, usual, shows_op, shows, calls_op, calls }

/* the list stamps' setter, which picks the server's or mine by its argument */
#define SIG_STAMPS "8b 44 24 04 85 c0 b8 ?? ?? ?? ?? 75 05 b8 ?? ?? ?? ?? 8b 4c 24 08 85 c9 74 02 89 08"
/* CreateDialogParamW(hinst, 0x67, NULL, splash proc, 0) */
#define SIG_SPLASH "6a 00 68 ?? ?? ?? ?? 6a 00 6a 67 52 ff 15 ?? ?? ?? ??"

static struct hook_addr g_addr[ADDRESS_COUNT] = {
    [AT_MENU_SESSION] = ADDR("menu session", IN_EXE, IS_CODE, REQUIRED, 0x13710, 0x13710, ADDR_MATCH,
        "6a ff 68 ?? ?? ?? ?? 64 a1 00 00 00 00 50 b8 88 10 00 00 e8 ?? ?? ?? ?? a1 ?? ?? ?? ?? 33 c4"),
    /* the other content launcher is the same code bar its addresses, so this starts at the GUID the right one asks
       FFXiEntry for and checks the bytes that address points at. The call at the end has to be the do-nothing trace
       function the hook takes over */
    [AT_GAME_RETURN] = ADDR_LAUNCHER("game return", IN_EXE, IS_CODE, REQUIRED, 0x1320a, 0x13188, ADDR_MATCH,
        "51 68 ?? ?? ?? ?? 6a 17 6a 00 50 c7 44 24 ?? 00 00 00 00 ff 15 ?? ?? ?? ?? 85 c0 7d 06 50 e8 ?? ?? ?? ?? "
        "68 ?? ?? ?? ?? e8 ?? ?? ?? ?? a1 ?? ?? ?? ?? c1 e0 04 05 ?? ?? ?? ?? 8b 10 8b 48 04 89 54 24 ?? 8b 50 08 "
        "8b 40 0c 89 4c 24 ?? 8b 4c 24 ?? 89 44 24 ?? 8b 06 83 c4 04 89 4c 24 ?? 8b 4c 24 ?? 8d 74 24 ?? 56 c7 44 24 "
        "?? 1c 00 00 00 c7 44 24 ?? 00 02 00 00 89 54 24 ?? 8b 11 50 8b 42 0c 51 ff d0 68 ?? ?? ?? ?? 8b f0 e8 ?? ?? ?? ??",
        2, "0c 79 9d 98 36 62 d4 11 80 e9 00 10 5a 81 e8 90",
        140, "51 a1 ?? ?? ?? ?? 33 c4 89 04 24 8b 0c 24 33 cc e8 ?? ?? ?? ?? 59 c3"),
    [AT_CREATE_MUTEX_SLOT] = ADDR("CreateMutexW slot", IN_EXE, IS_DATA, SKIPPABLE, 0x5c7c4, 0x75a6, 8,
        "6a 01 6a 00 8b f1 ff 15 ?? ?? ?? ?? 85 c0"),
    [AT_OPEN_MUTEX_SLOT] = ADDR("OpenMutexW slot", IN_EXE, IS_DATA, SKIPPABLE, 0x5c834, 0x146b2, 9,
        "6a 01 68 01 00 1f 00 ff 15 ?? ?? ?? ?? 85 c0"),
    [AT_CREATE_DIALOG_SLOT] = ADDR("CreateDialogParamW slot", IN_EXE, IS_DATA, SKIPPABLE, 0x5c864, 0x69e2, 14,
        SIG_SPLASH),
    [AT_SPLASH_PROC] = ADDR("splash proc", IN_EXE, IS_CODE, SKIPPABLE, 0x6800, 0x69e2, 3, SIG_SPLASH),
    [AT_SPLASH_CLOSE] = ADDR("splash close", IN_EXE, IS_CODE, REQUIRED, 0x6a10, 0x6a10, ADDR_MATCH,
        "83 ec 08 83 3d ?? ?? ?? ?? 00 74 64 6a 05 e8 ?? ?? ?? ?? 2b 05 ?? ?? ?? ?? 1b 15 ?? ?? ?? ??"),
    [AT_REGION_INDEX] = ADDR("region index", IN_EXE, IS_DATA, REQUIRED, 0x5cd90, 0x697c, 2,
        "8b 0d ?? ?? ?? ?? 8b c1 83 e8 00 74 3b 83 e8 01 74 2c"),

    /* the COM getter that hands the service table over */
    [AT_SERVICES] = ADDR("service table", IN_POLCORE, IS_DATA, REQUIRED, 0x6fbe8, 0x690e, 6,
        "8b 44 24 08 c7 00 ?? ?? ?? ?? 33 c0 c2 08 00"),
    [AT_SHA1_INIT] = ADDR("SHA-1 init", IN_POLCORE, IS_CODE, REQUIRED, 0x2d6a0, 0x2d6a0, ADDR_MATCH,
        "8b 44 24 04 33 c9 c7 00 01 23 45 67 c7 40 04 89 ab cd ef"),
    [AT_SHA1_UPDATE] = ADDR("SHA-1 update", IN_POLCORE, IS_CODE, REQUIRED, 0x2d6d0, 0x2d6d0, ADDR_MATCH,
        "8b 54 24 0c 53 56 8b 74 24 0c 57 8b 4e 14 8b c1 c1 e8 03 8d 3c d1"),
    [AT_SHA1_FINAL] = ADDR("SHA-1 final", IN_POLCORE, IS_CODE, REQUIRED, 0x2dac0, 0x2dac0, ADDR_MATCH,
        "53 55 8b 6c 24 10 56 57 55 e8 ?? ?? ?? ?? 8b 7c 24 18 83 c4 04"),
    [AT_SET_SEA_SECRET] = ADDR("SEA secret setter", IN_POLCORE, IS_CODE, REQUIRED, 0x1e4b0, 0x1e4b0, ADDR_MATCH,
        "e8 ?? ?? ?? ?? 8b 4c 24 04 33 c0 3b c8 7c 0b 83 f9 02 7f 06"),
    [AT_SESSION_INIT] = ADDR("session init", IN_POLCORE, IS_CODE, REQUIRED, 0x44390, 0x44390, ADDR_MATCH,
        "56 57 33 f6 a1 ?? ?? ?? ?? 3b c6 75 11 33 ff 57 e8 ?? ?? ?? ??"),
    [AT_INET_INIT] = ADDR("inet init", IN_POLCORE, IS_CODE, REQUIRED, 0xfe20, 0xfe20, ADDR_MATCH,
        "81 ec 90 01 00 00 a1 ?? ?? ?? ?? 85 c0 75 72 6a 00 e8 ?? ?? ?? ??"),
    [AT_CON_OPEN] = ADDR("POLCON_OPEN", IN_POLCORE, IS_CODE, REQUIRED, 0x448a0, 0x448a0, ADDR_MATCH,
        "83 ec 14 53 33 db 57 53 e8 ?? ?? ?? ?? a1 ?? ?? ?? ?? 83 c4 04 83 f8 01"),
    [AT_CON_STEP] = ADDR("POLCON_STEP", IN_POLCORE, IS_CODE, REQUIRED, 0x44a50, 0x44a50, ADDR_MATCH,
        "81 ec 78 01 00 00 53 55 56 57 33 ff 57 e8 ?? ?? ?? ?? 8b 0d ?? ?? ?? ??"),
    [AT_CON_IDLE] = ADDR("POLCON_IDLE", IN_POLCORE, IS_CODE, REQUIRED, 0x45480, 0x45480, ADDR_MATCH,
        "51 57 33 ff a1 ?? ?? ?? ?? 3b c7 75 08 b8 01 00 00 00 5f 59 c3"),
    /* its decoding twin straight after is the same bytes bar the call, so this starts in the function before it */
    [AT_ENCODE_POLID] = ADDR("ENCODE_POLID", IN_POLCORE, IS_CODE, REQUIRED, 0x46e70, 0x46e50, ADDR_MATCH,
        "56 8b 74 24 08 57 b9 10 00 00 00 bf ?? ?? ?? ?? f3 a5 66 a5 5f 5e c3 90 90 90 90 90 90 90 90 90 "
        "8b 44 24 08 8b 4c 24 04 6a 08 50 51 e8 ?? ?? ?? ?? 83 c4 0c c3"),
    [AT_ENCODED_POLID] = ADDR("encoded POL ID", IN_POLCORE, IS_DATA, REQUIRED, 0x99280, 0x46d98, 9,
        "8b 74 24 14 56 52 8b 06 a3 ?? ?? ?? ?? 8b 4e 04"),
    [AT_AUTH_STATE] = ADDR("auth state", IN_POLCORE, IS_DATA, REQUIRED, 0x99408, 0x44bdf, 2,
        "c7 05 ?? ?? ?? ?? 14 00 00 00 8d 44 24 44"),
    [AT_AUTH_REASON] = ADDR("auth reason", IN_POLCORE, IS_DATA, REQUIRED, 0x9940c, 0x451ff, 2,
        "c7 05 ?? ?? ?? ?? f4 fb ff ff 57 8b f1 e8 ?? ?? ?? ??"),
    [AT_AUTH_RESULT] = ADDR("auth result", IN_POLCORE, IS_DATA, REQUIRED, 0x996b4, 0x44c1e, 3,
        "3b c7 a3 ?? ?? ?? ?? 7c 15 b9 15 00 00 00"),
    [AT_AUTH_ERROR] = ADDR("auth error", IN_POLCORE, IS_DATA, REQUIRED, 0x9aa14, 0x45865, 14,
        "83 c4 08 c7 05 ?? ?? ?? ?? ff ff ff ff a3 ?? ?? ?? ??"),
    [AT_LOGIN_FLAGS] = ADDR("login flags", IN_POLCORE, IS_DATA, REQUIRED, 0x993f4, 0x44e3e, 7,
        "51 e8 ?? ?? ?? ?? a0 ?? ?? ?? ?? 83 c4 10 84 c3 74 16"),
    [AT_LINK_INDEX] = ADDR("link index", IN_POLCORE, IS_DATA, REQUIRED, 0x99250, 0x45861, 9,
        "8b 44 24 0c 83 c4 08 c7 05 ?? ?? ?? ?? ff ff ff ff"),
    [AT_SESSION_READY] = ADDR("session ready", IN_POLCORE, IS_DATA, REQUIRED, 0x99254, 0x45902, 6,
        "85 c0 74 0a c7 05 ?? ?? ?? ?? 00 00 00 00 6a 00"),
    [AT_SESSION_ALIVE] = ADDR("session alive", IN_POLCORE, IS_DATA, REQUIRED, 0x9924c, 0x4539d, 6,
        "be 01 00 00 00 a1 ?? ?? ?? ?? 3b c6 75 29"),
    [AT_LINKS] = ADDR("connection records", IN_POLCORE, IS_DATA, REQUIRED, 0x3e58a0, 0x147e6, 2,
        "81 c6 ?? ?? ?? ?? 56 e8 ?? ?? ?? ?? 8b be 18 02 00 00"),
    [AT_IGNORE_ROWS] = ADDR("ignore rows", IN_POLCORE, IS_DATA, REQUIRED, 0xafc18, 0x239e8, 8,
        "8d 3c 78 c1 e7 04 81 c7 ?? ?? ?? ?? 0b ca"),
    [AT_FRIENDS_A] = ADDR("friend rows", IN_POLCORE, IS_DATA, REQUIRED, 0xb40d8, 0x23dc1, 8,
        "8d 0c 47 c1 e1 04 81 c1 ?? ?? ?? ?? 51 56"),
    [AT_FRIENDS_B] = ADDR("friend extras", IN_POLCORE, IS_DATA, REQUIRED, 0x3fc920, 0x23ec3, 8,
        "89 46 08 89 56 0c 8b 97 ?? ?? ?? ?? 8d 87 ?? ?? ?? ??"),
    [AT_SERVER_STAMPS] = ADDR("server stamps", IN_POLCORE, IS_DATA, REQUIRED, 0x403040, 0x234f0, 7, SIG_STAMPS),
    [AT_LOCAL_STAMPS] = ADDR("local stamps", IN_POLCORE, IS_DATA, REQUIRED, 0x40304c, 0x234f0, 14, SIG_STAMPS),
    [AT_CONTENT_IDS] = ADDR("Content ID records", IN_POLCORE, IS_DATA, REQUIRED, 0x403080, 0x224a0, 3,
        "8d 34 cd ?? ?? ?? ?? 66 89 56 02 8b 47 04"),
    [AT_HANDLES] = ADDR("handle records", IN_POLCORE, IS_DATA, REQUIRED, 0x405800, 0x1c93b, 3,
        "8b 04 c5 ?? ?? ?? ?? 83 e0 01 0b c1 75 0c"),
    [AT_STATUS_ANSWERS] = ADDR("status answers", IN_POLCORE, IS_DATA, REQUIRED, 0x404df8, 0x29bba, 10,
        "57 8d 0c 40 8d 14 4e 8b 3c d5 ?? ?? ?? ??"),
    [AT_GCHAT_LINE_HELD] = ADDR("group chat line", IN_POLCORE, IS_DATA, REQUIRED, 0xbdc30, 0x2b265, 10,
        "8d 44 24 18 8d 54 09 08 52 68 ?? ?? ?? ??"),
};

/* a module as the resolver sees it: its image, what the addresses in its code are relative to (its base at run time,
   the file's ImageBase for a copy that isn't relocated), its .text as offsets and its section table */
typedef struct {
    BYTE *image;
    DWORD opbase, text, text_end, size;
    IMAGE_SECTION_HEADER *sections;
    WORD section_count;
} addr_module;

/* 0 when the headers don't make sense or there's no .text to look in */
static int addr_module_open(addr_module *m, BYTE *image, DWORD opbase)
{
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)image;
    IMAGE_NT_HEADERS32 *nt;
    IMAGE_SECTION_HEADER *s;
    DWORD table_end;
    WORD i;
    m->image = image;
    m->opbase = opbase;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 || dos->e_lfanew > 0x400) return 0;
    nt = (IMAGE_NT_HEADERS32 *)(image + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC ||
        nt->FileHeader.SizeOfOptionalHeader < sizeof nt->OptionalHeader || nt->FileHeader.NumberOfSections > 32) return 0;
    m->size = nt->OptionalHeader.SizeOfImage;
    s = IMAGE_FIRST_SECTION(nt);
    table_end = (DWORD)((BYTE *)(s + nt->FileHeader.NumberOfSections) - image);
    if (table_end > nt->OptionalHeader.SizeOfHeaders || nt->OptionalHeader.SizeOfHeaders > m->size) return 0;
    m->sections = s;
    m->section_count = nt->FileHeader.NumberOfSections;
    for (i = 0; i < m->section_count; i++, s++)
        if (!memcmp(s->Name, ".text", 6) && (s->Characteristics & IMAGE_SCN_MEM_EXECUTE)) {
            m->text = s->VirtualAddress;
            m->text_end = s->VirtualAddress + s->Misc.VirtualSize;
            return m->text >= 0x1000 && m->text_end > m->text && m->text_end <= m->size;
        }
    return 0;
}

static DWORD addr_sig_len(const char *sig) { return (DWORD)(lstrlenA(sig) + 1) / 3; }
static BYTE addr_hex(char c) { return (BYTE)(c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10); }
/* sig is two hex digits a byte, one space between, ?? for anything */
static int addr_sig_at(const BYTE *p, const char *sig)
{
    for (; *sig; sig += sig[2] ? 3 : 2, p++)
        if (*sig != '?' && *p != (BYTE)(addr_hex(sig[0]) << 4 | addr_hex(sig[1]))) return 0;
    return 1;
}
static int addr_sig_fits(const addr_module *m, DWORD off, const char *sig)
{
    DWORD n = addr_sig_len(sig);
    return off >= m->text && off <= m->text_end && m->text_end - off >= n && addr_sig_at(m->image + off, sig);
}

/* a writable, non-executable section with at least 4 bytes of it from a (packed polcore's .text and POL1 are
   writable too, but they're executable) */
static int addr_in_data(const addr_module *m, DWORD a)
{
    const IMAGE_SECTION_HEADER *s = m->sections;
    WORD i;
    for (i = 0; i < m->section_count; i++, s++) {
        DWORD end = s->VirtualAddress + s->Misc.VirtualSize;
        if (end < s->VirtualAddress || end > m->size) continue;
        if (a >= s->VirtualAddress && a < end)
            return end - a >= 4 && (s->Characteristics & (IMAGE_SCN_MEM_WRITE | IMAGE_SCN_MEM_EXECUTE)) == IMAGE_SCN_MEM_WRITE;
    }
    return 0;
}

/* sig fits at off, and if the entry has bytes it shows or a call it makes, they're what the match points at */
static int addr_matches(const struct hook_addr *e, const addr_module *m, DWORD off)
{
    DWORD p;
    if (!addr_sig_fits(m, off, e->sig)) return 0;
    if (e->shows) {
        p = *(DWORD *)(m->image + off + e->shows_op) - m->opbase;
        if (p >= m->size || m->size - p < addr_sig_len(e->shows) || !addr_sig_at(m->image + p, e->shows)) return 0;
    }
    if (e->calls) {
        p = off + e->calls_op + 4 + *(DWORD *)(m->image + off + e->calls_op);
        if (!addr_sig_fits(m, p, e->calls)) return 0;
    }
    return 1;
}

/* e's address for a match at off, or 0 when it'd be somewhere that kind of thing can't be: code in .text, data in a
   writable section that isn't code */
static DWORD addr_from(const struct hook_addr *e, const addr_module *m, DWORD off)
{
    DWORD a = e->operand == ADDR_MATCH ? off + (e->usual - e->sig_at)
                                       : *(DWORD *)(m->image + off + e->operand) - m->opbase;
    if (e->code) return a >= m->text && a < m->text_end ? a : 0;
    return addr_in_data(m, a) ? a : 0;
}

/* every place in .text the entry matches, whatever its address is, counted to 2, and where the first is */
static int addr_scan(const struct hook_addr *e, const addr_module *m, DWORD *first)
{
    DWORD n = addr_sig_len(e->sig), off;
    int hits = 0;
    for (off = m->text; off + n <= m->text_end && hits < 2; off++)
        if (addr_matches(e, m, off)) {
            if (!hits) *first = off;
            hits++;
        }
    return hits;
}

/* where it always was if it fits there, else the one place in .text it fits, else nowhere (at 0). Hits is how many
   places it fitted in, so 1 and 0 at means the address it gave was no good */
static void addr_find(struct hook_addr *e, const addr_module *m, int *hits)
{
    DWORD off = e->sig_at;
    if (addr_matches(e, m, off)) *hits = 1;
    else *hits = addr_scan(e, m, &off);
    e->at = *hits == 1 ? addr_from(e, m, off) : 0;
}

static void addr_list(char *list, int cap, const char *item)
{
    int n = lstrlenA(list);
    if (n >= 3 && !lstrcmpA(list + n - 3, "...")) return;
    if (n + (n ? 2 : 0) + lstrlenA(item) + 6 > cap) { lstrcatA(list, n ? ", ..." : "..."); return; }
    if (n) lstrcatA(list, ", ");
    lstrcatA(list, item);
}

/* 1 when every required address in module is found and a summary's logged, else a line for each that isn't. An entry
   with no signature always fails */
static int addr_resolve(int module, const char *label, BYTE *image, DWORD opbase)
{
    addr_module m;
    char moved[560] = "", skipped[200] = "", item[96], more[80] = "";
    int i, hits, usual = 0, nmoved = 0, nskipped = 0, missing = 0;
    int ok = image && addr_module_open(&m, image, opbase);
    if (!ok) base_log("addresses: %s's headers don't make sense, or it has no .text\r\n", label);
    for (i = 0; i < ADDRESS_COUNT; i++) {
        struct hook_addr *e = &g_addr[i];
        if (e->sig && e->module != module) continue;
        hits = 0;
        e->at = 0;
        if (ok && e->sig) addr_find(e, &m, &hits);
        if (e->at && e->at == e->usual) {
            usual++;
        } else if (e->at) {
            nmoved++;
            wsprintfA(item, "%s +%x -> +%x", e->name, (unsigned)e->usual, (unsigned)e->at);
            addr_list(moved, sizeof moved, item);
        } else if (e->sig && !e->required) {
            nskipped++;
            addr_list(skipped, sizeof skipped, e->name);
        } else {
            missing++;
            base_log("addresses: %s %s not found (%s)\r\n", label, e->sig ? e->name : "an entry with no signature",
                hits > 1 ? "more than one match" : hits ? "its address makes no sense" : "no match");
        }
    }
    if (missing) return 0;
    if (nskipped) wsprintfA(more, ", %d not found and not needed (", nskipped);
    base_log("addresses: %s %d at their usual place, %d moved%s%s%s%s%s%s\r\n", label, usual, nmoved,
        nmoved ? " (" : "", moved, nmoved ? ")" : "", more, skipped, nskipped ? ")" : "");
    return 1;
}

/* the same, but ends pol.exe when something it needs isn't there */
static void addr_require(int module, const char *label, BYTE *image, DWORD opbase)
{
    if (addr_resolve(module, label, image, opbase)) return;
    base_log("addresses: %s isn't a build the hook can use - nothing touched, ending pol.exe (exit %d)\r\n", label,
        ADDRESSES_EXIT);
    base_end_pol(ADDRESSES_EXIT);
}
