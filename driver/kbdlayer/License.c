// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: Apache-2.0

/*
  kbdlayer/License.c

  ライセンス状態の管理と再検証。設計は License.h を参照。
*/

#include <ntddk.h>
#include <wdm.h>
#include "..\\common\\regutil.h"
#include "..\\common\\kbdlayer_ioctl.h"
#include "DeviceExtension.h"
#include "LayerEngine.h"
#include "License.h"
#include "token.h"

#define KBDLAYER_LICENSE_SERVICE_NAME      L"kbdlayer"
#define KBDLAYER_LICENSE_TOKEN_VALUE       L"LicenseToken"
#define KBDLAYER_LICENSE_LASTSTATE_VALUE   L"LicenseLastState" // 1 = 前回はVALIDだった。ドライバだけが書く
#define KBDLAYER_LICENSE_TOKEN_CHARS       512

#ifndef KBDLAYER_PRO

#define KBDLAYER_LICENSE_RETRY_INTERVAL_MS   5000ULL                // 再試行の間隔
#define KBDLAYER_LICENSE_PROVISIONAL_100NS   (300ULL * 10000000ULL) // 暫定有効の期間（5分）
#define KBDLAYER_LICENSE_MAX_TARGETS         64                     // 一括中止で同時に扱うキーボードの上限

volatile LONG g_LicenseState = (LONG)KBDLAYER_LICENSE_STATE_INVALID;
volatile LONGLONG g_LicenseProvisionalDeadline = 0;

// 再検証の契機。
typedef enum _LICENSE_TRIGGER {
    LicenseTriggerRetry,      // ドライバ自身の再試行（タイマー）
    LicenseTriggerSoft,       // KBDLAYER_REVALIDATE_SOFT
    LicenseTriggerDefinitive, // KBDLAYER_REVALIDATE_DEFINITIVE
} LICENSE_TRIGGER;

// 状態の更新と、LicenseLastStateの書き込みを直列化する（PASSIVE_LEVELのみ）。
// FAST_MUTEXはAPC_LEVELに上がり、Zw*のレジストリ呼び出しの前提を崩すのでKMUTEXにする。
static KMUTEX g_LicenseMutex;
static ULONG g_LicensePersistedLastState = 0; // 直近で読んだ／書いたLicenseLastState

static KTIMER g_LicenseRetryTimer;
static KDPC g_LicenseRetryDpc;
static PIO_WORKITEM g_LicenseWorkItem = NULL;
static volatile LONG g_LicenseWorkInFlight = 0;
static volatile LONG g_LicenseShutdown = 0;

static KDEFERRED_ROUTINE LicenseRetryDpcRoutine;
static IO_WORKITEM_ROUTINE LicenseRetryWorkRoutine;

#ifdef ALLOC_PRAGMA
#pragma alloc_text(PAGE, InitializeLicense)
#pragma alloc_text(PAGE, TeardownLicense)
#pragma alloc_text(PAGE, LicenseRevalidate)
#pragma alloc_text(PAGE, LicenseRetryWorkRoutine)
#endif

// 以下のDPC・タイマーまわり、デバイスの収集は非ページ（DISPATCH_LEVELで動く／スピンロックを持つ。A-1）。

// 全キーボードの生きた状態を中止するために、接続中のデバイスを集めてremove lockを取って返す。
// g_listLockを持つ区間なので、PAGEセクションに置かない非ページのヘルパーにしている（A-1）。
// B-1: remove lockはg_listLockを保持したまま取得する。
_IRQL_requires_max_(DISPATCH_LEVEL)
static ULONG
CollectLicenseTargets(
    _Out_writes_to_(KBDLAYER_LICENSE_MAX_TARGETS, return) PDEVICE_EXTENSION* Targets
    )
{
    ULONG count = 0;
    KIRQL oldIrql;
    PLIST_ENTRY entry;

    KeAcquireSpinLock(&g_listLock, &oldIrql);
    for (entry = g_deviceList.Flink;
         entry != &g_deviceList && count < KBDLAYER_LICENSE_MAX_TARGETS;
         entry = entry->Flink) {
        PDEVICE_EXTENSION devExt = CONTAINING_RECORD(entry, DEVICE_EXTENSION, ListEntry);
        if (NT_SUCCESS(IoAcquireRemoveLock(&devExt->RemoveLock, NULL))) {
            Targets[count++] = devExt;
        }
    }
    KeReleaseSpinLock(&g_listLock, oldIrql);
    return count;
}

