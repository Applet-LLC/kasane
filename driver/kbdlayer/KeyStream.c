// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

/*
  kbdlayer/KeyStream.c

  キーイベントの継続的な通知（2026-10-14）。キーボードから届いた生の物理キー（レイヤーマップなどで
  置き換える前のスキャンコード）の押下・解放を、ドライバ全体で1本のリングに積み、
  IOCTL_KBDLAYER_WAIT_KEY_EVENT で待っているアプリ（kasaneの設定画面）へ渡す。キャリブレーションの
  「入力確認」と、緊急解除の「同時押しを試す」が使う。LLフック（WH_KEYBOARD_LL）は、キーボード
  レイアウトDLLの影響を受けて、右Shiftや日本語キーの解放を取りこぼすことがあるため、ドライバから
  直接受け取る（docs/KNOWN-ISSUES.md）。

  構造はHelperNotify.c（WAIT_HELPER_EVENT）と同じで、次の点だけが違う。
  - 積むのは、レイヤーマップの内容に関係なく、すべての生キー（観測のみ。イベントは書き換えない）。
  - **キー入力が丸見えになる**（キーロガーになりうる）ので、IOCTLは FILE_WRITE_ACCESS（管理者のみ。
    制御デバイスのSDDLが、Everyoneには読み取りしか与えない）。さらに、積むのは、このIOCTLで待ち始めた
    ハンドル（所有者）が1つ以上あるときだけ。所有者がいなければ、ServiceCallbackでは1回の読み取りで戻る。
  - 所有者は、最初にWAITを発行したハンドル。ハンドルのクローズ（プロセスの終了・クラッシュを含む）で外れる。
    WAITの合間（完了してから次を発行するまで）も、所有者でいる間はイベントを積む。

  ロック: イベントのリング（g_KeyEventLock）は、ServiceCallbackのOutputLock保持中に取る
  （OutputLock → g_KeyEventLock の一方向）。保留IRPのキュー（g_KeyWaitLock）、所有者の表（g_KeyOwnerLock）と
  同時には持たない。IRPの完了は、ロックを放した後（KeyStreamFlushIfNeeded。LayerNotifyIfChangedから）で行う。
*/

#include <ntddk.h>
#include <wdm.h>
#include <ntddkbd.h>
#include "DeviceExtension.h"
#include "LayerEngine.h"

// --- イベントのリング ---

static KSPIN_LOCK g_KeyEventLock;
static KBDLAYER_KEY_EVENT g_KeyEvents[KBDLAYER_KEY_EVENT_QUEUE_SIZE];
static ULONG g_KeyEventHead;   // 次に書く位置
static ULONG g_KeyEventCount;  // 有効な件数（KBDLAYER_KEY_EVENT_QUEUE_SIZE以下）
static ULONG g_KeyNewestSeq;   // 最後に積んだイベントのSeq（0 = まだ無い）
static volatile LONG g_KeyFlushNeeded;

// --- 所有者（WAITを発行したハンドル） ---

static KSPIN_LOCK g_KeyOwnerLock;
static PFILE_OBJECT g_KeyOwners[KBDLAYER_MAX_KEY_STREAM_OWNERS];
static volatile LONG g_KeyOwnerCount; // ServiceCallbackが、ロックなしで0かどうかだけを見る

// --- 保留IRPのキュー ---

typedef struct _KEY_WAIT_FILTER {
    PFILE_OBJECT FileObject; // 非NULLなら、このハンドルのIRPだけ
    BOOLEAN OnlyBehind;      // TRUE = LastSeqがNewestSeqより古いIRPだけ（= 返すイベントがあるもの）
    ULONG NewestSeq;
} KEY_WAIT_FILTER, *PKEY_WAIT_FILTER;

static IO_CSQ g_KeyWaitCsq;
static KSPIN_LOCK g_KeyWaitLock;
static LIST_ENTRY g_KeyWaitList;
static ULONG g_KeyWaitCount;

static IO_CSQ_INSERT_IRP_EX KeyCsqInsertIrpEx;
static IO_CSQ_REMOVE_IRP KeyCsqRemoveIrp;
static IO_CSQ_PEEK_NEXT_IRP KeyCsqPeekNextIrp;
static IO_CSQ_ACQUIRE_LOCK KeyCsqAcquireLock;
static IO_CSQ_RELEASE_LOCK KeyCsqReleaseLock;
static IO_CSQ_COMPLETE_CANCELED_IRP KeyCsqCompleteCanceledIrp;

