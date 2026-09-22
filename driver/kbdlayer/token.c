/*
 * kbdlayer LicenseToken validation -- ECDSA P-256 + SHA-256 + expiry + machine binding
 *
 * kbdaddid/token.c からのフォーク。検証ロジック自体は変更していない。
 *
 * Token format:
 *   <base64url(payloadJSON)>.<base64url(sig)>
 *
 *   payloadJSON  : {"uid":"...","mid":"<MachineGuid>","exp":<unix_ts>,"ver":1}
 *   sig          : ECDSA P-256 raw r||s (64 bytes) over SHA-256(base64url(payloadJSON))
 *
 * Public key is embedded in s_PubKeyBlob below (BCRYPT_ECCPUBLIC_BLOB, 72 bytes).
 * Private key is held by kbdlayer-license-svc (see KEYS.md -- 1st generation,
 * generated 2026-09-22, distinct from kbdaddid's key pair so the two products'
 * license tokens do not cross-validate).
 *
 * Must be called at PASSIVE_LEVEL.
 */

#include <ntddk.h>
#include <bcrypt.h>
#include "token.h"
#include "..\common\regutil.h"

#if defined(ALLOC_PRAGMA)
#pragma alloc_text(PAGE, ValidateLicenseToken)
#endif

/* ------------------------------------------------------------------ */
/* ECDSA P-256 public key (BCRYPT_ECCPUBLIC_BLOB, 72 bytes)           */
/*                                                                      */
/* Layout:                                                              */
/*   [0..3]  BCRYPT_ECDSA_PUBLIC_P256_MAGIC = 0x31534345 (LE)         */
/*   [4..7]  cbKey = 32 (LE)                                           */
/*   [8..39] X coordinate (big-endian, 32 bytes)                       */
/*   [40..71] Y coordinate (big-endian, 32 bytes)                      */
/*                                                                      */
/* 1st generation key, generated 2026-09-22 (see KEYS.md). Public only --    */
/* the matching private key lives in kbdlayer-license-svc/appsettings.json  */
/* (gitignored, local-only) until TokenSigner.cs's first run DPAPI-protects */
/* it into %ProgramData%\kbdlayer-license\license-svc\signingkey.dat.       */
/* ------------------------------------------------------------------ */
static const UCHAR s_PubKeyBlob[72] = {
    /* magic + cbKey */
    0x45, 0x43, 0x53, 0x31,  0x20, 0x00, 0x00, 0x00,
    /* X */
    0xEF, 0x80, 0x29, 0x95,  0x4D, 0x21, 0x8A, 0xC0,
    0xD4, 0x00, 0x85, 0xFB,  0x94, 0xFF, 0x21, 0xDC,
    0x07, 0xF4, 0x40, 0x4D,  0x9E, 0xA0, 0xED, 0xB0,
    0xC7, 0xC7, 0x1F, 0x11,  0x98, 0x01, 0x1D, 0xFD,
    /* Y */
    0x57, 0xA3, 0xFB, 0x9F,  0x14, 0x02, 0x90, 0x7C,
    0xC9, 0x45, 0xA6, 0x9F,  0x9C, 0xD7, 0x78, 0x1C,
    0xCE, 0x0C, 0xA4, 0x07,  0xFB, 0x5D, 0xC1, 0x77,
    0xA9, 0x68, 0x95, 0x27,  0xAE, 0xEC, 0x65, 0x93
};

/* Buffer size limits */
#define TOK_MAX_SEGMENT  256   /* max chars in each base64url segment */
#define TOK_MAX_JSON     256   /* max bytes in decoded JSON payload   */
#define GUID_BUF_LEN      40   /* "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx\0" */

/* ------------------------------------------------------------------ */
/* Base64URL decoder                                                   */
/* ------------------------------------------------------------------ */

static int B64ValOf(char c)
{
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '-') return 62;
    if (c == '_') return 63;
    return -1;
}

