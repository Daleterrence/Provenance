/* polcore's own SHA-1 (polcore.dll 0x1002d6a0/d6d0/dac0), the exact hash the wire uses. 0x5c-byte context. */
static void cred_sha1(BYTE *b, const void *data, unsigned len, BYTE out[20])
{
    BYTE ctx[0x80];
    pc_sha1_init(b, ctx);
    pc_sha1_update(b, ctx, data, len);
    pc_sha1_final(b, out, ctx);
    SecureZeroMemory(ctx, sizeof ctx);
}

/* NULL when it's missing, too long or won't convert */
static const WCHAR *cred_target(const char *env, WCHAR *buf, int n)
{
    char a[256];
    DWORD k = GetEnvironmentVariableA(env, a, sizeof a);
    if (k == 0) return NULL;
    if (k >= sizeof a || !MultiByteToWideChar(CP_ACP, MB_ERR_INVALID_CHARS, a, -1, buf, n)) {
        base_log("login: %s target refused (too long or not convertible)\r\n", env);
        return NULL;
    }
    return buf;
}
static int cred_launch_target(const WCHAR *t)
{
    static const WCHAR pre[] = L"Provenance:LAUNCH:";
    if (!t) return 0;
    for (int i = 0; pre[i]; i++)
        if (t[i] != pre[i]) return 0;
    return 1;
}
static const char *cred_keyfile(char *buf, DWORD n)
{
    static int warned;
    DWORD k = GetEnvironmentVariableA(ENV_CRED_KEYFILE, buf, n);
    if (k > 0 && k < n) return buf;
    if (!warned) {
        warned = 1;
        base_log("login: POL_CRED_KEYFILE empty or too long - no key file, no saved entry is read\r\n");
    }
    return NULL;
}
/* only wrapped secrets, any program of the user can write a plain entry */
static int cred_read(const WCHAR *target, char *user, int ucap, char *secret, int scap)
{
    char kf[MAX_PATH + 64];
    return pc_cred_read(target, cred_keyfile(kf, sizeof kf), user, ucap, secret, scap);
}
static const char *cred_why(int rc)
{
    static const char *const why[] = { "unknown", "not found", "not wrapped", "unwrap failed", "too long" };
    return rc >= PC_NOT_FOUND && rc <= PC_TOO_LONG ? why[rc] : why[0];
}
/* the name the screens use for a credential kind */
static const char *cred_kind_name(const char *kind)
{
    return kind[0] == 'P' ? "PlayOnline ID" : "Square Enix ID";
}
static void cred_consume(const WCHAR *target, const char *kind)
{
    char kinds[64];
    DWORD k = GetEnvironmentVariableA(ENV_CRED_ONESHOT, kinds, sizeof kinds);
    if (k == 0 || k >= sizeof kinds || !strstr(kinds, kind)) return;
    if (!cred_launch_target(target)) {
        base_log("login: one-shot %s target is not a launch entry - left in place\r\n", cred_kind_name(kind));
        return;
    }
    if (!CredDeleteW(target, CRED_TYPE_GENERIC, 0))
        base_log("login: one-shot target '%ls' delete failed err=%u\r\n", target, GetLastError());
}
/* a failure ends pol.exe (exit 11) so Provenance asks for the details again */
static void cred_read_details(const char *env, const char *kind, char *id, int idcap, char *pw, int pwcap)
{
    WCHAR tbuf[256];
    int rc = PC_OK;
    const WCHAR *t = cred_target(env, tbuf, 256);
    if (t) {
        rc = cred_read(t, id, idcap, pw, pwcap);
        if (rc != PC_NOT_FOUND) cred_consume(t, kind);
    }
    if (!t) base_log("login: no %s target - pol.exe ends\r\n", cred_kind_name(kind));
    else if (rc == PC_NOT_FOUND) base_log("login: no saved entry in Credential Manager for '%ls' - pol.exe ends\r\n", t);
    else if (rc != PC_OK) base_log("login: Credential Manager entry '%ls' %s - pol.exe ends\r\n", t, cred_why(rc));
    else if (id[0] == 0 || pw[0] == 0) {
        SecureZeroMemory(id, idcap);
        SecureZeroMemory(pw, pwcap);
        base_log("login: bad %s entry (ID or password empty) - pol.exe ends\r\n", cred_kind_name(kind));
    } else return;
    base_log(LOG_DETAILS_UNREADABLE " (%s) - exiting, the game wasn't started\r\n", cred_kind_name(kind));
    base_end_pol(DETAILS_UNREADABLE_EXIT);
}
/* account IDs in the log: first 2 chars + length only (the log stays on disk) */
static const char *cred_mask_id(const char *id, char *out)
{
    out[0] = id && id[0] ? id[0] : '?';
    out[1] = id && id[0] && id[1] ? id[1] : '?';
    wsprintfA(out + 2, "***(%d)", id ? lstrlenA(id) : 0);
    return out;
}
/* secret = SHA1(hex_lower(SHA1(password)) || "playonline") like the viewer (app.dll 0x1019ab29), into
   polcore.dll 0x1001e4b0 (svc+0x1778) with mode 1 (no OTP) or 2 (OTP) */
