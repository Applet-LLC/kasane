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
#define KBDLAYER_TAPDANCES_VALUE_NAME L"TapDances"
#define KBDLAYER_TAPDANCETERM_VALUE_NAME L"TapDanceTermMs"
#define KBDLAYER_NICOLA_VALUE_NAME L"Nicola"
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

// タップダンスの永続化形式（レジストリ"TapDances"値）。マクロ/コンボと同じ方式で、
// KBDLAYER_MAX_TAPDANCE_COUNT個ぶん固定サイズで連続して並べる。
typedef struct _KBDLAYER_TAPDANCE_PERSIST_ENTRY {
    ULONG Used;
    KBDLAYER_TAPDANCE_ENTRY Entry;
} KBDLAYER_TAPDANCE_PERSIST_ENTRY, *PKBDLAYER_TAPDANCE_PERSIST_ENTRY;

#define KBDLAYER_TAPDANCE_PERSIST_BLOB_BYTES \
    (KBDLAYER_MAX_TAPDANCE_COUNT * sizeof(KBDLAYER_TAPDANCE_PERSIST_ENTRY))

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
            if (type > KBDLAYER_ACTION_TAPDANCE) {
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

// ---------------------------------------------------------------------------
// 押下中キーの処理記録（devExt->HeldKeys。DeviceExtension.hのKBDLAYER_HELD_*参照）
// いずれもdevExt->EngineLock保持中に呼ぶこと。
// ---------------------------------------------------------------------------

_IRQL_requires_max_(DISPATCH_LEVEL)
static PKBDLAYER_HELD_KEY
HeldKeyFor(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ USHORT ScanCode16
    )
{
    ULONG index = ScanCode16 & 0xFF;
    UCHAR prefix = (UCHAR)(ScanCode16 >> 8);
    if (prefix == 0xE1) {
        index |= 0x200;
    } else if (prefix == 0xE0) {
        index |= 0x100;
    }
    return &devExt->HeldKeys[index];
}

_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
SetHeldKeyLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ USHORT ScanCode16,
    _In_ UCHAR State,
    _In_opt_ const KBDLAYER_ACTION* Action
    )
{
    PKBDLAYER_HELD_KEY held = HeldKeyFor(devExt, ScanCode16);

    if (held->State == KBDLAYER_HELD_NONE && State != KBDLAYER_HELD_NONE) {
        devExt->HeldKeyCount++;
    } else if (held->State != KBDLAYER_HELD_NONE && State == KBDLAYER_HELD_NONE && devExt->HeldKeyCount > 0) {
        devExt->HeldKeyCount--;
    }
    held->State = State;
    if (Action != NULL) {
        held->Action = *Action;
    } else {
        RtlZeroMemory(&held->Action, sizeof(held->Action));
    }
}

// 生のまま転送するバッチ（MT/LTのホールド確定・タップダンスの割り込み）の押下を
// RAWとして記録する。こうしておくと、その解放も通常経路で生のまま転送される。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
RecordRawMakesLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd
    )
{
    for (PKEYBOARD_INPUT_DATA p = InputDataStart; p < InputDataEnd; p++) {
        if (!(p->Flags & KEY_BREAK)) {
            USHORT sc = EncodeScancode16(p->MakeCode, p->Flags);
            if (HeldKeyFor(devExt, sc)->State == KBDLAYER_HELD_NONE) {
                SetHeldKeyLocked(devExt, sc, KBDLAYER_HELD_RAW, NULL);
            }
        }
    }
}

// 実効レイヤーマスク = キーボード側のレイヤー | アプリ管理レイヤー | DefaultLayerビット。
// レイヤーの解決・NICOLAの有効判定・状態の通知は、すべてこれを使う。
// devExt->EngineLock保持中に呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static ULONG
EffectiveLayerMaskLocked(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    return devExt->ActiveLayerMask | devExt->AppLayerMask | (1UL << devExt->DefaultLayer);
}

