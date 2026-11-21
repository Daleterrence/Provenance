enum { LB_NAME = 16, LB_ENTRIES = 16, LB_STRIDE = 0xb0, LB_PICK_MS = 10000, LB_LIST_MS = 5000, LB_SETTLE_MS = 500, LB_REPICK_MS = 500,
       LB_PICK_TRIES = 4, LB_ERROR_MS = 5000, LB_WORLD_MS = 120000, LB_RETRIES = 3 };
enum { LB_NONE, LB_LICENCE, LB_LOBBY, LB_LIST, LB_CONFIRM, LB_OK };
enum { LB_PICKING, LB_AWAIT_CONFIRM, LB_AWAIT_WORLD, LB_FINISHED };

static struct {
    int licence, picking, stage, answered, tried, picks, retries;
    int licence_off, picking_off;
    char name[LB_NAME], world[LB_NAME];
    ULONGLONG started_at, next_look, picked_at, list_at, confirmed_at, error_at;
} g_lb;

static const int g_lb_licence_needs[] = { AT_FX_MENU_FRONT, AT_FX_LICENCE_MENU };
static const int g_lb_pick_needs[] = { AT_FX_MENU_FRONT, AT_FX_LOBBY_MENU, AT_FX_CHAR_LIST_MENU, AT_FX_CONFIRM_MENU, AT_FX_CHAR_INDEX };

static int lb_name_ok(const char *s)
{
    int n = lstrlenA(s), i;
    if (n < 1 || n >= LB_NAME) return 0;
    for (i = 0; i < n; i++)
        if ((BYTE)s[i] < 0x21 || (BYTE)s[i] > 0x7e) return 0;
    return 1;
}

/* written as Play's pressed, so a character picked after the login still counts. An empty value switches one off */
static void lb_play_file(void)
{
    static const char *allowed[] = { ENV_ACCEPT_LICENCE, ENV_CHARACTER, ENV_CHARACTER_WORLD };
    char path[MAX_PATH + 64], buf[512], *p, *line, *eq;
    DWORD got = 0;
    HANDLE f;
    int k;
    base_ipc_file(path, IPC_LOBBY);
    f = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return;
    ReadFile(f, buf, sizeof buf - 1, &got, NULL);
    CloseHandle(f);
    DeleteFileA(path);
    buf[got] = 0;
    for (p = buf; *p; ) {
        line = requests_line(&p);
        if (!(eq = strchr(line, '='))) continue;
        *eq = 0;
        for (k = 0; k < 3; k++)
            if (!lstrcmpA(line, allowed[k])) SetEnvironmentVariableA(line, eq[1] ? eq + 1 : NULL);
    }
}

/* at Play: what's asked for. 1 when the callback has to be there for it */
static int lb_ready(BYTE *b)
{
    char v[8], name[32];
    DWORD n;
    (void)b;
    memset(&g_lb, 0, sizeof g_lb);
    lb_play_file();
    g_lb.licence = GetEnvironmentVariableA(ENV_ACCEPT_LICENCE, v, sizeof v) == 1 && v[0] == '1';
    n = GetEnvironmentVariableA(ENV_CHARACTER, name, sizeof name);
    if (n >= sizeof name || (n && !lb_name_ok(name))) {
        base_log("lobby: the character name can't be used, a character isn't picked\r\n");
    } else if (n) {
        lstrcpyA(g_lb.name, name);
        g_lb.picking = 1;
        if (!GetEnvironmentVariableA(ENV_CHARACTER_WORLD, g_lb.world, sizeof g_lb.world)) g_lb.world[0] = 0;
    }
    if (!g_lb.licence && !g_lb.picking) return 0;
    if (!pc_addr_found(AT_POLL_CALLBACK)) {
        base_log("lobby: no POLL callback pointer the hook knows, the lobby's left to the player\r\n");
        memset(&g_lb, 0, sizeof g_lb);
        return 0;
    }
    if (g_lb.licence) base_log("lobby: accepting the licence agreement\r\n");
    if (g_lb.picking) base_log("lobby: logging in to a chosen character\r\n");
    return 1;
}

