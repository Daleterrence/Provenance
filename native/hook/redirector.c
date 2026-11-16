/* polcore's chat login opens with a connection to the redirector, which is sent nothing and answers with one short
   line naming the chat server. When Provenance says a login's coming the hook asks that itself on its own thread as it
   loads, and when polcore's login gets there the answer's handed over on loopback, once. */
#define REDIRECTOR_HOST "ci000.pol.com"
#define REDIRECTOR_PORT 51240
#define REDIRECTOR_ASK_MS 5000       /* connect and answer together */
#define REDIRECTOR_WAIT_MS 6000      /* polcore waits this long for an ask still going, rather than make a second one */
#define REDIRECTOR_LIFE_MS 60000     /* an older answer is never used */
#define REDIRECTOR_SERVE_MS 1000
#define REDIRECTOR_LATE_MS 15000     /* how long a connection that didn't show at once is waited for */
#define REDIRECTOR_BACKLOG 0x7fffffff
#define REDIRECTOR_MAX 512
#define REDIRECTOR_EXCLUSIVE ((int)0xfffffffb)   /* SO_EXCLUSIVEADDRUSE, winsock2.h's */

/* ws2_32 by name, so nothing new is linked */
static struct {
    int (PASCAL *startup)(WORD, WSADATA *);
    SOCKET (PASCAL *socket)(int, int, int);
    int (PASCAL *connect)(SOCKET, const struct sockaddr *, int);
    int (PASCAL *recv)(SOCKET, char *, int, int);
    int (PASCAL *send)(SOCKET, const char *, int, int);
    int (PASCAL *closesocket)(SOCKET);
    int (PASCAL *bind)(SOCKET, const struct sockaddr *, int);
    int (PASCAL *listen)(SOCKET, int);
    SOCKET (PASCAL *accept)(SOCKET, struct sockaddr *, int *);
    int (PASCAL *getsockname)(SOCKET, struct sockaddr *, int *);
    int (PASCAL *getpeername)(SOCKET, struct sockaddr *, int *);
    int (PASCAL *select)(int, fd_set *, fd_set *, fd_set *, const struct timeval *);
    int (PASCAL *setsockopt)(SOCKET, int, int, const char *, int);
    int (PASCAL *ioctlsocket)(SOCKET, long, u_long *);
    struct hostent *(PASCAL *gethostbyname)(const char *);
    int (PASCAL *last_error)(void);
    void (PASCAL *set_last_error)(int);
} g_ws;
static INIT_ONCE g_ws_once = INIT_ONCE_STATIC_INIT;
static int g_ws_ok;
static BOOL CALLBACK redirector_ws_init(PINIT_ONCE once, PVOID param, PVOID *ctx)
{
    HMODULE ws = GetModuleHandleW(L"ws2_32.dll");
    WSADATA d;
    (void)once; (void)param; (void)ctx;
    if (!ws) ws = LoadLibraryExW(L"ws2_32.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!ws) return TRUE;
#define WS(field, name) *(FARPROC *)&g_ws.field = GetProcAddress(ws, name); if (!g_ws.field) return TRUE
    WS(startup, "WSAStartup"); WS(socket, "socket"); WS(connect, "connect"); WS(recv, "recv"); WS(send, "send");
    WS(closesocket, "closesocket"); WS(bind, "bind"); WS(listen, "listen"); WS(accept, "accept");
    WS(getsockname, "getsockname"); WS(getpeername, "getpeername"); WS(select, "select"); WS(setsockopt, "setsockopt");
    WS(ioctlsocket, "ioctlsocket"); WS(gethostbyname, "gethostbyname"); WS(last_error, "WSAGetLastError");
    WS(set_last_error, "WSASetLastError");
#undef WS
    g_ws_ok = g_ws.startup(0x0202, &d) == 0;
    return TRUE;
}
static int redirector_ws_load(void)
{
    InitOnceExecuteOnce(&g_ws_once, redirector_ws_init, NULL, NULL);
    return g_ws_ok;
}

static struct timeval redirector_timeval(ULONGLONG ms)
{
    struct timeval tv;
    tv.tv_sec = (long)(ms / 1000);
    tv.tv_usec = (long)(ms % 1000) * 1000;
    return tv;
}
/* 1 when s can be read (or written) inside ms */
static int redirector_wait(SOCKET s, int writing, ULONGLONG ms)
{
    fd_set set;
    struct timeval tv = redirector_timeval(ms);
    FD_ZERO(&set);
    FD_SET(s, &set);
    return g_ws.select(0, writing ? NULL : &set, writing ? &set : NULL, NULL, &tv) > 0;
}
static ULONGLONG redirector_left(ULONGLONG until)
{
    ULONGLONG now = GetTickCount64();
    return now < until ? until - now : 0;
}

/* asks host:port, which says nothing until it's connected to and then sends one short line. The bytes that came (never
   0) or -1, all of it inside timeout_ms apart from the name lookup. The connection's closed again straight away */
static int redirector_ask(const char *host, unsigned short port, DWORD timeout_ms, BYTE *out, int cap)
{
    struct sockaddr_in sa, peer;
    struct hostent *h;
    SOCKET s;
    u_long nonblocking = 1;
    ULONGLONG until;
    int n = 0, got, len = sizeof peer;
    if (!redirector_ws_load()) return -1;
    h = g_ws.gethostbyname(host);
    if (!h || h->h_addrtype != AF_INET || h->h_length != 4 || !h->h_addr_list || !h->h_addr_list[0]) return -1;
    s = g_ws.socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) return -1;
    until = GetTickCount64() + timeout_ms;
    memset(&sa, 0, sizeof sa);
    sa.sin_family = AF_INET;
    sa.sin_port = (unsigned short)(port >> 8 | port << 8);
    memcpy(&sa.sin_addr, h->h_addr_list[0], 4);
    if (g_ws.ioctlsocket(s, FIONBIO, &nonblocking) != 0) goto fail;
    if (g_ws.connect(s, (struct sockaddr *)&sa, sizeof sa) != 0 && g_ws.last_error() != WSAEWOULDBLOCK) goto fail;
    if (!redirector_wait(s, 1, redirector_left(until)) || g_ws.getpeername(s, (struct sockaddr *)&peer, &len) != 0) goto fail;
    while (n < cap) {
        /* what comes after the first of it gets a moment, the line may be in two bits */
        if (!redirector_wait(s, 0, n ? 100 : redirector_left(until))) break;
        got = g_ws.recv(s, (char *)out + n, cap - n, 0);
        if (got <= 0) break;
        n += got;
    }
    g_ws.closesocket(s);
    return n > 0 ? n : -1;
fail:
    g_ws.closesocket(s);
    return -1;
}

