/* polcore never sets TCP_NODELAY, so each request body waits for the server to ACK its header */
static int (PASCAL *g_polcore_connect)(SOCKET, const struct sockaddr *, int);

static int PASCAL polcore_connect(SOCKET s, const struct sockaddr *a, int n)
{
    int rc, on = 1;
    g_ws.setsockopt(s, IPPROTO_TCP, TCP_NODELAY, (const char *)&on, sizeof on);
    if (redirector_aimed(a, n) && redirector_connect(s, g_polcore_connect, &rc)) return rc;
    return g_polcore_connect(s, a, n);
}

static int polcore_sockets_hook(BYTE *image)
{
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)image;
    IMAGE_NT_HEADERS32 *nt;
    IMAGE_DATA_DIRECTORY dir;
    IMAGE_IMPORT_DESCRIPTOR *d;
    DWORD size;
    int hits = 0;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 || dos->e_lfanew > 0x400) return 0;
    nt = (IMAGE_NT_HEADERS32 *)(image + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC) return 0;
    size = nt->OptionalHeader.SizeOfImage;
    dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress || dir.VirtualAddress >= size || dir.Size > size - dir.VirtualAddress) return 0;
    for (d = (IMAGE_IMPORT_DESCRIPTOR *)(image + dir.VirtualAddress);
         (BYTE *)(d + 1) <= image + dir.VirtualAddress + dir.Size && d->Name && d->FirstThunk && d->Name < size &&
         d->FirstThunk < size;
         d++) {
        const char *dll = (const char *)(image + d->Name);
        void **slot = (void **)(image + d->FirstThunk), *want;
        HMODULE m;
        int k;
        if (lstrcmpiA(dll, "ws2_32.dll") && lstrcmpiA(dll, "wsock32.dll")) continue;
        m = GetModuleHandleA(dll);
        want = m ? (void *)GetProcAddress(m, "connect") : NULL;
        if (!want) continue;
        for (k = 0; k < 4096 && (BYTE *)(slot + 1) <= image + size && *slot; k++, slot++) {
            DWORD old;
            if (*slot != want || (g_polcore_connect && (void *)g_polcore_connect != want)) continue;
            if (!VirtualProtect(slot, sizeof *slot, PAGE_READWRITE, &old)) continue;
            g_polcore_connect = (int (PASCAL *)(SOCKET, const struct sockaddr *, int))want;
            *slot = (void *)polcore_connect;
            VirtualProtect(slot, sizeof *slot, old, &old);
            hits++;
        }
    }
    return hits;
}

static void polcore_sockets_patch(BYTE *pc)
{
    if (g_polcore_connect) return;
    if (!redirector_ws_load()) {
        base_log("sockets: no ws2_32, Nagle left on\r\n");
        return;
    }
    if (polcore_sockets_hook(pc)) base_log("sockets: Nagle off on polcore's connections\r\n");
    else base_log("sockets: no connect slot in polcore, Nagle left on\r\n");
}