/* Returns decoded byte count, 0 on error.  out[] not null-terminated. */
static ULONG B64UrlDecode(const char* in, ULONG inLen, UCHAR* out, ULONG maxOut)
{
    ULONG outPos = 0;
    ULONG i = 0;

    while (i < inLen) {
        /* skip trailing padding */
        while (i < inLen && in[i] == '=') i++;
        if (i >= inLen) break;

        int v[4] = { -1, -1, -1, -1 };
        ULONG n = 0;
        for (; n < 4 && i < inLen && in[i] != '='; n++, i++) {
            v[n] = B64ValOf(in[i]);
            if (v[n] < 0) return 0;
        }
        if (n < 2) break;

        if (outPos >= maxOut) return 0;
        out[outPos++] = (UCHAR)((v[0] << 2) | (v[1] >> 4));

        if (n >= 3 && v[2] >= 0) {
            if (outPos >= maxOut) return 0;
            out[outPos++] = (UCHAR)((v[1] << 4) | (v[2] >> 2));
        }
        if (n >= 4 && v[3] >= 0) {
            if (outPos >= maxOut) return 0;
            out[outPos++] = (UCHAR)((v[2] << 6) | v[3]);
        }
    }
    return outPos;
}

/* ------------------------------------------------------------------ */
/* Minimal single-level JSON field extractors (compact JSON only)     */
/* ------------------------------------------------------------------ */

/* Finds "key":"value" and copies the value string.  Returns TRUE on success. */
static BOOLEAN JsonGetStr(const char* json, ULONG jLen,
                           const char* key, char* out, ULONG maxOut)
{
    /* Build pattern: "key":" */
    char pat[72];
    ULONG kLen = 0;
    while (key[kLen]) kLen++;
    if (kLen + 4 >= sizeof(pat)) return FALSE;

    pat[0] = '"';
    ULONG p = 1;
    for (ULONG k = 0; k < kLen; k++) pat[p++] = key[k];
    pat[p++] = '"'; pat[p++] = ':'; pat[p++] = '"'; pat[p] = '\0';
    ULONG pLen = p;

    const char* pos = NULL;
    for (ULONG i = 0; i + pLen <= jLen; i++) {
        if (RtlCompareMemory(json + i, pat, pLen) == pLen) {
            pos = json + i + pLen;
            break;
        }
    }
    if (!pos) return FALSE;

    ULONG outLen = 0;
    while (*pos && *pos != '"' && outLen < maxOut - 1)
        out[outLen++] = *pos++;
    if (*pos != '"') return FALSE;
    out[outLen] = '\0';
    return TRUE;
}

/* Finds "key":digits and parses a 64-bit integer.  Returns TRUE on success. */
static BOOLEAN JsonGetI64(const char* json, ULONG jLen,
                           const char* key, LONGLONG* outVal)
{
    char pat[68];
    ULONG kLen = 0;
    while (key[kLen]) kLen++;
    if (kLen + 3 >= sizeof(pat)) return FALSE;

    pat[0] = '"';
    ULONG p = 1;
    for (ULONG k = 0; k < kLen; k++) pat[p++] = key[k];
    pat[p++] = '"'; pat[p++] = ':'; pat[p] = '\0';
    ULONG pLen = p;

    const char* pos = NULL;
    for (ULONG i = 0; i + pLen <= jLen; i++) {
        if (RtlCompareMemory(json + i, pat, pLen) == pLen) {
            pos = json + i + pLen;
            break;
        }
    }
    if (!pos) return FALSE;

    BOOLEAN neg = FALSE;
    if (*pos == '-') { neg = TRUE; pos++; }
    if (*pos < '0' || *pos > '9') return FALSE;

    LONGLONG val = 0;
    while (*pos >= '0' && *pos <= '9') {
        val = val * 10 + (*pos - '0');
        pos++;
    }
    *outVal = neg ? -val : val;
    return TRUE;
}

/* ------------------------------------------------------------------ */
/* SHA-256 via BCrypt one-shot                                         */
/* ------------------------------------------------------------------ */