// INVALIDへ遷移したときに、全キーボードの生きた状態を中止する。永続設定は書き換えない。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
AbortAllDevicesForLicense(
    VOID
    )
{
    PDEVICE_EXTENSION targets[KBDLAYER_LICENSE_MAX_TARGETS];
    ULONG count = CollectLicenseTargets(targets);

    for (ULONG i = 0; i < count; i++) {
        AbortLiveStateForDevice(targets[i]);
        IoReleaseRemoveLock(&targets[i]->RemoveLock, NULL);
    }
}

// LicenseTokenを読んで検証する。PASSIVE_LEVEL。
_IRQL_requires_max_(PASSIVE_LEVEL)
static BOOLEAN
CheckLicenseToken(
    VOID
    )
{
    WCHAR tokenBuf[KBDLAYER_LICENSE_TOKEN_CHARS] = {0};

    RegReadServiceString(KBDLAYER_LICENSE_SERVICE_NAME, KBDLAYER_LICENSE_TOKEN_VALUE,
                         tokenBuf, RTL_NUMBER_OF(tokenBuf));
    return (tokenBuf[0] != 0 && ValidateLicenseToken(tokenBuf)) ? TRUE : FALSE;
}

// 検証して、結果を状態へ反映する。戻りは反映後の状態。PASSIVE_LEVEL。
_IRQL_requires_max_(PASSIVE_LEVEL)
static ULONG
RevalidateAndApply(
    _In_ LICENSE_TRIGGER Trigger
    )
{
    BOOLEAN valid = CheckLicenseToken();
    LONG prev;
    LONG next;

    KeWaitForSingleObject(&g_LicenseMutex, Executive, KernelMode, FALSE, NULL);

    prev = g_LicenseState;
    next = prev;
    if (valid) {
        next = (LONG)KBDLAYER_LICENSE_STATE_VALID;
    } else {
        switch (Trigger) {
        case LicenseTriggerDefinitive:
            next = (LONG)KBDLAYER_LICENSE_STATE_INVALID;
            break;
        case LicenseTriggerRetry:
            // 期限まで失敗が続いた。VALIDは（並行した確定の検証が成功した場合）動かさない。
            if (prev != (LONG)KBDLAYER_LICENSE_STATE_VALID &&
                (LONGLONG)KeQueryInterruptTime() >= g_LicenseProvisionalDeadline) {
                next = (LONG)KBDLAYER_LICENSE_STATE_INVALID;
            }
            break;
        case LicenseTriggerSoft:
        default:
            break; // 失敗しても状態を変えない
        }
    }

    if (next != prev) {
        InterlockedExchange(&g_LicenseState, next);
        DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_WARNING_LEVEL,
                   "kbdlayer: license state %ld -> %ld (trigger=%d)\n", prev, next, (int)Trigger);
    }

    // 次回起動の暫定有効の判断に使う。状態が変わったときだけ書く。
    if (next == (LONG)KBDLAYER_LICENSE_STATE_VALID && g_LicensePersistedLastState != 1) {
        NTSTATUS st = RegWriteDword(KBDLAYER_LICENSE_SERVICE_NAME, NULL, KBDLAYER_LICENSE_LASTSTATE_VALUE, 1);
        if (NT_SUCCESS(st)) {
            g_LicensePersistedLastState = 1;
        } else {
            DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL,
                       "kbdlayer: LicenseLastState write failed 0x%08X\n", st);
        }
    } else if (next == (LONG)KBDLAYER_LICENSE_STATE_INVALID && g_LicensePersistedLastState != 0) {
        NTSTATUS st = RegWriteDword(KBDLAYER_LICENSE_SERVICE_NAME, NULL, KBDLAYER_LICENSE_LASTSTATE_VALUE, 0);
        if (NT_SUCCESS(st)) {
            g_LicensePersistedLastState = 0;
        } else {
            DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL,
                       "kbdlayer: LicenseLastState write failed 0x%08X\n", st);
        }
    }

    KeReleaseMutex(&g_LicenseMutex, FALSE);

    // 有効だったものが無効になったら、生きた状態を中止する（永続設定は書き換えない）。
    if (prev != (LONG)KBDLAYER_LICENSE_STATE_INVALID && next == (LONG)KBDLAYER_LICENSE_STATE_INVALID) {
        AbortAllDevicesForLicense();
    }
    return (ULONG)next;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
