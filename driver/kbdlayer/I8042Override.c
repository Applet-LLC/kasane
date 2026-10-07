// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT
//
// i8042prt\Parameters の書き換え／初期化（キーボードの接続時）。
//
// 設定は Services\kbdlayer\Parameters\I8042Override に、KBDLAYER_I8042_CONFIG全体を
// REG_BINARYとして1つだけ保存する（全体置換。ZwSetValueKeyは値単位で原子的なので、
// AddDeviceの読み出しとSETが競合しても、新旧どちらかの完全な設定が読める）。
// 反映は AddDevice の中（PASSIVE_LEVEL、port driverがStartされる前）で行う。

#include <ntddk.h>
#include <wdm.h>
#include <ntstrsafe.h>
#include "I8042Override.h"
#include "..\\common\\regutil.h"

#define I8042_POOL_TAG 'o24K'

#define I8042_SERVICE    L"i8042prt"
#define I8042_CFG_NAME   L"I8042Override"

#define I8042_VAL_TYPE       L"OverrideKeyboardType"
#define I8042_VAL_SUBTYPE    L"OverrideKeyboardSubtype"
#define I8042_VAL_IDENTIFIER L"OverrideKeyboardIdentifier"
#define I8042_VAL_LAYER_JPN  L"LayerDriver JPN"
#define I8042_VAL_LAYER_KOR  L"LayerDriver KOR"

// DWORD値が無いことを表す番兵。Type/SubtypeはSETで0xFFFF以下に制限するので衝突しない。
#define I8042_DWORD_ABSENT 0xFFFFFFFFUL

// AddDeviceは別のキーボードの分が並行して走りうる。i8042prtのParametersは全キーボードで1つなので、
// 「5値の書き換え／削除」を1台ずつ直列化する（途中で別の台の処理が混ざって、2台分の値が
// 入り混じった状態を残さないため）。KMUTEXはPASSIVE_LEVELのまま取れる（レジストリI/Oを保持中に行える）。
static KMUTEX g_I8042Mutex;

VOID
InitializeI8042Override(
    VOID
    )
{
    KeInitializeMutex(&g_I8042Mutex, 0);
}

#if defined(ALLOC_PRAGMA)
#pragma alloc_text(PAGE, ApplyI8042OverrideForDevice)
#pragma alloc_text(PAGE, GetI8042OverrideConfig)
#pragma alloc_text(PAGE, SetI8042OverrideConfig)
#endif

_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
LogI8042Failure(_In_ PCSTR What, _In_ PCWSTR ValueName, _In_ NTSTATUS Status)
{
    // D-11: Release版でも見えるようにする（KBDLAYER_TRACEはDBG専用）。
    DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL,
               "kbdlayer: i8042 override: %s '%ws' failed, status=0x%08X\n", What, ValueName, Status);
}

// 値を削除する（無ければ成功）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
DeleteI8042Value(_In_ PCWSTR ValueName)
{
    NTSTATUS status = RegWriteBinary(I8042_SERVICE, NULL, ValueName, NULL, 0);
    if (!NT_SUCCESS(status)) {
        LogI8042Failure("delete", ValueName, status);
    }
}

