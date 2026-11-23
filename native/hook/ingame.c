/* keeps the friends list up to date while the game runs. POLL's callback copies polcore's rows (microseconds, on the
   game's thread) and a worker writes them out. Any fault switches it off for the rest of the game. */
enum { IG_FRIENDS = 200, IG_IGNORES = 100, IG_SAMPLE_MS = 500, IG_BEAT_MS = 5000, IG_RETRY_MS = 250, IW_BEAT_MS = 150 };

typedef struct ig_copy {
    pol_friend friends[IG_FRIENDS];
    pol_friend_extra extras[IG_FRIENDS];
    pol_friend ignores[IG_IGNORES];
    pol_handle handle;
    unsigned __int64 me;
    int status, have_handle;
    WCHAR comment[0x34];
} ig_copy;

typedef void (__cdecl *ig_poll_fn)(int);

static void fx_polled(void);
static int lb_ready(BYTE *b);
static void lb_tick(BYTE *b);
static int lb_wanted(void);
static void fx_session_up(void);
static void fx_game_tick(BYTE *b);
static void fx_poll(BYTE *b);
static int fx_patched(void);
static void fx_worker(BYTE *b);
static int fx_wait(HANDLE wake, DWORD ms);
static void fx_stop(void);
static void fx_reset(void);
static void iw_poll(BYTE *b);
static void iw_worker(BYTE *b);
static int iw_quick(void);
static void iw_snapshot(BYTE *b, jw *w, const ig_copy *c);
static void iw_reset(void);

static struct {
    BYTE *b;
    void **slot;
    ig_poll_fn prev;
    volatile LONG on, off, stop;
    ULONGLONG next;
    CRITICAL_SECTION cs;
    HANDLE wake, thread;
    ig_copy shared, scratch, written;
    DWORD beat;
    DWORD code, at;
    int said, lobby_only, game_faults;
} g_ig;

static void ig_copy_read(BYTE *b, ig_copy *c)
{
    int slot;
    memcpy(c->friends, pc_friend_row(b, 0), sizeof c->friends);
    memcpy(c->extras, pc_friend_extra(b, 0), sizeof c->extras);
    memcpy(c->ignores, pc_ignore_row(b, 0), sizeof c->ignores);
    c->status = pc_my_status_or(b, 4);
    c->me = pc_my_id_or(b, 0);
    slot = pc_current_handle_or(b, -1);
    if (slot < 0) slot = g_my_slot;
    c->have_handle = slot >= 0 && pc_has(b, SV_GET_HANDLE) && pc_get_handle(b, (unsigned)slot, &c->handle) == 0;
    memset(c->comment, 0, sizeof c->comment);
    if (pc_has(b, SV_MY_COMMENT)) pc_my_comment(b, -2, c->comment);
    c->comment[0x33] = 0;
}

static int ig_write_snapshot(BYTE *b, const ig_copy *c, int beat)
{
    char sk[17], hn[64], mine[64];
    int ok = 0;
    jw w = {0};
    peers_polid_text(b, c->me, mine);
    jw_obj(&w, NULL);
    jw_lit(&w, "sampler", "live");
    jw_uint(&w, "beat", (DWORD)beat);
    jw_bool(&w, "friendsLoaded", g_friends_loaded);
    jw_lit(&w, "myStatus", snapshot_status_name(c->status));
    if (c->have_handle) json_sjis(b, hn, sizeof hn, (const BYTE *)c->handle.name, 0x0f);
    jw_raw(&w, "myHandle", c->have_handle ? hn : "\"\"");
    sk[0] = 0;
    if (c->me) peers_key(b, (unsigned)c->me, (unsigned)(c->me >> 32), (unsigned)g_my_slot, sk);
    jw_lit(&w, "myKey", sk);
    jw_uint(&w, "myPic", c->have_handle ? c->handle.pic : 0);
    snapshot_blocked(b, &w, c->ignores);
    snapshot_friends(b, &w, c->friends, c->extras, mine);
    iw_snapshot(b, &w, c);
    jw_end(&w);
    if (!w.fail) ok = snapshot_write(&w);
    jw_free(&w);
    return ok;
}