/* a listener on loopback, on a port the system picks (host order in *port). Nothing else can share it */
static SOCKET redirector_listen(unsigned short *port)
{
    struct sockaddr_in la;
    int len = sizeof la, on = 1;
    SOCKET s = g_ws.socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) return s;
    memset(&la, 0, sizeof la);
    la.sin_family = AF_INET;
    la.sin_addr.S_un.S_addr = 0x0100007f;   /* 127.0.0.1 */
    g_ws.setsockopt(s, SOL_SOCKET, REDIRECTOR_EXCLUSIVE, (const char *)&on, sizeof on);
    if (g_ws.bind(s, (struct sockaddr *)&la, sizeof la) != 0 || g_ws.listen(s, REDIRECTOR_BACKLOG) != 0 ||
        g_ws.getsockname(s, (struct sockaddr *)&la, &len) != 0) {
        g_ws.closesocket(s);
        return INVALID_SOCKET;
    }
    *port = (unsigned short)(la.sin_port >> 8 | la.sin_port << 8);
    return s;
}

/* polcore's socket has been connected to the listener from port (network order). The connection that came from that
   port gets the answer and is kept (*served), anything else that got there first is closed. 0 when it never came inside
   wait_ms */
static int redirector_serve(SOCKET listener, unsigned short port, const BYTE *answer, int n, DWORD wait_ms, SOCKET *served)
{
    struct sockaddr_in peer;
    ULONGLONG until = GetTickCount64() + wait_ms;
    int len, sent, k;
    *served = INVALID_SOCKET;
    if (!port) return 0;
    while (redirector_wait(listener, 0, redirector_left(until))) {
        SOCKET c = g_ws.accept(listener, NULL, NULL);
        if (c == INVALID_SOCKET) return 0;
        len = sizeof peer;
        if (g_ws.getpeername(c, (struct sockaddr *)&peer, &len) == 0 && peer.sin_port == port &&
            ((BYTE *)&peer.sin_addr)[0] == 127) {
            for (sent = 0; sent < n; sent += k) {
                k = g_ws.send(c, (const char *)answer + sent, n - sent, 0);
                if (k <= 0) break;
            }
            if (sent < n) { g_ws.closesocket(c); return 0; }
            *served = c;
            return 1;
        }
        g_ws.closesocket(c);
    }
    return 0;
}