static int lb_licence_on(void) { return g_lb.licence && !g_lb.licence_off; }
static int lb_picking_on(void) { return g_lb.picking && !g_lb.picking_off && g_lb.stage != LB_FINISHED; }
static int lb_wanted(void) { return lb_licence_on() || lb_picking_on(); }

static int lb_have(const int *ids, int n)
{
    while (n--)
        if (!pc_addr_found(ids[n])) return 0;
    return 1;
}

/* in a game with no friends list nothing else looks at FFXiMain, so this does */
static void lb_look(ULONGLONG now)
{
    if (!g_lb.started_at) g_lb.started_at = now;
    if (now - g_lb.started_at < FX_LOBBY_LOOK_MS || now < g_lb.next_look) return;
    g_lb.tried++;
    g_lb.next_look = now + (g_lb.tried <= 2 ? FX_LOBBY_RETRY_MS : FX_RETRY_MS);
    fx_resolve();
    if (g_fx.resolved && g_lb.tried < FX_TRIES &&
        ((lb_licence_on() && !lb_have(g_lb_licence_needs, 2)) || (lb_picking_on() && !lb_have(g_lb_pick_needs, 5))))
        g_fx.resolved = 0;
}

/* whether the looking for FFXiMain's addresses is over, so a missing one is missing for good */
static int lb_looked(void)
{
    return g_ig.lobby_only ? g_lb.tried >= FX_TRIES : g_fx.found || g_fx.tries >= FX_TRIES;
}

static int lb_kind(const char *name)
{
    static const char names[][17] = { "menu    ptc8lice", "menu    loby2win", "menu    dbnamese", "menu    ptc6yesn",
                                      "menu    ok      " };
    static const int kinds[] = { LB_LICENCE, LB_LOBBY, LB_LIST, LB_CONFIRM, LB_OK };
    int i;
    for (i = 0; i < 5; i++)
        if (!memcmp(name, names[i], 16)) return kinds[i];
    return LB_NONE;
}

static BYTE *lb_menu(int id) { return *(BYTE **)pc_addr_of(g_fx.base, id); }

static char lb_fold(char c) { return c >= 'A' && c <= 'Z' ? (char)(c + 32) : c; }

/* a name is NUL terminated inside its 16 bytes, no NUL means it isn't one */
static int lb_same_name(const char *entry, const char *want)
{
    int i;
    for (i = 0; i < LB_NAME; i++) {
        if (!entry[i]) return !want[i];
        if (!want[i] || lb_fold(entry[i]) != lb_fold(want[i])) return 0;
    }
    return 0;
}

/* spaces don't count, the list pads a world's name with them */
static int lb_same_world(const char *entry, const char *want)
{
    int i = 0, k = 0;
    for (;; i++, k++) {
        while (i < LB_NAME && entry[i] == ' ') i++;
        while (want[k] == ' ') k++;
        if (i >= LB_NAME || !entry[i]) return !want[k];
        if (!want[k] || lb_fold(entry[i]) != lb_fold(want[k])) return 0;
    }
}

static int lb_entry_usable(const BYTE *e)
{
    return *(const int *)(e + 0x34) == 1 && *(const int *)(e + 0x5c) == 0 && *(const int *)(e + 0x64) == 0;
}

