// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

/*
  kbdlayer/LayerNotify.c

  IOCTL_KBDLAYER_WAIT_LAYER_CHANGE の保留IRPの管理（レイヤー制御の双方向通知）。
  レイヤー状態そのものと変化の検出はLayerEngine.c（LayerNotifyIfChanged /
  GetLayerStateForDevice）、ここは「待っているIRPを持ち、変化したら完了させる」だけを担う。

  - 保留IRPはドライバ全体で1本のキャンセル安全キュー（IoCsq）に入れる。
    Irp->Tail.Overlay.DriverContext[0] = 待っているdevExt、[1] = クライアントのLastSeq。
  - 保留中の各IRPは、対象devExtのremove lockを保持する（取得はControlDevice.cの
    FindDeviceExtensionByUniqId）。完了時に解放する。
  - 完了のきっかけ: 状態の変化（STATUS_SUCCESS）、キャンセル（プロセス終了など、CSQが処理）、
    ハンドルのクローズ（LayerNotifyCleanupOwner）、デバイス削除（LayerNotifyAbortDevice）。
  - 同時に保留できる数はKBDLAYER_MAX_PENDING_LAYER_WAITSまで（Everyoneが呼べるIOCTLなので、
    非ページプールを使い切らせる攻撃を防ぐ）。
*/

#include <ntddk.h>
#include <wdm.h>
#include "DeviceExtension.h"
#include "LayerEngine.h"
#include "token.h" // KBDLAYER_TRACE

// IoCsqRemoveNextIrpに渡す絞り込み条件。NULL/FALSEの項目は条件にしない。
typedef struct _LAYER_WAIT_FILTER {
    PDEVICE_EXTENSION Device;
    PFILE_OBJECT FileObject;
    BOOLEAN OnlyStale; // TRUE = LastSeqがCurrentSeqと違うIRPだけ（= 待っていた変化が起きたもの）
    ULONG CurrentSeq;
} LAYER_WAIT_FILTER, *PLAYER_WAIT_FILTER;

static IO_CSQ g_WaitCsq;
static KSPIN_LOCK g_WaitLock;
static LIST_ENTRY g_WaitList;
static ULONG g_WaitCount;

static IO_CSQ_INSERT_IRP_EX WaitCsqInsertIrpEx;
static IO_CSQ_REMOVE_IRP WaitCsqRemoveIrp;
static IO_CSQ_PEEK_NEXT_IRP WaitCsqPeekNextIrp;
static IO_CSQ_ACQUIRE_LOCK WaitCsqAcquireLock;
static IO_CSQ_RELEASE_LOCK WaitCsqReleaseLock;
static IO_CSQ_COMPLETE_CANCELED_IRP WaitCsqCompleteCanceledIrp;

// 保留IRPを完了させる。State != NULL なら出力バッファへ書く。IRPが持つremove lockを解放する。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
CompleteWaitIrp(
    _In_ PIRP Irp,
    _In_ NTSTATUS Status,
    _In_opt_ const KBDLAYER_LAYER_STATE* State
    )
{
    PDEVICE_EXTENSION devExt = (PDEVICE_EXTENSION)Irp->Tail.Overlay.DriverContext[0];

    Irp->IoStatus.Status = Status;
    Irp->IoStatus.Information = 0;
    if (NT_SUCCESS(Status) && State != NULL) {
        // METHOD_BUFFERED。出力バッファ長はControlDevice.cのハンドラで検証済み。
        RtlCopyMemory(Irp->AssociatedIrp.SystemBuffer, State, sizeof(KBDLAYER_LAYER_STATE));
        Irp->IoStatus.Information = sizeof(KBDLAYER_LAYER_STATE);
    }
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
}