/*
 BCryptHash (one-shot) is a user-mode-only convenience export; the kernel-mode
 Ksecdd.lib does not export it (LNK2019). Kernel drivers must use the
 multi-step CNG pattern: CreateHash -> HashData -> FinishHash -> DestroyHash.
*/
static BOOLEAN Sha256Of(const UCHAR* data, ULONG dataLen, UCHAR hash[32])
{
    BCRYPT_ALG_HANDLE hAlg = NULL;
    BCRYPT_HASH_HANDLE hHash = NULL;
    PUCHAR hashObject = NULL;
    ULONG cbHashObject = 0, cbResult = 0;
    NTSTATUS st;
    BOOLEAN ok = FALSE;

    st = BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_SHA256_ALGORITHM, NULL, 0);
    if (!NT_SUCCESS(st)) {
        KBDLAYER_TRACE("token: BCryptOpenAlgorithmProvider(SHA256) 0x%08X\n", st);
        return FALSE;
    }

    st = BCryptGetProperty(hAlg, BCRYPT_OBJECT_LENGTH,
                            (PUCHAR)&cbHashObject, sizeof(cbHashObject), &cbResult, 0);
    if (!NT_SUCCESS(st) || cbHashObject == 0) {
        KBDLAYER_TRACE("token: BCryptGetProperty(OBJECT_LENGTH) 0x%08X\n", st);
        goto done;
    }

    hashObject = (PUCHAR)ExAllocatePool2(POOL_FLAG_PAGED, cbHashObject, 'hSkL');
    if (!hashObject) {
        KBDLAYER_TRACE("token: ExAllocatePool2(hashObject) failed\n");
        goto done;
    }

    st = BCryptCreateHash(hAlg, &hHash, hashObject, cbHashObject, NULL, 0, 0);
    if (!NT_SUCCESS(st)) {
        KBDLAYER_TRACE("token: BCryptCreateHash 0x%08X\n", st);
        goto done;
    }

    st = BCryptHashData(hHash, (PUCHAR)data, dataLen, 0);
    if (!NT_SUCCESS(st)) {
        KBDLAYER_TRACE("token: BCryptHashData 0x%08X\n", st);
        goto done;
    }

    st = BCryptFinishHash(hHash, hash, 32, 0);
    if (!NT_SUCCESS(st)) {
        KBDLAYER_TRACE("token: BCryptFinishHash 0x%08X\n", st);
        goto done;
    }

    ok = TRUE;

done:
    if (hHash) BCryptDestroyHash(hHash);
    if (hashObject) ExFreePoolWithTag(hashObject, 'hSkL');
    BCryptCloseAlgorithmProvider(hAlg, 0);
    return ok;
}

/* ------------------------------------------------------------------ */
/* ECDSA P-256 signature verification via BCrypt                       */
/* Signature format: raw r||s, each 32 bytes big-endian (64 bytes)    */
/* ------------------------------------------------------------------ */

static BOOLEAN EcdsaP256Verify(const UCHAR hash[32], const UCHAR sig[64])
{
    BCRYPT_ALG_HANDLE hAlg = NULL;
    BCRYPT_KEY_HANDLE hKey = NULL;
    NTSTATUS st;
    BOOLEAN ok = FALSE;

    st = BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_ECDSA_P256_ALGORITHM, NULL, 0);
    if (!NT_SUCCESS(st)) {
        KBDLAYER_TRACE("token: BCryptOpenAlgorithmProvider(ECDSA_P256) 0x%08X\n", st);
        return FALSE;
    }

    st = BCryptImportKeyPair(hAlg, NULL, BCRYPT_ECCPUBLIC_BLOB,
                              &hKey, (PUCHAR)s_PubKeyBlob, sizeof(s_PubKeyBlob), 0);
    if (!NT_SUCCESS(st)) {
        KBDLAYER_TRACE("token: BCryptImportKeyPair 0x%08X\n", st);
        goto done;
    }

    st = BCryptVerifySignature(hKey, NULL, (PUCHAR)hash, 32, (PUCHAR)sig, 64, 0);
    ok = NT_SUCCESS(st);
    if (!ok) KBDLAYER_TRACE("token: BCryptVerifySignature 0x%08X\n", st);