// a が b より新しいSeqか（ULONGの一周を考慮する）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN
KeySeqIsNewer(
    _In_ ULONG A,
    _In_ ULONG B
    )
{
    return (LONG)(A - B) > 0;
}

_Use_decl_annotations_
static NTSTATUS
KeyCsqInsertIrpEx(
    PIO_CSQ Csq,
    PIRP Irp,
    PVOID InsertContext
    )
{
    UNREFERENCED_PARAMETER(Csq);
    UNREFERENCED_PARAMETER(InsertContext);

    if (g_KeyWaitCount >= KBDLAYER_MAX_PENDING_KEY_WAITS) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    InsertTailList(&g_KeyWaitList, &Irp->Tail.Overlay.ListEntry);
    g_KeyWaitCount++;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
static VOID
KeyCsqRemoveIrp(
    PIO_CSQ Csq,
    PIRP Irp
    )
{
    UNREFERENCED_PARAMETER(Csq);

    RemoveEntryList(&Irp->Tail.Overlay.ListEntry);
    if (g_KeyWaitCount > 0) {
        g_KeyWaitCount--;
    }
}

_Use_decl_annotations_
static PIRP
KeyCsqPeekNextIrp(
    PIO_CSQ Csq,
    PIRP Irp,
    PVOID PeekContext
    )
{
    PKEY_WAIT_FILTER filter = (PKEY_WAIT_FILTER)PeekContext;
    PLIST_ENTRY next = (Irp != NULL) ? Irp->Tail.Overlay.ListEntry.Flink : g_KeyWaitList.Flink;

    UNREFERENCED_PARAMETER(Csq);

    for (; next != &g_KeyWaitList; next = next->Flink) {
        PIRP candidate = CONTAINING_RECORD(next, IRP, Tail.Overlay.ListEntry);

        if (filter == NULL) {
            return candidate;
        }
        if (filter->FileObject != NULL &&
            IoGetCurrentIrpStackLocation(candidate)->FileObject != filter->FileObject) {
            continue;
        }
        if (filter->OnlyBehind &&
            !KeySeqIsNewer(filter->NewestSeq, (ULONG)(ULONG_PTR)candidate->Tail.Overlay.DriverContext[0])) {
            continue;
        }
        return candidate;
    }
    return NULL;
}

_Use_decl_annotations_
static VOID
// CSQのAcquire/Releaseは対で呼ばれ、IRQLは*Irqlに保存して次のReleaseで戻す（仕様）。単体では対が見えない。
#pragma warning(suppress: 28167)
KeyCsqAcquireLock(
    PIO_CSQ Csq,
    PKIRQL Irql
    )
{
    UNREFERENCED_PARAMETER(Csq);
    KeAcquireSpinLock(&g_KeyWaitLock, Irql);
}

_Use_decl_annotations_
static VOID
#pragma warning(suppress: 28167)
KeyCsqReleaseLock(
    PIO_CSQ Csq,
    KIRQL Irql
    )
{
    UNREFERENCED_PARAMETER(Csq);
    // KeyCsqAcquireLockで取ったロックを、CSQが対にして呼ぶこの関数で解放する（HelperNotify.cと同じ）。
#pragma warning(suppress: 26110)
    KeReleaseSpinLock(&g_KeyWaitLock, Irql);
}

_Use_decl_annotations_
static VOID
KeyCsqCompleteCanceledIrp(
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
// キューから取り出したIRP（g_KeyWaitLock・g_KeyEventLockとも非保持）に対して呼ぶ。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
CompleteKeyWaitIrp(
    _In_ PIRP Irp
    )
{
    ULONG lastSeq = (ULONG)(ULONG_PTR)Irp->Tail.Overlay.DriverContext[0];
    ULONG outputLen = IoGetCurrentIrpStackLocation(Irp)->Parameters.DeviceIoControl.OutputBufferLength;
    PKBDLAYER_WAIT_KEY_EVENT_OUTPUT out = (PKBDLAYER_WAIT_KEY_EVENT_OUTPUT)Irp->AssociatedIrp.SystemBuffer;
    PKBDLAYER_KEY_EVENT events = (PKBDLAYER_KEY_EVENT)(out + 1);
    // 出力バッファの長さはControlDevice.cのハンドラで「出力の見出し＋1件以上」を検証済み。
    ULONG capacity = (outputLen - (ULONG)sizeof(KBDLAYER_WAIT_KEY_EVENT_OUTPUT)) / (ULONG)sizeof(KBDLAYER_KEY_EVENT);
    ULONG count = 0;
    ULONG dropped = 0;
    ULONG seq = lastSeq;
    KIRQL oldIrql;

    KeAcquireSpinLock(&g_KeyEventLock, &oldIrql);
    if (g_KeyEventCount > 0) {
        ULONG oldest = (g_KeyEventHead + KBDLAYER_KEY_EVENT_QUEUE_SIZE - g_KeyEventCount) % KBDLAYER_KEY_EVENT_QUEUE_SIZE;
        ULONG oldestSeq = g_KeyEvents[oldest].Seq;

        // LastSeqの次から最古のイベントの手前までは、リングから押し出された（概数）。
        if (KeySeqIsNewer(oldestSeq, lastSeq + 1)) {
            dropped = oldestSeq - lastSeq - 1;
        }
        for (ULONG i = 0; i < g_KeyEventCount && count < capacity; i++) {
            const KBDLAYER_KEY_EVENT* e = &g_KeyEvents[(oldest + i) % KBDLAYER_KEY_EVENT_QUEUE_SIZE];
            if (KeySeqIsNewer(e->Seq, lastSeq)) {
                events[count++] = *e;
                seq = e->Seq;
            }
        }
    }
    KeReleaseSpinLock(&g_KeyEventLock, oldIrql);

    // METHOD_BUFFERED: 入力（LastSeq）はStartWaitKeyEventで読み終えている。
    out->Seq = seq;
    out->Count = count;
    out->Dropped = dropped;
    out->Reserved = 0;

    Irp->IoStatus.Status = STATUS_SUCCESS;
    Irp->IoStatus.Information = sizeof(KBDLAYER_WAIT_KEY_EVENT_OUTPUT) + (ULONG_PTR)count * sizeof(KBDLAYER_KEY_EVENT);
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
}

// 返すイベントがある保留IRPを、すべて完了させる。ロック（EngineLock・OutputLock・ここのロック）を
// 保持せずに呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
KeyStreamFlush(
    VOID
    )
{
    KEY_WAIT_FILTER filter;
    KIRQL oldIrql;
    PIRP irp;

    KeAcquireSpinLock(&g_KeyEventLock, &oldIrql);
    filter.NewestSeq = g_KeyNewestSeq;
    KeReleaseSpinLock(&g_KeyEventLock, oldIrql);

    if (filter.NewestSeq == 0) {
        return; // まだ1件も積んでいない
    }
    filter.FileObject = NULL;
    filter.OnlyBehind = TRUE;

    while ((irp = IoCsqRemoveNextIrp(&g_KeyWaitCsq, &filter)) != NULL) {
        CompleteKeyWaitIrp(irp);
    }
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeKeyStream(
    VOID
    )
{
    KeInitializeSpinLock(&g_KeyEventLock);
    RtlZeroMemory(g_KeyEvents, sizeof(g_KeyEvents));
    g_KeyEventHead = 0;
    g_KeyEventCount = 0;
    g_KeyNewestSeq = 0;
    g_KeyFlushNeeded = 0;

    KeInitializeSpinLock(&g_KeyOwnerLock);
    RtlZeroMemory(g_KeyOwners, sizeof(g_KeyOwners));
    g_KeyOwnerCount = 0;

    KeInitializeSpinLock(&g_KeyWaitLock);
    InitializeListHead(&g_KeyWaitList);
    g_KeyWaitCount = 0;
    IoCsqInitializeEx(&g_KeyWaitCsq,
                      KeyCsqInsertIrpEx,
                      KeyCsqRemoveIrp,
                      KeyCsqPeekNextIrp,
                      KeyCsqAcquireLock,
                      KeyCsqReleaseLock,
                      KeyCsqCompleteCanceledIrp);
}

// エンコードは、キャリブレーションのCAPTURE_NEXT_KEYと同じ（LayerEngine.cのEncodeScancode16）:
// E0/E1の接頭辞を上位バイトに、MakeCodeの下位バイトを下位バイトに置く。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
KeyStreamQueueLocked(
    _In_ ULONG UniqId,
    _In_ USHORT ScanCode16,
    _In_ BOOLEAN IsBreak,
    _In_ ULONG TimeMs
    )
{
    KBDLAYER_KEY_EVENT* e;
    ULONG seq;

    // 0（未発行）とKBDLAYER_KEY_SEQ_LATEST（「今より後」の指定）は、イベントのSeqに使わない。
    seq = g_KeyNewestSeq + 1;
    if (seq == 0 || seq == KBDLAYER_KEY_SEQ_LATEST) {
        seq = 1;
    }
    g_KeyNewestSeq = seq;

    e = &g_KeyEvents[g_KeyEventHead];
    e->Seq = seq;
    e->UniqId = UniqId;
    e->ScanCode = ScanCode16;
    e->IsBreak = IsBreak ? 1 : 0;
    e->Reserved = 0;
    e->TimeMs = TimeMs;

    g_KeyEventHead = (g_KeyEventHead + 1) % KBDLAYER_KEY_EVENT_QUEUE_SIZE;
    if (g_KeyEventCount < KBDLAYER_KEY_EVENT_QUEUE_SIZE) {
        g_KeyEventCount++;
    }
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
KeyStreamObserve(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd
    )
{
    KIRQL oldIrql;
    ULONG timeMs;
    BOOLEAN queued = FALSE;

    // ホットパス: 所有者がいなければ、読み取り1回で戻る（単純な値の読み取り。境界でのレースは、
    // 高々、待ち始めた直後の1バッチ分の取りこぼしにしかならない）。
    if (g_KeyOwnerCount == 0) {
        // 所有者がいない間は積まないが、Pause の 2 イベント目の状態だけは追う（待ち始めた直後に続きの 1 件が届いて、
        // NumLock の押下として出ないように）。バッチの最後のエントリが E1 付きなら、次の 1 件を捨てる。
        if (InputDataEnd > InputDataStart) {
            devExt->KeyStreamSkipAfterE1 = ((InputDataEnd - 1)->Flags & KEY_E1) ? TRUE : FALSE;
        }
        return;
    }

    timeMs = (ULONG)(KeQueryInterruptTime() / 10000); // 100ns単位 → ms

    KeAcquireSpinLock(&g_KeyEventLock, &oldIrql);
    for (PKEYBOARD_INPUT_DATA p = InputDataStart; p < InputDataEnd; p++) {
        USHORT scan = (USHORT)(p->MakeCode & 0x00FF);
        USHORT prefix = 0;

        // Pause/Break は 2 イベント（{E1 1D}{45}、解放は {E1 9D}{C5}）で届き、2 イベント目には E1 が
        // 付かない（NumLock の 0x45 と同じに見える）。E1 付きの次の 1 件は、フラグに関わらず捨てる
        // （バッチをまたいでも効くよう、デバイスごとの状態で持つ。LayerEngine.c の CaptureSkipAfterE1 と同じ）。
        if (devExt->KeyStreamSkipAfterE1) {
            devExt->KeyStreamSkipAfterE1 = FALSE;
            continue;
        }
        if (p->Flags & KEY_E1) {
            devExt->KeyStreamSkipAfterE1 = TRUE;
            // 最初の 1D だけを E11D として出す（E1 付きのシーケンスは、Pause 以外には無い）。
            if (scan != 0x1D) {
                continue;
            }
            prefix = 0xE1;
        } else if (p->Flags & KEY_E0) {
            // PrintScreen等の前置きとして届く疑似Shift（E0 2A / E0 36）は、実キーではないので出さない
            // （本物の左右ShiftはE0無しの0x2A/0x36。LayerEngine.cのIsFakeShiftと同じ判定）。
            if (scan == 0x2A || scan == 0x36) {
                continue;
            }
            prefix = 0xE0;
        }
        KeyStreamQueueLocked(devExt->DeviceId, (USHORT)((prefix << 8) | scan), (p->Flags & KEY_BREAK) != 0, timeMs);
        queued = TRUE;
    }
    KeReleaseSpinLock(&g_KeyEventLock, oldIrql);

    if (queued) {
        // 待っているIRPの完了は、呼び出し元がロックを放した後（KeyStreamFlushIfNeeded）で行う。
        InterlockedExchange(&g_KeyFlushNeeded, 1);
    }
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
KeyStreamFlushIfNeeded(
    VOID
    )
{
    if (g_KeyFlushNeeded != 0 && InterlockedExchange(&g_KeyFlushNeeded, 0) != 0) {
        KeyStreamFlush();
    }
}

// ハンドルを所有者の表に載せる（すでに載っていれば何もしない）。表が満杯なら失敗。
_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
KeyStreamRegisterOwner(
    _In_ PFILE_OBJECT FileObject
    )
{
    KIRQL oldIrql;
    NTSTATUS status = STATUS_INSUFFICIENT_RESOURCES;
    LONG freeSlot = -1;

    KeAcquireSpinLock(&g_KeyOwnerLock, &oldIrql);
    for (LONG i = 0; i < KBDLAYER_MAX_KEY_STREAM_OWNERS; i++) {
        if (g_KeyOwners[i] == FileObject) {
            status = STATUS_SUCCESS;
            freeSlot = -2;
            break;
        }
        if (g_KeyOwners[i] == NULL && freeSlot < 0) {
            freeSlot = i;
        }
    }
    if (freeSlot >= 0) {
        g_KeyOwners[freeSlot] = FileObject;
        InterlockedIncrement(&g_KeyOwnerCount);
        status = STATUS_SUCCESS;
    }
    KeReleaseSpinLock(&g_KeyOwnerLock, oldIrql);
    return status;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
StartWaitKeyEvent(
    _In_ PIRP Irp,
    _In_ ULONG LastSeq
    )
{
    NTSTATUS status;
    KIRQL oldIrql;
    PFILE_OBJECT fileObject = IoGetCurrentIrpStackLocation(Irp)->FileObject;

    // 待ち始めたハンドルを所有者にする（以後、ハンドルを閉じるまで、WAITの合間もイベントを積む）。
    status = KeyStreamRegisterOwner(fileObject);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    if (LastSeq == KBDLAYER_KEY_SEQ_LATEST) {
        // 「今より後」を待つ。入れる前に最新のSeqに置き換える（この直後に積まれたイベントは、
        // 下のKeyStreamFlushが返す）。
        KeAcquireSpinLock(&g_KeyEventLock, &oldIrql);
        LastSeq = g_KeyNewestSeq;
        KeReleaseSpinLock(&g_KeyEventLock, oldIrql);
    }
    Irp->Tail.Overlay.DriverContext[0] = (PVOID)(ULONG_PTR)LastSeq;

    // 成功するとIoCsqInsertIrpExがIoMarkIrpPendingする。
    status = IoCsqInsertIrpEx(&g_KeyWaitCsq, Irp, NULL, NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    // 先にキューへ入れてから、返せるイベントがあるかを見る（HelperNotify.cと同じ順）。
    KeyStreamFlush();
    return STATUS_PENDING;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
KeyStreamCleanupOwner(
    _In_ PFILE_OBJECT FileObject
    )
{
    KEY_WAIT_FILTER filter;
    PIRP irp;
    KIRQL oldIrql;

    // 先に所有者から外す（以後、積まなくなる）。
    KeAcquireSpinLock(&g_KeyOwnerLock, &oldIrql);
    for (LONG i = 0; i < KBDLAYER_MAX_KEY_STREAM_OWNERS; i++) {
        if (g_KeyOwners[i] == FileObject) {
            g_KeyOwners[i] = NULL;
            InterlockedDecrement(&g_KeyOwnerCount);
            break;
        }
    }
    KeReleaseSpinLock(&g_KeyOwnerLock, oldIrql);

    filter.FileObject = FileObject;
    filter.OnlyBehind = FALSE;
    filter.NewestSeq = 0;

    while ((irp = IoCsqRemoveNextIrp(&g_KeyWaitCsq, &filter)) != NULL) {
        irp->IoStatus.Status = STATUS_CANCELLED;
        irp->IoStatus.Information = 0;
        IoCompleteRequest(irp, IO_NO_INCREMENT);
    }
}