/* 1 when it's dealt with, 0 to look again on the next tick */
static int lb_pick(ULONGLONG now)
{
    BYTE *m = lb_menu(AT_FX_CHAR_LIST_MENU);
    int k, hits = 0, at = -1, used = 0, named = 0, only = -1;
    if (!m) return 0;
    for (k = 0; k < LB_ENTRIES; k++) {
        const BYTE *e = m + 0x34 + LB_STRIDE * k;
        used += *(const int *)(e + 0x34) == 1;
        if (!lb_same_name((const char *)e + 0x10, g_lb.name)) continue;
        named++;
        only = k;
        if (g_lb.world[0] && !lb_same_world((const char *)e + 0x20, g_lb.world)) continue;
        hits++;
        at = k;
    }
    /* the world's only there to tell two with one name apart */
    if (!hits && named == 1) {
        hits = 1;
        at = only;
    }
    if (!g_lb.list_at) g_lb.list_at = now;
    if (!used && now - g_lb.list_at < LB_LIST_MS) return 0;
    if (now - g_lb.list_at < LB_SETTLE_MS) return 0;
    if (hits == 1 && lb_entry_usable(m + 0x34 + LB_STRIDE * at)) {
        *(int *)pc_addr_of(g_fx.base, AT_FX_CHAR_INDEX) = at;
        *(int *)(m + 0x14) = at;
        *(int *)(m + 0x18) = at;
        *(int *)(m + 0x1c) = 1;
        g_lb.stage = LB_AWAIT_CONFIRM;
        g_lb.picked_at = now;
        base_log(g_lb.picks++ ? "lobby: the game cleared that, picked the character again\r\n" : "lobby: picked a character in the list\r\n");
        return 1;
    }
    g_lb.stage = LB_FINISHED;
    base_log(hits > 1 || (!hits && named) ? "lobby: more than one character has that name, the pick's left to the player\r\n"
             : hits ? "lobby: that character can't be logged in to, the pick's left to the player\r\n"
                    : "lobby: that character isn't in the list, the pick's left to the player\r\n");
    return 1;
}

/* the game zeroes the list's answer once the list has faded in, so one written before that never counted */
static int lb_pick_lost(ULONGLONG now)
{
    BYTE *m = lb_menu(AT_FX_CHAR_LIST_MENU);
    return g_lb.stage == LB_AWAIT_CONFIRM && g_lb.answered == LB_LIST && g_lb.picks < LB_PICK_TRIES &&
           now - g_lb.picked_at >= LB_REPICK_MS && m && !*(int *)(m + 0x1c);
}

static void lb_finish(const char *why)
{
    g_lb.stage = LB_FINISHED;
    base_log("lobby: %s, the character pick is over for this game\r\n", why);
}

static int lb_says_3101(const BYTE *box)
{
    static const char code[] = "FFXI-3101";
    const char *text = *(const char *const *)(box + 0x24c);
    int len = *(const int *)(box + 0x250), i;
    if (!text || len < (int)sizeof code - 1 || len > 0x400) return 0;
    for (i = 0; i + (int)sizeof code - 1 <= len; i++)
        if (!memcmp(text + i, code, sizeof code - 1)) return 1;
    return 0;
}

/* 3101 comes and goes and the next go usually gets in, so its box is OK'd after a few seconds. Any other error is the player's */
static int lb_error(ULONGLONG now)
{
    BYTE *m = lb_menu(AT_FX_OK_MENU);
    if (!m) return 0;
    if (g_lb.retries >= LB_RETRIES || !lb_says_3101(m)) {
        lb_finish("the game gave an error");
        return 1;
    }
    if (!g_lb.error_at) g_lb.error_at = now;
    if (now - g_lb.error_at < LB_ERROR_MS) return 0;
    *(int *)(m + 0x25c) = 1;
    g_lb.retries++;
    g_lb.stage = LB_PICKING;
    g_lb.picks = 0;
    g_lb.error_at = 0;
    base_log("lobby: FFXI-3101 at the login, trying the character again (%d of %d)\r\n", g_lb.retries, LB_RETRIES);
    return 1;
}

