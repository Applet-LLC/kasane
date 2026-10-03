#ifndef _KBDLAYER_TOKEN_H_
#define _KBDLAYER_TOKEN_H_

#include <ntddk.h>

/*
 KBDLAYER_TRACE: DBGビルドのみ有効なデバッグトレース。
 Driver.c / token.cの両方からインクルードされるためここで定義する。
*/
#if DBG
#define KBDLAYER_TRACE(fmt, ...) \
    DbgPrintEx(DPFLTR_DEFAULT_ID, DPFLTR_ERROR_LEVEL, "kbdlayer: " fmt, ##__VA_ARGS__)
#else
#define KBDLAYER_TRACE(fmt, ...) ((void)0)
#endif

/*
 ValidateLicenseToken
 - HKLM\...\kbdlayer\Parameters\LicenseToken (REG_SZ) を受け取りECDSA P-256で検証する。
 - トークン形式: <base64url(payloadJSON)>.<base64url(sig_r_s_64bytes)>
   payloadJSON: {"uid":"...","mid":"<MachineGuid>","exp":<unix_ts>,"ver":1}
   署名対象: SHA-256(base64url(payloadJSON) のASCIIバイト列)
 - 公開鍵はtoken.cに埋め込み済み。秘密鍵はkasane-license-svcが保持する。
 - kbdaddid/token.hからのフォーク。検証ロジック自体は変更していない。
   埋め込み公開鍵はkbdaddidとは別の新規ECDSA鍵ペア（1st generation、
   2026-09-22生成、KEYS.md参照）で、同じ鍵を使い回していない
   （使い回すと、kbdaddidが発行したトークンがkbdlayer側でも有効になってしまい、
   別製品としてのライセンス分離が壊れる）。
 - 呼び出しはPASSIVE_LEVELを前提とする（BCrypt要件）。
 - 戻り値: TRUE=有効, FALSE=無効/検証失敗
*/
_IRQL_requires_max_(PASSIVE_LEVEL)
BOOLEAN
ValidateLicenseToken(
    _In_ PCWSTR tokenStr
);

#endif /* _KBDLAYER_TOKEN_H_ */