ArmLicenseRetryTimer(
    VOID
    )
{
    LARGE_INTEGER due;

    due.QuadPart = -(LONGLONG)(KBDLAYER_LICENSE_RETRY_INTERVAL_MS * 10000ULL);
    KeSetTimer(&g_LicenseRetryTimer, due, &g_LicenseRetryDpc);
}

// タイマー満了のDPC。PASSIVE_LEVELの処理（検証・レジストリ）が必要なので、作業項目を起こすだけ（A-4）。
// DPCから自分のタイマーを張り直さない（張り直しは作業項目の中で行う）。
_Use_decl_annotations_
static VOID
LicenseRetryDpcRoutine(
    PKDPC Dpc,
    PVOID DeferredContext,
    PVOID SystemArgument1,
    PVOID SystemArgument2
    )
{
    UNREFERENCED_PARAMETER(Dpc);
    UNREFERENCED_PARAMETER(DeferredContext);
    UNREFERENCED_PARAMETER(SystemArgument1);
    UNREFERENCED_PARAMETER(SystemArgument2);

    if (g_LicenseShutdown || g_LicenseWorkItem == NULL) {
        return;
    }
    if (InterlockedCompareExchange(&g_LicenseWorkInFlight, 1, 0) == 0) {
        IoQueueWorkItem(g_LicenseWorkItem, LicenseRetryWorkRoutine, DelayedWorkQueue, NULL);
    }
}