static void (*ig_read)(BYTE *, ig_copy *) = ig_copy_read;
static int (*ig_write)(BYTE *, const ig_copy *, int) = ig_write_snapshot;

static int ig_filter(EXCEPTION_POINTERS *ep)
{
    g_ig.code = ep->ExceptionRecord->ExceptionCode;
    g_ig.at = (DWORD)(ULONG_PTR)ep->ExceptionRecord->ExceptionAddress;
    return EXCEPTION_EXECUTE_HANDLER;
}

static void __cdecl ig_poll(int unused);

/* the pointer back to what it was, only if it's still mine */
static void ig_disarm(void)
{
    __try {
        if (g_ig.slot) InterlockedCompareExchangePointer((PVOID volatile *)g_ig.slot, (PVOID)g_ig.prev, (PVOID)ig_poll);
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

/* nothing's written after this, so one the app had open at that moment is tried again for a second. Never the game's thread */
static void ig_write_off(void)
{
    jw w = {0};
    int tries;
    jw_obj(&w, NULL);
    jw_lit(&w, "sampler", "off");
    jw_end(&w);
    for (tries = 0; !w.fail && !snapshot_write(&w) && tries < 40; tries++) Sleep(25);
    jw_free(&w);
}

static void ig_fault(const char *where, int game)
{
    LONG was = InterlockedExchange(&g_ig.off, 1);
    /* a second one on the game's thread and the hook is off it for good. One from another thread only ends that thread */
    if (game && g_ig.game_faults++) {
        ig_disarm();
        return;
    }
    if (was) {
        base_log("ingame: fault %08x at %08x in the %s as well\r\n", g_ig.code, g_ig.at, where);
        return;
    }
    fx_stop();
    if (g_ig.thread) SetEvent(g_ig.wake);
    else if (!g_ig.lobby_only) ig_write_off();
    base_log("ingame: fault %08x at %08x in the %s, friends stop updating for the rest of this game\r\n", g_ig.code, g_ig.at, where);
}

static void ig_tick(void)
{
    ULONGLONG now = GetTickCount64();
    LONG changed;
    if (now < g_ig.next) return;
    g_ig.next = now + IG_SAMPLE_MS;
    /* one line each, the first time the game's thread gets here and the first time FFXI's session is up */
    if (!(g_ig.said & 1)) {
        g_ig.said |= 1;
        base_log("ingame: the POLL callback is running on the game's thread\r\n");
        fx_polled();
    }
    fx_game_tick(g_ig.b);
    lb_tick(g_ig.b);
    if (g_ig.off || g_ig.lobby_only || pc_game_tag(g_ig.b) < 0) return;
    if (!(g_ig.said & 2)) {
        g_ig.said |= 2;
        base_log("ingame: FFXI's session is up, copying the friends list from here\r\n");
        fx_session_up();
        SetEvent(g_ig.wake);
    }
    ig_read(g_ig.b, &g_ig.scratch);
    /* the worker only holds this to copy, so a frame that finds it taken tries again on the next */
    if (!TryEnterCriticalSection(&g_ig.cs)) {
        g_ig.next = now;
        return;
    }
    changed = memcmp(&g_ig.shared, &g_ig.scratch, sizeof g_ig.shared) != 0;
    if (changed) memcpy(&g_ig.shared, &g_ig.scratch, sizeof g_ig.shared);
    LeaveCriticalSection(&g_ig.cs);
    if (changed) SetEvent(g_ig.wake);
}

static void __cdecl ig_poll(int unused)
{
    if (g_ig.prev) g_ig.prev(unused);
    if (g_ig.stop) return;
    __try {
        ig_tick();
    } __except (ig_filter(GetExceptionInformation())) {
        ig_fault("game thread", 1);
    }
    iw_poll(g_ig.b);
    /* with the friends list off it's only here for the chat's calls to go back and for the lobby */
    if (g_ig.off && !fx_patched() && !lb_wanted()) ig_disarm();
}

/* 0 when the file didn't go in, the worker tries again soon */
static int ig_flush(void)
{
    EnterCriticalSection(&g_ig.cs);
    memcpy(&g_ig.written, &g_ig.shared, sizeof g_ig.written);
    LeaveCriticalSection(&g_ig.cs);
    return ig_write(g_ig.b, &g_ig.written, (int)++g_ig.beat);
}

static DWORD WINAPI ig_worker(void *unused)
{
    int ok = 1, woke;
    ULONGLONG flushed = 0;
    (void)unused;
    while (!g_ig.stop) {
        woke = fx_wait(g_ig.wake, !ok ? IG_RETRY_MS : iw_quick() ? IW_BEAT_MS : IG_BEAT_MS);
        if (g_ig.stop) break;
        if (!g_ig.off) {
            iw_worker(g_ig.b);
            /* r\b changing on its own, or the quick beat for the app's requests, is no reason to write the friends out
               again unless something changed or the beat's due */
            if (!woke || g_snap_dirty || !ok || GetTickCount64() - flushed >= IG_BEAT_MS) {
                g_snap_dirty = 0;
                flushed = GetTickCount64();
                __try {
                    ok = ig_flush();
                } __except (ig_filter(GetExceptionInformation())) {
                    ig_fault("worker", 0);
                }
            }
            fx_worker(g_ig.b);
        }
        if (g_ig.off) {
            __try {
                ig_write_off();
            } __except (EXCEPTION_EXECUTE_HANDLER) {}
            break;
        }
    }
    return 0;
}

static void ingame_stop(void)
{
    if (!InterlockedExchange(&g_ig.on, 0)) return;
    InterlockedExchange(&g_ig.stop, 1);
    ig_disarm();
    if (g_ig.wake) SetEvent(g_ig.wake);
}

/* whether the list can keep updating in the game. Every snapshot says so while the hold has it, so the app knows before Play */
static int ingame_can(BYTE *b)
{
    return !g_lean && g_friends_loaded && pc_addr_found(AT_POLL_CALLBACK) && pc_has(b, SV_GAME_TAG);
}

/* at Play, with the reason in the log when it can't. 1 when it can */
static int ingame_ready(BYTE *b)
{
    if (g_lean || !g_friends_loaded) {
        base_log("ingame: this session has no friends list, so there's nothing to keep up to date\r\n");
        return 0;
    }
    if (!ingame_can(b)) {
        base_log("ingame: this PlayOnline build has no POLL callback pointer the hook knows, friends won't update in the game\r\n");
        return 0;
    }
    base_log("ingame: friends will keep updating while the game runs\r\n");
    return 1;
}

/* once Play has drained and the game's about to boot, on the thread that pumped polcore. With no friends list to keep it
   only answers the lobby's menus */
static void ingame_start(BYTE *b)
{
    void *cur;
    int friends = ingame_can(b);
    memset(&g_ig, 0, sizeof g_ig);
    g_ig.lobby_only = !friends;
    fx_reset();
    iw_reset();
    InitializeCriticalSection(&g_ig.cs);
    g_ig.b = b;
    g_ig.slot = (void **)pc_addr_of(b, AT_POLL_CALLBACK);
    g_ig.wake = CreateEventA(NULL, FALSE, FALSE, NULL);
    __try {
        cur = *g_ig.slot;
        g_ig.prev = (ig_poll_fn)cur;
        if (InterlockedCompareExchangePointer((PVOID volatile *)g_ig.slot, (PVOID)ig_poll, cur) != cur) {
            g_ig.slot = NULL;
            base_log("ingame: something else set the POLL callback just now, friends won't update in the game\r\n");
            if (friends) ig_write_off();
            return;
        }
        g_ig.on = 1;
        if (cur) base_log("ingame: the POLL callback was already set, so it's called first\r\n");
        if (!friends) return;
        /* the first copy's from the hold's own thread, the one that pumped polcore till now */
        ig_read(b, &g_ig.shared);
        ig_flush();
        g_ig.thread = CreateThread(NULL, 0, ig_worker, NULL, 0, NULL);
        if (!g_ig.thread) {
            g_ig.code = GetLastError();
            ig_fault("start", 0);
        }
    } __except (ig_filter(GetExceptionInformation())) {
        ig_fault("start", 0);
    }
}