static void seed_account_login(BYTE *b)
{
    char seid[32] = {0}, sepw[64] = {0}, hex[41], otp[8] = {0}, m[24];
    BYTE h1[20], secret[20], material[64];
    WCHAR obuf[256];
    int mode = 1, orc;
    cred_read_details(ENV_CRED_SEA, "SEA", seid, sizeof seid, sepw, sizeof sepw);
    cred_sha1(b, sepw, lstrlenA(sepw), h1);
    for (int i = 0; i < 20; i++) wsprintfA(hex + i * 2, "%02x", h1[i]);
    lstrcpyA((char *)material, hex);
    lstrcatA((char *)material, "playonline");
    cred_sha1(b, material, 50, secret);
    SecureZeroMemory(sepw, sizeof sepw);
    SecureZeroMemory(material, sizeof material);
    SecureZeroMemory(h1, sizeof h1);
    SecureZeroMemory(hex, sizeof hex);
    const WCHAR *totp = cred_target(ENV_CRED_OTP, obuf, 256);
    orc = totp ? cred_read(totp, NULL, 0, otp, sizeof otp) : PC_NOT_FOUND;
    if (orc != PC_NOT_FOUND) cred_consume(totp, "OTP");
    if (orc != PC_OK && orc != PC_NOT_FOUND)
        base_log("login: Credential Manager entry '%ls' %s - no one-time password\r\n", totp, cred_why(orc));
    if (orc == PC_OK) {
        int ok = lstrlenA(otp) == 6;
        for (int k = 0; ok && k < 6; k++)
            if (otp[k] < '0' || otp[k] > '9') ok = 0;
        if (ok) mode = 2;
        else { base_log("login: one-time password malformed (needs 6 digits) - ignoring\r\n"); otp[0] = 0; }
    }
    pc_set_sea_secret(b, mode, seid, secret, mode == 2 ? otp : NULL);
    SecureZeroMemory(otp, sizeof otp);
    SecureZeroMemory(secret, sizeof secret);
    base_log("login: Square Enix account set, mode %d (%s), Square Enix ID='%s'\r\n", mode,
        mode == 2 ? "one-time password" : "no one-time password",
        cred_mask_id(seid, m));
    SecureZeroMemory(seid, sizeof seid);
}

/* Play is the one-shot POL_CRED_PLAY entry, user "play", wrapped so nothing else can forge it */
static int cred_play_signalled(void)
{
    static WCHAR tbuf[256];
    static const WCHAR *t;
    static int resolved, warned;
    char user[64], go[64];
    if (!resolved) {
        t = cred_target(ENV_CRED_PLAY, tbuf, 256);
        resolved = 1;
    }
    if (!t) return 0;
    int rc = cred_read(t, user, sizeof user, go, sizeof go);
    SecureZeroMemory(go, sizeof go);
    if (rc == PC_OK && strcmp(user, "play") == 0) {
        if (cred_launch_target(t)) CredDeleteW(t, CRED_TYPE_GENERIC, 0);
        return 1;
    }
    if (rc == PC_NOT_FOUND) {
        warned = 0;
    } else if (rc != PC_OK && !warned) {
        warned = 1;   /* once, Provenance takes this line as a failed start */
        base_log(LOG_PLAY_UNREADABLE " (%s) - ignored\r\n", cred_why(rc));
    }
    return 0;
}