/* 1 when the menu was answered */
static int lb_answer(int kind, ULONGLONG now)
{
    BYTE *m;
    if (kind == LB_LICENCE && lb_licence_on()) {
        if (!(m = lb_menu(AT_FX_LICENCE_MENU))) return 0;
        *(int *)(m + 0x18) = 0;
        *(int *)(m + 0x1c) = 1;
        base_log("lobby: accepted the licence agreement\r\n");
        return 1;
    }
    if (!lb_picking_on()) return 0;
    if (kind == LB_LOBBY || kind == LB_LIST) {
        /* the list's in front again for a moment as the confirm box closes */
        if (kind == LB_LIST && g_lb.stage == LB_AWAIT_WORLD) return 0;
        if (g_lb.stage != LB_PICKING) {
            lb_finish("the player went back");
            return 0;
        }
        if (kind == LB_LIST) return lb_pick(now);
        if (!(m = lb_menu(AT_FX_LOBBY_MENU))) return 0;
        *(int *)(m + 0x18) = 0;
        *(int *)(m + 0x1c) = 1;
        return 1;
    }
    if (kind == LB_CONFIRM && g_lb.stage == LB_AWAIT_CONFIRM) {
        if (!(m = lb_menu(AT_FX_CONFIRM_MENU))) return 0;
        *(int *)(m + 0x14) = 0;
        *(int *)(m + 0x30) = 1;
        if (pc_addr_found(AT_FX_OK_MENU)) {
            g_lb.stage = LB_AWAIT_WORLD;
            g_lb.confirmed_at = now;
            g_lb.error_at = 0;
            base_log("lobby: answered the confirm box\r\n");
        } else {
            g_lb.stage = LB_FINISHED;
            base_log("lobby: answered the confirm box, the character pick is over for this game\r\n");
        }
        return 1;
    }
    if (kind == LB_OK && g_lb.stage == LB_AWAIT_WORLD) return lb_error(now);
    return 0;
}

static void lb_step(void)
{
    ULONGLONG now = GetTickCount64();
    char name[17];
    int kind;
    if (!g_fx.resolved) {
        if (!g_ig.lobby_only) return;
        lb_look(now);
        if (!g_fx.resolved) {
            if (g_lb.tried >= FX_TRIES) {
                g_lb.licence_off = g_lb.picking_off = 1;
                base_log("lobby: FFXiMain's addresses weren't found, the lobby's left to the player\r\n");
            }
            return;
        }
    }
    if (lb_licence_on() && !lb_have(g_lb_licence_needs, 2)) {
        if (!lb_looked()) return;
        g_lb.licence_off = 1;
        base_log(pc_addr_found(AT_FX_MENU_FRONT) ? "lobby: no licence menu in this build, the licence agreement is left to the player\r\n"
                                                : "lobby: no menu in front in this build, the lobby's left to the player\r\n");
    }
    if (lb_picking_on() && !lb_have(g_lb_pick_needs, 5)) {
        if (!lb_looked()) return;
        g_lb.picking_off = 1;
        base_log(pc_addr_found(AT_FX_MENU_FRONT) ? "lobby: no lobby menus in this build, the character pick is left to the player\r\n"
                                                : "lobby: no menu in front in this build, the lobby's left to the player\r\n");
    }
    if (lb_picking_on() && g_fx.in_world) lb_finish("the player's in the world");
    if (lb_picking_on() && g_lb.stage == LB_AWAIT_CONFIRM && now - g_lb.picked_at > LB_PICK_MS) lb_finish("no confirm box came");
    if (lb_picking_on() && g_lb.stage == LB_AWAIT_WORLD && now - g_lb.confirmed_at > LB_WORLD_MS) lb_finish("the login wasn't seen to finish");
    if (lb_picking_on() && g_lb.stage == LB_AWAIT_WORLD && !lb_menu(AT_FX_CHAR_LIST_MENU)) lb_finish("the lobby's closed");
    if (!lb_licence_on() && !lb_picking_on()) return;
    kind = fx_menu_name(name) ? lb_kind(name) : LB_NONE;
    if (kind == LB_LIST && lb_picking_on() && lb_pick_lost(now)) {
        g_lb.stage = LB_PICKING;
        g_lb.answered = LB_NONE;
    }
    if (kind == g_lb.answered && kind != LB_NONE) return;
    g_lb.answered = LB_NONE;
    if (kind != LB_LIST) g_lb.list_at = 0;
    if (kind != LB_OK) g_lb.error_at = 0;
    if (kind != LB_NONE && lb_answer(kind, now)) g_lb.answered = kind;
}

/* game thread, every sample */
static void lb_tick(BYTE *b)
{
    (void)b;
    if (!lb_licence_on() && !lb_picking_on()) return;
    __try {
        lb_step();
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        g_lb.licence_off = g_lb.picking_off = 1;
        base_log("lobby: fault %08x, the lobby's left to the player for the rest of this game\r\n", GetExceptionCode());
    }
}