enum { RED_NOT_ASKED, RED_ASKING, RED_READY, RED_TAKEN, RED_GONE };
/* the state moves on only by compare-exchange and the rest is written before the move to READY, so what polcore's side
   reads once it sees READY is whole */
static struct {
    volatile LONG state;
    HANDLE thread, answered, done;   /* answered: the ask is over, done: polcore took the answer or it expired */
    char host[64];
    unsigned short port, lport;
    DWORD ask_ms, wait_ms, life_ms, serve_ms, late_ms;
    unsigned short late_port;   /* polcore's port when its connection hadn't shown by serve_ms, 0 when it had */
    BYTE answer[REDIRECTOR_MAX];
    int len;
    ULONGLONG at;
    SOCKET listener, served;
} g_red;

static int redirector_move(LONG from, LONG to) { return InterlockedCompareExchange(&g_red.state, to, from) == from; }

static DWORD WINAPI redirector_thread(void *unused)
{
    ULONGLONG t0 = GetTickCount64();
    unsigned short lport = 0;
    SOCKET listener;
    int n;
    (void)unused;
    n = redirector_ask(g_red.host, g_red.port, g_red.ask_ms, g_red.answer, sizeof g_red.answer);
    listener = n > 0 ? redirector_listen(&lport) : INVALID_SOCKET;
    if (listener == INVALID_SOCKET) {
        base_log("redirector: %s, polcore asks for itself\r\n", n > 0 ? "no loopback listener" : "no answer to the early ask");
        if (redirector_move(RED_ASKING, RED_GONE)) SetEvent(g_red.answered);
        return 0;
    }
    g_red.len = n;
    g_red.listener = listener;
    g_red.lport = lport;
    g_red.at = GetTickCount64();
    if (!redirector_move(RED_ASKING, RED_READY)) {   /* polcore stopped waiting and has asked for itself */
        g_ws.closesocket(listener);
        base_log("redirector: answered after polcore stopped waiting, not used\r\n");
        return 0;
    }
    SetEvent(g_red.answered);
    base_log("redirector: asked early, %d bytes in %ums\r\n", n, (unsigned)(g_red.at - t0));
    if (WaitForSingleObject(g_red.done, g_red.life_ms) == WAIT_TIMEOUT && redirector_move(RED_READY, RED_GONE)) {
        g_ws.closesocket(listener);
        base_log("redirector: early answer not wanted in time, dropped\r\n");
        return 0;
    }
    /* polcore's end closes when it's read the line, like the redirector's would. This end goes after it */
    if (WaitForSingleObject(g_red.done, 5000) != WAIT_OBJECT_0) return 0;
    if (g_red.late_port) {
        /* polcore's socket is on this listener already, so it's kept open until the connection's been accepted */
        int ok = redirector_serve(listener, g_red.late_port, g_red.answer, g_red.len, g_red.late_ms, &g_red.served);
        g_ws.closesocket(listener);
        base_log("redirector: early answer %s polcore, late\r\n", ok ? "handed to" : "never taken by");
    }
    if (g_red.served != INVALID_SOCKET) {
        redirector_wait(g_red.served, 0, 10000);
        g_ws.closesocket(g_red.served);
    }
    return 0;
}

/* where the thread starts from a DllMain: it begins once the loader lock's let go, and nothing here waits for it */
static void redirector_begin(const char *host, unsigned short port, DWORD ask_ms, DWORD wait_ms, DWORD life_ms)
{
    memset(&g_red, 0, sizeof g_red);
    lstrcpynA(g_red.host, host, sizeof g_red.host);
    g_red.port = port;
    g_red.ask_ms = ask_ms;
    g_red.wait_ms = wait_ms;
    g_red.life_ms = life_ms;
    g_red.serve_ms = REDIRECTOR_SERVE_MS;
    g_red.late_ms = REDIRECTOR_LATE_MS;
    g_red.listener = g_red.served = INVALID_SOCKET;
    g_red.answered = CreateEventW(NULL, TRUE, FALSE, NULL);
    g_red.done = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!g_red.answered || !g_red.done) {
        base_log("redirector: not asked early (no event)\r\n");
        return;
    }
    g_red.state = RED_ASKING;
    g_red.thread = CreateThread(NULL, 0, redirector_thread, NULL, 0, NULL);
    if (!g_red.thread) {
        g_red.state = RED_GONE;
        base_log("redirector: not asked early (no thread)\r\n");
        return;
    }
}
static void redirector_start(void)
{
    if (!base_getenv_set(ENV_LOGIN_COMING)) {
        base_log("redirector: no login coming, not asked early\r\n");
        return;
    }
    redirector_begin(REDIRECTOR_HOST, REDIRECTOR_PORT, REDIRECTOR_ASK_MS, REDIRECTOR_WAIT_MS, REDIRECTOR_LIFE_MS);
}

