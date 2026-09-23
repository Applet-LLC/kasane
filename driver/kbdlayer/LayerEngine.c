// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

#include <ntddk.h>
#include <wdm.h>
#include <ntstrsafe.h>
#include "DeviceExtension.h"
#include "LayerEngine.h"
#include "..\\common\\regutil.h"
#include "token.h" // KBDLAYER_TRACE

#ifndef DBG
#define DbgPrint(...) ((void)0)
#endif

// KEYBOARD_INPUT_DATA.Flags のビット定義。kbdmou.h経由では取り込まれない場合が
// あるため、kbdaddid/RemapTable.c と同じ流儀で防御的に定義する。
#ifndef KEY_MAKE
#define KEY_MAKE  0x00
#endif
#ifndef KEY_BREAK
#define KEY_BREAK 0x01
#endif
#ifndef KEY_E0
#define KEY_E0    0x02
#endif
#ifndef KEY_E1
#define KEY_E1    0x04
#endif

#define KBDLAYER_SERVICE_NAME L"kbdlayer"
#define KBDLAYER_MAP_VALUE_NAME L"LayerMap"
#define KBDLAYER_ENABLED_VALUE_NAME L"Enabled"
#define KBDLAYER_TAPTERM_VALUE_NAME L"TapTermMs"
#define KBDLAYER_MACROS_VALUE_NAME L"Macros"
#define KBDLAYER_COMBOS_VALUE_NAME L"Combos"
#define KBDLAYER_COMBOTERM_VALUE_NAME L"ComboTermMs"
#define KBDLAYER_REGTEMP_POOL_TAG 'tLdK'

// マクロの永続化形式（レジストリ"Macros"値）。KBDLAYER_MAX_MACRO_COUNT個ぶん
// 固定サイズで連続して並べる。devExt->MacroStepCounts/Macros（別々の配列）とは
// レイアウトが異なるため、そのままmemcpyはできず明示的なシリアライズ/
// デシリアライズが必要（PersistAllMacros/LoadPersistedMacrosForDevice参照）。
typedef struct _KBDLAYER_MACRO_PERSIST_ENTRY {
    ULONG StepCount;
    KBDLAYER_MACRO_STEP Steps[KBDLAYER_MAX_MACRO_STEPS];
} KBDLAYER_MACRO_PERSIST_ENTRY, *PKBDLAYER_MACRO_PERSIST_ENTRY;

#define KBDLAYER_MACRO_PERSIST_BLOB_BYTES \
    (KBDLAYER_MAX_MACRO_COUNT * sizeof(KBDLAYER_MACRO_PERSIST_ENTRY))

// コンボの永続化形式（レジストリ"Combos"値）。マクロと同じ方式で、
// KBDLAYER_MAX_COMBO_COUNT個ぶん固定サイズで連続して並べる。
typedef struct _KBDLAYER_COMBO_PERSIST_ENTRY {
    ULONG Used;
    KBDLAYER_COMBO_ENTRY Entry;
} KBDLAYER_COMBO_PERSIST_ENTRY, *PKBDLAYER_COMBO_PERSIST_ENTRY;

#define KBDLAYER_COMBO_PERSIST_BLOB_BYTES \
    (KBDLAYER_MAX_COMBO_COUNT * sizeof(KBDLAYER_COMBO_PERSIST_ENTRY))

// ---------------------------------------------------------------------------
// スキャンコードの16bitエンコード/デコード（OS標準Scancode Mapと同じ規約。
// kbdaddid/RemapTable.c の EncodeScancode16/DecodeScancode16 と同一実装）
// ---------------------------------------------------------------------------

_IRQL_requires_max_(DISPATCH_LEVEL)
static USHORT
EncodeScancode16(
    _In_ USHORT MakeCode,
    _In_ USHORT Flags
    )
{
    USHORT prefix = 0;
    if (Flags & KEY_E1) {
        prefix = 0xE1;
    } else if (Flags & KEY_E0) {
        prefix = 0xE0;
    }
    return (USHORT)((prefix << 8) | (MakeCode & 0x00FF));
}

_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
DecodeScancode16(
    _In_ USHORT ScanCode16,
    _Inout_ PUSHORT MakeCode,
    _Inout_ PUSHORT Flags
    )
{
    UCHAR prefix = (UCHAR)(ScanCode16 >> 8);
    USHORT newFlags = (USHORT)(*Flags & ~(KEY_E0 | KEY_E1));

    if (prefix == 0xE1) {
        newFlags |= KEY_E1;
    } else if (prefix == 0xE0) {
        newFlags |= KEY_E0;
    }

    *MakeCode = (USHORT)(ScanCode16 & 0x00FF);
    *Flags = newFlags;
}