// [レイヤー解決規則] 実効レイヤーマスク（EffectiveLayerMaskLocked）の中で最も高いレイヤー
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
    ULONG effectiveMask = EffectiveLayerMaskLocked(devExt);

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
//
// [押下と解放の対応] 押下（make）はその時点のレイヤー状態で解決し、使ったアクションを
// devExt->HeldKeysに記録する。解放（break）とオートリピートの再makeは、その時点の
// レイヤー状態で解決し直さず、記録したアクションを使う（QMKのsource layers cache相当）。
// こうしないと、押している間にMOを離す・OSLを消費する・TG/DFで切り替えるなどして
// レイヤーが変わったとき、押下と解放が別のキーとしてOSに届き、押下したキーが
// 押しっぱなしのまま残る。記録はレイヤーマップのOFF・差し替え・CLEAR後も有効
// （ContextがNULLでない = マップ無効中の呼び出しで、記録のあるキーだけを処理する）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN
LayerEngineShouldDropEntry(
    _In_ PDEVICE_EXTENSION devExt,
    _Inout_ PKEYBOARD_INPUT_DATA Entry,
    _In_opt_ PVOID Context
    )
{
    PKBDLAYER_MAP_TABLE table = devExt->LayerMap; // EngineLock保持中に呼ばれる前提
    BOOLEAN heldOnly = (Context != NULL);
    PKBDLAYER_KEY_ENTRY keyEntry;
    PKBDLAYER_HELD_KEY held;
    KBDLAYER_ACTION recorded;
    PKBDLAYER_ACTION action;
    USHORT src;
    BOOLEAN isBreak;

    src = EncodeScancode16(Entry->MakeCode, Entry->Flags);
    isBreak = (Entry->Flags & KEY_BREAK) != 0;
    held = HeldKeyFor(devExt, src);

    if (held->State != KBDLAYER_HELD_NONE) {
        // 押下時の記録どおりに処理する（解放なら記録を消す）。
        UCHAR state = held->State;
        recorded = held->Action;
        if (isBreak) {
            SetHeldKeyLocked(devExt, src, KBDLAYER_HELD_NONE, NULL);
        }
        if (state == KBDLAYER_HELD_RAW) {
            return FALSE; // 生のまま転送する
        }
        if (state == KBDLAYER_HELD_SWALLOWED) {
            return TRUE;
        }
        action = &recorded;
        goto apply;
    }

    if (heldOnly || table == NULL) {
        return FALSE;
    }

    keyEntry = FindKeyEntry(table, src);
    if (keyEntry == NULL) {
        // レイヤーマップで管理されていない物理キー（Fnキー・電源キー等）はそのまま素通しする。
        // 押している間にマップが差し替えられても解放を生のまま通せるよう、記録しておく。
        if (!isBreak) {
            SetHeldKeyLocked(devExt, src, KBDLAYER_HELD_RAW, NULL);
        }
        return FALSE;
    }

    action = ResolveEffectiveAction(devExt, table, keyEntry);
    if (action == NULL) {
        // 全レイヤーで透過 = 実質KC_NO。出力しない（解放も対で出力しない）。
        if (!isBreak) {
            SetHeldKeyLocked(devExt, src, KBDLAYER_HELD_SWALLOWED, NULL);
        }
        return TRUE;
    }
    if (!isBreak) {
        SetHeldKeyLocked(devExt, src, KBDLAYER_HELD_ACTION, action);
    }

apply:
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
        // MODTAP/LAYERTAPはResolveTapHold、MACROはResolveMacro、TAPDANCEはResolveTapDance
        // （いずれもServiceCallbackからResolveLayerActionより前に呼ばれる、Driver.c参照）が
        // 先に横取りして処理するため、通常はここに到達しない。ここに来るのはOSM/CUSTOM
        // （未実装、予約のみ）や、複数件バッチの一部として届いたためResolveTapHold/
        // ResolveMacro/ResolveTapDanceがスキップしたMT/LT/MACRO/TAPDANCEキーなど、
        // 想定外の経路のみ。
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

    // キャリブレーションウィザードのキャプチャ記録は、Combo/TapHold等より前に
    // 動く必要があるためResolveCalibration（ServiceCallbackの先頭）へ移した。

    if (!devExt->LayerMapEnabled || devExt->LayerMap == NULL) {
        // マップが無効・未設定でも、有効だった間に押されたキーの解放だけは記録どおりに
        // 処理する（押しっぱなしを残さないため）。記録が無ければ何もしない。
        if (devExt->HeldKeyCount == 0) {
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            return InputDataEnd;
        }
        newEnd = CompactInputDataArray(devExt, InputDataStart, InputDataEnd,
                                       LayerEngineShouldDropEntry, (PVOID)devExt /* heldOnly */);
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return newEnd;
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

// kbdclassへ送る1イベントが修飾キーなら、devExt->OutputModifierDownを更新する。
// devExt->OutputLock保持中に呼ぶこと（送出はすべてOutputLock保持中に行う）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
TrackOutputModifierEvent(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ USHORT MakeCode,
    _In_ USHORT Flags
    )
{
    USHORT scanCode = EncodeScancode16(MakeCode, Flags);

    for (ULONG bit = 0; bit < RTL_NUMBER_OF(s_ModifierScanCodes); bit++) {
        if (s_ModifierScanCodes[bit] == scanCode) {
            if (Flags & KEY_BREAK) {
                devExt->OutputModifierDown &= (UCHAR)~(1u << bit);
            } else {
                devExt->OutputModifierDown |= (UCHAR)(1u << bit);
            }
            return;
        }
    }
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
TrackOutputModifiers(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd
    )
{
    for (PKEYBOARD_INPUT_DATA p = InputDataStart; p < InputDataEnd; p++) {
        TrackOutputModifierEvent(devExt, p->MakeCode, p->Flags);
    }
}

// devExt->EngineLock保持中に呼ぶこと。保留サイクル用タイマー（Hold/Combo/TapDance）の
// 期限を*Deadlineに記録し、KeSetTimerに渡す相対時間を返す（KeSetTimerはロック解放後に
// 呼んでよい）。KeSetTimerの呼び出しは記録より後なので、実際の満了は必ず期限以降になる。
// DPC側は「期限前なら何もしない」ことで、満了済みでキュー待ちだった古いDPCが、その後に
// 始まった新しいサイクルを即座に確定させるのを防ぐ（CaptureDeadlineと同じ手法）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static LARGE_INTEGER
TimerDueLocked(
    _Out_ PULONGLONG Deadline,
    _In_ ULONG TermMs
    )
{
    LARGE_INTEGER dueTime;

    *Deadline = KeQueryInterruptTime() + (ULONGLONG)TermMs * 10000ULL;
    dueTime.QuadPart = -((LONGLONG)TermMs * 10000LL);
    return dueTime;
}

// 1件のKEYBOARD_INPUT_DATAを単独でOriginalClassServiceへ送る。devExt->EngineLockを
// 保持していない状態で呼ぶこと（devExt->OutputLockは保持していてよい）。SystemArgument3には実IRPに紐づかない自発呼び出し
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

    TrackOutputModifierEvent(devExt, MakeCode, Flags);
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

    // 状態の決定から送出までをServiceCallbackと直列化する（OutputLockの説明参照）。
    KeAcquireSpinLockAtDpcLevel(&devExt->OutputLock);
    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    // PendingActiveがFALSEなら、ServiceCallback側（他キー押下によるホールド確定、
    // またはタップ確定としての解放）が既にこのサイクルを終えている
    // （KeCancelTimerとこのDPCのキューイングが競合した場合に起こりうる）。
    // 期限前なら、終わったサイクルのDPCが遅れて届いただけで、今のサイクルは
    // 自分のタイマーで改めて確定する。いずれの場合も何もしない。
    shouldConfirm = devExt->PendingActive && KeQueryInterruptTime() >= devExt->HoldDeadline;
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
    KeReleaseSpinLockFromDpcLevel(&devExt->OutputLock);

    // LTのホールド確定やOSLの消費でレイヤーが変わっていれば、待っているアプリへ知らせる。
    LayerNotifyIfChanged(devExt);
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
        BOOLEAN alreadyHeld = (HeldKeyFor(devExt, scanCode)->State != KBDLAYER_HELD_NONE);

        ConfirmHoldLocked(devExt, &action);
        if (!alreadyHeld) {
            // 生のまま転送する押下を記録し、その解放も生のまま転送されるようにする
            // （記録が無いと、解放がホールド確定後のレイヤーで解決されて別のキーになり、
            // 押下したキーが押しっぱなしになる）。
            RecordRawMakesLocked(devExt, InputDataStart, InputDataEnd);
        }
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

        KeCancelTimer(&devExt->HoldTimer);

        EmitModTapEventIfApplicable(devExt, &action, KEY_MAKE);

        if (alreadyHeld) {
            // MT/LTより前から押されていたキーのオートリピート。押下時の記録どおりに
            // 処理させるため、通常経路（ResolveLayerAction）に委ねる。
            return FALSE;
        }

        // 続けて元のバッチ（別キーのイベント）をそのままOriginalClassServiceに
        // 渡す。このキーはレイヤーエンジンでの再解決を経ない生のスキャンコードの
        // まま転送される（プラン記載の設計通り。既知の制約: 割り込んだキー自身が
        // 現在アクティブなレイヤーの影響を受けない）。
        for (PKEYBOARD_INPUT_DATA p = InputDataStart; p < InputDataEnd; p++) {
            p->ExtraInformation = (p->ExtraInformation & 0x0000FFFFUL) | (devExt->DeviceId & 0xFFFF0000UL);
        }
        TrackOutputModifiers(devExt, InputDataStart, InputDataEnd);
        devExt->OriginalClassService(
            devExt->OriginalClassDeviceObject,
            InputDataStart,
            InputDataEnd,
            SystemArgument3);
        return TRUE;
    }

    // --- 現在アイドル（保留中でもホールド中でもない）で、このキー自体が
    //     MT/LTに解決される押下イベントなら、新しい保留サイクルを開始する ---
    // （押下時の記録があるキーのオートリピートは、押下時の処理を続けるだけなので対象外）
    if (!devExt->PendingActive && !devExt->HoldConfirmed && !isBreak &&
        HeldKeyFor(devExt, scanCode)->State == KBDLAYER_HELD_NONE) {
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
                dueTime = TimerDueLocked(&devExt->HoldDeadline, devExt->TapTermMs);
                KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

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
    PKBDLAYER_HELD_KEY held;
    KBDLAYER_ACTION recordedMacro;
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

    // 押下時の記録があるキー（解放・オートリピート）は、その時点のレイヤーで解決し直さず
    // 記録どおりに処理する（LayerEngineShouldDropEntry参照）。例外として、マクロとして
    // 押下したキーのオートリピートは、記録したマクロをもう一度実行する（従来どおりの挙動）。
    // マクロキーの解放は、LayerEngineShouldDropEntryが記録どおりに飲み込む。
    held = HeldKeyFor(devExt, scanCode);
    if (held->State != KBDLAYER_HELD_NONE) {
        if (isBreak || held->State != KBDLAYER_HELD_ACTION || held->Action.Type != KBDLAYER_ACTION_MACRO) {
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            return FALSE;
        }
        recordedMacro = held->Action;
        action = &recordedMacro;
        goto run;
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

    // このキーの解放はレイヤーが変わっても飲み込み、オートリピートは同じマクロを実行する。
    SetHeldKeyLocked(devExt, scanCode, KBDLAYER_HELD_ACTION, action);

run:
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

// devExt->EngineLock保持中に呼ぶこと。保留を始める時点（= 押下時点）のレイヤーで、
// コンボ不成立時に復元する押下の内容を決めておく。
// - KEYアクション → その出力キー（例: 物理AをKC_Zにしていればz）
// - KC_NO・全レイヤー透過 → 何も送らない
// - それ以外（MO/MT/LT/マクロ等）、またはレイヤーマップに無いキー → 生のスキャンコード
//   （KEY以外のアクションを後から通常経路で再現することはしない。既知の制約）
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
ResolveComboRestoreLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ USHORT ScanCode
    )
{
    PKBDLAYER_KEY_ENTRY keyEntry = (devExt->LayerMap != NULL) ? FindKeyEntry(devExt->LayerMap, ScanCode) : NULL;

    devExt->ComboPendingRestore = KBDLAYER_HELD_RAW;
    RtlZeroMemory(&devExt->ComboPendingAction, sizeof(devExt->ComboPendingAction));
    if (keyEntry != NULL) {
        PKBDLAYER_ACTION action = ResolveEffectiveAction(devExt, devExt->LayerMap, keyEntry);
        if (action == NULL || action->Type == KBDLAYER_ACTION_NONE) {
            devExt->ComboPendingRestore = KBDLAYER_HELD_SWALLOWED;
        } else if (action->Type == KBDLAYER_ACTION_KEY) {
            devExt->ComboPendingRestore = KBDLAYER_HELD_ACTION;
            devExt->ComboPendingAction = *action;
        }
    }
}

// devExt->EngineLock保持中に呼ぶこと。コンボ不成立時に復元する押下のスキャンコード
// （Enc16、0 = 何も送らない）を返す。StillDown = 保留キーがまだ押されている
// （タイムアウト・割り込み）なら、その解放も同じキーになるようHeldKeysに記録する。
_IRQL_requires_max_(DISPATCH_LEVEL)
static USHORT
TakeComboRestoreLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ USHORT PendingScanCode,
    _In_ BOOLEAN StillDown
    )
{
    UCHAR restore = devExt->ComboPendingRestore;
    KBDLAYER_ACTION action = devExt->ComboPendingAction;
    USHORT out;

    switch (restore) {
    case KBDLAYER_HELD_ACTION:
        out = action.Param2;
        // 通常経路のKEYアクションと同じく、OSL(layer)のワンショットを消費する。
        if (devExt->OneShotLayerPending) {
            devExt->ActiveLayerMask &= ~devExt->OneShotLayerBit;
            devExt->OneShotLayerPending = FALSE;
        }
        break;
    case KBDLAYER_HELD_SWALLOWED:
        out = 0;
        break;
    default:
        restore = KBDLAYER_HELD_RAW;
        out = PendingScanCode;
        break;
    }

    if (StillDown && HeldKeyFor(devExt, PendingScanCode)->State == KBDLAYER_HELD_NONE) {
        SetHeldKeyLocked(devExt, PendingScanCode, restore,
                         restore == KBDLAYER_HELD_ACTION ? &action : NULL);
    }
    return out;
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
    USHORT restoreScanCode = 0;
    BOOLEAN shouldReplay;

    UNREFERENCED_PARAMETER(Dpc);
    UNREFERENCED_PARAMETER(SystemArgument1);
    UNREFERENCED_PARAMETER(SystemArgument2);

    if (devExt == NULL) {
        return;
    }

    // 状態の決定から送出までをServiceCallbackと直列化する（OutputLockの説明参照）。
    KeAcquireSpinLockAtDpcLevel(&devExt->OutputLock);
    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    // ComboPendingActiveがFALSEなら、ServiceCallback側（相方到着による成立、または
    // 保留キー自身の解放による不成立）が既にこのサイクルを終えている。期限前なら、
    // 終わったサイクルのDPCが遅れて届いただけ（Phase 2のHoldTapDpcRoutineと同じ理屈）。
    shouldReplay = devExt->ComboPendingActive && KeQueryInterruptTime() >= devExt->ComboDeadline;
    if (shouldReplay) {
        pendingScanCode = devExt->ComboPendingScanCode;
        devExt->ComboPendingActive = FALSE;
        // 押下時点のレイヤーで解決した内容で復元し、解放も同じキーになるよう記録する。
        restoreScanCode = TakeComboRestoreLocked(devExt, pendingScanCode, TRUE);
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    if (shouldReplay) {
        KBDLAYER_TRACE("LayerEngine: combo TIMEOUT unresolved sc=0x%X, replaying held press as 0x%X\n",
                       pendingScanCode, restoreScanCode);
        if (restoreScanCode != 0) {
            SendDecodedEvent(devExt, restoreScanCode, KEY_MAKE);
        }
    }
    KeReleaseSpinLockFromDpcLevel(&devExt->OutputLock);

    // LTのホールド確定やOSLの消費でレイヤーが変わっていれば、待っているアプリへ知らせる。
    LayerNotifyIfChanged(devExt);
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
            devExt->ComboDrainingScanCode =
                (scanCode == entry->ScanCode1) ? entry->ScanCode2 : entry->ScanCode1;
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
            // タップとして復元送出する（押下時点のレイヤーで解決した内容で）。
            {
                USHORT restoreScanCode;

                devExt->ComboPendingActive = FALSE;
                restoreScanCode = TakeComboRestoreLocked(devExt, pendingScanCode, FALSE);
                KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

                KeCancelTimer(&devExt->ComboTimer);
                if (restoreScanCode != 0) {
                    SendDecodedEvent(devExt, restoreScanCode, KEY_MAKE);
                    SendDecodedEvent(devExt, restoreScanCode, KEY_BREAK);
                }
                if (SystemArgument3 != NULL) {
                    *(PULONG)SystemArgument3 = 1;
                }
                return TRUE;
            }
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
        {
            USHORT restoreScanCode;

            devExt->ComboPendingActive = FALSE;
            // 押下時点のレイヤーで解決した内容で復元し、解放も同じキーになるよう記録する。
            restoreScanCode = TakeComboRestoreLocked(devExt, pendingScanCode, TRUE);
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

            KeCancelTimer(&devExt->ComboTimer);
            if (restoreScanCode != 0) {
                SendDecodedEvent(devExt, restoreScanCode, KEY_MAKE);
            }
            return FALSE;
        }
    }

    // --- Idle: このキー自体がコンボメンバーなら新しい保留サイクルを開始する
    //     （押下時の記録があるキーのオートリピートは対象外） ---
    if (!isBreak && HeldKeyFor(devExt, scanCode)->State == KBDLAYER_HELD_NONE) {
        LONG idx = FindComboIndexForScanCode(devExt, scanCode);
        if (idx >= 0) {
            LARGE_INTEGER dueTime;

            devExt->ComboPendingActive = TRUE;
            devExt->ComboPendingScanCode = scanCode;
            devExt->ComboActiveId = (ULONG)idx;
            // 不成立になったときの復元内容は、押下時点のレイヤーで決めておく。
            ResolveComboRestoreLocked(devExt, scanCode);
            dueTime = TimerDueLocked(&devExt->ComboDeadline, devExt->ComboTermMs);
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

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
// Phase 5: タップダンス（v1: タップ回数3回まで、ホールドは1回目の押下のみ）
//
// common/kbdlayer_ioctl.hに記載のスコープ限定を前提にした実装。タップ回数を数える
// 必要がある分、Phase 2/Phase 4よりも状態が1つ多い。4独立フラグで管理する:
//   TapDancePendingActive: 保留サイクル進行中（押下中 or 次のタップ待ち）
//   TapDanceKeyDown:       現在の物理キーが押されたまま（まだ離されていない）
//   TapDanceHoldConfirmed: SingleHold確定済み、対応する解放待ち
//   TapDanceDraining:      N-tap(N>1)確定済みだが、押されたままの解放を無音で待つだけ
//
// ホールド確定（1回目の押下中のみ）は、Phase 2のHold On Other Key Pressと全く同じ
// 経路（他キー割り込みでの即時確定・タイムアウトでの確定）を再利用する。2回目以降の
// 押下中に割り込み/タイムアウトが起きた場合は、対応するHoldアクションが無いため、
// その時点のタップ回数のタップアクションをその場で確定させる（DoubleHold/TripleHold
// 相当は非対応、v1のスコープ限定）。
//
// 2回目・3回目のタップ待ち中にタイムアウトした場合、DPCからpress+releaseの2イベントを
// 連続送出する（Phase 2/Phase 4のタイムアウト経路はいずれも1イベントのみだったので、
// ここが新規のリスク経路。実機で重点的に確認すること）。
// ---------------------------------------------------------------------------

// TapCount（1〜KBDLAYER_MAX_TAP_COUNT）に対応するタップアクションを返す。
_IRQL_requires_max_(DISPATCH_LEVEL)
static KBDLAYER_ACTION
TapActionForCount(
    _In_ PKBDLAYER_TAPDANCE_ENTRY Entry,
    _In_ ULONG TapCount
    )
{
    if (TapCount <= 1) {
        return Entry->SingleTapAction;
    } else if (TapCount == 2) {
        return Entry->DoubleTapAction;
    }
    return Entry->TripleTapAction; // TapCount >= 3
}

// devExt->EngineLock保持中に呼ぶこと。SingleHoldActionがLAYERTAPならActiveLayerMaskの
// ビットをセットする（Phase 2のConfirmHoldLockedと同じ理屈）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
ConfirmTapDanceHoldLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKBDLAYER_ACTION HoldAction
    )
{
    if (HoldAction->Type == KBDLAYER_ACTION_LAYERTAP && HoldAction->Param1 < KBDLAYER_MAX_LAYERS) {
        devExt->ActiveLayerMask |= (1UL << HoldAction->Param1);
    }
    devExt->TapDancePendingActive = FALSE;
    devExt->TapDanceHoldConfirmed = TRUE;
}

// devExt->EngineLock保持中に呼ぶこと。SingleHoldActionがLAYERTAPならActiveLayerMaskの
// ビットをクリアする。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
ReleaseTapDanceHoldLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKBDLAYER_ACTION HoldAction
    )
{
    if (HoldAction->Type == KBDLAYER_ACTION_LAYERTAP && HoldAction->Param1 < KBDLAYER_MAX_LAYERS) {
        devExt->ActiveLayerMask &= ~(1UL << HoldAction->Param1);
    }
    devExt->TapDanceHoldConfirmed = FALSE;
}

// HoldActionの種別に応じてイベントを合成送出する。KEYならそのキー、MODTAPなら対応する
// 修飾キーを送る（EmitModTapEventIfApplicableと違いKEYも扱う。TapDanceのSingleHoldは
// KEY/MODTAP/LAYERTAPの3種を許可するため）。LAYERTAPはActiveLayerMaskの操作のみで
// 完結し、キーイベントの合成は不要。devExt->EngineLockを保持していない状態で呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
EmitTapDanceHoldEventIfApplicable(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKBDLAYER_ACTION HoldAction,
    _In_ USHORT BreakBit
    )
{
    if (HoldAction->Type == KBDLAYER_ACTION_MODTAP && HoldAction->Param1 < RTL_NUMBER_OF(s_ModifierScanCodes)) {
        SendDecodedEvent(devExt, s_ModifierScanCodes[HoldAction->Param1], BreakBit);
    } else if (HoldAction->Type == KBDLAYER_ACTION_KEY) {
        SendDecodedEvent(devExt, HoldAction->Param2, BreakBit);
    }
}

// TapDanceTimerの満了で起動されるDPC。以下のいずれかの意味を持つ:
//   - 1回目の押下中にタイムアウト -> SingleHold確定
//   - 2回目以降の押下中にタイムアウト -> その時点のタップ回数を確定（押されたままの
//     解放はDrainingで無音に待つ）
//   - 解放後・次のタップ待ち中にタイムアウト -> その時点のタップ回数を確定
// DISPATCH_LEVELで実行される。
// *** 「解放後・次のタップ待ち」の確定はpress+releaseの2イベントを連続送出する、
//     Phase 2/Phase 4のタイムアウト経路には無い新規パターン。実機で重点的に確認すること ***
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
TapDanceDpcRoutine(
    _In_ PKDPC Dpc,
    _In_opt_ PVOID DeferredContext,
    _In_opt_ PVOID SystemArgument1,
    _In_opt_ PVOID SystemArgument2
    )
{
    PDEVICE_EXTENSION devExt = (PDEVICE_EXTENSION)DeferredContext;
    KIRQL oldIrql;
    BOOLEAN shouldFire = FALSE;
    BOOLEAN isHoldCase = FALSE;
    KBDLAYER_ACTION action;
    USHORT scanCode = 0;

    UNREFERENCED_PARAMETER(Dpc);
    UNREFERENCED_PARAMETER(SystemArgument1);
    UNREFERENCED_PARAMETER(SystemArgument2);

    if (devExt == NULL) {
        return;
    }

    RtlZeroMemory(&action, sizeof(action));

    // 状態の決定から送出までをServiceCallbackと直列化する（OutputLockの説明参照）。
    KeAcquireSpinLockAtDpcLevel(&devExt->OutputLock);
    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    // TapDancePendingActiveがFALSEなら、ServiceCallback側が既にこのサイクルを終えている。
    // 期限前なら、張り直す前のタイマー（前のタップの押下中・次のタップ待ち、または
    // 終わったサイクル）のDPCが遅れて届いただけ（Phase 2/Phase 4と同じ理屈）。
    if (devExt->TapDancePendingActive && KeQueryInterruptTime() >= devExt->TapDanceDeadline) {
        PKBDLAYER_TAPDANCE_ENTRY entry = &devExt->TapDances[devExt->TapDanceActiveId];
        ULONG tapCountSoFar = devExt->TapDanceTapCount;
        BOOLEAN wasKeyDown = devExt->TapDanceKeyDown;

        shouldFire = TRUE;
        scanCode = devExt->TapDanceScanCode;
        isHoldCase = (wasKeyDown && tapCountSoFar == 0);

        if (isHoldCase) {
            action = entry->SingleHoldAction;
            ConfirmTapDanceHoldLocked(devExt, &action);
        } else {
            ULONG finalCount = wasKeyDown ? (tapCountSoFar + 1) : tapCountSoFar;
            action = TapActionForCount(entry, finalCount);
            devExt->TapDancePendingActive = FALSE;
            if (wasKeyDown) {
                devExt->TapDanceDraining = TRUE;
            }
        }
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    if (shouldFire) {
        if (isHoldCase) {
            KBDLAYER_TRACE("LayerEngine: tapdance TIMEOUT SingleHold confirmed sc=0x%X\n", scanCode);
            EmitTapDanceHoldEventIfApplicable(devExt, &action, KEY_MAKE);
        } else {
            KBDLAYER_TRACE("LayerEngine: tapdance TIMEOUT tap confirmed sc=0x%X type=%u\n", scanCode, action.Type);
            SendDecodedEvent(devExt, action.Param2, KEY_MAKE);
            SendDecodedEvent(devExt, action.Param2, KEY_BREAK);
        }
    }
    KeReleaseSpinLockFromDpcLevel(&devExt->OutputLock);

    // LTのホールド確定やOSLの消費でレイヤーが変わっていれば、待っているアプリへ知らせる。
    LayerNotifyIfChanged(devExt);
}

_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
ResolveTapDance(
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

    // 他のResolve*と同じ理由で1件バッチのみを対象とする。
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

    // --- HoldConfirmed: SingleHold確定済み、自分自身の解放待ち ---
    if (devExt->TapDanceHoldConfirmed) {
        if (scanCode != devExt->TapDanceScanCode) {
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            return FALSE;
        }
        if (!isBreak) {
            // オートリピート、飲み込むだけ（Phase 2と同じ教訓）。
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            if (SystemArgument3 != NULL) {
                *(PULONG)SystemArgument3 = 1;
            }
            return TRUE;
        }
        {
            KBDLAYER_ACTION holdAction = devExt->TapDances[devExt->TapDanceActiveId].SingleHoldAction;
            ReleaseTapDanceHoldLocked(devExt, &holdAction);
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

            EmitTapDanceHoldEventIfApplicable(devExt, &holdAction, KEY_BREAK);
            if (SystemArgument3 != NULL) {
                *(PULONG)SystemArgument3 = 1;
            }
            return TRUE;
        }
    }

    // --- Draining: N-tap(N>1)確定済み、押されたままの物理キーの解放を無音で待つ ---
    if (devExt->TapDanceDraining) {
        if (scanCode != devExt->TapDanceScanCode) {
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            return FALSE;
        }
        if (isBreak) {
            devExt->TapDanceDraining = FALSE;
        }
        // オートリピートMAKEも含め飲み込む（Draining中は何も送出しない）。
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        if (SystemArgument3 != NULL) {
            *(PULONG)SystemArgument3 = 1;
        }
        return TRUE;
    }

    // --- Pending: 保留サイクル進行中 ---
    if (devExt->TapDancePendingActive) {
        PKBDLAYER_TAPDANCE_ENTRY entry = &devExt->TapDances[devExt->TapDanceActiveId];

        if (scanCode == devExt->TapDanceScanCode) {
            if (devExt->TapDanceKeyDown) {
                if (!isBreak) {
                    // オートリピート、無視。
                    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
                    if (SystemArgument3 != NULL) {
                        *(PULONG)SystemArgument3 = 1;
                    }
                    return TRUE;
                }

                // 解放 = このタップが完了。
                devExt->TapDanceKeyDown = FALSE;
                devExt->TapDanceTapCount += 1;

                if (devExt->TapDanceTapCount >= KBDLAYER_MAX_TAP_COUNT) {
                    // 上限到達、これ以上待たずに即確定。
                    KBDLAYER_ACTION tapAction = TapActionForCount(entry, devExt->TapDanceTapCount);
                    devExt->TapDancePendingActive = FALSE;
                    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

                    KeCancelTimer(&devExt->TapDanceTimer);
                    SendDecodedEvent(devExt, tapAction.Param2, KEY_MAKE);
                    SendDecodedEvent(devExt, tapAction.Param2, KEY_BREAK);
                    if (SystemArgument3 != NULL) {
                        *(PULONG)SystemArgument3 = 1;
                    }
                    return TRUE;
                }

                // 次のタップが来るか、タイムアウトするかを待つ。
                {
                    LARGE_INTEGER dueTime = TimerDueLocked(&devExt->TapDanceDeadline, devExt->TapDanceTermMs);
                    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
                    KeSetTimer(&devExt->TapDanceTimer, dueTime, &devExt->TapDanceDpc);
                }
                if (SystemArgument3 != NULL) {
                    *(PULONG)SystemArgument3 = 1;
                }
                return TRUE;
            }

            // 次のタップの押下が来た。
            devExt->TapDanceKeyDown = TRUE;
            {
                // 「次タップ待ち」のタイマーを解除し、今回の押下ぶんの
                // タイムアウト/ホールド監視タイマーを新たにアームする（期限を更新するので、
                // 既にキューされていた「次タップ待ち」のDPCは何もしない）。
                LARGE_INTEGER dueTime = TimerDueLocked(&devExt->TapDanceDeadline, devExt->TapDanceTermMs);
                KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
                KeCancelTimer(&devExt->TapDanceTimer);
                KeSetTimer(&devExt->TapDanceTimer, dueTime, &devExt->TapDanceDpc);
            }
            if (SystemArgument3 != NULL) {
                *(PULONG)SystemArgument3 = 1;
            }
            return TRUE;
        }

        // --- 無関係キーによる割り込み ---
        {
            ULONG tapCountSoFar = devExt->TapDanceTapCount;
            BOOLEAN wasKeyDown = devExt->TapDanceKeyDown;
            BOOLEAN isHoldCase = (wasKeyDown && tapCountSoFar == 0);
            // 押下時の記録があるキー（タップダンスより前から押されていたキーの解放・
            // オートリピート）は、生のまま転送せず通常経路で記録どおりに処理させる。
            // 記録が無い押下は、解放も生のまま転送されるようRAWとして記録する。
            BOOLEAN alreadyHeld = (HeldKeyFor(devExt, scanCode)->State != KBDLAYER_HELD_NONE);
            KBDLAYER_ACTION action;

            if (!alreadyHeld) {
                RecordRawMakesLocked(devExt, InputDataStart, InputDataEnd);
            }

            if (isHoldCase) {
                // 1回目の押下中の割り込み = SingleHold確定（Phase 2のHold On Other Key
                // Pressと同じ規律）。
                action = entry->SingleHoldAction;
                ConfirmTapDanceHoldLocked(devExt, &action);
                KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

                KeCancelTimer(&devExt->TapDanceTimer);
                EmitTapDanceHoldEventIfApplicable(devExt, &action, KEY_MAKE);
            } else {
                // 2回目以降の押下中、または次のタップ待ち中の割り込み = その時点の
                // タップ回数を確定。押されたままなら解放をDrainingで無音に待つ。
                ULONG finalCount = wasKeyDown ? (tapCountSoFar + 1) : tapCountSoFar;
                action = TapActionForCount(entry, finalCount);
                devExt->TapDancePendingActive = FALSE;
                if (wasKeyDown) {
                    devExt->TapDanceDraining = TRUE;
                }
                KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

                KeCancelTimer(&devExt->TapDanceTimer);
                SendDecodedEvent(devExt, action.Param2, KEY_MAKE);
                SendDecodedEvent(devExt, action.Param2, KEY_BREAK);
            }

            if (alreadyHeld) {
                return FALSE;
            }

            // 続けて割り込みバッチをそのままOriginalClassServiceに渡す（Phase 2の
            // ResolveTapHoldと同じ方式。このキーはレイヤーエンジンでの再解決を経ない
            // 生のスキャンコードのまま転送される、既知の制約も同様）。
            for (PKEYBOARD_INPUT_DATA p = InputDataStart; p < InputDataEnd; p++) {
                p->ExtraInformation = (p->ExtraInformation & 0x0000FFFFUL) | (devExt->DeviceId & 0xFFFF0000UL);
            }
            TrackOutputModifiers(devExt, InputDataStart, InputDataEnd);
            devExt->OriginalClassService(
                devExt->OriginalClassDeviceObject,
                InputDataStart,
                InputDataEnd,
                SystemArgument3);
            return TRUE;
        }
    }

    // --- Idle: このキー自体がTAPDANCEアクションに解決される押下イベントなら、
    //     新しい保留サイクルを開始する（押下時の記録があるキーのオートリピートは対象外） ---
    if (!isBreak && HeldKeyFor(devExt, scanCode)->State == KBDLAYER_HELD_NONE) {
        PKBDLAYER_KEY_ENTRY keyEntry = FindKeyEntry(devExt->LayerMap, scanCode);
        if (keyEntry != NULL) {
            PKBDLAYER_ACTION action = ResolveEffectiveAction(devExt, devExt->LayerMap, keyEntry);
            if (action != NULL && action->Type == KBDLAYER_ACTION_TAPDANCE &&
                action->Param1 < KBDLAYER_MAX_TAPDANCE_COUNT && devExt->TapDanceUsed[action->Param1]) {
                LARGE_INTEGER dueTime;

                devExt->TapDancePendingActive = TRUE;
                devExt->TapDanceKeyDown = TRUE;
                devExt->TapDanceTapCount = 0;
                devExt->TapDanceScanCode = scanCode;
                devExt->TapDanceActiveId = action->Param1;
                dueTime = TimerDueLocked(&devExt->TapDanceDeadline, devExt->TapDanceTermMs);
                KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

                KeSetTimer(&devExt->TapDanceTimer, dueTime, &devExt->TapDanceDpc);

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
// 設定変更・OFFで判定状態を打ち切るときの後始末
//
// 状態フラグを0に戻すだけだと、(1) 送出済みのmake（MTの修飾キー・コンボの出力キー・
// タップダンスのホールド出力）に対応するbreakが二度と送られず押しっぱなしになり、
// (2) まだ押されている物理キーの解放が後から素通しでOSに届く（makeの無いbreak）。
// Abort*Lockedは、送るべきbreakをReleasesに集め、まだ押されている物理キーをHeldKeysに
// SWALLOWEDとして記録してから状態をリセットする（記録した解放はResolveLayerActionで
// 捨てられる）。判定前の保留（MTの保留中・コンボの相方待ち・タップダンスの次タップ待ち）は
// 何も送らずに捨てる。いずれもEngineLock保持中に呼ぶ。集めたbreakは、EngineLockを
// 放した後、OutputLockを保持したままSendAbortReleasesで送る（SetNicolaForDeviceと同じ形）。
// ---------------------------------------------------------------------------

#define KBDLAYER_MAX_ABORT_RELEASES 4

typedef struct _KBDLAYER_ABORT_RELEASES {
    ULONG Count;
    USHORT ScanCodes[KBDLAYER_MAX_ABORT_RELEASES]; // Enc16
} KBDLAYER_ABORT_RELEASES, *PKBDLAYER_ABORT_RELEASES;

_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
AppendAbortRelease(
    _Inout_ PKBDLAYER_ABORT_RELEASES Releases,
    _In_ USHORT ScanCode
    )
{
    if (ScanCode != 0 && Releases->Count < KBDLAYER_MAX_ABORT_RELEASES) {
        Releases->ScanCodes[Releases->Count++] = ScanCode;
    }
}

// まだ押されている物理キーの解放を、後から届いたときに捨てるよう記録する。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
SwallowPhysicalKeyLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ USHORT ScanCode
    )
{
    if (ScanCode != 0 && HeldKeyFor(devExt, ScanCode)->State == KBDLAYER_HELD_NONE) {
        SetHeldKeyLocked(devExt, ScanCode, KBDLAYER_HELD_SWALLOWED, NULL);
    }
}

// Phase 2: MT/LT。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
AbortTapHoldLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _Inout_ PKBDLAYER_ABORT_RELEASES Releases
    )
{
    if (devExt->HoldConfirmed) {
        KBDLAYER_ACTION action = devExt->PendingAction;
        if (action.Type == KBDLAYER_ACTION_MODTAP && action.Param1 < RTL_NUMBER_OF(s_ModifierScanCodes)) {
            AppendAbortRelease(Releases, s_ModifierScanCodes[action.Param1]);
        } else if (action.Type == KBDLAYER_ACTION_LAYERTAP && action.Param1 < KBDLAYER_MAX_LAYERS) {
            devExt->ActiveLayerMask &= ~(1UL << action.Param1);
        }
        SwallowPhysicalKeyLocked(devExt, devExt->PendingScanCode);
    } else if (devExt->PendingActive) {
        SwallowPhysicalKeyLocked(devExt, devExt->PendingScanCode);
    }
    devExt->PendingActive = FALSE;
    devExt->HoldConfirmed = FALSE;
}

// Phase 4: コンボ。Combos[ComboActiveId]を読むので、SetComboForDeviceではエントリを
// 書き換える前に呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
AbortComboLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _Inout_ PKBDLAYER_ABORT_RELEASES Releases
    )
{
    PKBDLAYER_COMBO_ENTRY entry = &devExt->Combos[devExt->ComboActiveId];

    if (devExt->ComboConfirmed) {
        // 成立時は2つのメンバーがどちらも押されたまま（片方の解放でDrainingへ移る）。
        AppendAbortRelease(Releases, entry->OutputAction.Param2);
        SwallowPhysicalKeyLocked(devExt, entry->ScanCode1);
        SwallowPhysicalKeyLocked(devExt, entry->ScanCode2);
    } else if (devExt->ComboDraining) {
        SwallowPhysicalKeyLocked(devExt, devExt->ComboDrainingScanCode);
    } else if (devExt->ComboPendingActive) {
        SwallowPhysicalKeyLocked(devExt, devExt->ComboPendingScanCode);
    }
    devExt->ComboPendingActive = FALSE;
    devExt->ComboConfirmed = FALSE;
    devExt->ComboDraining = FALSE;
}

// Phase 5: タップダンス。TapDances[TapDanceActiveId]を読むので、SetTapDanceForDeviceでは
// エントリを書き換える前に呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
AbortTapDanceLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _Inout_ PKBDLAYER_ABORT_RELEASES Releases
    )
{
    if (devExt->TapDanceHoldConfirmed) {
        KBDLAYER_ACTION holdAction = devExt->TapDances[devExt->TapDanceActiveId].SingleHoldAction;
        if (holdAction.Type == KBDLAYER_ACTION_MODTAP && holdAction.Param1 < RTL_NUMBER_OF(s_ModifierScanCodes)) {
            AppendAbortRelease(Releases, s_ModifierScanCodes[holdAction.Param1]);
        } else if (holdAction.Type == KBDLAYER_ACTION_KEY) {
            AppendAbortRelease(Releases, holdAction.Param2);
        }
        ReleaseTapDanceHoldLocked(devExt, &holdAction); // LAYERTAPならレイヤーのビットを落とす
        SwallowPhysicalKeyLocked(devExt, devExt->TapDanceScanCode);
    } else if (devExt->TapDanceDraining ||
               (devExt->TapDancePendingActive && devExt->TapDanceKeyDown)) {
        SwallowPhysicalKeyLocked(devExt, devExt->TapDanceScanCode);
    }
    devExt->TapDancePendingActive = FALSE;
    devExt->TapDanceKeyDown = FALSE;
    devExt->TapDanceHoldConfirmed = FALSE;
    devExt->TapDanceDraining = FALSE;
}

// Abort*Lockedが集めたbreakを送る。OutputLockを保持し（=DISPATCH_LEVEL）、EngineLockは
// 保持していない状態で呼ぶこと。
_IRQL_requires_(DISPATCH_LEVEL)
static VOID
SendAbortReleases(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ const KBDLAYER_ABORT_RELEASES* Releases
    )
{
    if (devExt->OriginalClassService == NULL) {
        return; // まだkbdclassに接続されていない（この場合は送出済みのmakeも無い）
    }
    for (ULONG i = 0; i < Releases->Count; i++) {
        KBDLAYER_TRACE("LayerEngine: abort releases sc=0x%X\n", Releases->ScanCodes[i]);
        SendDecodedEvent(devExt, Releases->ScanCodes[i], KEY_BREAK);
    }
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

    // レイヤー数が変わるとNICOLAの有効判定などが変わり得るので、待っているアプリへ知らせる。
    LayerNotifyIfChanged(devExt);
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

    LayerNotifyIfChanged(devExt);
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetLayerMapEnabledForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ BOOLEAN Enabled
    )
{
    KIRQL outputIrql;
    WCHAR subKeyPath[64];
    KBDLAYER_ABORT_RELEASES releases;

    releases.Count = 0;

    // OutputLockはDISPATCH_LEVELに上げるので、以降のEngineLockはAtDpcLevelで取る。
    KeAcquireSpinLock(&devExt->OutputLock, &outputIrql);
    KeAcquireSpinLockAtDpcLevel(&devExt->EngineLock);
    if (Enabled) {
        if (devExt->LayerMap == NULL) {
            KeReleaseSpinLockFromDpcLevel(&devExt->EngineLock);
            KeReleaseSpinLock(&devExt->OutputLock, outputIrql);
            return STATUS_INVALID_DEVICE_STATE;
        }
        devExt->LayerMapEnabled = TRUE;
    } else {
        devExt->LayerMapEnabled = FALSE;
        // Phase 2/4/5: 保留/確定/ドレイン中の判定を打ち切る。送出済みのmakeのbreakを集め、
        // まだ押されている物理キーの解放は後から捨てるよう記録する（Abort*Locked参照）。
        // タイマーはこの後キャンセルする。DPCはOutputLockを取ってから状態を見るので、
        // キャンセルが競合で間に合わなくても、リセット後の状態を見て何もしない。
        AbortTapHoldLocked(devExt, &releases);
        AbortComboLocked(devExt, &releases);
        AbortTapDanceLocked(devExt, &releases);
        // 素通しに戻すときはレイヤー状態もリセットする（次にONにしたとき、
        // 直前にどのレイヤーがアクティブだったかに依存させないため）。
        devExt->ActiveLayerMask = 0;
        devExt->DefaultLayer = 0;
        devExt->OneShotLayerPending = FALSE;
        devExt->OneShotLayerBit = 0;
    }
    KeReleaseSpinLockFromDpcLevel(&devExt->EngineLock);

    if (!Enabled) {
        KeCancelTimer(&devExt->HoldTimer);
        KeCancelTimer(&devExt->ComboTimer);
        KeCancelTimer(&devExt->TapDanceTimer);
        SendAbortReleases(devExt, &releases);
    }
    KeReleaseSpinLock(&devExt->OutputLock, outputIrql);

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    RegWriteDword(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_ENABLED_VALUE_NAME, Enabled ? 1 : 0);

    // ON/OFF自体と、OFF時のレイヤー状態のリセットを、待っているアプリへ知らせる。
    // （アプリ管理レイヤーはOFFでも消さない。持ち主のアプリが下ろす）
    LayerNotifyIfChanged(devExt);
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
static VOID
LoadPersistedTapDancesForDevice(
    _In_ PDEVICE_EXTENSION devExt
    );

_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
LoadPersistedNicolaForDevice(
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
    LoadPersistedTapDancesForDevice(devExt);
    LoadPersistedNicolaForDevice(devExt);
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
NTSTATUS
SetTapDanceTermForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG TapDanceTermMs
    )
{
    KIRQL oldIrql;
    ULONG effective = (TapDanceTermMs != 0) ? TapDanceTermMs : KBDLAYER_DEFAULT_TAPDANCE_TERM_MS;
    WCHAR subKeyPath[64];

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    devExt->TapDanceTermMs = effective;
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    RegWriteDword(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_TAPDANCETERM_VALUE_NAME, effective);

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

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeTapDanceState(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    KeInitializeTimer(&devExt->TapDanceTimer);
    KeInitializeDpc(&devExt->TapDanceDpc, TapDanceDpcRoutine, devExt);
    devExt->TapDanceTermMs = KBDLAYER_DEFAULT_TAPDANCE_TERM_MS;
    devExt->TapDancePendingActive = FALSE;
    devExt->TapDanceKeyDown = FALSE;
    devExt->TapDanceHoldConfirmed = FALSE;
    devExt->TapDanceDraining = FALSE;
    devExt->TapDanceScanCode = 0;
    devExt->TapDanceActiveId = 0;
    devExt->TapDanceTapCount = 0;
    RtlZeroMemory(devExt->TapDances, sizeof(devExt->TapDances));
    RtlZeroMemory(devExt->TapDanceUsed, sizeof(devExt->TapDanceUsed));
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

    // 非ページプールで確保する。下でEngineLockを保持したまま（DISPATCH_LEVELで）書き込むため、
    // ページプールだとページアウトされていたときにBSOD（0xD1）になる。Driver Verifierの
    // Force IRQL checkingではIRQLを上げるたびにページ可能メモリが追い出されるので毎回落ちる
    // （2026-09-27、Verifier下の--set-macroで発生）。RegWriteBinaryに非ページのバッファを
    // 渡すのは問題ない。
    buffer = (PKBDLAYER_MACRO_PERSIST_ENTRY)ExAllocatePool2(
        POOL_FLAG_NON_PAGED, KBDLAYER_MACRO_PERSIST_BLOB_BYTES, KBDLAYER_REGTEMP_POOL_TAG);
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

    // 非ページプールで確保する。EngineLockを保持したまま書き込むため（PersistAllMacros参照）。
    buffer = (PKBDLAYER_COMBO_PERSIST_ENTRY)ExAllocatePool2(
        POOL_FLAG_NON_PAGED, KBDLAYER_COMBO_PERSIST_BLOB_BYTES, KBDLAYER_REGTEMP_POOL_TAG);
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
    KIRQL outputIrql;
    KBDLAYER_ABORT_RELEASES releases;

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

    releases.Count = 0;

    // OutputLockはDISPATCH_LEVELに上げるので、以降のEngineLockはAtDpcLevelで取る。
    KeAcquireSpinLock(&devExt->OutputLock, &outputIrql);
    KeAcquireSpinLockAtDpcLevel(&devExt->EngineLock);

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
                KeReleaseSpinLockFromDpcLevel(&devExt->EngineLock);
                KeReleaseSpinLock(&devExt->OutputLock, outputIrql);
                return STATUS_INVALID_PARAMETER;
            }
        }
    }

    // 設定変更と整合しない状態を残さないよう、ライブのコンボ判定状態は常に打ち切る。
    // 成立中の出力キーのbreakを集めるため、エントリを書き換える前に行う（AbortComboLocked参照）。
    AbortComboLocked(devExt, &releases);

    if (Entry != NULL) {
        devExt->Combos[ComboId] = *Entry;
        devExt->ComboUsed[ComboId] = TRUE;
    } else {
        RtlZeroMemory(&devExt->Combos[ComboId], sizeof(devExt->Combos[ComboId]));
        devExt->ComboUsed[ComboId] = FALSE;
    }
    KeReleaseSpinLockFromDpcLevel(&devExt->EngineLock);

    KeCancelTimer(&devExt->ComboTimer);
    SendAbortReleases(devExt, &releases);
    KeReleaseSpinLock(&devExt->OutputLock, outputIrql);

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
// Phase 5: タップダンスの設定関数（PASSIVE_LEVEL）
// ---------------------------------------------------------------------------

// devExt->TapDances/TapDanceUsed全体を1つのblobへシリアライズしてレジストリへ書き込む。
// ベストエフォート（確保失敗時は何もしない。ライブ状態には影響しない）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
PersistAllTapDances(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    WCHAR subKeyPath[64];
    PKBDLAYER_TAPDANCE_PERSIST_ENTRY buffer;
    KIRQL oldIrql;

    // 非ページプールで確保する。EngineLockを保持したまま書き込むため（PersistAllMacros参照）。
    buffer = (PKBDLAYER_TAPDANCE_PERSIST_ENTRY)ExAllocatePool2(
        POOL_FLAG_NON_PAGED, KBDLAYER_TAPDANCE_PERSIST_BLOB_BYTES, KBDLAYER_REGTEMP_POOL_TAG);
    if (buffer == NULL) {
        return;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    for (ULONG i = 0; i < KBDLAYER_MAX_TAPDANCE_COUNT; i++) {
        buffer[i].Used = devExt->TapDanceUsed[i] ? 1 : 0;
        buffer[i].Entry = devExt->TapDances[i];
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    RegWriteBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_TAPDANCES_VALUE_NAME,
                    buffer, KBDLAYER_TAPDANCE_PERSIST_BLOB_BYTES);

    ExFreePoolWithTag(buffer, KBDLAYER_REGTEMP_POOL_TAG);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetTapDanceForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG TapDanceId,
    _In_opt_ PKBDLAYER_TAPDANCE_ENTRY Entry
    )
{
    KIRQL outputIrql;
    KBDLAYER_ABORT_RELEASES releases;

    if (TapDanceId >= KBDLAYER_MAX_TAPDANCE_COUNT) {
        return STATUS_INVALID_PARAMETER;
    }

    if (Entry != NULL) {
        // v1制限: SingleTap/DoubleTap/TripleTapはKEYのみ、SingleHoldはKEY/MODTAP/LAYERTAPの
        // みを許可する（common/kbdlayer_ioctl.h参照）。
        if (Entry->SingleTapAction.Type != KBDLAYER_ACTION_KEY ||
            Entry->DoubleTapAction.Type != KBDLAYER_ACTION_KEY ||
            Entry->TripleTapAction.Type != KBDLAYER_ACTION_KEY) {
            return STATUS_INVALID_PARAMETER;
        }
        if (Entry->SingleHoldAction.Type != KBDLAYER_ACTION_KEY &&
            Entry->SingleHoldAction.Type != KBDLAYER_ACTION_MODTAP &&
            Entry->SingleHoldAction.Type != KBDLAYER_ACTION_LAYERTAP) {
            return STATUS_INVALID_PARAMETER;
        }
    }

    releases.Count = 0;

    // OutputLockはDISPATCH_LEVELに上げるので、以降のEngineLockはAtDpcLevelで取る。
    KeAcquireSpinLock(&devExt->OutputLock, &outputIrql);
    KeAcquireSpinLockAtDpcLevel(&devExt->EngineLock);

    // 設定変更と整合しない状態を残さないよう、ライブのタップダンス判定状態は常に打ち切る
    // （SetComboForDeviceと同じ理屈。ホールド出力のbreakを集めるため、エントリを
    // 書き換える前に行う）。
    AbortTapDanceLocked(devExt, &releases);

    if (Entry != NULL) {
        devExt->TapDances[TapDanceId] = *Entry;
        devExt->TapDanceUsed[TapDanceId] = TRUE;
    } else {
        RtlZeroMemory(&devExt->TapDances[TapDanceId], sizeof(devExt->TapDances[TapDanceId]));
        devExt->TapDanceUsed[TapDanceId] = FALSE;
    }
    KeReleaseSpinLockFromDpcLevel(&devExt->EngineLock);

    KeCancelTimer(&devExt->TapDanceTimer);
    SendAbortReleases(devExt, &releases);
    KeReleaseSpinLock(&devExt->OutputLock, outputIrql);

    PersistAllTapDances(devExt);

    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
GetTapDanceForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG TapDanceId,
    _Out_ PKBDLAYER_TAPDANCE_ENTRY OutEntry,
    _Out_ PBOOLEAN OutUsed
    )
{
    KIRQL oldIrql;

    if (TapDanceId >= KBDLAYER_MAX_TAPDANCE_COUNT) {
        return STATUS_INVALID_PARAMETER;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    *OutEntry = devExt->TapDances[TapDanceId];
    *OutUsed = devExt->TapDanceUsed[TapDanceId];
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    return STATUS_SUCCESS;
}

// AddDeviceから呼ばれる: レジストリの"TapDances"/"TapDanceTermMs"値を読み、
// devExt->TapDances/TapDanceUsed/TapDanceTermMsを初期化する
// （LoadPersistedCombosForDeviceと同じ理屈）。失敗時は全タップダンス未設定・
// TapDanceTermMs=既定値のままでよい（ベストエフォート）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
LoadPersistedTapDancesForDevice(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    WCHAR subKeyPath[64];
    PKBDLAYER_TAPDANCE_PERSIST_ENTRY buffer;
    ULONG actualLength = 0;
    NTSTATUS status;
    ULONG tapDanceTermValue = 0;

    buffer = (PKBDLAYER_TAPDANCE_PERSIST_ENTRY)ExAllocatePool2(
        POOL_FLAG_PAGED, KBDLAYER_TAPDANCE_PERSIST_BLOB_BYTES, KBDLAYER_REGTEMP_POOL_TAG);
    if (buffer != NULL) {
        BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
        status = RegReadBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_TAPDANCES_VALUE_NAME,
                                buffer, KBDLAYER_TAPDANCE_PERSIST_BLOB_BYTES, &actualLength);
        if (NT_SUCCESS(status) && actualLength == KBDLAYER_TAPDANCE_PERSIST_BLOB_BYTES) {
            for (ULONG i = 0; i < KBDLAYER_MAX_TAPDANCE_COUNT; i++) {
                devExt->TapDanceUsed[i] = (buffer[i].Used != 0);
                devExt->TapDances[i] = buffer[i].Entry;
            }
        }
        // actualLengthが一致しない場合（値が存在しない、または将来の互換性が
        // 壊れた場合）は何もしない。InitializeTapDanceStateでRtlZeroMemory済みのため
        // 全タップダンス未設定のままで安全。
        ExFreePoolWithTag(buffer, KBDLAYER_REGTEMP_POOL_TAG);
    }

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    RegReadDwordEx(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_TAPDANCETERM_VALUE_NAME,
                    &tapDanceTermValue, KBDLAYER_DEFAULT_TAPDANCE_TERM_MS);
    devExt->TapDanceTermMs = (tapDanceTermValue != 0) ? tapDanceTermValue : KBDLAYER_DEFAULT_TAPDANCE_TERM_MS;
}

// ---------------------------------------------------------------------------
// キャリブレーションウィザード
// ---------------------------------------------------------------------------

// CAPTURE_NEXT_KEYの保留IRPを完了させる。EngineLockを保持していない状態で呼ぶこと。
// IrpはTakeCaptureIrpLocked/キャンセルルーチンで所有権を得たものに限る。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
CompleteCaptureIrp(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PIRP Irp,
    _In_ NTSTATUS Status,
    _In_ USHORT ScanCode,
    _In_ BOOLEAN TimedOut
    )
{
    Irp->IoStatus.Status = Status;
    Irp->IoStatus.Information = 0;
    if (NT_SUCCESS(Status)) {
        // METHOD_BUFFERED。出力バッファ長はStartCaptureNextKeyの呼び出し元で検証済み。
        PKBDLAYER_CAPTURE_NEXT_KEY_OUTPUT out = (PKBDLAYER_CAPTURE_NEXT_KEY_OUTPUT)Irp->AssociatedIrp.SystemBuffer;
        out->ScanCode = ScanCode;
        out->TimedOut = TimedOut ? 1 : 0;
        Irp->IoStatus.Information = sizeof(KBDLAYER_CAPTURE_NEXT_KEY_OUTPUT);
    }
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
}

// devExt->EngineLock保持中に呼ぶこと。保留中のIRPの所有権を取り出して捕捉を終える。
// キャンセルルーチンが既に動き出している場合はNULLを返す（完了はキャンセルルーチンが行う）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static PIRP
TakeCaptureIrpLocked(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    PIRP irp = devExt->CaptureIrp;

    devExt->CaptureActive = FALSE;
    devExt->CaptureDone = FALSE;
    if (irp == NULL) {
        return NULL;
    }
    if (IoSetCancelRoutine(irp, NULL) == NULL) {
        // キャンセルルーチンが所有権を持っている。CaptureIrpはそちらで消す。
        return NULL;
    }
    devExt->CaptureIrp = NULL;
    return irp;
}

_Function_class_(DRIVER_CANCEL)
_Requires_lock_held_(_Global_cancel_spin_lock_)
_Releases_lock_(_Global_cancel_spin_lock_)
_IRQL_requires_min_(DISPATCH_LEVEL)
_IRQL_requires_(DISPATCH_LEVEL)
static VOID
CaptureCancelRoutine(
    _Inout_ PDEVICE_OBJECT DeviceObject,
    _Inout_ _IRQL_uses_cancel_ PIRP Irp
    )
{
    PDEVICE_EXTENSION devExt = (PDEVICE_EXTENSION)Irp->Tail.Overlay.DriverContext[0];
    KIRQL oldIrql;

    UNREFERENCED_PARAMETER(DeviceObject);
    IoReleaseCancelSpinLock(Irp->CancelIrql);

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    if (devExt->CaptureIrp == Irp) {
        devExt->CaptureIrp = NULL;
        devExt->CaptureActive = FALSE;
        devExt->CaptureDone = FALSE;
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    KBDLAYER_TRACE("CaptureCancelRoutine: cancelled DeviceId=0x%08X\n", devExt->DeviceId);
    CompleteCaptureIrp(devExt, Irp, STATUS_CANCELLED, 0, FALSE);
}

// CaptureTimerの満了で起動されるDPC。期限を過ぎていれば捕捉をタイムアウトとして完了させる
// （期限前なら、直前の捕捉用に張ったタイマーが遅れて届いただけなので何もしない）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
CaptureDpcRoutine(
    _In_ PKDPC Dpc,
    _In_opt_ PVOID DeferredContext,
    _In_opt_ PVOID SystemArgument1,
    _In_opt_ PVOID SystemArgument2
    )
{
    PDEVICE_EXTENSION devExt = (PDEVICE_EXTENSION)DeferredContext;
    KIRQL oldIrql;
    PIRP irp = NULL;

    UNREFERENCED_PARAMETER(Dpc);
    UNREFERENCED_PARAMETER(SystemArgument1);
    UNREFERENCED_PARAMETER(SystemArgument2);

    if (devExt == NULL) {
        return;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    if (devExt->CaptureIrp != NULL && !devExt->CaptureDone &&
        KeQueryInterruptTime() >= devExt->CaptureDeadline) {
        irp = TakeCaptureIrpLocked(devExt);
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    if (irp != NULL) {
        CompleteCaptureIrp(devExt, irp, STATUS_SUCCESS, 0, TRUE);
    }
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeCaptureState(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    KeInitializeTimer(&devExt->CaptureTimer);
    KeInitializeDpc(&devExt->CaptureDpc, CaptureDpcRoutine, devExt);
    devExt->CaptureActive = FALSE;
    devExt->CaptureDone = FALSE;
    devExt->CaptureIrp = NULL;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
StartCaptureNextKey(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PIRP Irp,
    _In_ ULONG TimeoutMs
    )
{
    KIRQL oldIrql;
    LARGE_INTEGER dueTime;

    if (TimeoutMs > KBDLAYER_CAPTURE_MAX_TIMEOUT_MS) {
        TimeoutMs = KBDLAYER_CAPTURE_MAX_TIMEOUT_MS;
    }
    Irp->Tail.Overlay.DriverContext[0] = devExt;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    if (devExt->RemovePending) {
        // デバイス削除が始まっている（DeviceExtension.hのRemovePending参照）。保留中のIRPを
        // 削除で終わらせるときと同じSTATUS_DEVICE_REMOVED（Win32: 1617）を返す。
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return STATUS_DEVICE_REMOVED;
    }
    if (devExt->CaptureActive || devExt->CaptureIrp != NULL) {
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return STATUS_DEVICE_BUSY;
    }

    IoSetCancelRoutine(Irp, CaptureCancelRoutine);
    if (Irp->Cancel && IoSetCancelRoutine(Irp, NULL) != NULL) {
        // 保留にする前に既にキャンセルされていた。
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return STATUS_CANCELLED;
    }

    // 完了させる側（キー到着・DPC・キャンセル）はいずれもEngineLockを取ってから
    // CaptureIrpを見るので、ここでIoMarkIrpPendingしておけば順序は守られる。
    IoMarkIrpPending(Irp);
    devExt->CaptureIrp = Irp;
    devExt->CaptureActive = TRUE;
    devExt->CaptureDone = FALSE;
    devExt->CapturedScanCode = 0;
    devExt->CaptureDeadline = KeQueryInterruptTime() + (ULONGLONG)TimeoutMs * 10000ULL;
    // 入力抑止のアイドルタイムアウトを延長する（キャプチャ待機そのものが
    // 「キャリブレーション継続中」のアクティビティ）。
    devExt->SuppressLastActivity = KeQueryInterruptTime();
    // ロック内で張る。前回の捕捉用のタイマーは、ここで張り直されて無効になる
    // （既にキュー済みのDPCはCaptureDeadlineで見分ける）。
    dueTime.QuadPart = -((LONGLONG)TimeoutMs * 10000LL);
    KeSetTimer(&devExt->CaptureTimer, dueTime, &devExt->CaptureDpc);
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    return STATUS_PENDING;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
AbortCaptureNextKey(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ NTSTATUS Status
    )
{
    KIRQL oldIrql;
    PIRP irp;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    irp = TakeCaptureIrpLocked(devExt);
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    KeCancelTimer(&devExt->CaptureTimer);
    if (irp != NULL) {
        CompleteCaptureIrp(devExt, irp, Status, 0, FALSE);
    }
}

// ---------------------------------------------------------------------------
// キャリブレーション: キャプチャ記録と入力抑止（ServiceCallbackの先頭）
// ---------------------------------------------------------------------------

// KeQueryInterruptTime()は100ns単位。
#define KBDLAYER_SUPPRESS_IDLE_TIMEOUT_100NS \
    ((ULONGLONG)KBDLAYER_CAPTURE_SUPPRESS_IDLE_TIMEOUT_MS * 10000ULL)

_IRQL_requires_max_(DISPATCH_LEVEL)
static ULONG
SuppressBitIndex(
    _In_ PKEYBOARD_INPUT_DATA Entry
    )
{
    ULONG index = Entry->MakeCode & 0xFF;
    if (Entry->Flags & KEY_E1) {
        index |= 0x200;
    } else if (Entry->Flags & KEY_E0) {
        index |= 0x100;
    }
    return index;
}

// PrintScreen等の前置きとして届く疑似Shift（E0 2A / E0 36）。本物の左右Shiftは
// E0無しの0x2A/0x36なので、E0付きだけを除外すれば実キーとは衝突しない。
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN
IsFakeShift(
    _In_ PKEYBOARD_INPUT_DATA Entry
    )
{
    return (Entry->Flags & KEY_E0) &&
           ((Entry->MakeCode & 0xFF) == 0x2A || (Entry->MakeCode & 0xFF) == 0x36);
}

// devExt->EngineLock保持中にCompactInputDataArrayから1エントリずつ呼ばれる。
// キャプチャ記録を行い、抑止中なら捨てるべきエントリにTRUEを返す。
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN
CalibrationShouldDropEntry(
    _In_ PDEVICE_EXTENSION devExt,
    _Inout_ PKEYBOARD_INPUT_DATA Entry,
    _In_opt_ PVOID Context
    )
{
    BOOLEAN isBreak = (Entry->Flags & KEY_BREAK) != 0;

    UNREFERENCED_PARAMETER(Context);

    if (!isBreak) {
        // E1シーケンスの2イベント目は、キャプチャ待機の有無に関わらずここで消費する。
        BOOLEAN skip = devExt->CaptureSkipAfterE1;
        devExt->CaptureSkipAfterE1 = FALSE;

        if (devExt->CaptureActive && !skip && !IsFakeShift(Entry)) {
            devExt->CapturedScanCode = EncodeScancode16(Entry->MakeCode, Entry->Flags);
            devExt->CaptureActive = FALSE;
            // IRPの完了はロック解放後にResolveCalibrationが行う。
            devExt->CaptureDone = TRUE;
        }
        if (Entry->Flags & KEY_E1) {
            devExt->CaptureSkipAfterE1 = TRUE;
        }
    }

    {
        ULONG index = SuppressBitIndex(Entry);
        UCHAR mask = (UCHAR)(1u << (index & 7));
        BOOLEAN wasSuppressed = (devExt->SuppressedDown[index >> 3] & mask) != 0;

        if (isBreak) {
            // 押下を捨てたキーの解放は、抑止の解除後に届いた場合も1回だけ捨てる
            // （解除のきっかけになった最後のキャプチャキーの解放がOSに漏れないように）。
            if (wasSuppressed) {
                devExt->SuppressedDown[index >> 3] &= (UCHAR)~mask;
                if (devExt->SuppressedDownCount > 0) {
                    devExt->SuppressedDownCount--;
                }
                return TRUE;
            }
            // 抑止開始前から押されていたキーの解放（例: コマンド実行時のEnter）は通す。
            // 通さないとOS側でそのキーが押しっぱなし扱いになる。
            return FALSE;
        }

        if (devExt->SuppressActive) {
            if (!wasSuppressed) {
                devExt->SuppressedDown[index >> 3] |= mask;
                devExt->SuppressedDownCount++;
            }
            return TRUE;
        }
        if (wasSuppressed) {
            // 抑止中に押して、解除後も押しっぱなしのキーのオートリピート。押下を捨てた
            // ままなので、OS側で押下だけが届いた状態にしないよう引き続き捨てる。
            return TRUE;
        }
    }
    return FALSE;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
PKEYBOARD_INPUT_DATA
ResolveCalibration(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd
    )
{
    KIRQL oldIrql;
    PKEYBOARD_INPUT_DATA newEnd;
    PIRP captureIrp = NULL;
    USHORT capturedScanCode = 0;

    // 通常時（キャリブレーションしていない間）のホットパスを軽く保つため、
    // ロック無しで先に判定する。単純な値の読み取りなので、境界でのレースは
    // 高々1イベント分の記録・抑止のずれにしかならない。
    if (!devExt->CaptureActive && !devExt->SuppressActive && !devExt->CaptureSkipAfterE1 &&
        devExt->SuppressedDownCount == 0) {
        return InputDataEnd;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);

    // アイドルタイムアウト: キャプチャ待機中でもなく、一定時間アクティビティが
    // 無ければ抑止を解除する（ツールがハングした場合でもキーボードを取り戻せるように）。
    // SuppressedDownは消さない（押下を捨てたキーの解放を、解除後も捨てるため）。
    if (devExt->SuppressActive && !devExt->CaptureActive &&
        KeQueryInterruptTime() - devExt->SuppressLastActivity > KBDLAYER_SUPPRESS_IDLE_TIMEOUT_100NS) {
        devExt->SuppressActive = FALSE;
        devExt->SuppressOwner = NULL;
        KBDLAYER_TRACE("ResolveCalibration: suppress idle timeout, released DeviceId=0x%08X\n", devExt->DeviceId);
    }

    newEnd = CompactInputDataArray(devExt, InputDataStart, InputDataEnd, CalibrationShouldDropEntry, NULL);

    if (devExt->CaptureDone) {
        capturedScanCode = devExt->CapturedScanCode;
        captureIrp = TakeCaptureIrpLocked(devExt);
    }

    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    if (captureIrp != NULL) {
        KeCancelTimer(&devExt->CaptureTimer);
        CompleteCaptureIrp(devExt, captureIrp, STATUS_SUCCESS, capturedScanCode, FALSE);
    }
    return newEnd;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetCaptureSuppressForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ BOOLEAN Enable,
    _In_ PVOID Owner
    )
{
    KIRQL oldIrql;
    NTSTATUS status = STATUS_SUCCESS;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    if (Enable) {
        if (devExt->SuppressActive && devExt->SuppressOwner != Owner) {
            status = STATUS_DEVICE_BUSY;
        } else {
            // SuppressedDownは消さない（前回の抑止で押下を捨て、まだ押されたままのキーの
            // 解放を捨てるため）。
            devExt->SuppressActive = TRUE;
            devExt->SuppressOwner = Owner;
            devExt->SuppressLastActivity = KeQueryInterruptTime();
        }
    } else {
        // 既に解除済み（タイムアウト等）なら冪等に成功。他のハンドルの抑止は解除させない。
        if (devExt->SuppressActive && devExt->SuppressOwner != Owner) {
            status = STATUS_ACCESS_DENIED;
        } else {
            // SuppressedDownは消さない（押下を捨てたキーの解放を、解除後も捨てるため）。
            devExt->SuppressActive = FALSE;
            devExt->SuppressOwner = NULL;
        }
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    return status;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
ReleaseCaptureSuppressForOwner(
    _In_ PVOID Owner
    )
{
    KIRQL oldIrql;
    PLIST_ENTRY entry;
    LIST_ENTRY toComplete;

    InitializeListHead(&toComplete);

    // ロック順序: g_listLock -> EngineLock（逆順で取る箇所は無い）。
    KeAcquireSpinLock(&g_listLock, &oldIrql);
    for (entry = g_deviceList.Flink; entry != &g_deviceList; entry = entry->Flink) {
        PDEVICE_EXTENSION devExt = CONTAINING_RECORD(entry, DEVICE_EXTENSION, ListEntry);
        KeAcquireSpinLockAtDpcLevel(&devExt->EngineLock);
        if (devExt->SuppressActive && devExt->SuppressOwner == Owner) {
            // SuppressedDownは消さない（押下を捨てたキーの解放を、解除後も捨てるため）。
            devExt->SuppressActive = FALSE;
            devExt->SuppressOwner = NULL;
            KBDLAYER_TRACE("ReleaseCaptureSuppressForOwner: released DeviceId=0x%08X\n", devExt->DeviceId);
        }
        // このハンドルで保留中のCAPTURE_NEXT_KEYも終わらせる（別スレッドがハンドルを
        // 閉じた場合など、スレッド終了によるキャンセルが届かない経路のため）。
        if (devExt->CaptureIrp != NULL &&
            IoGetCurrentIrpStackLocation(devExt->CaptureIrp)->FileObject == (PFILE_OBJECT)Owner) {
            PIRP irp = TakeCaptureIrpLocked(devExt);
            if (irp != NULL) {
                // 完了はロック解放後に行う。devExtはIrp->Tail.Overlay.DriverContext[0]から引く。
                InsertTailList(&toComplete, &irp->Tail.Overlay.ListEntry);
            }
        }
        KeReleaseSpinLockFromDpcLevel(&devExt->EngineLock);
    }
    KeReleaseSpinLock(&g_listLock, oldIrql);

    while (!IsListEmpty(&toComplete)) {
        PLIST_ENTRY le = RemoveHeadList(&toComplete);
        PIRP irp = CONTAINING_RECORD(le, IRP, Tail.Overlay.ListEntry);
        PDEVICE_EXTENSION devExt = (PDEVICE_EXTENSION)irp->Tail.Overlay.DriverContext[0];
        KeCancelTimer(&devExt->CaptureTimer);
        CompleteCaptureIrp(devExt, irp, STATUS_CANCELLED, 0, FALSE);
    }
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
GetConfigForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _Out_ PKBDLAYER_GET_CONFIG_OUTPUT Out
    )
{
    KIRQL oldIrql;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    Out->TapTermMs = devExt->TapTermMs;
    Out->ComboTermMs = devExt->ComboTermMs;
    Out->TapDanceTermMs = devExt->TapDanceTermMs;
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
}


// ---------------------------------------------------------------------------
// Phase 6: NICOLA（親指シフト）
//
// 同時打鍵の判定はNicolaCore.c（カーネル非依存の状態機械）が行い、ここでは
//   - 物理キーの分類（文字キー / 親指左右 / 対象外）
//   - EngineLockによる直列化と、判定結果 → スキャンコード列への変換
//   - タイマー（NicolaState.Deadline）の張り直し
//   - ロック解放後の送出
// だけを担う。送出はマクロと同じくSendDecodedEventでkbdclassへ直接渡すため、
// 文字キーの出力はレイヤーマップを経由しない（NICOLAは物理位置で配列を決めるため）。
// ---------------------------------------------------------------------------

// 1回の判定で送出しうるステップ数の上限（出力件数 × 1文字の最大ステップ数）。
#define NICOLA_MAX_EMIT_STEPS (NICOLA_CORE_MAX_OUTPUTS * KBDLAYER_NICOLA_MAX_STEPS)

typedef struct _NICOLA_EMIT {
    ULONG StepCount;
    BOOLEAN Pass; // 今回の生イベントを通常経路へ流す
    KBDLAYER_MACRO_STEP Steps[NICOLA_MAX_EMIT_STEPS];
} NICOLA_EMIT, *PNICOLA_EMIT;

_IRQL_requires_max_(DISPATCH_LEVEL)
static PKBDLAYER_NICOLA_KEY_ENTRY
NicolaEntries(
    _In_ PKBDLAYER_NICOLA_HEADER Table
    )
{
    return (PKBDLAYER_NICOLA_KEY_ENTRY)((PUCHAR)Table + sizeof(KBDLAYER_NICOLA_HEADER));
}

_IRQL_requires_max_(DISPATCH_LEVEL)
static PKBDLAYER_NICOLA_KEY_ENTRY
FindNicolaEntry(
    _In_ PKBDLAYER_NICOLA_HEADER Table,
    _In_ USHORT ScanCode
    )
{
    PKBDLAYER_NICOLA_KEY_ENTRY entries = NicolaEntries(Table);
    for (ULONG i = 0; i < Table->KeyCount; i++) {
        if (entries[i].SourceScanCode == ScanCode) {
            return &entries[i];
        }
    }
    return NULL;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
NicolaParamsFromTable(
    _In_ PKBDLAYER_NICOLA_HEADER Table,
    _Out_ PNICOLA_CORE_PARAMS Params
    )
{
    Params->SimulTermMs = (Table->SimulTermMs != 0) ? Table->SimulTermMs : KBDLAYER_NICOLA_DEFAULT_SIMUL_TERM_MS;
    Params->OverlapRatioPercent = (Table->OverlapRatioPercent != 0)
        ? Table->OverlapRatioPercent : KBDLAYER_NICOLA_DEFAULT_OVERLAP_RATIO_PCT;
    Params->MinOverlapMs = Table->MinOverlapMs;
    Params->Flags = Table->Flags;
}

// 判定用の時刻（ms）。KeQueryInterruptTimeはクロック割り込み単位（既定15.6ms）でしか
// 進まず、数十msの時間差を比べる同時打鍵判定には粗すぎるため、高精度版を使う。
_IRQL_requires_max_(DISPATCH_LEVEL)
static ULONGLONG
NicolaNowMs(
    VOID
    )
{
    ULONG64 qpc;
    return KeQueryInterruptTimePrecise(&qpc) / 10000ULL;
}

// devExt->EngineLock保持中に呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN
NicolaIsActiveLocked(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    PKBDLAYER_NICOLA_HEADER table = devExt->NicolaTable;

    if (table == NULL || !table->Enabled || !devExt->LayerMapEnabled || devExt->LayerMap == NULL) {
        return FALSE;
    }
    return (EffectiveLayerMaskLocked(devExt) & (1UL << table->Layer)) != 0;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
NicolaAppendStep(
    _Inout_ PNICOLA_EMIT Emit,
    _In_ USHORT ScanCode,
    _In_ UCHAR IsBreak
    )
{
    if (ScanCode != 0 && Emit->StepCount < NICOLA_MAX_EMIT_STEPS) {
        Emit->Steps[Emit->StepCount].ScanCode = ScanCode;
        Emit->Steps[Emit->StepCount].IsBreak = IsBreak;
        Emit->Steps[Emit->StepCount].Reserved = 0;
        Emit->StepCount++;
    }
}

// devExt->EngineLock保持中に呼ぶこと。判定結果を送出するスキャンコード列に変換する。
// Tableは判定に使った設定（SetNicolaForDeviceでは差し替え前の設定）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
NicolaCollectLocked(
    _In_opt_ PKBDLAYER_NICOLA_HEADER Table,
    _In_ const NICOLA_CORE_RESULT* Result,
    _Inout_ PNICOLA_EMIT Emit
    )
{
    for (ULONG i = 0; i < Result->Count; i++) {
        const NICOLA_CORE_OUTPUT* item = &Result->Items[i];

        switch (item->Type) {
        case NICOLA_OUT_CHAR:
            if (Table != NULL && item->Arg < KBDLAYER_NICOLA_PLANE_COUNT) {
                PKBDLAYER_NICOLA_KEY_ENTRY entry = FindNicolaEntry(Table, item->ScanCode);
                if (entry != NULL) {
                    PKBDLAYER_NICOLA_OUTPUT out = &entry->Planes[item->Arg];
                    ULONG count = min(out->StepCount, KBDLAYER_NICOLA_MAX_STEPS);
                    for (ULONG s = 0; s < count; s++) {
                        NicolaAppendStep(Emit, out->Steps[s].ScanCode, out->Steps[s].IsBreak);
                    }
                }
            }
            break;

        case NICOLA_OUT_TAP_MAKE:
        case NICOLA_OUT_TAP_BREAK:
            if (Table != NULL) {
                USHORT tapKey = (item->Arg == NICOLA_SIDE_LEFT) ? Table->ThumbLeftTapKey : Table->ThumbRightTapKey;
                NicolaAppendStep(Emit, tapKey, item->Type == NICOLA_OUT_TAP_BREAK ? 1 : 0);
            }
            break;

        case NICOLA_OUT_PASS:
            Emit->Pass = TRUE;
            break;

        default:
            break;
        }
    }
}

// devExt->EngineLock保持中に、NicolaCoreResetの直前に呼ぶこと。NICOLAが押下を飲み込み、
// まだ押されたままの物理キー（文字・親指）をHeldKeysにSWALLOWEDとして記録する。
// 判定を打ち切ると保留中のキーはその場で確定送出（タップ）されるが、物理キーの解放は
// 後から届き、そのときNICOLAは無効なので素通しになって孤立した解放がOSに届く。
// 記録しておけば、その解放はResolveLayerActionで捨てられる。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
NicolaSwallowOwnedKeysLocked(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    const UCHAR* owned = devExt->NicolaState.OwnedDown;

    for (ULONG byteIndex = 0; byteIndex < sizeof(devExt->NicolaState.OwnedDown); byteIndex++) {
        if (owned[byteIndex] == 0) {
            continue;
        }
        for (ULONG bit = 0; bit < 8; bit++) {
            ULONG index = byteIndex * 8 + bit;
            USHORT scanCode;
            if (!(owned[byteIndex] & (1u << bit))) {
                continue;
            }
            // NicolaCore.cのOwnedIndexの逆変換（下位8bit + E0:0x100 / E1:0x200）。
            scanCode = (USHORT)(index & 0xFF);
            if (index & 0x200) {
                scanCode |= 0xE100;
            } else if (index & 0x100) {
                scanCode |= 0xE000;
            }
            if (HeldKeyFor(devExt, scanCode)->State == KBDLAYER_HELD_NONE) {
                SetHeldKeyLocked(devExt, scanCode, KBDLAYER_HELD_SWALLOWED, NULL);
            }
        }
    }
}

// EngineLockを保持していない状態で、DISPATCH_LEVELで呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
NicolaSend(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ const NICOLA_EMIT* Emit
    )
{
    if (devExt->OriginalClassService == NULL) {
        return; // まだkbdclassに接続されていない（IOCTL経路からの呼び出しで起こりうる）
    }
    for (ULONG i = 0; i < Emit->StepCount; i++) {
        SendDecodedEvent(devExt, Emit->Steps[i].ScanCode, Emit->Steps[i].IsBreak ? KEY_BREAK : KEY_MAKE);
    }
}

// EngineLockを保持していない状態で呼ぶこと。Deadline=0ならタイマーを止める。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
NicolaArmTimer(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONGLONG DeadlineMs,
    _In_ ULONGLONG NowMs
    )
{
    LARGE_INTEGER dueTime;
    ULONGLONG delayMs;

    if (DeadlineMs == 0) {
        KeCancelTimer(&devExt->NicolaTimer);
        return;
    }
    delayMs = (DeadlineMs > NowMs) ? (DeadlineMs - NowMs) : 1;
    dueTime.QuadPart = -((LONGLONG)delayMs * 10000LL);
    KeSetTimer(&devExt->NicolaTimer, dueTime, &devExt->NicolaDpc);
}

// NicolaTimerの満了で起動されるDPC。保留中の判定を期限切れとして確定させる。
// ServiceCallbackとの競合はEngineLockで直列化し、NicolaCoreOnTimeoutは期限前に
// 呼ばれても何もしない（KeSetTimerの張り直しと満了が競合した場合）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
NicolaDpcRoutine(
    _In_ PKDPC Dpc,
    _In_opt_ PVOID DeferredContext,
    _In_opt_ PVOID SystemArgument1,
    _In_opt_ PVOID SystemArgument2
    )
{
    PDEVICE_EXTENSION devExt = (PDEVICE_EXTENSION)DeferredContext;
    KIRQL oldIrql;
    NICOLA_CORE_PARAMS params;
    NICOLA_CORE_RESULT result;
    NICOLA_EMIT emit;
    ULONGLONG now;

    UNREFERENCED_PARAMETER(Dpc);
    UNREFERENCED_PARAMETER(SystemArgument1);
    UNREFERENCED_PARAMETER(SystemArgument2);

    if (devExt == NULL) {
        return;
    }

    emit.StepCount = 0;
    emit.Pass = FALSE;

    // 状態の決定から送出までをServiceCallbackと直列化する（OutputLockの説明参照）。
    // 現在時刻もロックを取ってから読む（ServiceCallbackが先に進めた時刻より古い値で
    // 判定しないため）。
    KeAcquireSpinLockAtDpcLevel(&devExt->OutputLock);
    now = NicolaNowMs();

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    if (!NicolaIsActiveLocked(devExt)) {
        // 無効化はNicolaSyncActivationが状態ごと片付ける。
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        KeReleaseSpinLockFromDpcLevel(&devExt->OutputLock);
        return;
    }
    NicolaParamsFromTable(devExt->NicolaTable, &params);
    NicolaCoreOnTimeout(&devExt->NicolaState, &params, now, &result);
    NicolaCollectLocked(devExt->NicolaTable, &result, &emit);
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    if (result.Deadline != 0) {
        NicolaArmTimer(devExt, result.Deadline, now);
    }
    if (emit.StepCount > 0) {
        KBDLAYER_TRACE("LayerEngine: nicola TIMEOUT emit steps=%lu\n", emit.StepCount);
    }
    NicolaSend(devExt, &emit);
    KeReleaseSpinLockFromDpcLevel(&devExt->OutputLock);
}

_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
NicolaIsActive(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    KIRQL oldIrql;
    BOOLEAN active;

    if (devExt->NicolaTable == NULL) {
        return FALSE; // 未設定時のホットパスはロックを取らない
    }
    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    active = NicolaIsActiveLocked(devExt);
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
    return active;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
NicolaSyncActivation(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    NicolaSyncActivationEx(devExt, TRUE);
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
NicolaSyncActivationEx(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ BOOLEAN SendImeKeys
    )
{
    KIRQL oldIrql;
    BOOLEAN active;
    USHORT tapKey = 0;
    NICOLA_CORE_RESULT result;
    NICOLA_EMIT emit;

    if (devExt->NicolaTable == NULL && !devExt->NicolaWasActive) {
        return; // 未設定時のホットパスはロックを取らない
    }

    emit.StepCount = 0;
    emit.Pass = FALSE;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    active = NicolaIsActiveLocked(devExt);
    if (active == devExt->NicolaWasActive) {
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return;
    }
    devExt->NicolaWasActive = active;
    if (!active) {
        // 保留中の判定を確定させ、押下送出済みの単独打鍵キーを解放してから初期化する。
        // まだ押されている物理キーの解放は、後から届いたときに捨てる。
        NicolaSwallowOwnedKeysLocked(devExt);
        NicolaCoreReset(&devExt->NicolaState, &result);
        NicolaCollectLocked(devExt->NicolaTable, &result, &emit);
    }
    // アプリ（IME連動エージェント等）が起点の切り替えでは、IMEのオン/オフキーを送らない。
    if (devExt->NicolaTable != NULL && SendImeKeys) {
        tapKey = active ? devExt->NicolaTable->OnEnterKey : devExt->NicolaTable->OnExitKey;
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    if (!active) {
        KeCancelTimer(&devExt->NicolaTimer);
    }
    KBDLAYER_TRACE("LayerEngine: nicola %s steps=%lu tapKey=0x%X\n",
                   active ? "ENTER" : "EXIT", emit.StepCount, tapKey);
    NicolaAppendStep(&emit, tapKey, 0);
    NicolaAppendStep(&emit, tapKey, 1);
    NicolaSend(devExt, &emit);
}

_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
ResolveNicola(
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
    PKBDLAYER_NICOLA_HEADER table;
    NICOLA_KEY_KIND kind;
    NICOLA_CORE_PARAMS params;
    NICOLA_CORE_RESULT result;
    NICOLA_EMIT emit;
    ULONGLONG now;

    // 他のResolve*と同じく1件バッチのみ。判定有効中の複数件バッチはServiceCallbackが
    // 1件ずつに分けてから呼ぶ（USBキーボードでは親指キーと文字キーの同時押しが
    // 1つのレポート = 1バッチで届きうるため、素通しにはできない）。
    if ((InputDataEnd - InputDataStart) != 1) {
        return FALSE;
    }
    if (devExt->NicolaTable == NULL && !devExt->NicolaWasActive) {
        return FALSE; // 未設定時のホットパスはロックを取らない
    }

    NicolaSyncActivation(devExt);

    ev = InputDataStart;
    scanCode = EncodeScancode16(ev->MakeCode, ev->Flags);
    isBreak = (ev->Flags & KEY_BREAK) != 0;
    emit.StepCount = 0;
    emit.Pass = FALSE;
    now = NicolaNowMs();

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);

    if (!NicolaIsActiveLocked(devExt)) {
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return FALSE;
    }
    table = devExt->NicolaTable;
    if (table == NULL) {
        // NicolaIsActiveLockedがTRUEならNULLではないが、静的解析（C28182）向けに明示する。
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return FALSE;
    }

    if (scanCode == table->ThumbLeftScanCode) {
        kind = NICOLA_KEY_THUMB_LEFT;
    } else if (scanCode == table->ThumbRightScanCode) {
        kind = NICOLA_KEY_THUMB_RIGHT;
    } else if (FindNicolaEntry(table, scanCode) != NULL) {
        kind = NICOLA_KEY_CHAR;
    } else {
        kind = NICOLA_KEY_OTHER;
    }
    // Ctrl/Alt/Win/ShiftをOSへ押下中はショートカット等を壊さないよう判定を迂回する。
    // 物理キーではなく、実際に送った出力で見る（TrackOutputModifierEvent参照）。
    // レイヤーマップでCtrlにしたキーやMTのホールドも対象になり、物理Ctrlを別のキーに
    // した場合は対象外になる。ServiceCallbackはOutputLockを保持しているので、ロック無しで読める。
    if (kind != NICOLA_KEY_OTHER && devExt->OutputModifierDown != 0) {
        kind = NICOLA_KEY_OTHER;
    }
    // MT/LT（とタップダンスの1回目の押下）のタップ/ホールドが判定中に、別のキーの押下が来た
    // 場合は、NICOLAは横取りせず後段に譲る。通常のキーと同じく「他キー押下でホールド確定」に
    // なり、MT(Ctrl)を押した直後の文字キーはCtrl+Fになる（譲らないと、NICOLAが先に文字を
    // 取ってしまい、MTはその後タップとして確定していた）。OTHER扱いにするので、NICOLAの
    // 保留中の判定は先に確定し、このキーの解放も素通しになる。
    if (kind != NICOLA_KEY_OTHER && !isBreak &&
        ((devExt->PendingActive && !devExt->HoldConfirmed && scanCode != devExt->PendingScanCode) ||
         (devExt->TapDancePendingActive && devExt->TapDanceKeyDown && devExt->TapDanceTapCount == 0 &&
          scanCode != devExt->TapDanceScanCode))) {
        kind = NICOLA_KEY_OTHER;
    }

    NicolaParamsFromTable(table, &params);
    NicolaCoreOnKey(&devExt->NicolaState, &params, kind, scanCode, isBreak, now, &result);
    NicolaCollectLocked(table, &result, &emit);
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    NicolaArmTimer(devExt, result.Deadline, now);
    NicolaSend(devExt, &emit);

    if (emit.Pass) {
        return FALSE;
    }
    if (SystemArgument3 != NULL) {
        *(PULONG)SystemArgument3 = 1;
    }
    return TRUE;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeNicolaState(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    KeInitializeTimer(&devExt->NicolaTimer);
    KeInitializeDpc(&devExt->NicolaDpc, NicolaDpcRoutine, devExt);
    NicolaCoreInit(&devExt->NicolaState);
    devExt->NicolaTable = NULL;
    devExt->NicolaTableBytes = 0;
    devExt->NicolaWasActive = FALSE;
    devExt->OutputModifierDown = 0;
}

// NICOLA設定blobの形式を検証する（SET_NICOLAとレジストリからの読み込みの両方で使う）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
ValidateNicolaBlob(
    _In_reads_bytes_(BlobLength) PVOID Blob,
    _In_ ULONG BlobLength
    )
{
    PKBDLAYER_NICOLA_HEADER header = (PKBDLAYER_NICOLA_HEADER)Blob;
    PKBDLAYER_NICOLA_KEY_ENTRY entries;

    if (Blob == NULL || BlobLength < sizeof(KBDLAYER_NICOLA_HEADER)) {
        return STATUS_INVALID_PARAMETER;
    }
    if (header->Version != KBDLAYER_NICOLA_VERSION ||
        header->KeyCount > KBDLAYER_NICOLA_MAX_KEYS ||
        BlobLength != sizeof(KBDLAYER_NICOLA_HEADER) + header->KeyCount * sizeof(KBDLAYER_NICOLA_KEY_ENTRY)) {
        return STATUS_INVALID_PARAMETER;
    }
    if (header->Layer >= KBDLAYER_MAX_LAYERS) {
        return STATUS_INVALID_PARAMETER;
    }
    if (header->ThumbLeftScanCode == 0 || header->ThumbRightScanCode == 0 ||
        header->ThumbLeftScanCode == header->ThumbRightScanCode) {
        return STATUS_INVALID_PARAMETER;
    }
    // 極端な値は設定ミスとして弾く（判定時間は規格上50〜200msが妥当とされる）。
    if (header->SimulTermMs > 1000 || header->OverlapRatioPercent > 1000 || header->MinOverlapMs > 1000) {
        return STATUS_INVALID_PARAMETER;
    }

    entries = NicolaEntries(header);
    for (ULONG i = 0; i < header->KeyCount; i++) {
        USHORT sc = entries[i].SourceScanCode;
        if (sc == 0 || sc == header->ThumbLeftScanCode || sc == header->ThumbRightScanCode) {
            return STATUS_INVALID_PARAMETER;
        }
        for (ULONG j = 0; j < i; j++) {
            if (entries[j].SourceScanCode == sc) {
                return STATUS_INVALID_PARAMETER;
            }
        }
        for (ULONG p = 0; p < KBDLAYER_NICOLA_PLANE_COUNT; p++) {
            if (entries[i].Planes[p].StepCount > KBDLAYER_NICOLA_MAX_STEPS) {
                return STATUS_INVALID_PARAMETER;
            }
        }
    }
    return STATUS_SUCCESS;
}

// 検証済みのblobをNonPagedへ複製する。
_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
CopyNicolaBlob(
    _In_reads_bytes_(BlobLength) PVOID Blob,
    _In_ ULONG BlobLength,
    _Outptr_ PKBDLAYER_NICOLA_HEADER* OutTable
    )
{
    PKBDLAYER_NICOLA_HEADER table;
    NTSTATUS status;

    *OutTable = NULL;
    status = ValidateNicolaBlob(Blob, BlobLength);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    table = (PKBDLAYER_NICOLA_HEADER)ExAllocatePool2(POOL_FLAG_NON_PAGED, BlobLength, KBDLAYER_NICOLA_POOL_TAG);
    if (table == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }
    RtlCopyMemory(table, Blob, BlobLength);
    *OutTable = table;
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetNicolaForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_reads_bytes_opt_(BlobLength) PVOID Blob,
    _In_ ULONG BlobLength
    )
{
    PKBDLAYER_NICOLA_HEADER newTable = NULL;
    PKBDLAYER_NICOLA_HEADER oldTable;
    NICOLA_CORE_RESULT result;
    NICOLA_EMIT emit;
    KIRQL outputIrql;
    WCHAR subKeyPath[64];
    NTSTATUS status;

    if (BlobLength > 0) {
        if (Blob == NULL) {
            return STATUS_INVALID_PARAMETER;
        }
        status = CopyNicolaBlob(Blob, BlobLength, &newTable);
        if (!NT_SUCCESS(status)) {
            return status;
        }
    }

    emit.StepCount = 0;
    emit.Pass = FALSE;

    // 確定させた保留の送出を、ServiceCallback・NicolaDpcの送出と直列化する（OutputLockの
    // 説明参照）。OutputLockはDISPATCH_LEVELに上げるので、kbdclassのサービスコールバックを
    // port driverと同じIRQL（DISPATCH_LEVEL）で呼ぶことにもなる。
    KeAcquireSpinLock(&devExt->OutputLock, &outputIrql);
    KeAcquireSpinLockAtDpcLevel(&devExt->EngineLock);
    // 進行中の判定は差し替え前の設定で確定させてから捨てる。新しい設定で判定が有効なら、
    // 次のキーイベントでNicolaSyncActivationがOnEnterKeyを送る。
    // まだ押されている物理キーの解放は、後から届いたときに捨てる。
    NicolaSwallowOwnedKeysLocked(devExt);
    NicolaCoreReset(&devExt->NicolaState, &result);
    NicolaCollectLocked(devExt->NicolaTable, &result, &emit);
    oldTable = devExt->NicolaTable;
    devExt->NicolaTable = newTable;
    devExt->NicolaTableBytes = BlobLength;
    devExt->NicolaWasActive = FALSE;
    KeReleaseSpinLockFromDpcLevel(&devExt->EngineLock);

    KeCancelTimer(&devExt->NicolaTimer);
    NicolaSend(devExt, &emit);
    KeReleaseSpinLock(&devExt->OutputLock, outputIrql);

    // ServiceCallback/DPCはEngineLock保持中にしかNicolaTableを読まないので、差し替え後の
    // 旧テーブルはここで安全に解放できる。
    if (oldTable != NULL) {
        ExFreePoolWithTag(oldTable, KBDLAYER_NICOLA_POOL_TAG);
    }

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    RegWriteBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_NICOLA_VALUE_NAME,
                    BlobLength > 0 ? Blob : NULL, BlobLength);

    LayerNotifyIfChanged(devExt);
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
GetNicolaForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _Out_writes_bytes_to_opt_(OutBufferLength, *BytesReturned) PVOID OutBuffer,
    _In_ ULONG OutBufferLength,
    _Out_ PULONG BytesReturned
    )
{
    KIRQL oldIrql;
    ULONG required;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    required = (devExt->NicolaTable != NULL) ? devExt->NicolaTableBytes : 0;
    *BytesReturned = required;
    if (required > 0) {
        if (OutBuffer == NULL || OutBufferLength < required) {
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            return STATUS_BUFFER_TOO_SMALL;
        }
        RtlCopyMemory(OutBuffer, devExt->NicolaTable, required);
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    return STATUS_SUCCESS;
}

// AddDeviceから呼ばれる: レジストリの"Nicola"値を読み、devExt->NicolaTableを初期化する
// （LoadPersistedCombosForDeviceと同じ理屈でロック無しに直接書き込む）。
// 失敗時は未設定のままでよい（ベストエフォート）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
LoadPersistedNicolaForDevice(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    WCHAR subKeyPath[64];
    PVOID buffer;
    ULONG actualLength = 0;
    NTSTATUS status;

    buffer = ExAllocatePool2(POOL_FLAG_PAGED, KBDLAYER_MAX_NICOLA_BLOB_BYTES, KBDLAYER_REGTEMP_POOL_TAG);
    if (buffer == NULL) {
        return;
    }

    BuildLayerMapsSubKeyPath(devExt->DeviceId, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    status = RegReadBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_NICOLA_VALUE_NAME,
                            buffer, KBDLAYER_MAX_NICOLA_BLOB_BYTES, &actualLength);
    if (NT_SUCCESS(status) && actualLength > 0) {
        PKBDLAYER_NICOLA_HEADER table = NULL;
        if (NT_SUCCESS(CopyNicolaBlob(buffer, actualLength, &table))) {
            devExt->NicolaTable = table;
            devExt->NicolaTableBytes = actualLength;
        }
    }

    ExFreePoolWithTag(buffer, KBDLAYER_REGTEMP_POOL_TAG);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
FreeNicolaForDevice(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    if (devExt->NicolaTable != NULL) {
        ExFreePoolWithTag(devExt->NicolaTable, KBDLAYER_NICOLA_POOL_TAG);
        devExt->NicolaTable = NULL;
        devExt->NicolaTableBytes = 0;
    }
}

// ---------------------------------------------------------------------------
// アプリ管理レイヤーとレイヤー変化の通知（common/kbdlayer_ioctl.h参照）
//
// レイヤー状態 = 実効マスクを決める3要素（ActiveLayerMask・AppLayerMask・DefaultLayer）と、
// レイヤーマップのON/OFF・NICOLA判定の有効。これが前回から変わっていればSeqを進める。
// 変化を起こす処理はばらばら（MO/TG/OSL/LT・各DPC・IOCTL）なので、個々の箇所で
// 通知するのではなく、それらの後でLayerNotifyIfChangedを呼んで差分で検出する。
// 保留IRPの管理はLayerNotify.c。
// ---------------------------------------------------------------------------

// devExt->EngineLock保持中に呼ぶこと。状態が前回から変わっていればSeqを進めてTRUEを返す。
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN
RefreshLayerStateLocked(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    BOOLEAN enabled = devExt->LayerMapEnabled && devExt->LayerMap != NULL;
    BOOLEAN nicola = NicolaIsActiveLocked(devExt);

    if (devExt->LastActiveLayerMask == devExt->ActiveLayerMask &&
        devExt->LastAppLayerMask == devExt->AppLayerMask &&
        devExt->LastDefaultLayer == devExt->DefaultLayer &&
        devExt->LastLayerMapEnabled == enabled &&
        devExt->LastNicolaActive == nicola) {
        return FALSE;
    }
    devExt->LastActiveLayerMask = devExt->ActiveLayerMask;
    devExt->LastAppLayerMask = devExt->AppLayerMask;
    devExt->LastDefaultLayer = devExt->DefaultLayer;
    devExt->LastLayerMapEnabled = enabled;
    devExt->LastNicolaActive = nicola;
    devExt->LayerStateSeq++;
    return TRUE;
}

// devExt->EngineLock保持中に呼ぶこと（RefreshLayerStateLockedの直後に呼ぶ）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
FillLayerStateLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _Out_ PKBDLAYER_LAYER_STATE Out
    )
{
    Out->Seq = devExt->LayerStateSeq;
    Out->ActiveLayerMask = devExt->ActiveLayerMask;
    Out->AppLayerMask = devExt->AppLayerMask;
    Out->DefaultLayer = devExt->DefaultLayer;
    Out->EffectiveLayerMask = EffectiveLayerMaskLocked(devExt);
    Out->LayerMapEnabled = devExt->LastLayerMapEnabled ? 1 : 0;
    Out->NicolaActive = devExt->LastNicolaActive ? 1 : 0;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
LayerNotifyIfChanged(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    KIRQL oldIrql;
    BOOLEAN changed;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    changed = RefreshLayerStateLocked(devExt);
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    if (changed) {
        LayerNotifyCompleteWaiters(devExt);
    }
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
GetLayerStateForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _Out_ PKBDLAYER_LAYER_STATE Out
    )
{
    KIRQL oldIrql;
    BOOLEAN changed;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    changed = RefreshLayerStateLocked(devExt);
    FillLayerStateLocked(devExt, Out);
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    if (changed) {
        // 取得のついでに変化を見つけた場合も、待っているIRPへ知らせる。
        LayerNotifyCompleteWaiters(devExt);
    }
}

// アプリ管理レイヤーを変えた後の後始末（PASSIVE_LEVELから呼ぶ）。NICOLAの有効/無効を
// 反映し（IMEのオン/オフキーはSendImeKeysのときだけ送る）、待っているIRPへ通知する。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
ApplyAppLayerChange(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ BOOLEAN SendImeKeys
    )
{
    KIRQL outputIrql;

    // NICOLAの確定送出・IMEキーの送出を、ServiceCallback・DPCの送出と直列化する
    // （OutputLockの説明参照）。OutputLockはDISPATCH_LEVELに上げるので、kbdclassの
    // サービスコールバックをport driverと同じIRQLで呼べる。
    KeAcquireSpinLock(&devExt->OutputLock, &outputIrql);
    NicolaSyncActivationEx(devExt, SendImeKeys);
    KeReleaseSpinLock(&devExt->OutputLock, outputIrql);

    LayerNotifyIfChanged(devExt);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetAppLayerStateForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG Op,
    _In_ ULONG Layer,
    _In_ ULONG Mask,
    _In_ ULONG Flags,
    _In_ PVOID Owner,
    _Out_opt_ PKBDLAYER_LAYER_STATE Out
    )
{
    KIRQL oldIrql;
    ULONG layerCount;
    ULONG validMask;
    ULONG newMask;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);

    // 立てられるのは、レイヤーマップにあるレイヤーだけ（未設定なら上限まで）。
    layerCount = (devExt->LayerMap != NULL) ? devExt->LayerMap->LayerCount : KBDLAYER_MAX_LAYERS;
    validMask = (layerCount >= 32) ? 0xFFFFFFFFUL : ((1UL << layerCount) - 1);

    newMask = devExt->AppLayerMask;
    switch (Op) {
    case KBDLAYER_LAYER_OP_ON:
    case KBDLAYER_LAYER_OP_OFF:
    case KBDLAYER_LAYER_OP_TOGGLE:
        if (Layer >= layerCount) {
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            return STATUS_INVALID_PARAMETER;
        }
        if (Op == KBDLAYER_LAYER_OP_ON) {
            newMask |= (1UL << Layer);
        } else if (Op == KBDLAYER_LAYER_OP_OFF) {
            newMask &= ~(1UL << Layer);
        } else {
            newMask ^= (1UL << Layer);
        }
        break;
    case KBDLAYER_LAYER_OP_SET_MASK:
        if (Mask & ~validMask) {
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            return STATUS_INVALID_PARAMETER;
        }
        newMask = Mask;
        break;
    default:
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return STATUS_INVALID_PARAMETER;
    }

    devExt->AppLayerMask = newMask;
    // 最後に変更したハンドルが所有者になる（乗っ取り可）。全部下ろしたら所有者なし。
    devExt->AppLayerOwner = (newMask != 0) ? Owner : NULL;
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    KBDLAYER_TRACE("SetAppLayerStateForDevice: DeviceId=0x%08X op=%lu AppLayerMask=0x%X\n",
                   devExt->DeviceId, Op, newMask);

    ApplyAppLayerChange(devExt, (Flags & KBDLAYER_LAYER_STATE_FLAG_NO_NICOLA_KEYS) == 0);

    if (Out != NULL) {
        GetLayerStateForDevice(devExt, Out);
    }
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
ReleaseAppLayersForOwner(
    _In_ PVOID Owner
    )
{
    // 所有するデバイスを1台ずつ探し、g_listLock内でremove lockを取ってからロック外で後始末する
    // （後始末ではkbdclassへの送出もあるので、g_listLockを持ったままにはしない）。
    // 見つけた時点で所有者を外すので、同じデバイスを二度処理することはない。
    for (;;) {
        KIRQL oldIrql;
        PLIST_ENTRY entry;
        PDEVICE_EXTENSION found = NULL;

        KeAcquireSpinLock(&g_listLock, &oldIrql);
        for (entry = g_deviceList.Flink; entry != &g_deviceList; entry = entry->Flink) {
            PDEVICE_EXTENSION devExt = CONTAINING_RECORD(entry, DEVICE_EXTENSION, ListEntry);
            BOOLEAN owned;

            KeAcquireSpinLockAtDpcLevel(&devExt->EngineLock);
            owned = (devExt->AppLayerOwner == Owner);
            if (owned) {
                devExt->AppLayerOwner = NULL;
                devExt->AppLayerMask = 0;
            }
            KeReleaseSpinLockFromDpcLevel(&devExt->EngineLock);

            if (owned) {
                if (NT_SUCCESS(IoAcquireRemoveLock(&devExt->RemoveLock, Owner))) {
                    found = devExt;
                }
                // 削除中なら後始末は不要（デバイスごと消える）。次を探す。
                if (found != NULL) {
                    break;
                }
            }
        }
        KeReleaseSpinLock(&g_listLock, oldIrql);

        if (found == NULL) {
            break;
        }

        KBDLAYER_TRACE("ReleaseAppLayersForOwner: released DeviceId=0x%08X\n", found->DeviceId);
        // 持ち主のアプリが消えただけなので、IMEのオン/オフキーは送らない。
        ApplyAppLayerChange(found, FALSE);
        IoReleaseRemoveLock(&found->RemoveLock, Owner);
    }
}