done:
    if (hKey) BCryptDestroyKey(hKey);
    BCryptCloseAlgorithmProvider(hAlg, 0);
    return ok;
}

/* ------------------------------------------------------------------ */
/* MachineGuid reader                                                  */
/* HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid (REG_SZ)          */
/* ------------------------------------------------------------------ */

static BOOLEAN ReadMachineGuid(WCHAR* buf, ULONG maxChars)
{
    UNICODE_STRING keyName, valName;
    OBJECT_ATTRIBUTES oa;
    HANDLE hKey = NULL;
    NTSTATUS st;
    ULONG cbNeeded = 0;
    PKEY_VALUE_PARTIAL_INFORMATION info = NULL;
    BOOLEAN ok = FALSE;

    RtlInitUnicodeString(&keyName,
        L"\\Registry\\Machine\\SOFTWARE\\Microsoft\\Cryptography");
    RtlInitUnicodeString(&valName, L"MachineGuid");
    InitializeObjectAttributes(&oa, &keyName,
        OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);

    st = ZwOpenKey(&hKey, KEY_READ, &oa);
    if (!NT_SUCCESS(st)) {
        KBDLAYER_TRACE("token: ZwOpenKey(Cryptography) 0x%08X\n", st);
        return FALSE;
    }

    /* Query required size */
    st = ZwQueryValueKey(hKey, &valName, KeyValuePartialInformation,
                          NULL, 0, &cbNeeded);
    if (st != STATUS_BUFFER_TOO_SMALL && !NT_SUCCESS(st)) goto done;

    info = (PKEY_VALUE_PARTIAL_INFORMATION)
           ExAllocatePool2(POOL_FLAG_PAGED, cbNeeded, 'gMkL');
    if (!info) goto done;

    st = ZwQueryValueKey(hKey, &valName, KeyValuePartialInformation,
                          info, cbNeeded, &cbNeeded);
    if (NT_SUCCESS(st) && info->Type == REG_SZ && info->DataLength >= sizeof(WCHAR)) {
        ULONG wchars = info->DataLength / sizeof(WCHAR);
        ULONG copy   = (wchars < maxChars) ? wchars : maxChars - 1;
        RtlCopyMemory(buf, info->Data, copy * sizeof(WCHAR));
        buf[copy] = 0;
        ok = (buf[0] != 0);
    }

done:
    if (info) ExFreePoolWithTag(info, 'gMkL');
    ZwClose(hKey);
    return ok;
}

/* ------------------------------------------------------------------ */
/* ASCII case-insensitive comparison                                   */
/* ------------------------------------------------------------------ */

static BOOLEAN AsciiEqCI(const char* a, const char* b)
{
    while (*a && *b) {
        char ca = (*a >= 'a' && *a <= 'z') ? (char)(*a - 32) : *a;
        char cb = (*b >= 'a' && *b <= 'z') ? (char)(*b - 32) : *b;
        if (ca != cb) return FALSE;
        a++; b++;
    }
    return (*a == '\0') && (*b == '\0');
}

/* ================================================================== */
/* ValidateLicenseToken -- public entry point                          */
/* ================================================================== */

