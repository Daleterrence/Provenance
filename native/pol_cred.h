/* pol_cred.h - reads a Provenance credential from Credential Manager and unwraps it. Static only, so more than one
 * file can include it (link advapi32.lib and crypt32.lib). */
#ifndef POL_CRED_H
#define POL_CRED_H

#include <windows.h>
#include <wincrypt.h>
#include <wincred.h>
#include <string.h>

#define PC_WRAP_KEY_LEN 32

#define PC_OK            0
#define PC_NOT_FOUND     1
#define PC_NOT_WRAPPED   2   /* a plain blob, never read */
#define PC_UNWRAP_FAILED 3   /* key file missing or not 32 bytes, or DPAPI refused (wrong key, other target) */
#define PC_TOO_LONG      4

/* the admin-only key file, 0 when it's missing or not exactly 32 bytes (Provenance replaces any other size) */
static int pc_read_key(const char *keyfile, BYTE key[PC_WRAP_KEY_LEN])
{
    DWORD got = 0;
    LARGE_INTEGER size;
    if (!keyfile) return 0;
    HANDLE h = CreateFileA(keyfile, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return 0;
    int ok = GetFileSizeEx(h, &size) && size.QuadPart == PC_WRAP_KEY_LEN &&
             ReadFile(h, key, PC_WRAP_KEY_LEN, &got, NULL) && got == PC_WRAP_KEY_LEN;
    CloseHandle(h);
    if (!ok) SecureZeroMemory(key, PC_WRAP_KEY_LEN);
    return ok;
}

/* 0 ok, 1 too long. exact turns best fit off, so a fullwidth look-alike user name can't pass for an ASCII one. */
static int pc_narrow(const WCHAR *w, int n, char *out, int cap, int exact)
{
    int need;
    /* UTF-8 has no best fit and refuses the flag */
    DWORD flags = exact && GetACP() != CP_UTF8 ? WC_NO_BEST_FIT_CHARS : 0;
    if (cap <= 0) return 1;
    out[0] = 0;
    if (n <= 0) return 0;
    need = WideCharToMultiByte(CP_ACP, flags, w, n, NULL, 0, NULL, NULL);
    if (need <= 0 || need >= cap) return 1;
    if (WideCharToMultiByte(CP_ACP, flags, w, n, out, cap - 1, NULL, NULL) != need) {
        SecureZeroMemory(out, cap);
        return 1;
    }
    out[need] = 0;
    return 0;
}

/* key + UTF-16LE target name, so a blob moved to another target won't unwrap. The caller wipes and frees it. */
static BYTE *pc_entropy(const BYTE key[PC_WRAP_KEY_LEN], const WCHAR *target, DWORD *len)
{
    int tn = lstrlenW(target);
    if (tn <= 0 || tn > CRED_MAX_GENERIC_TARGET_NAME_LENGTH) return NULL;
    *len = PC_WRAP_KEY_LEN + (DWORD)tn * sizeof(WCHAR);
    BYTE *e = (BYTE *)LocalAlloc(LMEM_FIXED, *len);
    if (!e) return NULL;
    memcpy(e, key, PC_WRAP_KEY_LEN);
    memcpy(e + PC_WRAP_KEY_LEN, target, (size_t)tn * sizeof(WCHAR));
    return e;
}

/* user and secret can be NULL. Any error but PC_NOT_FOUND wipes both. */
static int pc_cred_read(const WCHAR *target, const char *keyfile, char *user, int ucap, char *secret, int scap)
{
    PCREDENTIALW cred = NULL;
    BYTE key[PC_WRAP_KEY_LEN];
    DATA_BLOB in, ent = { 0, NULL }, out = { 0, NULL };
    const WCHAR *w = NULL;
    int wn = 0, rc = PC_OK;

    if (user && ucap > 0) user[0] = 0;
    if (secret && scap > 0) secret[0] = 0;

    if (!CredReadW(target, CRED_TYPE_GENERIC, 0, &cred) || !cred)
        return PC_NOT_FOUND;

    /* wrapped = "PVW1" + CryptProtectData of the UTF-16LE secret (the app's WindowsCredentialStore) */
    if (cred->CredentialBlobSize <= 4 || !cred->CredentialBlob || memcmp(cred->CredentialBlob, "PVW1", 4) != 0) {
        rc = PC_NOT_WRAPPED;
        goto done;
    }
    if (!pc_read_key(keyfile, key)) { rc = PC_UNWRAP_FAILED; goto done; }
    ent.pbData = pc_entropy(key, target, &ent.cbData);
    if (!ent.pbData) { ent.cbData = 0; rc = PC_UNWRAP_FAILED; goto done; }
    in.cbData = cred->CredentialBlobSize - 4;
    in.pbData = cred->CredentialBlob + 4;
    if (!CryptUnprotectData(&in, NULL, &ent, NULL, NULL, CRYPTPROTECT_UI_FORBIDDEN, &out) || !out.pbData) {
        out.pbData = NULL; out.cbData = 0;
        rc = PC_UNWRAP_FAILED;
        goto done;
    }
    w = (const WCHAR *)out.pbData;
    wn = (int)(out.cbData / sizeof(WCHAR));

    if (user && ucap > 0) {
        int un = cred->UserName ? lstrlenW(cred->UserName) : 0;
        if (pc_narrow(cred->UserName, un, user, ucap, 1)) { rc = PC_TOO_LONG; goto done; }
    }
    if (secret && scap > 0) {
        if (pc_narrow(w, wn, secret, scap, 0)) { rc = PC_TOO_LONG; goto done; }
    }

done:
    if (out.pbData) {
        SecureZeroMemory(out.pbData, out.cbData);
        LocalFree(out.pbData);
    }
    SecureZeroMemory(key, sizeof(key));
    if (ent.pbData) {
        SecureZeroMemory(ent.pbData, ent.cbData);
        LocalFree(ent.pbData);
    }
    if (cred->CredentialBlob && cred->CredentialBlobSize)
        SecureZeroMemory(cred->CredentialBlob, cred->CredentialBlobSize);
    CredFree(cred);
    if (rc != PC_OK) {
        if (user && ucap > 0) SecureZeroMemory(user, ucap);
        if (secret && scap > 0) SecureZeroMemory(secret, scap);
    }
    return rc;
}

#endif /* POL_CRED_H */