// ---------------------------------------------------------------------------
// blob(KBDLAYER_MAP_HEADER形式) <-> 内部表現(KBDLAYER_MAP_TABLE)
// ---------------------------------------------------------------------------

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
ParseLayerMapBlob(
    _In_reads_bytes_(BlobLength) PVOID Blob,
    _In_ ULONG BlobLength,
    _Outptr_ PKBDLAYER_MAP_TABLE* OutTable
    )
{
    PKBDLAYER_MAP_HEADER header;
    PKBDLAYER_KEY_ENTRY entries;
    PKBDLAYER_MAP_TABLE newTable;
    ULONG expectedLength;
    ULONG tableSize;

    *OutTable = NULL;

    if (Blob == NULL || BlobLength < sizeof(KBDLAYER_MAP_HEADER)) {
        return STATUS_INVALID_PARAMETER;
    }

    header = (PKBDLAYER_MAP_HEADER)Blob;
    if (header->Header != 0) {
        return STATUS_INVALID_PARAMETER;
    }
    if (header->LayerCount < 1 || header->LayerCount > KBDLAYER_MAX_LAYERS) {
        return STATUS_INVALID_PARAMETER;
    }
    if (header->KeyCount > KBDLAYER_MAX_KEYS) {
        return STATUS_INVALID_PARAMETER;
    }

    expectedLength = sizeof(KBDLAYER_MAP_HEADER) + header->KeyCount * sizeof(KBDLAYER_KEY_ENTRY);
    if (BlobLength != expectedLength) {
        return STATUS_INVALID_PARAMETER;
    }

    entries = (PKBDLAYER_KEY_ENTRY)((PUCHAR)Blob + sizeof(KBDLAYER_MAP_HEADER));

    // Action.Type が未知の値を含んでいないか検証する（将来のPhase向けに予約された
    // 値も含め、このドライババイナリが理解できるTypeのみを許可する）。
    for (ULONG k = 0; k < header->KeyCount; k++) {
        for (ULONG L = 0; L < header->LayerCount; L++) {
            UCHAR type = entries[k].Actions[L].Type;
            if (type > KBDLAYER_ACTION_CUSTOM) {
                return STATUS_INVALID_PARAMETER;
            }
        }
    }

    tableSize = sizeof(KBDLAYER_MAP_TABLE) + header->KeyCount * sizeof(KBDLAYER_KEY_ENTRY);
    newTable = (PKBDLAYER_MAP_TABLE)ExAllocatePool2(POOL_FLAG_NON_PAGED, tableSize, KBDLAYER_MAP_POOL_TAG);
    if (newTable == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    newTable->LayerCount = header->LayerCount;
    newTable->KeyCount = header->KeyCount;
    if (header->KeyCount > 0) {
        RtlCopyMemory(newTable->Entries, entries, header->KeyCount * sizeof(KBDLAYER_KEY_ENTRY));
    }

    *OutTable = newTable;
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
static ULONG
LayerMapTableBlobSize(
    _In_opt_ PKBDLAYER_MAP_TABLE Table
    )
{
    if (Table == NULL) {
        return 0;
    }
    return sizeof(KBDLAYER_MAP_HEADER) + Table->KeyCount * sizeof(KBDLAYER_KEY_ENTRY);
}

_IRQL_requires_max_(DISPATCH_LEVEL)
static NTSTATUS
SerializeLayerMapTableToBlob(
    _In_opt_ PKBDLAYER_MAP_TABLE Table,
    _Out_writes_bytes_to_opt_(BufferLength, *BytesWritten) PVOID Buffer,
    _In_ ULONG BufferLength,
    _Out_ PULONG BytesWritten
    )
{
    ULONG required = LayerMapTableBlobSize(Table);
    PKBDLAYER_MAP_HEADER header;
    PKBDLAYER_KEY_ENTRY entries;

    *BytesWritten = required;

    if (Table == NULL) {
        return STATUS_SUCCESS;
    }
    if (Buffer == NULL || BufferLength < required) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    header = (PKBDLAYER_MAP_HEADER)Buffer;
    header->Header = 0;
    header->LayerCount = Table->LayerCount;
    header->KeyCount = Table->KeyCount;

    entries = (PKBDLAYER_KEY_ENTRY)((PUCHAR)Buffer + sizeof(KBDLAYER_MAP_HEADER));
    if (Table->KeyCount > 0) {
        RtlCopyMemory(entries, Table->Entries, Table->KeyCount * sizeof(KBDLAYER_KEY_ENTRY));
    }

    *BytesWritten = required;
    return STATUS_SUCCESS;
}

// ---------------------------------------------------------------------------
// ホットパス
// ---------------------------------------------------------------------------

_IRQL_requires_max_(DISPATCH_LEVEL)
PKEYBOARD_INPUT_DATA
CompactInputDataArray(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _In_ PKBDLAYER_SHOULD_DROP_ENTRY ShouldDropAndTransform,
    _In_opt_ PVOID Context
    )
{
    PKEYBOARD_INPUT_DATA writePtr = InputDataStart;
    PKEYBOARD_INPUT_DATA readPtr;

    for (readPtr = InputDataStart; readPtr < InputDataEnd; readPtr++) {
        BOOLEAN drop = ShouldDropAndTransform(devExt, readPtr, Context);
        if (!drop) {
            if (writePtr != readPtr) {
                *writePtr = *readPtr;
            }
            writePtr++;
        }
    }
    return writePtr;
}

// devExt->EngineLock 保持中に呼ばれる前提。table->Entries を線形探索して
// SourceScanCode が一致するキーを探す（見つからなければNULL）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static PKBDLAYER_KEY_ENTRY
FindKeyEntry(
    _In_ PKBDLAYER_MAP_TABLE table,
    _In_ USHORT SourceScanCode
    )
{
    for (ULONG i = 0; i < table->KeyCount; i++) {
        if (table->Entries[i].SourceScanCode == SourceScanCode) {
            return &table->Entries[i];
        }
    }
    return NULL;
}

// [レイヤー解決規則] ActiveLayerMask(+DefaultLayerビット)の中で最も高いレイヤー
// 番号から順に、KBDLAYER_ACTION_TRANSPARENT でないアクションを探す
// （QMKの「上位レイヤー優先、透過ならフォールスルー」と同じ規則）。
// devExt->EngineLock保持中に呼ばれる前提。
_IRQL_requires_max_(DISPATCH_LEVEL)
static PKBDLAYER_ACTION
ResolveEffectiveAction(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKBDLAYER_MAP_TABLE table,
    _In_ PKBDLAYER_KEY_ENTRY entry
    )
{
    ULONG effectiveMask = devExt->ActiveLayerMask | (1UL << devExt->DefaultLayer);

    for (LONG layer = (LONG)table->LayerCount - 1; layer >= 0; layer--) {
        if (!(effectiveMask & (1UL << layer))) {
            continue;
        }
        if (entry->Actions[layer].Type != KBDLAYER_ACTION_TRANSPARENT) {
            return &entry->Actions[layer];
        }
    }
    return NULL; // どのレイヤーでも透過のまま -> 実質KC_NO
}

// devExt->EngineLock保持中に呼ばれる前提。CompactInputDataArrayのコールバックとして
// 1エントリずつ処理し、MO/TG/DF/OSLの副作用（ActiveLayerMask等の更新）もここで行う。
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN
LayerEngineShouldDropEntry(
    _In_ PDEVICE_EXTENSION devExt,
    _Inout_ PKEYBOARD_INPUT_DATA Entry,
    _In_opt_ PVOID Context
    )
{
    PKBDLAYER_MAP_TABLE table = devExt->LayerMap; // EngineLock保持中に呼ばれる前提
    PKBDLAYER_KEY_ENTRY keyEntry;
    PKBDLAYER_ACTION action;
    USHORT src;
    BOOLEAN isBreak;

    UNREFERENCED_PARAMETER(Context);

    if (table == NULL) {
        return FALSE;
    }

    src = EncodeScancode16(Entry->MakeCode, Entry->Flags);
    isBreak = (Entry->Flags & KEY_BREAK) != 0;

    keyEntry = FindKeyEntry(table, src);
    if (keyEntry == NULL) {
        // レイヤーマップで管理されていない物理キー（Fnキー・電源キー等）はそのまま素通しする。
        return FALSE;
    }

    action = ResolveEffectiveAction(devExt, table, keyEntry);
    if (action == NULL) {
        return TRUE; // 全レイヤーで透過 = 実質KC_NO。出力しない。
    }

    switch (action->Type) {
    case KBDLAYER_ACTION_NONE:
        return TRUE; // 出力しない

    case KBDLAYER_ACTION_KEY:
        DecodeScancode16(action->Param2, &Entry->MakeCode, &Entry->Flags);
        if (!isBreak && devExt->OneShotLayerPending) {
            // OSL(layer)発動後、最初に解決された実キーでワンショットを消費する。
            devExt->ActiveLayerMask &= ~devExt->OneShotLayerBit;
            devExt->OneShotLayerPending = FALSE;
        }
        return FALSE; // 出力する（スキャンコードは書き換え済み）

    case KBDLAYER_ACTION_MOMENTARY:
        if (action->Param1 < KBDLAYER_MAX_LAYERS) {
            ULONG bit = 1UL << action->Param1;
            if (!isBreak) {
                devExt->ActiveLayerMask |= bit;
            } else {
                devExt->ActiveLayerMask &= ~bit;
            }
        }
        return TRUE; // 出力しない

    case KBDLAYER_ACTION_TOGGLE:
        if (!isBreak && action->Param1 < KBDLAYER_MAX_LAYERS) {
            devExt->ActiveLayerMask ^= (1UL << action->Param1);
        }
        return TRUE; // 出力しない

    case KBDLAYER_ACTION_DEFAULT:
        if (!isBreak && action->Param1 < KBDLAYER_MAX_LAYERS) {
            devExt->DefaultLayer = action->Param1;
        }
        return TRUE; // 出力しない

    case KBDLAYER_ACTION_ONESHOT_LAYER:
        if (!isBreak && action->Param1 < KBDLAYER_MAX_LAYERS) {
            ULONG bit = 1UL << action->Param1;
            devExt->ActiveLayerMask |= bit;
            devExt->OneShotLayerPending = TRUE;
            devExt->OneShotLayerBit = bit;
        }
        return TRUE; // 出力しない

    default:
        // MODTAP/LAYERTAPはResolveTapHold、MACROはResolveMacro（どちらもServiceCallback
        // からResolveLayerActionより前に呼ばれる、Driver.c参照）が先に横取りして処理する
        // ため、通常はここに到達しない。ここに来るのはOSM/CUSTOM（未実装、予約のみ）や、
        // 複数件バッチの一部として届いたためResolveTapHold/ResolveMacroがスキップした
        // MT/LT/MACROキーなど、想定外の経路のみ。
        // 安全側に倒して「何もしない」として扱う（ParseLayerMapBlobで型自体は
        // 許可しているため、KeymapTool側が未対応アクションを送ってきてもクラッシュしない）。
        return TRUE;
    }
}

_IRQL_requires_max_(DISPATCH_LEVEL)
PKEYBOARD_INPUT_DATA
ResolveLayerAction(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd
    )
{
    KIRQL oldIrql;
    PKEYBOARD_INPUT_DATA newEnd;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);

    // キャリブレーションウィザード: レイヤーマップの適用有無に関わらず、
    // CaptureActive中は最初に見つかった押下イベントのスキャンコードを記録する。
    if (devExt->CaptureActive) {
        for (PKEYBOARD_INPUT_DATA p = InputDataStart; p < InputDataEnd; p++) {
            if (!(p->Flags & KEY_BREAK)) {
                devExt->CapturedScanCode = EncodeScancode16(p->MakeCode, p->Flags);
                devExt->CaptureActive = FALSE;
                KeSetEvent(&devExt->CaptureEvent, IO_NO_INCREMENT, FALSE);
                break;
            }
        }
    }

    if (!devExt->LayerMapEnabled || devExt->LayerMap == NULL) {
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return InputDataEnd;
    }

    newEnd = CompactInputDataArray(devExt, InputDataStart, InputDataEnd, LayerEngineShouldDropEntry, NULL);
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
    return newEnd;
}

// ---------------------------------------------------------------------------
// Phase 2: Mod-Tap(MT)/Layer-Tap(LT) -- Hold On Other Key Press方式
//
// phase2-spikeブランチでの実機検証（該当コミット群参照）を踏まえた実装。
// 検証済み: 1回のServiceCallback呼び出し内でOriginalClassServiceを2回呼ぶ
// パターン（他キー押下によるホールド確定）はkbdclass.sysで安全に動作する。
// 未検証: HoldDpc（タイムアウト経路）からのOriginalClassService自発呼び出し。
// これはMODTAPがタイムアウトで確定したときにのみ発生する（LAYERTAPのタイムアウト
// 確定はActiveLayerMaskのビット操作のみで、OriginalClassServiceを呼ばない）。
// 実機テストではこの経路を重点的に確認すること。
// ---------------------------------------------------------------------------

// QMKのMOD_LCTL等に対応するビット番号（KeymapTool側のKeycodeTable.cppの
// ModifierBitTableと同じ割り当て）から、Enc16形式のスキャンコードへの変換表。
static const USHORT s_ModifierScanCodes[8] = {
    /* 0: LCTL */ 0x001D,
    /* 1: LSFT */ 0x002A,
    /* 2: LALT */ 0x0038,
    /* 3: LGUI */ 0xE05B,
    /* 4: RCTL */ 0xE01D,
    /* 5: RSFT */ 0x0036,
    /* 6: RALT */ 0xE038,
    /* 7: RGUI */ 0xE05C,
};

// 1件のKEYBOARD_INPUT_DATAを単独でOriginalClassServiceへ送る。devExt->EngineLockを
// 保持していない状態で呼ぶこと。SystemArgument3には実IRPに紐づかない自発呼び出し
// である旨、書き込み可能なダミー領域を渡す（書き込まれる値そのものに意味は無い）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
SendSyntheticEvent(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ USHORT MakeCode,
    _In_ USHORT Flags
    )
{
    KEYBOARD_INPUT_DATA ev;
    ULONG dummyConsumed = 0;

    RtlZeroMemory(&ev, sizeof(ev));
    ev.MakeCode = MakeCode;
    ev.Flags = Flags;
    ev.ExtraInformation = devExt->DeviceId & 0xFFFF0000UL;

    devExt->OriginalClassService(
        devExt->OriginalClassDeviceObject,
        &ev,
        &ev + 1,
        &dummyConsumed);
}

// Enc16形式のスキャンコードをデコードし、BreakBit（KEY_MAKEまたはKEY_BREAK）を
// 合成して単独イベントとして送る。devExt->EngineLockを保持していない状態で
// 呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
SendDecodedEvent(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ USHORT EncodedScanCode,
    _In_ USHORT BreakBit
    )
{
    USHORT makeCode;
    USHORT flags = BreakBit;

    DecodeScancode16(EncodedScanCode, &makeCode, &flags);
    SendSyntheticEvent(devExt, makeCode, flags);
}

// actionがMODTAPなら、対応する修飾キーのmake/breakイベントを合成して送る
// （LAYERTAPの場合は何もしない -- ActiveLayerMaskのビット操作だけで完結し、
// キーイベントの合成は不要なため）。devExt->EngineLockを保持していない状態で
// 呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
EmitModTapEventIfApplicable(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKBDLAYER_ACTION Action,
    _In_ USHORT BreakBit
    )
{
    if (Action->Type == KBDLAYER_ACTION_MODTAP && Action->Param1 < RTL_NUMBER_OF(s_ModifierScanCodes)) {
        SendDecodedEvent(devExt, s_ModifierScanCodes[Action->Param1], BreakBit);
    }
}

// devExt->EngineLock保持中に呼ぶこと。ホールド確定処理のうちロックが必要な部分
// （PendingActionのコピー・LAYERTAPならActiveLayerMaskのビットセット・
// PendingActive/HoldConfirmedの更新）だけを行う。MODTAPの合成イベント送出は
// EmitModTapEventIfApplicableでロック解放後に呼び出し元が行うこと
// （OriginalClassServiceをEngineLock保持中に呼ばない、という既存の規律を踏襲）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
ConfirmHoldLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _Out_ PKBDLAYER_ACTION OutAction
    )
{
    *OutAction = devExt->PendingAction;
    if (OutAction->Type == KBDLAYER_ACTION_LAYERTAP && OutAction->Param1 < KBDLAYER_MAX_LAYERS) {
        devExt->ActiveLayerMask |= (1UL << OutAction->Param1);
    }
    devExt->PendingActive = FALSE;
    devExt->HoldConfirmed = TRUE;
}

