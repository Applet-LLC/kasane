// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

/*
  kbdlayer/HelperNotify.c

  ヘルパーイベント（CUSTOMアクションの通知、2026-10-08）。レイヤーマップのCUSTOM(id)を割り当てた
  キーの押下・オートリピート・解放をドライバ全体で1本のリングに積み、IOCTL_KBDLAYER_WAIT_HELPER_EVENT
  で待っているアプリ（kasane-tray）へ渡す。キーを解決してイベントを積むのはLayerEngine.c
  （LayerEngineShouldDropEntry）、ここは「イベントを持ち、待っているIRPを完了させる」だけを担う。

  - イベントのリング（g_Events）は g_EventLock で守る。積むのは ServiceCallback・DPC の中で、
    devExt->EngineLock を保持したまま呼ばれる（ロックの順序は EngineLock → g_EventLock の一方向。
    g_EventLock を保持したまま EngineLock を取らない）。
  - 保留IRPはドライバ全体で1本のキャンセル安全キュー（IoCsq、g_WaitLock）に入れる。
    Irp->Tail.Overlay.DriverContext[0] = クライアントのLastSeq（LATESTは入れる時点の最新に置き換える）。
    g_WaitLock と g_EventLock は同時に持たない。
  - 保留IRPは特定のキーボードに結び付かない（どのキーボードのイベントも返す）ので、デバイスの
    remove lockは持たない。キーボードの取り外しでは完了させない（他のキーボードのイベントを
    待ち続ける）。完了のきっかけは、イベント（STATUS_SUCCESS）、キャンセル（プロセス終了など、
    CSQが処理）、ハンドルのクローズ（HelperNotifyCleanupOwner）。制御デバイスは、ハンドルが
    すべて閉じるまでアンロードされないので、アンロード時に残る保留IRPは無い。
  - IRPの完了は、積んだ場所ではなく、ロックを放した後で行う（HelperNotifyFlushIfNeeded。
    LayerNotifyIfChanged の先頭から呼ばれる。ServiceCallbackの末尾・各DPCの末尾・各IOCTLの後）。
    積む側は g_FlushNeeded を立てるだけ。
  - 同時に保留できる数は KBDLAYER_MAX_PENDING_HELPER_WAITS まで（Everyoneが呼べるIOCTLなので、
    非ページプールを使い切らせる攻撃を防ぐ）。
*/

#include <ntddk.h>
#include <wdm.h>
#include "DeviceExtension.h"
#include "LayerEngine.h"

// --- イベントのリング ---

static KSPIN_LOCK g_EventLock;
static KBDLAYER_HELPER_EVENT g_Events[KBDLAYER_HELPER_EVENT_QUEUE_SIZE];
static ULONG g_EventHead;   // 次に書く位置
static ULONG g_EventCount;  // 有効な件数（KBDLAYER_HELPER_EVENT_QUEUE_SIZE以下）
static ULONG g_NewestSeq;   // 最後に積んだイベントのSeq（0 = まだ無い）
static volatile LONG g_FlushNeeded;

// --- 保留IRPのキュー ---

// IoCsqRemoveNextIrpに渡す絞り込み条件。
typedef struct _HELPER_WAIT_FILTER {
    PFILE_OBJECT FileObject; // 非NULLなら、このハンドルのIRPだけ
    BOOLEAN OnlyBehind;      // TRUE = LastSeqがNewestSeqより古いIRPだけ（= 返すイベントがあるもの）
    ULONG NewestSeq;
} HELPER_WAIT_FILTER, *PHELPER_WAIT_FILTER;

static IO_CSQ g_WaitCsq;
static KSPIN_LOCK g_WaitLock;
static LIST_ENTRY g_WaitList;
static ULONG g_WaitCount;