_Use_decl_annotations_
static NTSTATUS
WaitCsqInsertIrpEx(
    PIO_CSQ Csq,
    PIRP Irp,
    PVOID InsertContext
    )
{
    UNREFERENCED_PARAMETER(Csq);
    UNREFERENCED_PARAMETER(InsertContext);

    if (g_WaitCount >= KBDLAYER_MAX_PENDING_LAYER_WAITS) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    InsertTailList(&g_WaitList, &Irp->Tail.Overlay.ListEntry);
    g_WaitCount++;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
static VOID
WaitCsqRemoveIrp(
    PIO_CSQ Csq,
    PIRP Irp
    )
{
    UNREFERENCED_PARAMETER(Csq);

    RemoveEntryList(&Irp->Tail.Overlay.ListEntry);
    if (g_WaitCount > 0) {
        g_WaitCount--;
    }
}

_Use_decl_annotations_
static PIRP
WaitCsqPeekNextIrp(
    PIO_CSQ Csq,
    PIRP Irp,
    PVOID PeekContext
    )
{
    PLAYER_WAIT_FILTER filter = (PLAYER_WAIT_FILTER)PeekContext;
    PLIST_ENTRY next = (Irp != NULL) ? Irp->Tail.Overlay.ListEntry.Flink : g_WaitList.Flink;

    UNREFERENCED_PARAMETER(Csq);

    for (; next != &g_WaitList; next = next->Flink) {
        PIRP candidate = CONTAINING_RECORD(next, IRP, Tail.Overlay.ListEntry);

        if (filter == NULL) {
            return candidate;
        }
        if (filter->Device != NULL && candidate->Tail.Overlay.DriverContext[0] != filter->Device) {
            continue;
        }
        if (filter->FileObject != NULL &&
            IoGetCurrentIrpStackLocation(candidate)->FileObject != filter->FileObject) {
            continue;
        }
        if (filter->OnlyStale &&
            (ULONG)(ULONG_PTR)candidate->Tail.Overlay.DriverContext[1] == filter->CurrentSeq) {
            continue;
        }
        return candidate;
    }
    return NULL;
}

_Use_decl_annotations_
static VOID
WaitCsqAcquireLock(
    PIO_CSQ Csq,
    PKIRQL Irql
    )
{
    UNREFERENCED_PARAMETER(Csq);
    KeAcquireSpinLock(&g_WaitLock, Irql);
}

_Use_decl_annotations_
static VOID
WaitCsqReleaseLock(
    PIO_CSQ Csq,
    KIRQL Irql
    )
{
    UNREFERENCED_PARAMETER(Csq);
    // WaitCsqAcquireLockで取ったロックを、CSQが対にして呼ぶこの関数で解放する。
    // 取得と解放が別の関数に分かれているので、静的解析（C26110）には対が見えない。
#pragma warning(suppress: 26110)
    KeReleaseSpinLock(&g_WaitLock, Irql);
}

_Use_decl_annotations_
static VOID
WaitCsqCompleteCanceledIrp(
    PIO_CSQ Csq,
    PIRP Irp
    )
{
    UNREFERENCED_PARAMETER(Csq);
    CompleteWaitIrp(Irp, STATUS_CANCELLED, NULL);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeLayerNotify(
    VOID
    )
{
    KeInitializeSpinLock(&g_WaitLock);
    InitializeListHead(&g_WaitList);
    g_WaitCount = 0;
    IoCsqInitializeEx(&g_WaitCsq,
                      WaitCsqInsertIrpEx,
                      WaitCsqRemoveIrp,
                      WaitCsqPeekNextIrp,
                      WaitCsqAcquireLock,
                      WaitCsqReleaseLock,
                      WaitCsqCompleteCanceledIrp);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
StartWaitLayerChange(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PIRP Irp,
    _In_ ULONG LastSeq
    )
{
    KBDLAYER_LAYER_STATE state;
    NTSTATUS status;
    KIRQL oldIrql;
    BOOLEAN removing;

    Irp->Tail.Overlay.DriverContext[0] = devExt;
    Irp->Tail.Overlay.DriverContext[1] = (PVOID)(ULONG_PTR)LastSeq;

    // 先にキューへ入れてから現在のSeqを見る。こうすると、入れる直前に状態が変わっていても
    // （通知側がSeqを進めてからキューを見る順序なので）取りこぼさない。
    // 成功するとIoCsqInsertIrpExがIoMarkIrpPendingする。
    status = IoCsqInsertIrpEx(&g_WaitCsq, Irp, NULL, NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    // デバイス削除が始まっていたら、入れたIRPをすぐに終わらせる。削除側は「RemovePendingを
    // 立てる → キューを空にする」順なので、入れた後にここで確認すれば取りこぼさない
    // （確認時に立っていなければ、削除側のキュー掃除がこのIRPを拾う）。
    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    removing = devExt->RemovePending;
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
    if (removing) {
        LayerNotifyAbortDevice(devExt, STATUS_DELETE_PENDING);
        return STATUS_PENDING;
    }

    GetLayerStateForDevice(devExt, &state);
    if (state.Seq != LastSeq) {
        // すでに変わっていた。このIRP（と、同じく変化を待っていた他のIRP）を即座に完了させる。
        LayerNotifyCompleteWaiters(devExt);
    }
    return STATUS_PENDING;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
LayerNotifyCompleteWaiters(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    KBDLAYER_LAYER_STATE state;
    LAYER_WAIT_FILTER filter;
    PIRP irp;

    GetLayerStateForDevice(devExt, &state);

    filter.Device = devExt;
    filter.FileObject = NULL;
    filter.OnlyStale = TRUE;
    filter.CurrentSeq = state.Seq;

    while ((irp = IoCsqRemoveNextIrp(&g_WaitCsq, &filter)) != NULL) {
        CompleteWaitIrp(irp, STATUS_SUCCESS, &state);
    }
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
LayerNotifyAbortDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ NTSTATUS Status
    )
{
    LAYER_WAIT_FILTER filter;
    PIRP irp;

    filter.Device = devExt;
    filter.FileObject = NULL;
    filter.OnlyStale = FALSE;
    filter.CurrentSeq = 0;

    while ((irp = IoCsqRemoveNextIrp(&g_WaitCsq, &filter)) != NULL) {
        CompleteWaitIrp(irp, Status, NULL);
    }
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
LayerNotifyCleanupOwner(
    _In_ PFILE_OBJECT FileObject
    )
{
    LAYER_WAIT_FILTER filter;
    PIRP irp;

    filter.Device = NULL;
    filter.FileObject = FileObject;
    filter.OnlyStale = FALSE;
    filter.CurrentSeq = 0;

    while ((irp = IoCsqRemoveNextIrp(&g_WaitCsq, &filter)) != NULL) {
        CompleteWaitIrp(irp, STATUS_CANCELLED, NULL);
    }
}