// HoldTimerの満了で起動されるDPC（タイムアウト経路。他キー押下による割り込みが
// 無いままTapTermMsが経過した場合にホールド確定する）。DISPATCH_LEVELで実行される。
// *** phase2-spikeブランチで未検証の経路。実機テストで重点的に確認すること ***
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
HoldTapDpcRoutine(
    _In_ PKDPC Dpc,
    _In_opt_ PVOID DeferredContext,
    _In_opt_ PVOID SystemArgument1,
    _In_opt_ PVOID SystemArgument2
    )
{
    PDEVICE_EXTENSION devExt = (PDEVICE_EXTENSION)DeferredContext;
    KIRQL oldIrql;
    KBDLAYER_ACTION action;
    USHORT pendingScanCode = 0;
    BOOLEAN shouldConfirm;

    UNREFERENCED_PARAMETER(Dpc);
    UNREFERENCED_PARAMETER(SystemArgument1);
    UNREFERENCED_PARAMETER(SystemArgument2);

    if (devExt == NULL) {
        return;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    // PendingActiveがFALSEなら、ServiceCallback側（他キー押下によるホールド確定、
    // またはタップ確定としての解放）が既にこのサイクルを終えている
    // （KeCancelTimerとこのDPCのキューイングが競合した場合に起こりうる）。
    // その場合は何もしない。ServiceCallback経由とDPC経由の競合排他はこの
    // EngineLockで行っている。
    shouldConfirm = devExt->PendingActive;
    if (shouldConfirm) {
        pendingScanCode = devExt->PendingScanCode;
        ConfirmHoldLocked(devExt, &action);
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    if (shouldConfirm) {
        KBDLAYER_TRACE("LayerEngine: tap-hold TIMEOUT confirmed sc=0x%X type=%u\n",
                       pendingScanCode, action.Type);
        EmitModTapEventIfApplicable(devExt, &action, KEY_MAKE);
    }
}

_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
ResolveTapHold(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _Inout_opt_ PVOID SystemArgument3
    )
{
    PKEYBOARD_INPUT_DATA ev;
    USHORT scanCode;
    BOOLEAN isBreak;
    KIRQL oldIrql;

    // 1件バッチのみを対象とする。複数件バッチ（コアレス）はホットパスの単純さを
    // 保つため対象外とし、通常のResolveLayerAction側の処理に委ねる（MT/LTキー
    // 自体が複数件バッチの一部として届いた場合、タップ/ホールド判定は行われず
    // 通常のKEYアクションと同様に解決される。既知のスコープ限定）。
    if ((InputDataEnd - InputDataStart) != 1) {
        return FALSE;
    }

    ev = InputDataStart;
    scanCode = EncodeScancode16(ev->MakeCode, ev->Flags);
    isBreak = (ev->Flags & KEY_BREAK) != 0;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);

    if (!devExt->LayerMapEnabled || devExt->LayerMap == NULL) {
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return FALSE;
    }

    // --- このイベントが、現在保留中/ホールド中のMT/LTキー自身のものか ---
    if ((devExt->PendingActive || devExt->HoldConfirmed) && scanCode == devExt->PendingScanCode) {
        if (!isBreak) {
            // 押しっぱなし中のオートリピートMAKE。状態を変えず黙って飲み込む
            // （phase2-spikeで踏み抜いた「オートリピートが確定済み状態を巻き戻す」
            // バグの教訓）。
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            if (SystemArgument3 != NULL) {
                *(PULONG)SystemArgument3 = 1;
            }
            return TRUE;
        }

        // 解放。
        BOOLEAN wasHoldConfirmed = devExt->HoldConfirmed;
        BOOLEAN wasPending = devExt->PendingActive;
        KBDLAYER_ACTION action = devExt->PendingAction;

        devExt->PendingActive = FALSE;
        devExt->HoldConfirmed = FALSE;
        if (wasHoldConfirmed && action.Type == KBDLAYER_ACTION_LAYERTAP &&
            action.Param1 < KBDLAYER_MAX_LAYERS) {
            devExt->ActiveLayerMask &= ~(1UL << action.Param1);
        }
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

        // 保留サイクルはここで終わるので、タイマーが未発火ならキャンセルする。
        // DPCが既にキューされていた場合KeCancelTimerはFALSEを返すが、DPC側は
        // PendingActiveを見て自分で無害化するので競合しても安全
        // （HoldTapDpcRoutine参照。EngineLockで直列化している）。
        KeCancelTimer(&devExt->HoldTimer);

        if (wasHoldConfirmed) {
            EmitModTapEventIfApplicable(devExt, &action, KEY_BREAK);
        } else if (wasPending) {
            // 保留中に他キーが割り込まず、タイムアウトも発火しなかった = タップ確定。
            SendDecodedEvent(devExt, action.Param2, KEY_MAKE);
            SendDecodedEvent(devExt, action.Param2, KEY_BREAK);
        }

        if (SystemArgument3 != NULL) {
            *(PULONG)SystemArgument3 = 1;
        }
        return TRUE;
    }

    // --- 保留中（未確定）に、別のキーの押下が来た場合 = ホールド確定 ---
    // 【検証対象】1回のServiceCallback呼び出しの中でOriginalClassServiceを
    // 最大2回呼ぶ（MODTAPの場合: 修飾キー合成 + 元バッチ転送。LAYERTAPの場合は
    // ActiveLayerMaskの操作だけなので元バッチ転送の1回のみ）。
    if (devExt->PendingActive && !devExt->HoldConfirmed && !isBreak) {
        KBDLAYER_ACTION action;

        ConfirmHoldLocked(devExt, &action);
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

        KeCancelTimer(&devExt->HoldTimer);

        EmitModTapEventIfApplicable(devExt, &action, KEY_MAKE);

        // 続けて元のバッチ（別キーのイベント）をそのままOriginalClassServiceに
        // 渡す。このキーはレイヤーエンジンでの再解決を経ない生のスキャンコードの
        // まま転送される（プラン記載の設計通り。既知の制約: 割り込んだキー自身が
        // 現在アクティブなレイヤーの影響を受けない）。
        for (PKEYBOARD_INPUT_DATA p = InputDataStart; p < InputDataEnd; p++) {
            p->ExtraInformation = (p->ExtraInformation & 0x0000FFFFUL) | (devExt->DeviceId & 0xFFFF0000UL);
        }
        devExt->OriginalClassService(
            devExt->OriginalClassDeviceObject,
            InputDataStart,
            InputDataEnd,
            SystemArgument3);
        return TRUE;
    }

    // --- 現在アイドル（保留中でもホールド中でもない）で、このキー自体が
    //     MT/LTに解決される押下イベントなら、新しい保留サイクルを開始する ---
    if (!devExt->PendingActive && !devExt->HoldConfirmed && !isBreak) {
        PKBDLAYER_KEY_ENTRY keyEntry = FindKeyEntry(devExt->LayerMap, scanCode);
        if (keyEntry != NULL) {
            PKBDLAYER_ACTION action = ResolveEffectiveAction(devExt, devExt->LayerMap, keyEntry);
            if (action != NULL &&
                (action->Type == KBDLAYER_ACTION_MODTAP || action->Type == KBDLAYER_ACTION_LAYERTAP)) {
                LARGE_INTEGER dueTime;

                devExt->PendingActive = TRUE;
                devExt->HoldConfirmed = FALSE;
                devExt->PendingScanCode = scanCode;
                devExt->PendingAction = *action;
                KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

                dueTime.QuadPart = -((LONGLONG)devExt->TapTermMs * 10000LL);
                KeSetTimer(&devExt->HoldTimer, dueTime, &devExt->HoldDpc);

                if (SystemArgument3 != NULL) {
                    *(PULONG)SystemArgument3 = 1;
                }
                return TRUE;
            }
        }
    }

    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
    return FALSE;
}

// ---------------------------------------------------------------------------
// Phase 3: マクロ（MACRO(id)）
//
// MT/LTのようなタップ/ホールドの時間的な曖昧さが無いため、Phase 2のタイマー
// 基盤は不要。押下イベントを受け取ったその場で、マクロの各ステップぶん
// OriginalClassServiceを逐次呼ぶだけ（プラン記載の通り）。ResolveTapHoldと同じく
// ServiceCallbackからResolveLayerActionより前に呼ばれる、独立した横取りパス。
// ---------------------------------------------------------------------------

_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
ResolveMacro(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _Inout_opt_ PVOID SystemArgument3
    )
{
    PKEYBOARD_INPUT_DATA ev;
    USHORT scanCode;
    BOOLEAN isBreak;
    KIRQL oldIrql;
    PKBDLAYER_KEY_ENTRY keyEntry;
    PKBDLAYER_ACTION action;
    ULONG macroId;
    ULONG stepCount;
    KBDLAYER_MACRO_STEP localSteps[KBDLAYER_MAX_MACRO_STEPS];

    // ResolveTapHoldと同じ理由で1件バッチのみを対象とする。
    if ((InputDataEnd - InputDataStart) != 1) {
        return FALSE;
    }

    ev = InputDataStart;
    scanCode = EncodeScancode16(ev->MakeCode, ev->Flags);
    isBreak = (ev->Flags & KEY_BREAK) != 0;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);

    if (!devExt->LayerMapEnabled || devExt->LayerMap == NULL) {
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return FALSE;
    }

    keyEntry = FindKeyEntry(devExt->LayerMap, scanCode);
    if (keyEntry == NULL) {
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return FALSE;
    }

    action = ResolveEffectiveAction(devExt, devExt->LayerMap, keyEntry);
    if (action == NULL || action->Type != KBDLAYER_ACTION_MACRO) {
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return FALSE;
    }

    // マクロキー自身の生のスキャンコードは押下・解放とも転送しない
    // （解放は必ず飲み込む。対応する押下も転送していないため、Phase 2の
    // スパイクで踏み抜いた「飲み込んでいない押下の解放まで飲み込む」バグとは
    // 逆に、こちらは「押下を飲み込んだなら対応する解放も必ず飲み込む」が
    // 常に成立する設計 -- マクロキーの押下/解放はタップ/ホールドのような
    // 時間差判定が無く、このServiceCallback呼び出し単体で完結するため）。
    if (isBreak) {
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        if (SystemArgument3 != NULL) {
            *(PULONG)SystemArgument3 = 1;
        }
        return TRUE;
    }

    // KEYアクションと同様、マクロの実行も「実際に何かを送出する行為」なので
    // OSL(layer)発動中の一発消費として扱う（LayerEngineShouldDropEntryのKEYケースと
    // 同じ規則）。
    if (devExt->OneShotLayerPending) {
        devExt->ActiveLayerMask &= ~devExt->OneShotLayerBit;
        devExt->OneShotLayerPending = FALSE;
    }

    macroId = action->Param1;
    if (macroId >= KBDLAYER_MAX_MACRO_COUNT) {
        // 不正なマクロID（KeymapTool側のバグ等）。安全側に倒して何もしない。
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        if (SystemArgument3 != NULL) {
            *(PULONG)SystemArgument3 = 1;
        }
        return TRUE;
    }

    stepCount = devExt->MacroStepCounts[macroId];
    if (stepCount > KBDLAYER_MAX_MACRO_STEPS) {
        stepCount = KBDLAYER_MAX_MACRO_STEPS; // 念のための防御（本来起こり得ない）
    }
    if (stepCount > 0) {
        RtlCopyMemory(localSteps, devExt->Macros[macroId], stepCount * sizeof(KBDLAYER_MACRO_STEP));
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    // OriginalClassServiceはEngineLockを解放してから呼ぶ（既存の規律を踏襲）。
    // 未設定のマクロID（stepCount==0）を押しても単に何も出力しないだけで安全。
    // localSteps[i].ScanCodeはEnc16形式なので、SendDecodedEventでMakeCode/Flagsへ
    // デコードしてから送る（SendSyntheticEventは既にデコード済みのMakeCode/Flagsを
    // 直接取る低レベルヘルパーで、ここでは使わない）。
    for (ULONG i = 0; i < stepCount; i++) {
        USHORT breakBit = localSteps[i].IsBreak ? KEY_BREAK : KEY_MAKE;
        SendDecodedEvent(devExt, localSteps[i].ScanCode, breakBit);
    }

    if (SystemArgument3 != NULL) {
        *(PULONG)SystemArgument3 = 1;
    }
    return TRUE;
}

// ---------------------------------------------------------------------------
// Phase 4: コンボ（2キー限定 v1） -- Phase 2の「保留＋タイマー」機構を再利用
//
// common/kbdlayer_ioctl.hに記載のスコープ限定（2キー限定・重複禁止・出力は
// KEY型のみ・解除はいずれかのメンバー解放時点）を前提にした実装。重複を
// 禁止しているため、押されたキーがコンボメンバーであれば相方は一意に定まり、
// Phase 2のPendingActiveと同様に単一の保留スロットで足りる（候補集合を
// 絞り込むロジックは不要）。
//
// 状態は3つの独立フラグで管理する（Phase 2のPendingActive/HoldConfirmedと
// 同じ「独立フラグ」方式。1つの履歴から推測するのではなく、各状態を明示的に
// 持つことで遷移の見通しを良くする）:
//   ComboPendingActive: 最初のメンバー押下中、相方待ち
//   ComboConfirmed:     コンボ成立済み、出力キー押下中
//   ComboDraining:      出力は解除済み、もう片方の物理キー解放を待つだけ
//
// タイムアウト時（相方が来ないままComboTermMs経過）のリプレイはComboDpcRoutineから
// 保留していたキーのpressを1回だけ復元送出する（v1の2キー限定により、リプレイが
// 必要なイベントは常に高々1件で済む。Phase 2で実機検証済みの「DPCからの
// OriginalClassService自発呼び出し」と同じパターンであり、新規リスクの増加は
// 単発呼び出しである限り無い）。
// ---------------------------------------------------------------------------

// devExt->EngineLock保持中に呼ぶこと。押されたキーが（重複禁止のもとで）ちょうど
// 1つのコンボのメンバーであれば、そのインデックスを返す（無ければ-1）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static LONG
FindComboIndexForScanCode(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ USHORT ScanCode
    )
{
    for (ULONG i = 0; i < KBDLAYER_MAX_COMBO_COUNT; i++) {
        if (devExt->ComboUsed[i] &&
            (devExt->Combos[i].ScanCode1 == ScanCode || devExt->Combos[i].ScanCode2 == ScanCode)) {
            return (LONG)i;
        }
    }
    return -1;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
static USHORT
ComboPartnerScanCode(
    _In_ PKBDLAYER_COMBO_ENTRY Entry,
    _In_ USHORT ScanCode
    )
{
    return (Entry->ScanCode1 == ScanCode) ? Entry->ScanCode2 : Entry->ScanCode1;
}

// ComboTimerの満了で起動されるDPC（タイムアウト経路。相方の押下が来ないまま
// ComboTermMsが経過した場合に不成立とし、保留していたキーのpressを復元送出する）。
// DISPATCH_LEVELで実行される。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
ComboDpcRoutine(
    _In_ PKDPC Dpc,
    _In_opt_ PVOID DeferredContext,
    _In_opt_ PVOID SystemArgument1,
    _In_opt_ PVOID SystemArgument2
    )
{
    PDEVICE_EXTENSION devExt = (PDEVICE_EXTENSION)DeferredContext;
    KIRQL oldIrql;
    USHORT pendingScanCode = 0;
    BOOLEAN shouldReplay;

    UNREFERENCED_PARAMETER(Dpc);
    UNREFERENCED_PARAMETER(SystemArgument1);
    UNREFERENCED_PARAMETER(SystemArgument2);

    if (devExt == NULL) {
        return;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    // ComboPendingActiveがFALSEなら、ServiceCallback側（相方到着による成立、または
    // 保留キー自身の解放による不成立）が既にこのサイクルを終えている
    // （KeCancelTimerとこのDPCのキューイングが競合した場合に起こりうる。Phase 2の
    // HoldTapDpcRoutineと同じ理屈でEngineLockにより直列化される）。
    shouldReplay = devExt->ComboPendingActive;
    if (shouldReplay) {
        pendingScanCode = devExt->ComboPendingScanCode;
        devExt->ComboPendingActive = FALSE;
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    if (shouldReplay) {
        KBDLAYER_TRACE("LayerEngine: combo TIMEOUT unresolved sc=0x%X, replaying held press\n", pendingScanCode);
        SendDecodedEvent(devExt, pendingScanCode, KEY_MAKE);
    }
}

_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
ResolveCombo(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _Inout_opt_ PVOID SystemArgument3
    )
{
    PKEYBOARD_INPUT_DATA ev;
    USHORT scanCode;
    BOOLEAN isBreak;
    KIRQL oldIrql;

    // ResolveTapHold/ResolveMacroと同じ理由で1件バッチのみを対象とする。
    if ((InputDataEnd - InputDataStart) != 1) {
        return FALSE;
    }

    ev = InputDataStart;
    scanCode = EncodeScancode16(ev->MakeCode, ev->Flags);
    isBreak = (ev->Flags & KEY_BREAK) != 0;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);

    if (!devExt->LayerMapEnabled || devExt->LayerMap == NULL) {
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return FALSE;
    }

    // --- Confirmed: 出力キー押下中。メンバーのBREAKで出力解除しDrainingへ ---
    if (devExt->ComboConfirmed) {
        PKBDLAYER_COMBO_ENTRY entry = &devExt->Combos[devExt->ComboActiveId];
        BOOLEAN isMember = (scanCode == entry->ScanCode1 || scanCode == entry->ScanCode2);

        if (!isMember) {
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            return FALSE;
        }
        if (!isBreak) {
            // オートリピート、または既に押されているもう片方の再MAKE。飲み込むだけ。
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            if (SystemArgument3 != NULL) {
                *(PULONG)SystemArgument3 = 1;
            }
            return TRUE;
        }

        {
            KBDLAYER_ACTION outputAction = entry->OutputAction;
            devExt->ComboConfirmed = FALSE;
            devExt->ComboDraining = TRUE;
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

            SendDecodedEvent(devExt, outputAction.Param2, KEY_BREAK);
            if (SystemArgument3 != NULL) {
                *(PULONG)SystemArgument3 = 1;
            }
            return TRUE;
        }
    }

    // --- Draining: 出力は解除済み。もう片方のメンバーの解放を無害に飲み込むだけ ---
    if (devExt->ComboDraining) {
        PKBDLAYER_COMBO_ENTRY entry = &devExt->Combos[devExt->ComboActiveId];
        BOOLEAN isMember = (scanCode == entry->ScanCode1 || scanCode == entry->ScanCode2);

        if (!isMember) {
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            return FALSE;
        }
        if (isBreak) {
            devExt->ComboDraining = FALSE;
        }
        // オートリピートMAKEも含め飲み込む（Draining中は何も送出しない）。
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        if (SystemArgument3 != NULL) {
            *(PULONG)SystemArgument3 = 1;
        }
        return TRUE;
    }

    // --- Pending: 最初のメンバー押下中、相方待ち ---
    if (devExt->ComboPendingActive) {
        PKBDLAYER_COMBO_ENTRY entry = &devExt->Combos[devExt->ComboActiveId];
        USHORT pendingScanCode = devExt->ComboPendingScanCode;
        USHORT partner = ComboPartnerScanCode(entry, pendingScanCode);

        if (scanCode == pendingScanCode) {
            if (!isBreak) {
                // 押しっぱなし中のオートリピートMAKE。状態を変えず黙って飲み込む
                // （Phase 2のResolveTapHoldと同じ教訓）。
                KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
                if (SystemArgument3 != NULL) {
                    *(PULONG)SystemArgument3 = 1;
                }
                return TRUE;
            }

            // 相方が来る前に離した = 不成立。保留していたpress+今回のbreakを
            // タップとして復元送出する。
            devExt->ComboPendingActive = FALSE;
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

            KeCancelTimer(&devExt->ComboTimer);
            SendDecodedEvent(devExt, pendingScanCode, KEY_MAKE);
            SendDecodedEvent(devExt, pendingScanCode, KEY_BREAK);
            if (SystemArgument3 != NULL) {
                *(PULONG)SystemArgument3 = 1;
            }
            return TRUE;
        }

        if (scanCode == partner && !isBreak) {
            // 相方到着 = コンボ成立。
            KBDLAYER_ACTION outputAction = entry->OutputAction;

            devExt->ComboPendingActive = FALSE;
            devExt->ComboConfirmed = TRUE;
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

            KeCancelTimer(&devExt->ComboTimer);
            SendDecodedEvent(devExt, outputAction.Param2, KEY_MAKE);
            if (SystemArgument3 != NULL) {
                *(PULONG)SystemArgument3 = 1;
            }
            return TRUE;
        }

        // --- 無関係キーによる割り込み = 不成立。保留していたpressのみ復元送出し、
        //     割り込みキー自身は通常経路（呼び出し元でのResolveTapHold以降）に委ねる。
        devExt->ComboPendingActive = FALSE;
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

        KeCancelTimer(&devExt->ComboTimer);
        SendDecodedEvent(devExt, pendingScanCode, KEY_MAKE);
        return FALSE;
    }

    // --- Idle: このキー自体がコンボメンバーなら新しい保留サイクルを開始する ---
    if (!isBreak) {
        LONG idx = FindComboIndexForScanCode(devExt, scanCode);
        if (idx >= 0) {
            LARGE_INTEGER dueTime;

            devExt->ComboPendingActive = TRUE;
            devExt->ComboPendingScanCode = scanCode;
            devExt->ComboActiveId = (ULONG)idx;
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

            dueTime.QuadPart = -((LONGLONG)devExt->ComboTermMs * 10000LL);
            KeSetTimer(&devExt->ComboTimer, dueTime, &devExt->ComboDpc);

            if (SystemArgument3 != NULL) {
                *(PULONG)SystemArgument3 = 1;
            }
            return TRUE;
        }
    }

    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
    return FALSE;
}

// ---------------------------------------------------------------------------
// IOCTLハンドラから呼ばれる設定関数（PASSIVE_LEVEL）
// ---------------------------------------------------------------------------

_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
BuildLayerMapsSubKeyPath(
    _In_ ULONG UniqId,
    _Out_writes_z_(PathLengthInChars) PWCHAR PathBuffer,
    _In_ ULONG PathLengthInChars
    )
{
    RtlStringCchPrintfW(PathBuffer, PathLengthInChars, L"LayerMaps\\%08X", UniqId);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetLayerMapForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_reads_bytes_(BlobLength) PVOID Blob,
    _In_ ULONG BlobLength
    )
{
    PKBDLAYER_MAP_TABLE newTable = NULL;
    PKBDLAYER_MAP_TABLE oldTable = NULL;
    NTSTATUS status;
    KIRQL oldIrql;
    WCHAR subKeyPath[64];

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    if (devExt->LayerMapEnabled) {
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return STATUS_INVALID_DEVICE_STATE;
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    status = ParseLayerMapBlob(Blob, BlobLength, &newTable);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    if (devExt->LayerMapEnabled) {
        // SET受付判定からここまでの間にONにされた場合の競合防止
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        ExFreePoolWithTag(newTable, KBDLAYER_MAP_POOL_TAG);
        return STATUS_INVALID_DEVICE_STATE;
    }
    oldTable = devExt->LayerMap;
    devExt->LayerMap = newTable;
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    if (oldTable != NULL) {
        ExFreePoolWithTag(oldTable, KBDLAYER_MAP_POOL_TAG);
    }

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    RegWriteBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_MAP_VALUE_NAME, Blob, BlobLength);

    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
ClearLayerMapForDevice(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    PKBDLAYER_MAP_TABLE oldTable;
    KIRQL oldIrql;
    WCHAR subKeyPath[64];

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    if (devExt->LayerMapEnabled) {
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return STATUS_INVALID_DEVICE_STATE;
    }
    oldTable = devExt->LayerMap;
    devExt->LayerMap = NULL;
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    if (oldTable != NULL) {
        ExFreePoolWithTag(oldTable, KBDLAYER_MAP_POOL_TAG);
    }

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    RegWriteBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_MAP_VALUE_NAME, NULL, 0);

    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetLayerMapEnabledForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ BOOLEAN Enabled
    )
{
    KIRQL oldIrql;
    WCHAR subKeyPath[64];

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    if (Enabled) {
        if (devExt->LayerMap == NULL) {
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            return STATUS_INVALID_DEVICE_STATE;
        }
        devExt->LayerMapEnabled = TRUE;
    } else {
        devExt->LayerMapEnabled = FALSE;
        // 素通しに戻すときはレイヤー状態もリセットする（次にONにしたとき、
        // 直前にどのレイヤーがアクティブだったかに依存させないため）。
        devExt->ActiveLayerMask = 0;
        devExt->DefaultLayer = 0;
        devExt->OneShotLayerPending = FALSE;
        devExt->OneShotLayerBit = 0;
        // Phase 2: MT/LTの保留/ホールド確定状態も同様にリセットする。タイマーが
        // armされていた場合はキャンセルする（HoldTapDpcRoutineはPendingActiveを
        // 見てから動くので、キャンセルが競合で失敗しても実害は無い）。
        devExt->PendingActive = FALSE;
        devExt->HoldConfirmed = FALSE;
        KeCancelTimer(&devExt->HoldTimer);
        // Phase 4: コンボの保留/成立/ドレイン状態も同様にリセットする（タイマーが
        // armされていた場合はキャンセルする。ComboDpcRoutineはComboPendingActiveを
        // 見てから動くので、キャンセルが競合で失敗しても実害は無い）。
        devExt->ComboPendingActive = FALSE;
        devExt->ComboConfirmed = FALSE;
        devExt->ComboDraining = FALSE;
        KeCancelTimer(&devExt->ComboTimer);
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    RegWriteDword(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_ENABLED_VALUE_NAME, Enabled ? 1 : 0);

    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
GetLayerMapForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _Out_writes_bytes_to_opt_(OutBufferLength, *BytesReturned) PVOID OutBuffer,
    _In_ ULONG OutBufferLength,
    _Out_ PULONG BytesReturned,
    _Out_opt_ PBOOLEAN OutEnabled
    )
{
    KIRQL oldIrql;
    NTSTATUS status;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    status = SerializeLayerMapTableToBlob(devExt->LayerMap, OutBuffer, OutBufferLength, BytesReturned);
    if (OutEnabled != NULL) {
        *OutEnabled = devExt->LayerMapEnabled;
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    return status;
}

// Phase 3/4のLayerEngine.c後半で定義されるが、AddDeviceからの「永続化状態を
// まとめてロードする」単一の呼び出し(LoadPersistedLayerMapForDevice)に
// 統合するため、ここから先行して呼び出す。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
LoadPersistedMacrosForDevice(
    _In_ PDEVICE_EXTENSION devExt
    );

_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
LoadPersistedCombosForDevice(
    _In_ PDEVICE_EXTENSION devExt
    );

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
LoadPersistedLayerMapForDevice(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    WCHAR subKeyPath[64];
    PVOID blobBuffer;
    ULONG actualLength = 0;
    NTSTATUS status;
    ULONG enabledValue = 0;

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));

    blobBuffer = ExAllocatePool2(POOL_FLAG_PAGED, KBDLAYER_MAX_MAP_BLOB_BYTES, KBDLAYER_REGTEMP_POOL_TAG);
    if (blobBuffer == NULL) {
        return;
    }

    status = RegReadBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_MAP_VALUE_NAME,
                            blobBuffer, KBDLAYER_MAX_MAP_BLOB_BYTES, &actualLength);
    if (NT_SUCCESS(status) && actualLength > 0) {
        PKBDLAYER_MAP_TABLE table = NULL;
        if (NT_SUCCESS(ParseLayerMapBlob(blobBuffer, actualLength, &table))) {
            devExt->LayerMap = table;
        }
    }

    ExFreePoolWithTag(blobBuffer, KBDLAYER_REGTEMP_POOL_TAG);

    RegReadDwordEx(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_ENABLED_VALUE_NAME, &enabledValue, 0);
    devExt->LayerMapEnabled = (devExt->LayerMap != NULL) && (enabledValue != 0);

    {
        ULONG tapTermValue = 0;
        RegReadDwordEx(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_TAPTERM_VALUE_NAME,
                        &tapTermValue, KBDLAYER_DEFAULT_TAP_TERM_MS);
        devExt->TapTermMs = (tapTermValue != 0) ? tapTermValue : KBDLAYER_DEFAULT_TAP_TERM_MS;
    }

    LoadPersistedMacrosForDevice(devExt);
    LoadPersistedCombosForDevice(devExt);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetTapTermForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG TapTermMs
    )
{
    KIRQL oldIrql;
    ULONG effective = (TapTermMs != 0) ? TapTermMs : KBDLAYER_DEFAULT_TAP_TERM_MS;
    WCHAR subKeyPath[64];

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    devExt->TapTermMs = effective;
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    RegWriteDword(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_TAPTERM_VALUE_NAME, effective);

    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetComboTermForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG ComboTermMs
    )
{
    KIRQL oldIrql;
    ULONG effective = (ComboTermMs != 0) ? ComboTermMs : KBDLAYER_DEFAULT_COMBO_TERM_MS;
    WCHAR subKeyPath[64];

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    devExt->ComboTermMs = effective;
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    RegWriteDword(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_COMBOTERM_VALUE_NAME, effective);

    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeTapHoldState(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    KeInitializeTimer(&devExt->HoldTimer);
    KeInitializeDpc(&devExt->HoldDpc, HoldTapDpcRoutine, devExt);
    devExt->TapTermMs = KBDLAYER_DEFAULT_TAP_TERM_MS;
    devExt->PendingActive = FALSE;
    devExt->HoldConfirmed = FALSE;
    devExt->PendingScanCode = 0;
    RtlZeroMemory(&devExt->PendingAction, sizeof(devExt->PendingAction));
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeComboState(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    KeInitializeTimer(&devExt->ComboTimer);
    KeInitializeDpc(&devExt->ComboDpc, ComboDpcRoutine, devExt);
    devExt->ComboTermMs = KBDLAYER_DEFAULT_COMBO_TERM_MS;
    devExt->ComboPendingActive = FALSE;
    devExt->ComboConfirmed = FALSE;
    devExt->ComboDraining = FALSE;
    devExt->ComboPendingScanCode = 0;
    devExt->ComboActiveId = 0;
    RtlZeroMemory(devExt->Combos, sizeof(devExt->Combos));
    RtlZeroMemory(devExt->ComboUsed, sizeof(devExt->ComboUsed));
}

// ---------------------------------------------------------------------------
// Phase 3: マクロの設定関数（PASSIVE_LEVEL）
// ---------------------------------------------------------------------------

// devExt->MacroStepCounts/Macros全体を1つのblobへシリアライズしてレジストリへ
// 書き込む。ベストエフォート（確保失敗時は何もしない。ライブ状態には影響しない）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
PersistAllMacros(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    WCHAR subKeyPath[64];
    PKBDLAYER_MACRO_PERSIST_ENTRY buffer;
    KIRQL oldIrql;

    buffer = (PKBDLAYER_MACRO_PERSIST_ENTRY)ExAllocatePool2(
        POOL_FLAG_PAGED, KBDLAYER_MACRO_PERSIST_BLOB_BYTES, KBDLAYER_REGTEMP_POOL_TAG);
    if (buffer == NULL) {
        return;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    for (ULONG i = 0; i < KBDLAYER_MAX_MACRO_COUNT; i++) {
        buffer[i].StepCount = devExt->MacroStepCounts[i];
        RtlCopyMemory(buffer[i].Steps, devExt->Macros[i], sizeof(buffer[i].Steps));
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    RegWriteBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_MACROS_VALUE_NAME,
                    buffer, KBDLAYER_MACRO_PERSIST_BLOB_BYTES);

    ExFreePoolWithTag(buffer, KBDLAYER_REGTEMP_POOL_TAG);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetMacroForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG MacroId,
    _In_reads_opt_(StepCount) PKBDLAYER_MACRO_STEP Steps,
    _In_ ULONG StepCount
    )
{
    KIRQL oldIrql;

    if (MacroId >= KBDLAYER_MAX_MACRO_COUNT || StepCount > KBDLAYER_MAX_MACRO_STEPS) {
        return STATUS_INVALID_PARAMETER;
    }
    if (StepCount > 0 && Steps == NULL) {
        return STATUS_INVALID_PARAMETER;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    if (StepCount > 0) {
        RtlCopyMemory(devExt->Macros[MacroId], Steps, StepCount * sizeof(KBDLAYER_MACRO_STEP));
    }
    if (StepCount < KBDLAYER_MAX_MACRO_STEPS) {
        RtlZeroMemory(&devExt->Macros[MacroId][StepCount],
                      (KBDLAYER_MAX_MACRO_STEPS - StepCount) * sizeof(KBDLAYER_MACRO_STEP));
    }
    devExt->MacroStepCounts[MacroId] = StepCount;
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    // 生きているマクロ実行（ResolveMacro）とはEngineLockで直列化されているので、
    // ここでのSET中にちょうどマクロが実行されてもロック単位で整合した状態
    // （更新前 or 更新後のどちらか）しか見えない。
    PersistAllMacros(devExt);

    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
GetMacroForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG MacroId,
    _Out_writes_bytes_to_opt_(OutBufferLength, *BytesReturned) PVOID OutBuffer,
    _In_ ULONG OutBufferLength,
    _Out_ PULONG BytesReturned,
    _Out_ PULONG OutStepCount
    )
{
    KIRQL oldIrql;
    ULONG stepCount;
    ULONG required;

    *BytesReturned = 0;
    *OutStepCount = 0;

    if (MacroId >= KBDLAYER_MAX_MACRO_COUNT) {
        return STATUS_INVALID_PARAMETER;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    stepCount = devExt->MacroStepCounts[MacroId];
    required = stepCount * sizeof(KBDLAYER_MACRO_STEP);

    if (OutBuffer == NULL || OutBufferLength < required) {
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        *BytesReturned = required;
        return STATUS_BUFFER_TOO_SMALL;
    }

    if (stepCount > 0) {
        RtlCopyMemory(OutBuffer, devExt->Macros[MacroId], required);
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    *BytesReturned = required;
    *OutStepCount = stepCount;
    return STATUS_SUCCESS;
}

// AddDeviceから呼ばれる: レジストリの"Macros"値を読み、devExt->MacroStepCounts/
// Macrosを初期化する（devExtはまだg_deviceListに公開されておらず、他スレッドとの
// 競合が無い前提でロック無しに直接書き込む。LoadPersistedLayerMapForDeviceと
// 同じ理屈）。失敗時は全マクロ未設定のままでよい（ベストエフォート。
// RtlZeroMemory済みのAddDeviceの初期状態と同じ）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
LoadPersistedMacrosForDevice(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    WCHAR subKeyPath[64];
    PKBDLAYER_MACRO_PERSIST_ENTRY buffer;
    ULONG actualLength = 0;
    NTSTATUS status;

    buffer = (PKBDLAYER_MACRO_PERSIST_ENTRY)ExAllocatePool2(
        POOL_FLAG_PAGED, KBDLAYER_MACRO_PERSIST_BLOB_BYTES, KBDLAYER_REGTEMP_POOL_TAG);
    if (buffer == NULL) {
        return;
    }

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    status = RegReadBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_MACROS_VALUE_NAME,
                            buffer, KBDLAYER_MACRO_PERSIST_BLOB_BYTES, &actualLength);
    if (NT_SUCCESS(status) && actualLength == KBDLAYER_MACRO_PERSIST_BLOB_BYTES) {
        for (ULONG i = 0; i < KBDLAYER_MAX_MACRO_COUNT; i++) {
            ULONG stepCount = buffer[i].StepCount;
            if (stepCount > KBDLAYER_MAX_MACRO_STEPS) {
                stepCount = 0; // 壊れたデータは無視する（安全側）
            }
            devExt->MacroStepCounts[i] = stepCount;
            if (stepCount > 0) {
                RtlCopyMemory(devExt->Macros[i], buffer[i].Steps, stepCount * sizeof(KBDLAYER_MACRO_STEP));
            }
        }
    }
    // actualLengthがKBDLAYER_MACRO_PERSIST_BLOB_BYTESと一致しない場合（値が
    // 存在しない、または将来の互換性が壊れた場合）は何もしない。AddDeviceで
    // RtlZeroMemory済みのため全マクロ未設定のままで安全。

    ExFreePoolWithTag(buffer, KBDLAYER_REGTEMP_POOL_TAG);
}

// ---------------------------------------------------------------------------
// Phase 4: コンボの設定関数（PASSIVE_LEVEL）
// ---------------------------------------------------------------------------

// devExt->Combos/ComboUsed全体を1つのblobへシリアライズしてレジストリへ書き込む。
// ベストエフォート（確保失敗時は何もしない。ライブ状態には影響しない）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
PersistAllCombos(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    WCHAR subKeyPath[64];
    PKBDLAYER_COMBO_PERSIST_ENTRY buffer;
    KIRQL oldIrql;

    buffer = (PKBDLAYER_COMBO_PERSIST_ENTRY)ExAllocatePool2(
        POOL_FLAG_PAGED, KBDLAYER_COMBO_PERSIST_BLOB_BYTES, KBDLAYER_REGTEMP_POOL_TAG);
    if (buffer == NULL) {
        return;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    for (ULONG i = 0; i < KBDLAYER_MAX_COMBO_COUNT; i++) {
        buffer[i].Used = devExt->ComboUsed[i] ? 1 : 0;
        buffer[i].Entry = devExt->Combos[i];
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    RegWriteBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_COMBOS_VALUE_NAME,
                    buffer, KBDLAYER_COMBO_PERSIST_BLOB_BYTES);

    ExFreePoolWithTag(buffer, KBDLAYER_REGTEMP_POOL_TAG);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetComboForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG ComboId,
    _In_opt_ PKBDLAYER_COMBO_ENTRY Entry
    )
{
    KIRQL oldIrql;

    if (ComboId >= KBDLAYER_MAX_COMBO_COUNT) {
        return STATUS_INVALID_PARAMETER;
    }

    if (Entry != NULL) {
        if (Entry->ScanCode1 == Entry->ScanCode2) {
            return STATUS_INVALID_PARAMETER;
        }
        if (Entry->OutputAction.Type != KBDLAYER_ACTION_KEY) {
            // v1制限: 出力はKEYアクションのみ許可（common/kbdlayer_ioctl.h参照）。
            return STATUS_INVALID_PARAMETER;
        }
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);

    if (Entry != NULL) {
        // 重複禁止(v1): 自分自身のスロット以外の既存コンボと物理スキャンコードが
        // 重ならないことを検証する。
        for (ULONG i = 0; i < KBDLAYER_MAX_COMBO_COUNT; i++) {
            if (i == ComboId || !devExt->ComboUsed[i]) {
                continue;
            }
            if (devExt->Combos[i].ScanCode1 == Entry->ScanCode1 ||
                devExt->Combos[i].ScanCode1 == Entry->ScanCode2 ||
                devExt->Combos[i].ScanCode2 == Entry->ScanCode1 ||
                devExt->Combos[i].ScanCode2 == Entry->ScanCode2) {
                KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
                return STATUS_INVALID_PARAMETER;
            }
        }
        devExt->Combos[ComboId] = *Entry;
        devExt->ComboUsed[ComboId] = TRUE;
    } else {
        RtlZeroMemory(&devExt->Combos[ComboId], sizeof(devExt->Combos[ComboId]));
        devExt->ComboUsed[ComboId] = FALSE;
    }

    // 設定変更と整合しない状態を残さないよう、ライブのコンボ判定状態は常に
    // リセットする（SetLayerMapEnabledForDeviceのMT/LTリセットと同じ理屈）。
    devExt->ComboPendingActive = FALSE;
    devExt->ComboConfirmed = FALSE;
    devExt->ComboDraining = FALSE;
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    KeCancelTimer(&devExt->ComboTimer);

    PersistAllCombos(devExt);

    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
GetComboForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG ComboId,
    _Out_ PKBDLAYER_COMBO_ENTRY OutEntry,
    _Out_ PBOOLEAN OutUsed
    )
{
    KIRQL oldIrql;

    if (ComboId >= KBDLAYER_MAX_COMBO_COUNT) {
        return STATUS_INVALID_PARAMETER;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    *OutEntry = devExt->Combos[ComboId];
    *OutUsed = devExt->ComboUsed[ComboId];
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    return STATUS_SUCCESS;
}

// AddDeviceから呼ばれる: レジストリの"Combos"/"ComboTermMs"値を読み、
// devExt->Combos/ComboUsed/ComboTermMsを初期化する（LoadPersistedMacrosForDeviceと
// 同じ理屈で、devExtがまだg_deviceListに公開されておらずロック無しに直接書き込む）。
// 失敗時は全コンボ未設定・ComboTermMs=既定値のままでよい（ベストエフォート）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
LoadPersistedCombosForDevice(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    WCHAR subKeyPath[64];
    PKBDLAYER_COMBO_PERSIST_ENTRY buffer;
    ULONG actualLength = 0;
    NTSTATUS status;
    ULONG comboTermValue = 0;

    buffer = (PKBDLAYER_COMBO_PERSIST_ENTRY)ExAllocatePool2(
        POOL_FLAG_PAGED, KBDLAYER_COMBO_PERSIST_BLOB_BYTES, KBDLAYER_REGTEMP_POOL_TAG);
    if (buffer != NULL) {
        BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
        status = RegReadBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_COMBOS_VALUE_NAME,
                                buffer, KBDLAYER_COMBO_PERSIST_BLOB_BYTES, &actualLength);
        if (NT_SUCCESS(status) && actualLength == KBDLAYER_COMBO_PERSIST_BLOB_BYTES) {
            for (ULONG i = 0; i < KBDLAYER_MAX_COMBO_COUNT; i++) {
                devExt->ComboUsed[i] = (buffer[i].Used != 0);
                devExt->Combos[i] = buffer[i].Entry;
            }
        }
        // actualLengthが一致しない場合（値が存在しない、または将来の互換性が
        // 壊れた場合）は何もしない。InitializeComboStateでRtlZeroMemory済みのため
        // 全コンボ未設定のままで安全。
        ExFreePoolWithTag(buffer, KBDLAYER_REGTEMP_POOL_TAG);
    }

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    RegReadDwordEx(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_COMBOTERM_VALUE_NAME,
                    &comboTermValue, KBDLAYER_DEFAULT_COMBO_TERM_MS);
    devExt->ComboTermMs = (comboTermValue != 0) ? comboTermValue : KBDLAYER_DEFAULT_COMBO_TERM_MS;
}

// ---------------------------------------------------------------------------
// キャリブレーションウィザード
// ---------------------------------------------------------------------------

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
BeginCaptureNextKey(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    KIRQL oldIrql;
    NTSTATUS status;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    if (devExt->CaptureActive) {
        status = STATUS_DEVICE_BUSY;
    } else {
        devExt->CaptureActive = TRUE;
        devExt->CapturedScanCode = 0;
        KeClearEvent(&devExt->CaptureEvent);
        status = STATUS_SUCCESS;
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    return status;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
CancelCaptureNextKey(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    KIRQL oldIrql;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    devExt->CaptureActive = FALSE;
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
USHORT
ConsumeCapturedScanCode(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    KIRQL oldIrql;
    USHORT scanCode;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    scanCode = devExt->CapturedScanCode;
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    return scanCode;
}