// Present なら値を書く（現在値と同じなら書かない）。無ければ削除する。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
ApplyI8042Dword(_In_ PCWSTR ValueName, _In_ BOOLEAN Present, _In_ ULONG Value)
{
    ULONG current = I8042_DWORD_ABSENT;
    NTSTATUS status;

    if (!Present) {
        DeleteI8042Value(ValueName);
        return;
    }
    RegReadDwordEx(I8042_SERVICE, NULL, ValueName, &current, I8042_DWORD_ABSENT);
    if (current == Value) {
        return;
    }
    status = RegWriteDword(I8042_SERVICE, NULL, ValueName, Value);
    if (!NT_SUCCESS(status)) {
        LogI8042Failure("write", ValueName, status);
    }
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
ApplyI8042String(_In_ PCWSTR ValueName, _In_ BOOLEAN Present, _In_reads_(KBDLAYER_I8042_STR_CHARS) const WCHAR* Value)
{
    WCHAR current[KBDLAYER_I8042_STR_CHARS + 1];
    NTSTATUS status;

    if (!Present) {
        DeleteI8042Value(ValueName);
        return;
    }
    // 空文字列は書かない（SETが拒否する）ので、current[0]==0は「値が無い／別の型」を表す。
    RtlZeroMemory(current, sizeof(current));
    RegReadServiceString(I8042_SERVICE, ValueName, current, RTL_NUMBER_OF(current));
    if (wcsncmp(current, Value, KBDLAYER_I8042_STR_CHARS) == 0) {
        return;
    }
    status = RegWriteString(I8042_SERVICE, NULL, ValueName, Value);
    if (!NT_SUCCESS(status)) {
        LogI8042Failure("write", ValueName, status);
    }
}

// 保存済みの設定を読む。無い・サイズ違い・壊れている場合は FALSE（Configは全ゼロ）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static BOOLEAN
LoadI8042Config(_Out_ PKBDLAYER_I8042_CONFIG Config)
{
    ULONG actual = 0;
    NTSTATUS status;

    RtlZeroMemory(Config, sizeof(*Config));
    status = RegReadBinary(L"kbdlayer", NULL, I8042_CFG_NAME, Config, sizeof(*Config), &actual);
    if (!NT_SUCCESS(status) || actual != sizeof(*Config) ||
        Config->ProfileCount > KBDLAYER_I8042_MAX_PROFILES) {
        RtlZeroMemory(Config, sizeof(*Config));
        return FALSE;
    }
    return TRUE;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
ApplyI8042OverrideForDevice(
    _In_ ULONG ModelKey
    )
{
    PKBDLAYER_I8042_CONFIG cfg;
    const KBDLAYER_I8042_PROFILE* profile = NULL;
    ULONG i = 0;

    PAGED_CODE();

    // ModelKey=0（HardwareIdを取得できなかった）は、どのプロファイルとも照合できない。
    // 「未指定」として値を消すと、指定済みのキーボード用の設定を誤って壊すので何もしない。
    if (ModelKey == 0) {
        return;
    }

    // 約3.4KBあるのでスタックに置かない。
    cfg = (PKBDLAYER_I8042_CONFIG)ExAllocatePool2(POOL_FLAG_PAGED, sizeof(*cfg), I8042_POOL_TAG);
    if (cfg == NULL) {
        return;
    }

    KeWaitForSingleObject(&g_I8042Mutex, Executive, KernelMode, FALSE, NULL);
    if (LoadI8042Config(cfg) && cfg->Enabled != 0) {
        for (i = 0; i < cfg->ProfileCount; ++i) {
            if (cfg->Profiles[i].ModelKey == ModelKey) {
                profile = &cfg->Profiles[i];
                break;
            }
        }

        if (profile != NULL) {
            const ULONG f = profile->Flags;
            ApplyI8042Dword(I8042_VAL_TYPE, (BOOLEAN)((f & KBDLAYER_I8042_HAS_TYPE) != 0), profile->Type);
            ApplyI8042Dword(I8042_VAL_SUBTYPE, (BOOLEAN)((f & KBDLAYER_I8042_HAS_SUBTYPE) != 0), profile->Subtype);
            ApplyI8042String(I8042_VAL_IDENTIFIER, (BOOLEAN)((f & KBDLAYER_I8042_HAS_IDENTIFIER) != 0), profile->Identifier);
            ApplyI8042String(I8042_VAL_LAYER_JPN, (BOOLEAN)((f & KBDLAYER_I8042_HAS_LAYER_JPN) != 0), profile->LayerJpn);
            ApplyI8042String(I8042_VAL_LAYER_KOR, (BOOLEAN)((f & KBDLAYER_I8042_HAS_LAYER_KOR) != 0), profile->LayerKor);
        } else {
            // 未指定のキーボード: 5値をすべて削除する。
            DeleteI8042Value(I8042_VAL_TYPE);
            DeleteI8042Value(I8042_VAL_SUBTYPE);
            DeleteI8042Value(I8042_VAL_IDENTIFIER);
            DeleteI8042Value(I8042_VAL_LAYER_JPN);
            DeleteI8042Value(I8042_VAL_LAYER_KOR);
        }
    }
    KeReleaseMutex(&g_I8042Mutex, FALSE);

    ExFreePoolWithTag(cfg, I8042_POOL_TAG);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
GetI8042OverrideConfig(
    _Out_ PKBDLAYER_I8042_CONFIG OutConfig
    )
{
    PAGED_CODE();
    LoadI8042Config(OutConfig); // 失敗時は全ゼロ（Enabled=0, ProfileCount=0）
}

// 文字列が有効か。配列内でNUL終端され、1文字以上、印字可能なASCIIのみで、
// \ / : * ? " < > | と ".." を含まない（レイアウトDLL名・識別子であってパスではない）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static BOOLEAN
IsValidI8042String(_In_reads_(KBDLAYER_I8042_STR_CHARS) const WCHAR* Str)
{
    ULONG i;
    BOOLEAN terminated = FALSE;

    for (i = 0; i < KBDLAYER_I8042_STR_CHARS; ++i) {
        WCHAR c = Str[i];
        if (c == 0) {
            terminated = TRUE;
            break;
        }
        if (c < 0x21 || c > 0x7E) {
            return FALSE;
        }
        if (c == L'\\' || c == L'/' || c == L':' || c == L'*' || c == L'?' ||
            c == L'"' || c == L'<' || c == L'>' || c == L'|') {
            return FALSE;
        }
        if (c == L'.' && i + 1 < KBDLAYER_I8042_STR_CHARS && Str[i + 1] == L'.') {
            return FALSE;
        }
    }
    return (BOOLEAN)(terminated && i > 0);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static BOOLEAN
IsValidI8042Config(_In_ const KBDLAYER_I8042_CONFIG* Config)
{
    ULONG i;
    ULONG j;

    if (Config->Enabled > 1 || Config->ProfileCount > KBDLAYER_I8042_MAX_PROFILES) {
        return FALSE;
    }
    for (i = 0; i < Config->ProfileCount; ++i) {
        const KBDLAYER_I8042_PROFILE* p = &Config->Profiles[i];

        if (p->ModelKey == 0 || (p->Flags & ~KBDLAYER_I8042_HAS_ALL) != 0) {
            return FALSE;
        }
        for (j = 0; j < i; ++j) {
            if (Config->Profiles[j].ModelKey == p->ModelKey) {
                return FALSE; // 重複
            }
        }
        if ((p->Flags & KBDLAYER_I8042_HAS_TYPE) && p->Type > 0xFFFF) {
            return FALSE;
        }
        if ((p->Flags & KBDLAYER_I8042_HAS_SUBTYPE) && p->Subtype > 0xFFFF) {
            return FALSE;
        }
        if ((p->Flags & KBDLAYER_I8042_HAS_IDENTIFIER) && !IsValidI8042String(p->Identifier)) {
            return FALSE;
        }
        if ((p->Flags & KBDLAYER_I8042_HAS_LAYER_JPN) && !IsValidI8042String(p->LayerJpn)) {
            return FALSE;
        }
        if ((p->Flags & KBDLAYER_I8042_HAS_LAYER_KOR) && !IsValidI8042String(p->LayerKor)) {
            return FALSE;
        }
    }
    return TRUE;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetI8042OverrideConfig(
    _In_ const KBDLAYER_I8042_CONFIG* Config
    )
{
    PKBDLAYER_I8042_CONFIG copy;
    NTSTATUS status;
    ULONG i = 0;

    PAGED_CODE();

    if (!IsValidI8042Config(Config)) {
        return STATUS_INVALID_PARAMETER;
    }

    // 保存する形を一意にする: 使っていないスロットと、Flagsに無い値は0にする
    // （GETで返す値が、前回の入力の残りで変わらないように）。
    copy = (PKBDLAYER_I8042_CONFIG)ExAllocatePool2(POOL_FLAG_PAGED, sizeof(*copy), I8042_POOL_TAG);
    if (copy == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    copy->Enabled = Config->Enabled;
    copy->ProfileCount = Config->ProfileCount;
    for (i = 0; i < Config->ProfileCount; ++i) {
        const KBDLAYER_I8042_PROFILE* src = &Config->Profiles[i];
        PKBDLAYER_I8042_PROFILE dst = &copy->Profiles[i];

        dst->ModelKey = src->ModelKey;
        dst->Flags = src->Flags;
        if (src->Flags & KBDLAYER_I8042_HAS_TYPE) {
            dst->Type = src->Type;
        }
        if (src->Flags & KBDLAYER_I8042_HAS_SUBTYPE) {
            dst->Subtype = src->Subtype;
        }
        if (src->Flags & KBDLAYER_I8042_HAS_IDENTIFIER) {
            RtlCopyMemory(dst->Identifier, src->Identifier, sizeof(dst->Identifier));
        }
        if (src->Flags & KBDLAYER_I8042_HAS_LAYER_JPN) {
            RtlCopyMemory(dst->LayerJpn, src->LayerJpn, sizeof(dst->LayerJpn));
        }
        if (src->Flags & KBDLAYER_I8042_HAS_LAYER_KOR) {
            RtlCopyMemory(dst->LayerKor, src->LayerKor, sizeof(dst->LayerKor));
        }
    }

    status = RegWriteBinary(L"kbdlayer", NULL, I8042_CFG_NAME, copy, sizeof(*copy));
    if (!NT_SUCCESS(status)) {
        LogI8042Failure("persist", I8042_CFG_NAME, status);
    }
    ExFreePoolWithTag(copy, I8042_POOL_TAG);
    return status;
}