static IO_CSQ_INSERT_IRP_EX HelperCsqInsertIrpEx;
static IO_CSQ_REMOVE_IRP HelperCsqRemoveIrp;
static IO_CSQ_PEEK_NEXT_IRP HelperCsqPeekNextIrp;
static IO_CSQ_ACQUIRE_LOCK HelperCsqAcquireLock;
static IO_CSQ_RELEASE_LOCK HelperCsqReleaseLock;
static IO_CSQ_COMPLETE_CANCELED_IRP HelperCsqCompleteCanceledIrp;

// a が b より新しいSeqか（ULONGの一周を考慮する）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN
SeqIsNewer(
    _In_ ULONG A,
    _In_ ULONG B
    )
{
    return (LONG)(A - B) > 0;
}

_Use_decl_annotations_
static NTSTATUS
HelperCsqInsertIrpEx(
    PIO_CSQ Csq,
    PIRP Irp,
    PVOID InsertContext
    )
{
    UNREFERENCED_PARAMETER(Csq);
    UNREFERENCED_PARAMETER(InsertContext);

    if (g_WaitCount >= KBDLAYER_MAX_PENDING_HELPER_WAITS) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    InsertTailList(&g_WaitList, &Irp->Tail.Overlay.ListEntry);
    g_WaitCount++;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
static VOID
HelperCsqRemoveIrp(
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
HelperCsqPeekNextIrp(
    PIO_CSQ Csq,
    PIRP Irp,
    PVOID PeekContext
    )
{
    PHELPER_WAIT_FILTER filter = (PHELPER_WAIT_FILTER)PeekContext;
    PLIST_ENTRY next = (Irp != NULL) ? Irp->Tail.Overlay.ListEntry.Flink : g_WaitList.Flink;

    UNREFERENCED_PARAMETER(Csq);

    for (; next != &g_WaitList; next = next->Flink) {
        PIRP candidate = CONTAINING_RECORD(next, IRP, Tail.Overlay.ListEntry);

        if (filter == NULL) {
            return candidate;
        }
        if (filter->FileObject != NULL &&
            IoGetCurrentIrpStackLocation(candidate)->FileObject != filter->FileObject) {
            continue;
        }
        if (filter->OnlyBehind &&
            !SeqIsNewer(filter->NewestSeq, (ULONG)(ULONG_PTR)candidate->Tail.Overlay.DriverContext[0])) {
            continue;
        }
        return candidate;
    }
    return NULL;
}

_Use_decl_annotations_
static VOID
HelperCsqAcquireLock(
    PIO_CSQ Csq,
    PKIRQL Irql
    )
{
    UNREFERENCED_PARAMETER(Csq);
    KeAcquireSpinLock(&g_WaitLock, Irql);
}

_Use_decl_annotations_
static VOID
HelperCsqReleaseLock(
    PIO_CSQ Csq,
    KIRQL Irql
    )
{
    UNREFERENCED_PARAMETER(Csq);
    // HelperCsqAcquireLockで取ったロックを、CSQが対にして呼ぶこの関数で解放する
    // （LayerNotify.cのWaitCsqReleaseLockと同じ。C26110は対が見えないだけ）。
#pragma warning(suppress: 26110)
    KeReleaseSpinLock(&g_WaitLock, Irql);
}

_Use_decl_annotations_
static VOID
HelperCsqCompleteCanceledIrp(
    PIO_CSQ Csq,
    PIRP Irp
    )
{
    UNREFERENCED_PARAMETER(Csq);
    Irp->IoStatus.Status = STATUS_CANCELLED;
    Irp->IoStatus.Information = 0;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
}

// LastSeqより新しいイベントを出力バッファへ写して、IRPを完了させる。
// キューから取り出したIRP（g_WaitLock・g_EventLockとも非保持）に対して呼ぶ。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
CompleteHelperWaitIrp(
    _In_ PIRP Irp
    )
{
    ULONG lastSeq = (ULONG)(ULONG_PTR)Irp->Tail.Overlay.DriverContext[0];
    ULONG outputLen = IoGetCurrentIrpStackLocation(Irp)->Parameters.DeviceIoControl.OutputBufferLength;
    PKBDLAYER_WAIT_HELPER_EVENT_OUTPUT out = (PKBDLAYER_WAIT_HELPER_EVENT_OUTPUT)Irp->AssociatedIrp.SystemBuffer;
    PKBDLAYER_HELPER_EVENT events = (PKBDLAYER_HELPER_EVENT)(out + 1);
    // 出力バッファの長さはControlDevice.cのハンドラで「出力の見出し＋1件以上」を検証済み。
    ULONG capacity = (outputLen - (ULONG)sizeof(KBDLAYER_WAIT_HELPER_EVENT_OUTPUT)) / (ULONG)sizeof(KBDLAYER_HELPER_EVENT);
    ULONG count = 0;
    ULONG dropped = 0;
    ULONG seq = lastSeq;
    KIRQL oldIrql;

    KeAcquireSpinLock(&g_EventLock, &oldIrql);
    if (g_EventCount > 0) {
        ULONG oldest = (g_EventHead + KBDLAYER_HELPER_EVENT_QUEUE_SIZE - g_EventCount) % KBDLAYER_HELPER_EVENT_QUEUE_SIZE;
        ULONG oldestSeq = g_Events[oldest].Seq;

        // LastSeqの次から最古のイベントの手前までは、リングから押し出された（概数。Seqが一周する
        // ときに飛ばす値の分だけずれうる）。
        if (SeqIsNewer(oldestSeq, lastSeq + 1)) {
            dropped = oldestSeq - lastSeq - 1;
        }
        for (ULONG i = 0; i < g_EventCount && count < capacity; i++) {
            const KBDLAYER_HELPER_EVENT* e = &g_Events[(oldest + i) % KBDLAYER_HELPER_EVENT_QUEUE_SIZE];
            if (SeqIsNewer(e->Seq, lastSeq)) {
                events[count++] = *e;
                seq = e->Seq;
            }
        }
    }
    KeReleaseSpinLock(&g_EventLock, oldIrql);

    // METHOD_BUFFERED: 入力（LastSeq）はStartWaitHelperEventで読み終えている。
    out->Seq = seq;
    out->Count = count;
    out->Dropped = dropped;
    out->Reserved = 0;

    Irp->IoStatus.Status = STATUS_SUCCESS;
    Irp->IoStatus.Information = sizeof(KBDLAYER_WAIT_HELPER_EVENT_OUTPUT) + (ULONG_PTR)count * sizeof(KBDLAYER_HELPER_EVENT);
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
}

// 返すイベントがある保留IRPを、すべて完了させる。ロック（EngineLock・OutputLock・ここのロック）を
// 保持せずに呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
HelperNotifyFlush(
    VOID
    )
{
    HELPER_WAIT_FILTER filter;
    KIRQL oldIrql;
    PIRP irp;

    KeAcquireSpinLock(&g_EventLock, &oldIrql);
    filter.NewestSeq = g_NewestSeq;
    KeReleaseSpinLock(&g_EventLock, oldIrql);

    if (filter.NewestSeq == 0) {
        return; // まだ1件も積んでいない
    }
    filter.FileObject = NULL;
    filter.OnlyBehind = TRUE;

    while ((irp = IoCsqRemoveNextIrp(&g_WaitCsq, &filter)) != NULL) {
        CompleteHelperWaitIrp(irp);
    }
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeHelperNotify(
    VOID
    )
{
    KeInitializeSpinLock(&g_EventLock);
    RtlZeroMemory(g_Events, sizeof(g_Events));
    g_EventHead = 0;
    g_EventCount = 0;
    g_NewestSeq = 0;
    g_FlushNeeded = 0;

    KeInitializeSpinLock(&g_WaitLock);
    InitializeListHead(&g_WaitList);
    g_WaitCount = 0;
    IoCsqInitializeEx(&g_WaitCsq,
                      HelperCsqInsertIrpEx,
                      HelperCsqRemoveIrp,
                      HelperCsqPeekNextIrp,
                      HelperCsqAcquireLock,
                      HelperCsqReleaseLock,
                      HelperCsqCompleteCanceledIrp);
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
HelperNotifyQueueEvent(
    _In_ ULONG UniqId,
    _In_ USHORT CustomId,
    _In_ BOOLEAN IsBreak,
    _In_ BOOLEAN IsRepeat,
    _In_ UCHAR Modifiers
    )
{
    KBDLAYER_HELPER_EVENT* e;
    KIRQL oldIrql;
    ULONG seq;
    ULONG timeMs = (ULONG)(KeQueryInterruptTime() / 10000); // 100ns単位 → ms

    KeAcquireSpinLock(&g_EventLock, &oldIrql);

    // 0（未発行）とKBDLAYER_HELPER_SEQ_LATEST（「今より後」の指定）は、イベントのSeqに使わない。
    seq = g_NewestSeq + 1;
    if (seq == 0 || seq == KBDLAYER_HELPER_SEQ_LATEST) {
        seq = 1;
    }
    g_NewestSeq = seq;

    e = &g_Events[g_EventHead];
    e->Seq = seq;
    e->UniqId = UniqId;
    e->CustomId = CustomId;
    e->IsBreak = IsBreak ? 1 : 0;
    e->Flags = IsRepeat ? KBDLAYER_HELPER_EVENT_FLAG_REPEAT : 0;
    e->Modifiers = Modifiers;
    e->Reserved[0] = e->Reserved[1] = e->Reserved[2] = 0;
    e->TimeMs = timeMs;

    g_EventHead = (g_EventHead + 1) % KBDLAYER_HELPER_EVENT_QUEUE_SIZE;
    if (g_EventCount < KBDLAYER_HELPER_EVENT_QUEUE_SIZE) {
        g_EventCount++;
    }

    KeReleaseSpinLock(&g_EventLock, oldIrql);

    // 待っているIRPの完了は、呼び出し元がロックを放した後（HelperNotifyFlushIfNeeded）で行う。
    InterlockedExchange(&g_FlushNeeded, 1);
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
HelperNotifyFlushIfNeeded(
    VOID
    )
{
    if (InterlockedExchange(&g_FlushNeeded, 0) != 0) {
        HelperNotifyFlush();
    }
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
StartWaitHelperEvent(
    _In_ PIRP Irp,
    _In_ ULONG LastSeq
    )
{
    NTSTATUS status;
    KIRQL oldIrql;

    if (LastSeq == KBDLAYER_HELPER_SEQ_LATEST) {
        // 「今より後」を待つ。入れる前に最新のSeqに置き換える（この直後に積まれたイベントは、
        // 下のHelperNotifyFlushが返す）。
        KeAcquireSpinLock(&g_EventLock, &oldIrql);
        LastSeq = g_NewestSeq;
        KeReleaseSpinLock(&g_EventLock, oldIrql);
    }
    Irp->Tail.Overlay.DriverContext[0] = (PVOID)(ULONG_PTR)LastSeq;

    // 成功するとIoCsqInsertIrpExがIoMarkIrpPendingする。
    status = IoCsqInsertIrpEx(&g_WaitCsq, Irp, NULL, NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    // 先にキューへ入れてから、返せるイベントがあるかを見る。積む側は「リングに積む →
    // g_FlushNeededを立てる → （ロックを放した後で）キューを見る」順なので、入れる直前に
    // 積まれたイベントも、ここか積む側のどちらかが拾う。
    HelperNotifyFlush();
    return STATUS_PENDING;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
HelperNotifyCleanupOwner(
    _In_ PFILE_OBJECT FileObject
    )
{
    HELPER_WAIT_FILTER filter;
    PIRP irp;

    filter.FileObject = FileObject;
    filter.OnlyBehind = FALSE;
    filter.NewestSeq = 0;

    while ((irp = IoCsqRemoveNextIrp(&g_WaitCsq, &filter)) != NULL) {
        irp->IoStatus.Status = STATUS_CANCELLED;
        irp->IoStatus.Information = 0;
        IoCompleteRequest(irp, IO_NO_INCREMENT);
    }
}