_IRQL_requires_max_(PASSIVE_LEVEL)
BOOLEAN ValidateLicenseToken(_In_ PCWSTR tokenStr)
{
    PAGED_CODE();

    if (!tokenStr || tokenStr[0] == 0) return FALSE;

    /* -- Measure token string length (cap at safety limit) -- */
    ULONG tokLen = 0;
    while (tokenStr[tokLen] != 0 && tokLen < (TOK_MAX_SEGMENT * 2 + 2))
        tokLen++;

    /* -- Find '.' separator -- */
    ULONG dotPos = 0;
    BOOLEAN hasDot = FALSE;
    for (ULONG i = 0; i < tokLen; i++) {
        if (tokenStr[i] == L'.') { dotPos = i; hasDot = TRUE; break; }
    }
    if (!hasDot || dotPos == 0 || dotPos + 2 > tokLen) {
        KBDLAYER_TRACE("token: missing or misplaced '.'\n");
        return FALSE;
    }

    /* -- Narrow-convert payload b64 segment -- */
    char payB64[TOK_MAX_SEGMENT];
    if (dotPos >= TOK_MAX_SEGMENT) return FALSE;
    for (ULONG i = 0; i < dotPos; i++) {
        if (tokenStr[i] > 127) return FALSE;
        payB64[i] = (char)tokenStr[i];
    }
    payB64[dotPos] = '\0';

    /* -- Narrow-convert signature b64 segment -- */
    char sigB64[TOK_MAX_SEGMENT];
    ULONG sigStart = dotPos + 1;
    ULONG sigLen   = tokLen - sigStart;
    if (sigLen >= TOK_MAX_SEGMENT) return FALSE;
    for (ULONG i = 0; i < sigLen; i++) {
        if (tokenStr[sigStart + i] > 127) return FALSE;
        sigB64[i] = (char)tokenStr[sigStart + i];
    }
    sigB64[sigLen] = '\0';

    /* -- Decode 64-byte ECDSA signature -- */
    UCHAR sigBytes[64];
    ULONG sigDecoded = B64UrlDecode(sigB64, sigLen, sigBytes, sizeof(sigBytes));
    if (sigDecoded != 64) {
        KBDLAYER_TRACE("token: sig decoded len %lu != 64\n", sigDecoded);
        return FALSE;
    }

    /* -- SHA-256 hash of the payload b64 string bytes -- */
    UCHAR hash[32];
    if (!Sha256Of((PUCHAR)payB64, dotPos, hash)) return FALSE;

    /* -- ECDSA P-256 verify -- */
    if (!EcdsaP256Verify(hash, sigBytes)) {
        KBDLAYER_TRACE("token: ECDSA failed\n");
        return FALSE;
    }
    KBDLAYER_TRACE("token: ECDSA OK\n");

    /* -- Decode JSON payload -- */
    UCHAR json[TOK_MAX_JSON];
    ULONG jsonLen = B64UrlDecode(payB64, dotPos, json, sizeof(json) - 1);
    if (jsonLen == 0) {
        KBDLAYER_TRACE("token: payload b64 decode failed\n");
        return FALSE;
    }
    json[jsonLen] = '\0';

    /* -- Check expiry -- */
    LONGLONG exp = 0;
    if (!JsonGetI64((char*)json, jsonLen, "exp", &exp)) {
        KBDLAYER_TRACE("token: 'exp' not found\n");
        return FALSE;
    }
    LARGE_INTEGER sysTime;
    KeQuerySystemTime(&sysTime);
    /* FILETIME epoch 1601-01-01 -> Unix epoch 1970-01-01: diff = 11644473600 s */
    LONGLONG unixNow = sysTime.QuadPart / 10000000LL - 11644473600LL;
    if (exp <= unixNow) {
        KBDLAYER_TRACE("token: expired (exp=%I64d now=%I64d)\n", exp, unixNow);
        return FALSE;
    }
    KBDLAYER_TRACE("token: expiry OK\n");

    /* -- Check machine ID -- */
    char midTok[GUID_BUF_LEN];
    if (!JsonGetStr((char*)json, jsonLen, "mid", midTok, sizeof(midTok))) {
        KBDLAYER_TRACE("token: 'mid' not found\n");
        return FALSE;
    }

    WCHAR guidW[GUID_BUF_LEN];
    if (!ReadMachineGuid(guidW, GUID_BUF_LEN)) {
        KBDLAYER_TRACE("token: ReadMachineGuid failed\n");
        return FALSE;
    }

    /* narrow-convert the registry GUID for comparison */
    char guidA[GUID_BUF_LEN] = {0};
    for (ULONG i = 0; i < GUID_BUF_LEN - 1 && guidW[i]; i++)
        guidA[i] = (char)guidW[i];

    if (!AsciiEqCI(midTok, guidA)) {
        KBDLAYER_TRACE("token: machine ID mismatch\n");
        return FALSE;
    }
    KBDLAYER_TRACE("token: machine ID OK\n");

    return TRUE;
}