/* polcore's connect to a: the redirector's, and is there anything of ours for it */
static int redirector_aimed(const struct sockaddr *a, int n)
{
    const struct sockaddr_in *in = (const struct sockaddr_in *)a;
    return g_red.state != RED_NOT_ASKED && a && n >= (int)sizeof *in && in->sin_family == AF_INET &&
           in->sin_port == (unsigned short)(g_red.port >> 8 | g_red.port << 8);
}

/* polcore is connecting s to the redirector. 1 when s went to the early answer instead (*rc is what connect should
   give back), 0 when polcore goes ahead on its own: nothing asked, it failed, it's too old or it's been used. Waits for
   an ask that's still going, rather than let a second one out */
static int redirector_connect(SOCKET s, int (PASCAL *real)(SOCKET, const struct sockaddr *, int), int *rc)
{
    struct sockaddr_in lo, me;
    SOCKET served;
    unsigned age;
    int ok, err, len = sizeof me;
    if (g_red.state == RED_ASKING) {
        WaitForSingleObject(g_red.answered, g_red.wait_ms);
        if (redirector_move(RED_ASKING, RED_GONE)) {
            base_log("redirector: early ask still going, polcore asks for itself\r\n");
            return 0;
        }
    }
    if (g_red.state != RED_READY) return 0;
    age = (unsigned)(GetTickCount64() - g_red.at);
    if (age >= g_red.life_ms) {
        if (redirector_move(RED_READY, RED_GONE)) {
            g_ws.closesocket(g_red.listener);
            SetEvent(g_red.done);
            base_log("redirector: early answer too old, polcore asks for itself\r\n");
        }
        return 0;
    }
    if (!redirector_move(RED_READY, RED_TAKEN)) return 0;
    memset(&lo, 0, sizeof lo);
    lo.sin_family = AF_INET;
    lo.sin_port = (unsigned short)(g_red.lport >> 8 | g_red.lport << 8);
    lo.sin_addr.S_un.S_addr = 0x0100007f;
    *rc = real(s, (struct sockaddr *)&lo, sizeof lo);
    err = *rc != 0 ? g_ws.last_error() : 0;
    if (err && err != WSAEWOULDBLOCK) {
        g_ws.closesocket(g_red.listener);
        g_red.listener = INVALID_SOCKET;
        SetEvent(g_red.done);
        /* Winsock only says a socket can connect again after these three. For the rest polcore gets the error, and
           the one it retries on with a new socket (address in use) comes back here and goes to the redirector */
        if (err == WSAECONNREFUSED || err == WSAENETUNREACH || err == WSAETIMEDOUT) {
            base_log("redirector: connect to the early answer failed (%d), polcore asks for itself\r\n", err);
            return 0;
        }
        base_log("redirector: connect to the early answer failed (%d), polcore gets that\r\n", err);
        g_ws.set_last_error(err);
        return 1;
    }
    if (g_ws.getsockname(s, (struct sockaddr *)&me, &len) != 0) me.sin_port = 0;
    ok = redirector_serve(g_red.listener, me.sin_port, g_red.answer, g_red.len, g_red.serve_ms, &served);
    g_red.served = served;
    if (!ok && me.sin_port) {
        /* a reset here would fail polcore's login (it only retries a connect that was refused as address in use), so
           the listener stays up and the thread serves it when it shows */
        g_red.late_port = me.sin_port;
        base_log("redirector: polcore's connection not seen yet, kept waiting for it\r\n");
    } else {
        g_ws.closesocket(g_red.listener);
        base_log("redirector: early answer %s polcore, %d bytes, %ums old\r\n", ok ? "handed to" : "not taken by", g_red.len, age);
    }
    g_red.listener = INVALID_SOCKET;
    SetEvent(g_red.done);
    if (*rc != 0) g_ws.set_last_error(WSAEWOULDBLOCK);
    return 1;
}