_Use_decl_annotations_
static VOID
LicenseRetryWorkRoutine(
    PDEVICE_OBJECT WorkItemDeviceObject,
    PVOID Context
    )
{
    UNREFERENCED_PARAMETER(WorkItemDeviceObject);
    UNREFERENCED_PARAMETER(Context);
    PAGED_CODE();

    if (!g_LicenseShutdown && g_LicenseState != (LONG)KBDLAYER_LICENSE_STATE_VALID) {
        ULONG state = RevalidateAndApply(LicenseTriggerRetry);

        // VALIDになった、または期限が来てINVALIDになったら、再試行は終わり。
        if (!g_LicenseShutdown &&
            state != KBDLAYER_LICENSE_STATE_VALID &&
            (LONGLONG)KeQueryInterruptTime() < g_LicenseProvisionalDeadline) {
            ArmLicenseRetryTimer();
        }
    }

    // この関数の最後。TeardownLicenseはこれが0になるのを待ってから作業項目を解放する。
    InterlockedExchange(&g_LicenseWorkInFlight, 0);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeLicense(
    _In_opt_ PDEVICE_OBJECT ControlDeviceObject
    )
{
    ULONG lastState = 0;

    PAGED_CODE();

    KeInitializeMutex(&g_LicenseMutex, 0);
    KeInitializeTimer(&g_LicenseRetryTimer);
    KeInitializeDpc(&g_LicenseRetryDpc, LicenseRetryDpcRoutine, NULL);

    RegReadDword(KBDLAYER_LICENSE_SERVICE_NAME, KBDLAYER_LICENSE_LASTSTATE_VALUE, &lastState, 0);
    g_LicensePersistedLastState = (lastState == 1) ? 1 : 0;

    // 再検証を続ける期間（と、暫定有効の期限）。ここからの5分。
    g_LicenseProvisionalDeadline = (LONGLONG)(KeQueryInterruptTime() + KBDLAYER_LICENSE_PROVISIONAL_100NS);
    g_LicenseState = (lastState == 1)
        ? (LONG)KBDLAYER_LICENSE_STATE_PROVISIONAL
        : (LONG)KBDLAYER_LICENSE_STATE_INVALID;

    DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_INFO_LEVEL,
               "kbdlayer: license initial state=%ld (LicenseLastState=%lu)\n", g_LicenseState, lastState);

    // 制御デバイスが無いと作業項目を確保できず、再検証できない。暫定有効は期限で自動的に無効になる。
    if (ControlDeviceObject != NULL) {
        g_LicenseWorkItem = IoAllocateWorkItem(ControlDeviceObject);
    }
    if (g_LicenseWorkItem == NULL) {
        DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL,
                   "kbdlayer: license work item unavailable; no automatic revalidation\n");
        return;
    }
    ArmLicenseRetryTimer();
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
TeardownLicense(
    VOID
    )
{
    LARGE_INTEGER delay;

    PAGED_CODE();

    // B-3: 新しい再試行を止める → 実行中の作業項目の完了を待つ → タイマー取消 → DPCの完了を待つ → 解放。
    // 作業項目は最後にタイマーを張り直しうるので、完了を待ってから取り消す。
    InterlockedExchange(&g_LicenseShutdown, 1);

    delay.QuadPart = -(LONGLONG)(10 * 10000); // 10ms
    while (g_LicenseWorkInFlight != 0) {
        KeDelayExecutionThread(KernelMode, FALSE, &delay);
    }
    KeCancelTimer(&g_LicenseRetryTimer);
    KeFlushQueuedDpcs();

    // DPCはShutdownを見るので、フラッシュの後に作業項目が起こされることはない。
    // 念のため、もう一度完了を待つ。
    while (g_LicenseWorkInFlight != 0) {
        KeDelayExecutionThread(KernelMode, FALSE, &delay);
    }
    if (g_LicenseWorkItem != NULL) {
        IoFreeWorkItem(g_LicenseWorkItem);
        g_LicenseWorkItem = NULL;
    }
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
LicenseRevalidate(
    _In_ ULONG Mode,
    _Out_ PULONG State
    )
{
    LICENSE_TRIGGER trigger;

    PAGED_CODE();

    if (Mode == KBDLAYER_REVALIDATE_DEFINITIVE) {
        trigger = LicenseTriggerDefinitive;
    } else if (Mode == KBDLAYER_REVALIDATE_SOFT) {
        trigger = LicenseTriggerSoft;
    } else {
        *State = LicenseGetState();
        return STATUS_INVALID_PARAMETER;
    }

    *State = RevalidateAndApply(trigger);
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
ULONG
LicenseGetState(
    VOID
    )
{
    if (g_LicenseState == (LONG)KBDLAYER_LICENSE_STATE_PROVISIONAL && !LicenseIsEffective()) {
        return KBDLAYER_LICENSE_STATE_INVALID; // 期限切れ（再検証のタイマーがまだ状態を落としていない）
    }
    return (ULONG)g_LicenseState;
}

#else // KBDLAYER_PRO

// 買い切り版: キーの確認はインストール時に済んでいる（インストーラーがGumroadに照会する）ので、
// ドライバはトークンを読まず、常にライセンス有効とする。token.cも、この構成ではビルドに含めない
// （公開鍵も載らない）。

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeLicense(
    _In_opt_ PDEVICE_OBJECT ControlDeviceObject
    )
{
    UNREFERENCED_PARAMETER(ControlDeviceObject);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
TeardownLicense(
    VOID
    )
{
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
LicenseRevalidate(
    _In_ ULONG Mode,
    _Out_ PULONG State
    )
{
    *State = KBDLAYER_LICENSE_STATE_VALID;
    return (Mode == KBDLAYER_REVALIDATE_DEFINITIVE || Mode == KBDLAYER_REVALIDATE_SOFT)
        ? STATUS_SUCCESS : STATUS_INVALID_PARAMETER;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
ULONG
LicenseGetState(
    VOID
    )
{
    return KBDLAYER_LICENSE_STATE_VALID;
}

#endif // KBDLAYER_PRO
