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
#define KBDLAYER_TAPHOLDMODE_VALUE_NAME L"TapHoldMode"
#define KBDLAYER_QUICKTAPTERM_VALUE_NAME L"QuickTapTermMs"
#define KBDLAYER_TAPPINGTOGGLE_VALUE_NAME L"TappingToggle"
#define KBDLAYER_MACROS_VALUE_NAME L"Macros"
#define KBDLAYER_COMBOS_VALUE_NAME L"Combos"
#define KBDLAYER_COMBOTERM_VALUE_NAME L"ComboTermMs"
#define KBDLAYER_TAPDANCES_VALUE_NAME L"TapDances"
#define KBDLAYER_TAPDANCETERM_VALUE_NAME L"TapDanceTermMs"
#define KBDLAYER_NICOLA_VALUE_NAME L"Nicola"
#define KBDLAYER_FEATURES_VALUE_NAME L"EnabledFeatures"
#define KBDLAYER_FEATURES_EX_VALUE_NAME L"EnabledFeaturesEx" // 2026-10-01: 全ビット（Key Overrideを含む）
#define KBDLAYER_KEYOVERRIDES_VALUE_NAME L"KeyOverrides"
#define KBDLAYER_REGTEMP_POOL_TAG 'tLdK'

// 緊急時の解除手段（release-roadmap-priority.md 項番3）。ドライバ全体で1つの設定
// （キーボードごとではない）なので、他のLayerMapsサブキーとは別の場所へ持つ。
#define KBDLAYER_EMERGENCY_SUBKEY L"EmergencyRelease"
#define KBDLAYER_EMERGENCY_KEYCOUNT_VALUE_NAME L"EmergencyReleaseKeyCount"
#define KBDLAYER_EMERGENCY_SCANCODES_VALUE_NAME L"EmergencyReleaseScanCodes"
#define KBDLAYER_EMERGENCY_MAX_TARGETS 64 // 一括OFF処理で同時に扱うキーボードの上限

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

// 2026-09-30にマクロの最大数を32→128へ上げる前の保存サイズ（32個分、8320バイト）。
// 読み込み（LoadPersistedMacrosForDevice）とE-2の設定コピー（CopyPersistedSettings）の
// 両方でこのサイズも受け付ける（アップグレードで既存のマクロが消えないように）。
// 次のSET_MACROで新しいサイズに書き直される。
#define KBDLAYER_MACRO_PERSIST_LEGACY_COUNT 32

// "Macros"値のバイト数から、何個分のマクロが入っているかを返す。受け付けないサイズなら0。
_IRQL_requires_max_(PASSIVE_LEVEL)
static ULONG
MacroPersistCountFromLength(
    _In_ ULONG Length
    )
{
    if (Length == KBDLAYER_MACRO_PERSIST_BLOB_BYTES) {
        return KBDLAYER_MAX_MACRO_COUNT;
    }
    if (Length == KBDLAYER_MACRO_PERSIST_LEGACY_COUNT * sizeof(KBDLAYER_MACRO_PERSIST_ENTRY)) {
        return KBDLAYER_MACRO_PERSIST_LEGACY_COUNT;
    }
    return 0;
}

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

// Key Overrideの永続化形式（レジストリ"KeyOverrides"値）。コンボと同じ方式。
typedef struct _KBDLAYER_KEY_OVERRIDE_PERSIST_ENTRY {
    ULONG Used;
    KBDLAYER_KEY_OVERRIDE_ENTRY Entry;
} KBDLAYER_KEY_OVERRIDE_PERSIST_ENTRY, *PKBDLAYER_KEY_OVERRIDE_PERSIST_ENTRY;

#define KBDLAYER_KEY_OVERRIDE_PERSIST_BLOB_BYTES \
    (KBDLAYER_MAX_KEY_OVERRIDE_COUNT * sizeof(KBDLAYER_KEY_OVERRIDE_PERSIST_ENTRY))

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
    _Out_ PUSHORT MakeCode,
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
    if (header->KeyCount > KBDLAYER_MAX_KEYS) {
        return STATUS_INVALID_PARAMETER;
    }

    expectedLength = sizeof(KBDLAYER_MAP_HEADER) + header->KeyCount * sizeof(KBDLAYER_KEY_ENTRY);
    if (BlobLength != expectedLength) {
        return STATUS_INVALID_PARAMETER;
    }

    entries = (PKBDLAYER_KEY_ENTRY)((PUCHAR)Blob + sizeof(KBDLAYER_MAP_HEADER));

    // Action.Type が未知の値を含んでいないか検証する（将来のPhase向けに予約された
    // 値も含め、このドライババイナリが理解できるTypeのみを許可する）。レイヤー数は
    // 常にKBDLAYER_MAX_LAYERS（2026-09-29にLayerCountフィールドを廃止）。
    BOOLEAN needsSingleEvents = FALSE;
    for (ULONG k = 0; k < header->KeyCount; k++) {
        for (ULONG L = 0; L < KBDLAYER_MAX_LAYERS; L++) {
            PKBDLAYER_ACTION a = &entries[k].Actions[L];
            if (a->Type > KBDLAYER_ACTION_TYPE_MAX) {
                return STATUS_INVALID_PARAMETER;
            }
            // 修飾付きキー: 修飾子マスクが0なら単なるKEYと同じで、意図しない形（F-3）。
            if (a->Type == KBDLAYER_ACTION_MODDED_KEY) {
                if (a->Param1 == 0) {
                    return STATUS_INVALID_PARAMETER;
                }
                needsSingleEvents = TRUE;
            }
            // LM: レイヤー番号は範囲内、Param2は下位8bitの修飾子マスク（0以外）のみ。
            if (a->Type == KBDLAYER_ACTION_LAYER_MOD) {
                if (a->Param1 >= KBDLAYER_MAX_LAYERS || (a->Param2 & 0xFF00) != 0 || (a->Param2 & 0x00FF) == 0) {
                    return STATUS_INVALID_PARAMETER;
                }
                needsSingleEvents = TRUE;
            }
            // MT/LT・タップダンス: 判定中に届いたイベントを1件ずつ溜める・判定するため、複数件の
            // バッチは1件ずつに分ける（分けないと、複数件のバッチが判定を素通りする）。
            if (a->Type == KBDLAYER_ACTION_MODTAP || a->Type == KBDLAYER_ACTION_LAYERTAP ||
                a->Type == KBDLAYER_ACTION_TAPDANCE) {
                needsSingleEvents = TRUE;
            }
            // OSM: 修飾子マスクは0以外、Param2は0。修飾付きキーと同じく1件ずつ処理する。
            if (a->Type == KBDLAYER_ACTION_ONESHOT_MOD) {
                if (a->Param1 == 0 || a->Param2 != 0) {
                    return STATUS_INVALID_PARAMETER;
                }
                needsSingleEvents = TRUE;
            }
            // CUSTOM（ヘルパーイベント）: Param1は予約で0。Param2（ヘルパー動作のID）は任意。
            if (a->Type == KBDLAYER_ACTION_CUSTOM && a->Param1 != 0) {
                return STATUS_INVALID_PARAMETER;
            }
            // TO/TT: レイヤー番号は範囲内、Param2は0。
            if (a->Type == KBDLAYER_ACTION_LAYER_TO || a->Type == KBDLAYER_ACTION_LAYER_TAP_TOGGLE) {
                if (a->Param1 >= KBDLAYER_MAX_LAYERS || a->Param2 != 0) {
                    return STATUS_INVALID_PARAMETER;
                }
            }
        }
    }

    tableSize = sizeof(KBDLAYER_MAP_TABLE) + header->KeyCount * sizeof(KBDLAYER_KEY_ENTRY);
    newTable = (PKBDLAYER_MAP_TABLE)ExAllocatePool2(POOL_FLAG_NON_PAGED, tableSize, KBDLAYER_MAP_POOL_TAG);
    if (newTable == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    newTable->KeyCount = header->KeyCount;
    newTable->NeedsSingleEvents = needsSingleEvents;
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

// MODDED_KEY/LAYER_MOD（修飾キーを足すアクション）か。
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN
IsModdedActionType(
    _In_ UCHAR Type
    )
{
    return Type == KBDLAYER_ACTION_MODDED_KEY || Type == KBDLAYER_ACTION_LAYER_MOD ||
           Type == KBDLAYER_ACTION_INTERNAL_KEY_OVERRIDE || Type == KBDLAYER_ACTION_ONESHOT_MOD;
}

// 記録の「足した修飾キー」を変え、devExt->AddedModCount[]（ビットごとの足している記録の数）を
// 合わせる。OutputLockとEngineLockの両方を保持して呼ぶこと（AddedModCountはどちらか一方の
// 保持中に読まれる）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
SetHeldAddedModsLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _Inout_ PKBDLAYER_HELD_KEY Held,
    _In_ UCHAR AddedMods
    )
{
    for (ULONG bit = 0; bit < 8; bit++) {
        UCHAR m = (UCHAR)(1u << bit);
        if ((Held->AddedMods & m) && !(AddedMods & m) && devExt->AddedModCount[bit] > 0) {
            devExt->AddedModCount[bit]--;
        } else if (!(Held->AddedMods & m) && (AddedMods & m)) {
            devExt->AddedModCount[bit]++;
        }
    }
    Held->AddedMods = AddedMods;
}

// 押下中の記録のどれかが足している修飾キー（KBDLAYER_MOD_*のOR）。
// OSMをタップした後、次のキーのために押したままにしている修飾キー（OneShotMods）と、コンボの出力・
// タップダンスの長押しが足している修飾キー（ComboAddedMods・TapDanceAddedMods）も含める。
// OutputLockを保持して呼ぶこと（これらはOutputLockで保護。呼び出し元はすべてOutputLock保持中）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static UCHAR
AddedModsUnion(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    UCHAR mods = (UCHAR)(devExt->OneShotMods | devExt->ComboAddedMods | devExt->TapDanceAddedMods);
    for (ULONG bit = 0; bit < 8; bit++) {
        if (devExt->AddedModCount[bit] != 0) {
            mods |= (UCHAR)(1u << bit);
        }
    }
    return mods;
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
    // MODDED_KEY/LAYER_MODの記録の数（ServiceCallbackが1件ずつに分けるかの判定に使う）。
    // 足した修飾キーは、ResolveModdedKeyが記録を消す前にbreakを送って0に戻している。
    // ここで0でないまま上書きされるのは想定外の経路だけで、その場合も数は合わせておく。
    if (held->State == KBDLAYER_HELD_ACTION && IsModdedActionType(held->Action.Type) &&
        devExt->ModdedHeldCount > 0) {
        devExt->ModdedHeldCount--;
    }
    if (held->AddedMods != 0) {
        SetHeldAddedModsLocked(devExt, held, 0);
    }
    held->State = State;
    if (Action != NULL) {
        held->Action = *Action;
    } else {
        RtlZeroMemory(&held->Action, sizeof(held->Action));
    }
    if (State == KBDLAYER_HELD_ACTION && Action != NULL && IsModdedActionType(Action->Type)) {
        devExt->ModdedHeldCount++;
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

// TT(layer)（QMKのTT相当）の押下・解放。devExt->EngineLock保持中に呼ぶこと。
// - 押下: MOと同じくレイヤーのビットを立てる。別のTTキーだった、または前のタップから
//   TapTermMs以上経っていたら、タップの回数を数え直す。
// - 解放: TapTermMs未満で、押している間に別のキーが押されていなければタップとして数える。
//   TappingToggle回（既定KBDLAYER_DEFAULT_TAPPING_TOGGLE、SET_CONFIGで変更可）に達したら、そのレイヤーの
//   固定（TtToggledLayers）を反転する。
//   固定されていればビットを立てたまま、されていなければ落とす（MOと同じ）。
// - オートリピートは何もしない。
// 「別のキーが押された」はLayerEngineObserveKeyEventsが、どのResolve*より先に記録する。
// タイマーは使わない（時刻の比較だけで決まる）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
HandleTapToggleLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ USHORT ScanCode16,
    _In_ UCHAR Layer,
    _In_ BOOLEAN IsBreak,
    _In_ BOOLEAN IsRepeat
    )
{
    ULONG bit = 1UL << Layer;
    ULONGLONG now = KeQueryInterruptTime();
    ULONGLONG term = (ULONGLONG)devExt->TapTermMs * 10000ULL;

    if (!IsBreak) {
        if (IsRepeat) {
            return;
        }
        devExt->ActiveLayerMask |= bit;
        if (devExt->TtScanCode != ScanCode16 || now - devExt->TtLastTapTime >= term) {
            devExt->TtTapCount = 0;
        }
        devExt->TtScanCode = ScanCode16;
        devExt->TtKeyDown = TRUE;
        devExt->TtInterrupted = FALSE;
        devExt->TtPressTime = now;
        return;
    }

    if (devExt->TtScanCode == ScanCode16 && devExt->TtKeyDown && !devExt->TtInterrupted &&
        now - devExt->TtPressTime < term) {
        devExt->TtTapCount++;
        devExt->TtLastTapTime = now;
        if (devExt->TtTapCount >= devExt->TappingToggle) {
            devExt->TtToggledLayers ^= bit;
            devExt->TtTapCount = 0;
        }
    } else {
        devExt->TtTapCount = 0;
    }
    if (devExt->TtScanCode == ScanCode16) {
        devExt->TtKeyDown = FALSE;
    }

    if (devExt->TtToggledLayers & bit) {
        devExt->ActiveLayerMask |= bit;
    } else {
        devExt->ActiveLayerMask &= ~bit;
    }
}

// TT(layer)のために、TTキー以外のキーの押下を記録する（押している間に別のキーが押されたら
// タップにしない。タップの間に別のキーが押されたら回数を数え直す）。MT/LT・マクロ・コンボなど、
// ResolveLayerActionまで届かないキーも数えるため、ResolveAndForwardの先頭（どのResolve*よりも前）
// から呼ぶ。OutputLock保持中・EngineLock非保持で呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
LayerEngineObserveKeyEvents(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd
    )
{
    KIRQL oldIrql;

    // TtKeyDown/TtTapCountはOutputLockとEngineLockの両方を保持して書かれるので、OutputLock保持中の
    // ここでは一貫した値が読める。TTを使っていない間はロックを取らない。
    if (!devExt->TtKeyDown && devExt->TtTapCount == 0) {
        return;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    for (PKEYBOARD_INPUT_DATA p = InputDataStart; p < InputDataEnd; p++) {
        if (!(p->Flags & KEY_BREAK) && EncodeScancode16(p->MakeCode, p->Flags) != devExt->TtScanCode) {
            devExt->TtInterrupted = TRUE;
            devExt->TtTapCount = 0;
        }
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
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

    // レイヤー数は常にKBDLAYER_MAX_LAYERS（LayerCountフィールド廃止）なので、tableは
    // ここでは使わない（呼び出し元の互換のため引数は残す）。
    UNREFERENCED_PARAMETER(table);

    for (LONG layer = KBDLAYER_MAX_LAYERS - 1; layer >= 0; layer--) {
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
    BOOLEAN isRepeat = FALSE; // 押下記録のあるキーのmake = オートリピート

    src = EncodeScancode16(Entry->MakeCode, Entry->Flags);
    isBreak = (Entry->Flags & KEY_BREAK) != 0;
    held = HeldKeyFor(devExt, src);

    if (held->State != KBDLAYER_HELD_NONE) {
        // 押下時の記録どおりに処理する（解放なら記録を消す）。
        UCHAR state = held->State;
        recorded = held->Action;
        isRepeat = !isBreak;
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

    case KBDLAYER_ACTION_LAYER_TO:
        // キーボード側のレイヤーをこのレイヤーだけにする（DefaultLayerとAppLayerMaskは別に持つので
        // そのまま）。OSLの待ちとTTで固定したレイヤーも消える。押している間に押されていたMOの
        // 解放は、落とすビットがもう無いだけなので問題ない。解放では何もしない。
        if (!isBreak && action->Param1 < KBDLAYER_MAX_LAYERS) {
            devExt->ActiveLayerMask = 1UL << action->Param1;
            devExt->OneShotLayerPending = FALSE;
            devExt->TtToggledLayers = 0;
        }
        return TRUE; // 出力しない

    case KBDLAYER_ACTION_LAYER_TAP_TOGGLE:
        if (action->Param1 < KBDLAYER_MAX_LAYERS) {
            HandleTapToggleLocked(devExt, src, action->Param1, isBreak, isRepeat);
        }
        return TRUE; // 出力しない

    case KBDLAYER_ACTION_MODDED_KEY:
    case KBDLAYER_ACTION_LAYER_MOD:
    case KBDLAYER_ACTION_INTERNAL_KEY_OVERRIDE:
    case KBDLAYER_ACTION_ONESHOT_MOD:
        // 通常はResolveModdedKeyが先に処理する（これらを含むマップ・押下中の記録がある間は、
        // ServiceCallbackが複数件バッチを1件ずつに分けるため）。ここに来るのは想定外の経路
        // だけで、修飾キーは足さずに、キー（またはレイヤー）部分だけを処理する。
        // 修飾付きキーの解放がここに来た場合、足した修飾キーの記録は上のSetHeldKeyLockedが
        // 消すだけで、breakはまだ送っていない。そのbreakは、このイベントを送るForwardToClassの
        // 安全網（ReleaseLeakedModifiers）が送る。通ったことが分かるようトレースを残す。
        KBDLAYER_TRACE("ShouldDropEntry: fallback for MODDED/LM sc=0x%X break=%d\n", src, isBreak);
        if (action->Type == KBDLAYER_ACTION_MODDED_KEY || action->Type == KBDLAYER_ACTION_INTERNAL_KEY_OVERRIDE) {
            DecodeScancode16(action->Param2, &Entry->MakeCode, &Entry->Flags);
            return FALSE;
        }
        if (action->Type == KBDLAYER_ACTION_ONESHOT_MOD) {
            return TRUE; // OSMはキーを持たない（修飾キーも足さない）
        }
        if (action->Param1 < KBDLAYER_MAX_LAYERS) {
            ULONG bit = 1UL << action->Param1;
            if (!isBreak) {
                devExt->ActiveLayerMask |= bit;
            } else {
                devExt->ActiveLayerMask &= ~bit;
            }
        }
        return TRUE;

    case KBDLAYER_ACTION_MODTAP:
    case KBDLAYER_ACTION_LAYERTAP:
        // タップ/ホールドの判定（ResolveTapHold）を経ずにここへ来るMT/LTは、タップ側のキー
        // （Param2）として出す。次の場合に来る:
        // - 別のMT/LTの保留中・ホールド中に押された（保留スロットは1つだけなので、後から
        //   押したほうは判定しない。以前は割り込んだキーを生のスキャンコードのまま送っていた）
        // - Quick Tap（同じMT/LTをタップした直後にもう一度押した。ResolveTapHold参照）
        // - 複数件のバッチの一部として届いた
        // 押下時の記録どおりに処理するので、オートリピートと解放も同じキーになる。
        if (action->Param2 == 0) {
            return TRUE; // タップ側がKC_NO
        }
        DecodeScancode16(action->Param2, &Entry->MakeCode, &Entry->Flags);
        if (!isBreak && devExt->OneShotLayerPending) {
            devExt->ActiveLayerMask &= ~devExt->OneShotLayerBit;
            devExt->OneShotLayerPending = FALSE;
        }
        return FALSE;

    case KBDLAYER_ACTION_CUSTOM:
        // ヘルパーイベント: キーは出力せず、押下（オートリピートを含む）と解放をHelperNotify.cの
        // キューに積む。解放とリピートは押下時の記録（HeldKeys）から来るので、押している間に
        // レイヤーが変わっても、マップをOFFにしても、同じIDで届く。待っているIRPの完了は、
        // ServiceCallback・DPCの末尾のLayerNotifyIfChanged（→HelperNotifyFlushIfNeeded）で行う。
        // Modifiersは、普通の出力として押されている修飾キー（RealModsDown。OutputLockで保護され、
        // 呼び出し元はすべてOutputLock保持中）。
        HelperNotifyQueueEvent(devExt->DeviceId, action->Param2, isBreak, isRepeat, devExt->RealModsDown);
        if (!isBreak && devExt->OneShotLayerPending) {
            // KEYと同じく、OSL(layer)発動後に最初に解決された実キーとしてワンショットを消費する。
            devExt->ActiveLayerMask &= ~devExt->OneShotLayerBit;
            devExt->OneShotLayerPending = FALSE;
        }
        return TRUE; // 出力しない（解放も対で出力しない）

    default:
        // MACROはResolveMacro、TAPDANCEはResolveTapDance
        // （いずれもServiceCallbackからResolveLayerActionより前に呼ばれる、Driver.c参照）が
        // 先に横取りして処理するため、通常はここに到達しない。ここに来るのはOSM、
        // 機能別ON/OFFで無効にしたMACRO/TAPDANCE、複数件バッチの一部
        // として届いたMACRO/TAPDANCE、タップダンスの判定中（次のタップ待ちで確定できない
        // とき）に押された別のタップダンスキーなど。
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

// スキャンコードが修飾キーならKBDLAYER_MOD_*のビット（1つ）を、そうでなければ0を返す。
_IRQL_requires_max_(DISPATCH_LEVEL)
static UCHAR
ModifierMaskOf(
    _In_ USHORT MakeCode,
    _In_ USHORT Flags
    )
{
    USHORT scanCode = EncodeScancode16(MakeCode, Flags);

    for (ULONG bit = 0; bit < RTL_NUMBER_OF(s_ModifierScanCodes); bit++) {
        if (s_ModifierScanCodes[bit] == scanCode) {
            return (UCHAR)(1u << bit);
        }
    }
    return 0;
}

// 修飾キー1つ（Enc16形式）のmake/breakを、ForwardToClassを通さずに単独で送る。
// RealModsDown・OutputModsDownは変えない（呼び出し側が扱う）。OutputLock保持中・
// EngineLock非保持で呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
SendRawModifierEvent(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ USHORT EncodedScanCode,
    _In_ USHORT BreakBit
    )
{
    KEYBOARD_INPUT_DATA ev;
    ULONG dummyConsumed = 0;
    USHORT makeCode;
    USHORT flags = BreakBit;

    if (devExt->OriginalClassService == NULL) {
        return;
    }
    DecodeScancode16(EncodedScanCode, &makeCode, &flags);
    RtlZeroMemory(&ev, sizeof(ev));
    ev.MakeCode = makeCode;
    ev.Flags = flags;
    ev.ExtraInformation = devExt->DeviceId & 0xFFFF0000UL;
    devExt->OriginalClassService(
        devExt->OriginalClassDeviceObject,
        &ev,
        &ev + 1,
        &dummyConsumed);
}

// 修飾付きキー・LMが「足す／戻す」修飾キー（Modsの各ビット）のmake/breakを送る。
// RealModsDownは変えない（ForwardToClassを通さない）。どのビットを送るべきかは呼び出し側が
// RealModsDownとAddedModsUnionから決める（ResolveModdedKey参照）。OutputLock保持中・
// EngineLock非保持で呼ぶこと。
// OutputModsDownを見て、OSが既に押されていると思っているビットのmake、既に離されていると
// 思っているビットのbreakは送らない（二重送信の防止。のどかのgenerateKeyEventと同じ考え方）。
// Alt・Winのbreakで、直前のmakeが修飾キー（=間に何も押していない単独押し）なら、先に
// Ctrlのタップを挟む。こうしないと、LM(n,LALT)を押して何も押さずに離しただけで
// メニューバーが開く（Winならスタートメニュー）。Shiftでなく Ctrl を使うのは、
// Alt+Shift が入力言語の切り替えに割り当てられていることがあるため。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
SendAddedModifiers(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ UCHAR Mods,
    _In_ USHORT BreakBit
    )
{
    const UCHAR menuMods = KBDLAYER_MOD_LALT | KBDLAYER_MOD_RALT | KBDLAYER_MOD_LGUI | KBDLAYER_MOD_RGUI;

    if (BreakBit == KEY_BREAK) {
        Mods &= devExt->OutputModsDown;
        if ((Mods & menuMods) != 0 && devExt->LastOutputWasModifier) {
            USHORT ctrl = 0;
            if (!(devExt->OutputModsDown & KBDLAYER_MOD_LCTL)) {
                ctrl = s_ModifierScanCodes[0];
            } else if (!(devExt->OutputModsDown & KBDLAYER_MOD_RCTL)) {
                ctrl = s_ModifierScanCodes[4];
            }
            if (ctrl != 0) {
                SendRawModifierEvent(devExt, ctrl, KEY_MAKE);
                SendRawModifierEvent(devExt, ctrl, KEY_BREAK);
            }
            devExt->LastOutputWasModifier = FALSE;
        }
    } else {
        Mods &= (UCHAR)~devExt->OutputModsDown;
    }

    for (ULONG bit = 0; bit < RTL_NUMBER_OF(s_ModifierScanCodes); bit++) {
        UCHAR m = (UCHAR)(1u << bit);
        if (Mods & m) {
            SendRawModifierEvent(devExt, s_ModifierScanCodes[bit], BreakBit);
            if (BreakBit == KEY_BREAK) {
                devExt->OutputModsDown &= (UCHAR)~m;
            } else {
                devExt->OutputModsDown |= m;
                devExt->LastOutputWasModifier = TRUE;
            }
        }
    }
}

// バッチに、ScanCode16以外の「修飾キーでないキーのmake」があるか。
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN
BatchHasOtherKeyMake(
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _In_ USHORT ScanCode16
    )
{
    for (PKEYBOARD_INPUT_DATA p = InputDataStart; p < InputDataEnd; p++) {
        if (!(p->Flags & KEY_BREAK) &&
            ModifierMaskOf(p->MakeCode, p->Flags) == 0 &&
            EncodeScancode16(p->MakeCode, p->Flags) != ScanCode16) {
            return TRUE;
        }
    }
    return FALSE;
}

// [D-12] 修飾付きキー（LSFT(KC_1)等）を押したまま別のキーを押したら、その修飾付きキーが
// 足した修飾キーを外す（QMKのweak modsの考え方。のどかも、キーを押すたびに修飾キーを
// 物理的な状態に合わせ直すので同じ動作になる）。こうしないと、「!」を打つキーを離す前に
// 次の「a」を押すと「A」になる。
// - 対象は MODDED_KEY の記録だけ。LM は「押している間は修飾キーが掛かる」のが目的なので外さない。
// - 「別のキー」は修飾キーでないキーのmake。修飾付きキー自身の出力キー（オートリピート・
//   押下時のmake）は含めない。
// - 記録は残し、AddedModsだけを0にする（そのキーを離したときに二重にbreakを送らない）。
// ここではカウンタを直すだけで、OSへのbreakは呼び出し元（ForwardToClass）の
// ReleaseLeakedModifiersが、そのキーのmakeより前に送る。
// OutputLock保持中・EngineLock非保持で呼ぶこと（ここでEngineLockを取る）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN
StripModdedKeyMods(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd
    )
{
    KIRQL oldIrql;
    BOOLEAN stripped = FALSE;

    // ModdedHeldCountはOutputLockとEngineLockの両方を保持して書かれるので、OutputLock保持中の
    // ここでは一貫した値が読める。
    if (devExt->ModdedHeldCount == 0) {
        return FALSE;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    for (ULONG i = 0; i < KBDLAYER_HELD_KEY_SLOTS; i++) {
        PKBDLAYER_HELD_KEY held = &devExt->HeldKeys[i];
        // Key Overrideの置き換え先が修飾付きキーなら、それも同じ扱い（足した修飾キーを外す）。
        if (held->AddedMods == 0 || held->State != KBDLAYER_HELD_ACTION ||
            (held->Action.Type != KBDLAYER_ACTION_MODDED_KEY &&
             held->Action.Type != KBDLAYER_ACTION_INTERNAL_KEY_OVERRIDE)) {
            continue;
        }
        if (BatchHasOtherKeyMake(InputDataStart, InputDataEnd, held->Action.Param2)) {
            SetHeldAddedModsLocked(devExt, held, 0);
            stripped = TRUE;
        }
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
    return stripped;
}

// [安全網] OSが押されていると思っている修飾キーのうち、普通の出力にも足した記録にも
// 押されていないもの（OutputModsDown & ~(RealModsDown | AddedModsUnion)）にbreakを送る。
// D-12で外したビット・修飾付きキーの解放で足した記録が消えたビットは、ここで離れる。
// 想定外の経路（押下記録が足した修飾キーごと上書きされた等。LayerEngineShouldDropEntry参照）
// でbreakを送り損ねても、次の送出で必ず直る（のどかで繰り返し起きた「カウンタは合って
// いるのにOSでは押されたまま」を防ぐため）。OutputLock保持中・EngineLock非保持で呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
ReleaseLeakedModifiers(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    // OS上であるべき状態は (RealModsDown & ~SuppressedMods) | AddedModsUnion。SuppressedModsは、
    // Key Overrideが置き換え中に一時的に離している修飾キー（ResolveModdedKey参照）。
    UCHAR desired = (UCHAR)((devExt->RealModsDown & ~devExt->SuppressedMods) | AddedModsUnion(devExt));
    UCHAR leaked = (UCHAR)(devExt->OutputModsDown & ~desired);

    if (leaked != 0) {
        SendAddedModifiers(devExt, leaked, KEY_BREAK);
    }
}

// Key Overrideが一時的に離していた修飾キー（SuppressedMods）を戻す。まだ普通の出力として押されて
// いる（RealModsDown）ものだけをOSへmakeし直す。OutputLock保持中・EngineLock非保持で呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
RestoreSuppressedModifiers(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    UCHAR restore = (UCHAR)(devExt->SuppressedMods & devExt->RealModsDown);

    devExt->SuppressedMods = 0;
    devExt->SuppressOwnerScanCode = 0;
    if (restore != 0) {
        SendAddedModifiers(devExt, restore, KEY_MAKE); // OSで押されていないビットだけを送る
    }
}

// kbdclassへの送出の入口。普通の出力（素通し・置き換え・MTのホールド・マクロ・コンボ・
// タップダンス・NICOLA）はすべてここを通す。修飾付きキー・LMが足した修飾キーの送出だけは
// SendAddedModifiersを使う。devExt->OutputLock保持中・EngineLock非保持で呼ぶこと。
// - 修飾キーのmakeはRealModsDownに立てて送る（足した分と重なっていてもそのまま送る。
//   OSにはオートリピートと同じに見える）。
// - 修飾キーのbreakはRealModsDownから落とす。足した分にまだ含まれていれば**送らない**
//   （OSではまだ押されている。足した記録が消えたときにReleaseLeakedModifiersで送られる）。
//   例: CapsLock→Ctrlを押したまま LCTL(KC_C) を離しても、Ctrlは離れない。逆に、
//   LCTL(KC_C) を押している間にマクロ・MTの打ち切り・物理Ctrlの解放でCtrlのbreakが来ても、
//   足したCtrlは離れない。
// - 修飾キーでないキーのmakeがあれば、先に修飾付きキーが足した修飾キーを外す（D-12、
//   StripModdedKeyMods）。
// - 送った後、OSで押されたままになっている修飾キーを離す（安全網、ReleaseLeakedModifiers）。
// 送らなかったイベントがあれば配列を詰め、*SystemArgument3を元の件数で上書きして
// TRUEを返す（D-3）。全部詰めたらkbdclassは呼ばない。
_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
ForwardToClass(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _Inout_opt_ PVOID SystemArgument3
    )
{
    ULONG originalCount = (ULONG)(InputDataEnd - InputDataStart);
    UCHAR added;
    PKEYBOARD_INPUT_DATA writePtr = InputDataStart;
    // 修飾キーでないキーのmakeがあるか（スキャンコード0は実在しないので、どのキーとも「別のキー」）。
    BOOLEAN hasKeyMake = BatchHasOtherKeyMake(InputDataStart, InputDataEnd, 0);

    if (hasKeyMake) {
        // OSMを押している間に別のキーを押した → そのOSMは離したときに普通の修飾キーとして戻す
        // （One Shotにしない）。ResolveModdedKey参照。
        devExt->OsmUsedWhileHeld = TRUE;
    }

    if (StripModdedKeyMods(devExt, InputDataStart, InputDataEnd)) {
        ReleaseLeakedModifiers(devExt); // 外した修飾キーのbreakを、キーのmakeより前に送る
    }
    // Key Overrideの置き換え中に別のキーが押されたら、一時的に離していた修飾キーを、そのキーの
    // makeより前に戻す（置き換えは続く。置き換え先のキー自身のmake=オートリピートでは戻さない）。
    if (devExt->SuppressedMods != 0 &&
        BatchHasOtherKeyMake(InputDataStart, InputDataEnd, devExt->SuppressOwnerScanCode)) {
        RestoreSuppressedModifiers(devExt);
    }
    added = AddedModsUnion(devExt);

    for (PKEYBOARD_INPUT_DATA readPtr = InputDataStart; readPtr < InputDataEnd; readPtr++) {
        UCHAR mod = ModifierMaskOf(readPtr->MakeCode, readPtr->Flags);
        BOOLEAN keep = TRUE;

        if (mod != 0) {
            if (readPtr->Flags & KEY_BREAK) {
                devExt->RealModsDown &= (UCHAR)~mod;
                if (added & mod) {
                    keep = FALSE;
                } else if (devExt->SuppressedMods & mod) {
                    // Key Overrideが一時的に離している修飾キーの解放。OSではもう離れているので
                    // 送らない（二重のbreakにしない）。置き換えを離したときにも戻さない。
                    devExt->SuppressedMods &= (UCHAR)~mod;
                    keep = FALSE;
                } else {
                    devExt->OutputModsDown &= (UCHAR)~mod;
                }
            } else {
                devExt->RealModsDown |= mod;
                devExt->OutputModsDown |= mod;
                devExt->SuppressedMods &= (UCHAR)~mod; // 押し直したら、もう離しておかない
            }
        }
        if (keep) {
            if (!(readPtr->Flags & KEY_BREAK)) {
                devExt->LastOutputWasModifier = (mod != 0);
            }
            if (writePtr != readPtr) {
                *writePtr = *readPtr;
            }
            writePtr++;
        }
    }

    if (writePtr != InputDataStart) {
        devExt->OriginalClassService(
            devExt->OriginalClassDeviceObject,
            InputDataStart,
            writePtr,
            SystemArgument3);
    }

    // OSM: 次のキー（修飾キーでないキーのmake）を、修飾キーを押したまま送ったので、One Shotを使い切る。
    // breakは下の安全網が送る（マクロ・コンボの出力なら、最初のキー1つにだけ掛かる）。
    if (hasKeyMake && devExt->OneShotMods != 0) {
        devExt->OneShotMods = 0;
    }

    ReleaseLeakedModifiers(devExt);

    if (writePtr != InputDataEnd) {
        if (SystemArgument3 != NULL) {
            *(PULONG)SystemArgument3 = originalCount;
        }
        return TRUE;
    }
    return FALSE;
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

    // 普通の出力なので、足した修飾キーとの関係はForwardToClassが扱う。
    ForwardToClass(devExt, &ev, &ev + 1, &dummyConsumed);
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

// 判定中に溜めたイベント（devExt->HoldBuffer）をEventsへ移し、件数を返す（溜めたものは空になる）。
// devExt->EngineLock保持中に呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static ULONG
TakeHoldBufferLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _Out_writes_to_(KBDLAYER_MAX_HOLD_BUFFER, return) PKEYBOARD_INPUT_DATA Events
    )
{
    ULONG count = devExt->HoldBufferCount;

    if (count > KBDLAYER_MAX_HOLD_BUFFER) {
        count = KBDLAYER_MAX_HOLD_BUFFER; // 念のための防御（本来起こり得ない）
    }
    if (count > 0) {
        RtlCopyMemory(Events, devExt->HoldBuffer, count * sizeof(KEYBOARD_INPUT_DATA));
    }
    devExt->HoldBufferCount = 0;
    return count;
}

// TakeHoldBufferLockedで取り出したイベントを、届いた順に1件ずつ、タップ/ホールド判定より後ろの
// 処理（ResolveAndForwardTail）へ流す。判定が決まった後（MT/LT自身の出力を送った後）に呼ぶので、
// ホールドならホールドした状態（LTならそのレイヤー）で、タップならタップした後の状態で解決される。
// 押下記録も通常どおり作られる（D-6）。OutputLock保持中・EngineLock非保持で呼ぶこと。
// 溜めたキーがMT/LTでも、改めて判定はしない（タップ側のキーになる。LayerEngineShouldDropEntry参照）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
ReplayHoldBuffer(
    _In_ PDEVICE_EXTENSION devExt,
    _In_reads_(Count) const KEYBOARD_INPUT_DATA* Events,
    _In_ ULONG Count
    )
{
    for (ULONG i = 0; i < Count; i++) {
        KEYBOARD_INPUT_DATA ev = Events[i];
        ULONG dummyConsumed = 0;
        ResolveAndForwardTail(devExt, &ev, &ev + 1, &dummyConsumed);
    }
}

// PERMISSIVE_HOLD/TAP_PREFERREDで、判定中に届いた別のキーのイベントEvを溜める。溜めた結果、
// ホールドに確定すべきならTRUEを返す（呼び出し元が確定して流し直す）。
// - 溜めたものがKBDLAYER_MAX_HOLD_BUFFER件に達した（あふれる前にホールドに確定する）。
// - PERMISSIVE_HOLDで、溜めた中に押下があるキーの解放が来た（別のキーのタップが判定中に終わった）。
// devExt->EngineLock保持中に呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN
BufferHoldEventLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ const KEYBOARD_INPUT_DATA* Ev
    )
{
    USHORT scanCode = EncodeScancode16(Ev->MakeCode, Ev->Flags);
    BOOLEAN isBreak = (Ev->Flags & KEY_BREAK) != 0;
    ULONG before;

    if (devExt->HoldBufferCount >= KBDLAYER_MAX_HOLD_BUFFER) {
        return TRUE; // 念のための防御（満杯になった時点で確定しているので、本来起こり得ない）
    }
    before = devExt->HoldBufferCount;
    devExt->HoldBuffer[devExt->HoldBufferCount++] = *Ev;
    if (devExt->HoldBufferCount >= KBDLAYER_MAX_HOLD_BUFFER) {
        return TRUE;
    }
    if (isBreak && devExt->TapHoldMode == KBDLAYER_TAPHOLD_MODE_PERMISSIVE_HOLD) {
        for (ULONG i = 0; i < before; i++) {
            const KEYBOARD_INPUT_DATA* p = &devExt->HoldBuffer[i];
            if (!(p->Flags & KEY_BREAK) && EncodeScancode16(p->MakeCode, p->Flags) == scanCode) {
                return TRUE;
            }
        }
    }
    return FALSE;
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
    KEYBOARD_INPUT_DATA buffered[KBDLAYER_MAX_HOLD_BUFFER] = { 0 };
    ULONG bufferedCount = 0;

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
    // PendingActiveがFALSEなら、ServiceCallback側（他キー押下によるホールド確定、
    // またはタップ確定としての解放）が既にこのサイクルを終えている
    // （KeCancelTimerとこのDPCのキューイングが競合した場合に起こりうる）。
    // 期限前なら、終わったサイクルのDPCが遅れて届いただけで、今のサイクルは
    // 自分のタイマーで改めて確定する。いずれの場合も何もしない。
    shouldConfirm = devExt->PendingActive && KeQueryInterruptTime() >= devExt->HoldDeadline;
    if (shouldConfirm) {
        pendingScanCode = devExt->PendingScanCode;
        ConfirmHoldLocked(devExt, &action);
        bufferedCount = TakeHoldBufferLocked(devExt, buffered);
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    if (shouldConfirm) {
        KBDLAYER_TRACE("LayerEngine: tap-hold TIMEOUT confirmed sc=0x%X type=%u buffered=%lu\n",
                       pendingScanCode, action.Type, bufferedCount);
        EmitModTapEventIfApplicable(devExt, &action, KEY_MAKE);
        // 判定中に溜めたイベント（PERMISSIVE_HOLD/TAP_PREFERRED）を、ホールドした状態で流し直す
        // （key-remap-engine T-1: タイマー経路も入力経路と同じ後始末・送出を通す）。
        ReplayHoldBuffer(devExt, buffered, bufferedCount);
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
    KEYBOARD_INPUT_DATA buffered[KBDLAYER_MAX_HOLD_BUFFER] = { 0 };
    ULONG bufferedCount = 0;

    // 1件バッチのみを対象とする。MT/LTを含むマップが有効な間は、ServiceCallbackが複数件の
    // バッチを1件ずつに分けて流す（ParseLayerMapBlobのNeedsSingleEvents）。それ以外で複数件の
    // バッチが来たら、通常のResolveLayerAction側の処理に委ねる（MT/LTはタップ側のキーになる）。
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
        if (!wasHoldConfirmed && wasPending) {
            // タップ確定。Quick Tap のために、キーと時刻を覚えておく。判定中に溜めたイベント
            // （PERMISSIVE_HOLD/TAP_PREFERRED）は、タップのキーの後に流し直す。
            devExt->LastTapScanCode = scanCode;
            devExt->LastTapTime = KeQueryInterruptTime();
            bufferedCount = TakeHoldBufferLocked(devExt, buffered);
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
            // ホールドに確定しないまま、タイムアウトより先に離された = タップ確定。
            if (action.Param2 != 0) {
                SendDecodedEvent(devExt, action.Param2, KEY_MAKE);
                SendDecodedEvent(devExt, action.Param2, KEY_BREAK);
            }
            ReplayHoldBuffer(devExt, buffered, bufferedCount);
        }

        if (SystemArgument3 != NULL) {
            *(PULONG)SystemArgument3 = 1;
        }
        return TRUE;
    }

    // --- HOLD_ON_OTHER_KEY: 保留中（未確定）に、別のキーの押下が来た場合 = ホールド確定 ---
    // MODTAPなら修飾キーのmakeを送り、LAYERTAPならレイヤーのビットを立ててから、割り込んだ
    // キーは後段（ResolveTapDance以降、ResolveLayerActionまで）に渡す。ホールドを確定した後の
    // レイヤーで解決されるので、LT(1,KC_SPC)を押してすぐJを押すと、レイヤー1のJの割り当てに
    // なる。押下記録も後段が通常どおり作るので、解放は記録どおりに処理される（D-6）。
    // 以前は割り込んだキーを生のスキャンコードのまま転送していた（レイヤーの影響を受けない
    // 既知の制約。2026-09-30に解消）。割り込んだキー自体がMT/LTなら、後段がタップ側のキーとして
    // 出す（LayerEngineShouldDropEntry参照）。
    if (devExt->PendingActive && !devExt->HoldConfirmed &&
        devExt->TapHoldMode == KBDLAYER_TAPHOLD_MODE_HOLD_ON_OTHER_KEY && !isBreak) {
        KBDLAYER_ACTION action;

        ConfirmHoldLocked(devExt, &action);
        // 通常は空。判定の途中でSET_CONFIGによりPERMISSIVE_HOLD/TAP_PREFERREDから切り替えられた
        // 場合だけ、それまでに溜めたイベントがある（このイベントより先に流し直す）。
        bufferedCount = TakeHoldBufferLocked(devExt, buffered);
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

        KeCancelTimer(&devExt->HoldTimer);

        EmitModTapEventIfApplicable(devExt, &action, KEY_MAKE);
        ReplayHoldBuffer(devExt, buffered, bufferedCount);
        return FALSE;
    }

    // --- PERMISSIVE_HOLD/TAP_PREFERRED: 保留中に届いた別のキーのイベントは溜める ---
    // 判定が決まるまでOSへは何も送らない。決まったら、MT/LT自身の出力（ホールドなら修飾キー・
    // レイヤー、タップならタップのキー）の後に、届いた順に流し直す（ReplayHoldBuffer）。
    // 溜めるのは、押下と、溜めたものがあるときの解放（順序を保つため）。溜めたものが無いときの
    // 解放（MT/LTより前から押されていたキー）は、そのまま後段へ渡す（そのmakeは送り済み）。
    // PERMISSIVE_HOLDで別のキーのタップが判定中に終わった、または溜めたものが満杯になったら、
    // ホールドに確定して流し直す。
    if (devExt->PendingActive && !devExt->HoldConfirmed &&
        devExt->TapHoldMode != KBDLAYER_TAPHOLD_MODE_HOLD_ON_OTHER_KEY &&
        (!isBreak || devExt->HoldBufferCount != 0)) {
        KBDLAYER_ACTION action;

        if (!BufferHoldEventLocked(devExt, ev)) {
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            if (SystemArgument3 != NULL) {
                *(PULONG)SystemArgument3 = 1;
            }
            return TRUE;
        }

        ConfirmHoldLocked(devExt, &action);
        bufferedCount = TakeHoldBufferLocked(devExt, buffered);
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

        KeCancelTimer(&devExt->HoldTimer);

        EmitModTapEventIfApplicable(devExt, &action, KEY_MAKE);
        ReplayHoldBuffer(devExt, buffered, bufferedCount);
        if (SystemArgument3 != NULL) {
            *(PULONG)SystemArgument3 = 1;
        }
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
                ULONGLONG now = KeQueryInterruptTime();

                ULONG quickTermMs = (devExt->QuickTapTermMs == 0) ? devExt->TapTermMs : devExt->QuickTapTermMs;

                // Quick Tap: 同じMT/LTをタップした直後（QuickTapTermMs以内。0ならTapTermMs、
                // KBDLAYER_QUICK_TAP_TERM_DISABLEDなら使わない）にもう一度押したら、判定せずに
                // タップ側のキーとして押す（後段のLayerEngineShouldDropEntryがタップ側のキーとして
                // 押下記録を作るので、押し続ければそのキーのリピートになり、解放も同じキーになる）。
                // 時刻をこの押下で更新するので、続けて素早く押しても同じ扱いが続く。
                if (devExt->QuickTapTermMs != KBDLAYER_QUICK_TAP_TERM_DISABLED &&
                    devExt->LastTapScanCode == scanCode &&
                    now - devExt->LastTapTime < (ULONGLONG)quickTermMs * 10000ULL) {
                    devExt->LastTapTime = now;
                    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
                    return FALSE;
                }

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

// マクロMacroIdのステップ列をStepsへコピーし、ステップ数を返す（未設定・範囲外は0）。
// devExt->EngineLock保持中に呼ぶこと（送出はロックを離してからSendMacroStepsで行う）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static ULONG
CopyMacroStepsLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG MacroId,
    _Out_writes_to_(KBDLAYER_MAX_MACRO_STEPS, return) PKBDLAYER_MACRO_STEP Steps
    )
{
    ULONG stepCount;

    if (MacroId >= KBDLAYER_MAX_MACRO_COUNT) {
        return 0;
    }
    stepCount = devExt->MacroStepCounts[MacroId];
    if (stepCount > KBDLAYER_MAX_MACRO_STEPS) {
        stepCount = KBDLAYER_MAX_MACRO_STEPS; // 念のための防御（本来起こり得ない）
    }
    if (stepCount > 0) {
        RtlCopyMemory(Steps, devExt->Macros[MacroId], stepCount * sizeof(KBDLAYER_MACRO_STEP));
    }
    return stepCount;
}

// CopyMacroStepsLockedでコピーしたステップ列を順に送る。EngineLockを保持していない状態で
// 呼ぶこと（OutputLockは保持していてよい）。ステップのScanCodeはEnc16形式なので、
// SendDecodedEventでMakeCode/Flagsへデコードしてから送る。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
SendMacroSteps(
    _In_ PDEVICE_EXTENSION devExt,
    _In_reads_(StepCount) const KBDLAYER_MACRO_STEP* Steps,
    _In_ ULONG StepCount
    )
{
    for (ULONG i = 0; i < StepCount; i++) {
        SendDecodedEvent(devExt, Steps[i].ScanCode, Steps[i].IsBreak ? KEY_BREAK : KEY_MAKE);
    }
}

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

    // 不正なマクロID（KeymapTool側のバグ等）・未設定のマクロは、何も出力しないだけで安全。
    stepCount = CopyMacroStepsLocked(devExt, action->Param1, localSteps);
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    // OriginalClassServiceはEngineLockを解放してから呼ぶ（既存の規律を踏襲）。
    SendMacroSteps(devExt, localSteps, stepCount);

    if (SystemArgument3 != NULL) {
        *(PULONG)SystemArgument3 = 1;
    }
    return TRUE;
}

// タップダンスのタップ側（SingleTap/DoubleTap/TripleTap）の出力。コンボとタップダンスの長押しの
// MACRO(n)も、確定した時点で1回実行するのにこれを使う。確定した時点で、押下と解放を
// 続けて送る（押している間の状態は持たない）。PrepareTapDanceTapLocked（EngineLock保持中）で
// 用意し、EmitTapDanceTap（EngineLock非保持・OutputLock保持中）で送る。
// マクロのステップ列は、マクロの設定変更と競合しないようEngineLock保持中にコピーしておく。
typedef struct _KBDLAYER_TAP_OUTPUT {
    KBDLAYER_ACTION Action;
    ULONG StepCount; // Action.Type == MACRO のときだけ使う
    KBDLAYER_MACRO_STEP Steps[KBDLAYER_MAX_MACRO_STEPS];
} KBDLAYER_TAP_OUTPUT, *PKBDLAYER_TAP_OUTPUT;

_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
PrepareTapDanceTapLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ const KBDLAYER_ACTION* Action,
    _Out_ PKBDLAYER_TAP_OUTPUT Out
    )
{
    Out->Action = *Action;
    Out->StepCount = 0;
    if (Action->Type == KBDLAYER_ACTION_MACRO && (devExt->EnabledFeatures & KBDLAYER_FEATURE_MACRO)) {
        // マクロの機能がOFFなら何も送らない（MACRO(id)のキーと同じ扱い）。
        Out->StepCount = CopyMacroStepsLocked(devExt, Action->Param1, Out->Steps);
    }
}

// 型ごとに送る。
// - KEY: キーを押して離す。
// - MODDED_KEY: 「修飾キーのmake（ビット順）→ キーのmake → キーのbreak → 修飾キーのbreak（逆順）」。
//   マクロで LCTL(KC_C) を展開したのと同じ形で、普通の出力としてForwardToClassを通す。
//   OSでもう押されている修飾キー（OutputModsDown。物理的に押しているCtrlや、修飾付きキーが
//   足しているもの）は送らない（押しも離しもしない。QMKのweak modsの考え方）。
// - MACRO: コピーしておいたステップ列を送る。
// それ以外の型（読み込んだ古い設定などの想定外）は何もしない。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
EmitTapDanceTap(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ const KBDLAYER_TAP_OUTPUT* Out
    )
{
    const KBDLAYER_ACTION* action = &Out->Action;

    switch (action->Type) {
    case KBDLAYER_ACTION_KEY:
        if (action->Param2 != 0) {
            SendDecodedEvent(devExt, action->Param2, KEY_MAKE);
            SendDecodedEvent(devExt, action->Param2, KEY_BREAK);
        }
        break;

    case KBDLAYER_ACTION_MODDED_KEY:
    {
        UCHAR mods = (UCHAR)(action->Param1 & ~devExt->OutputModsDown);
        for (ULONG bit = 0; bit < RTL_NUMBER_OF(s_ModifierScanCodes); bit++) {
            if (mods & (1u << bit)) {
                SendDecodedEvent(devExt, s_ModifierScanCodes[bit], KEY_MAKE);
            }
        }
        if (action->Param2 != 0) {
            SendDecodedEvent(devExt, action->Param2, KEY_MAKE);
            SendDecodedEvent(devExt, action->Param2, KEY_BREAK);
        }
        for (LONG bit = RTL_NUMBER_OF(s_ModifierScanCodes) - 1; bit >= 0; bit--) {
            if (mods & (1u << bit)) {
                SendDecodedEvent(devExt, s_ModifierScanCodes[bit], KEY_BREAK);
            }
        }
        break;
    }

    case KBDLAYER_ACTION_MACRO:
        SendMacroSteps(devExt, Out->Steps, Out->StepCount);
        break;

    default:
        break;
    }
}

// ---------------------------------------------------------------------------
// 修飾付きキー（MODDED_KEY: QMKのLCTL(kc)等相当）とLM（LAYER_MOD: QMKのLM(layer,mod)相当）
//
// 押下で「普通の出力として押されていない」修飾キーを足し（HeldKeys[].AddedModsに記録）、
// 解放でキーを離してから、足した修飾キーのうち他のどこからも押されていないものを離す。
// 普通の出力の修飾キー（RealModsDown）との重なりはForwardToClassと合わせて扱う
// （DeviceExtension.hのAddedModCountのコメント参照）。送出はすべてOutputLock保持中（D-5）。
// ---------------------------------------------------------------------------

// 修飾子マスク → そのマスクが含む修飾キーの「種類」（左右を合わせたマスク）。
// 例: LSFT(0x02) → Shift全体(0x22)。
_IRQL_requires_max_(DISPATCH_LEVEL)
static UCHAR
ModifierKindsOf(
    _In_ UCHAR Mods
    )
{
    static const UCHAR kinds[] = { 0x11 /* Ctrl */, 0x22 /* Shift */, 0x44 /* Alt */, 0x88 /* Win */ };
    UCHAR result = 0;

    for (ULONG i = 0; i < RTL_NUMBER_OF(kinds); i++) {
        if (Mods & kinds[i]) {
            result |= kinds[i];
        }
    }
    return result;
}

// Key Overrideの表から、出力キーOutScanCodeと今の修飾キーの状態に当てはまるものを探す（最初に
// 当たったもの）。当たったら、押下記録にするアクション（INTERNAL_KEY_OVERRIDE、Param1=置き換え先の
// 修飾子マスク、Param2=置き換え先のキー）と、OS上で一時的に離す修飾キーを返す。
// 条件: TriggerModsの種類（Ctrl・Shift・Alt・Win。左右は区別しない）が全部、押されている
// （普通の出力・足した修飾キー）。別の置き換えが一時的に離している修飾キー（SuppressedMods）も、
// まだ押され続けていれば（RealModsDown）含める。OS上の状態ではなく、ユーザーが押しているかで
// 判定する（2026-10-01の実機試験B-4: Shift+2の置き換え中にShift+BSを押すと、Shiftが一時的に
// 離されていたため条件に当たらず、素のBackspaceになっていた）。
// 離すのは、押されている条件の種類の修飾キー（左右とも）のうち、置き換え先自身が使わないもの。
// devExt->EngineLockとOutputLockを保持して呼ぶこと（RealModsDown・SuppressedModsはOutputLockで保護）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN
MatchKeyOverrideLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ USHORT OutScanCode,
    _Out_ PKBDLAYER_ACTION OutAction,
    _Out_ PUCHAR OutSuppress
    )
{
    static const UCHAR kinds[] = { 0x11, 0x22, 0x44, 0x88 };
    UCHAR down;

    RtlZeroMemory(OutAction, sizeof(*OutAction));
    *OutSuppress = 0;

    if (!(devExt->EnabledFeatures & KBDLAYER_FEATURE_KEYOVERRIDE) || devExt->KeyOverrideCount == 0) {
        return FALSE;
    }
    down = (UCHAR)(devExt->RealModsDown | AddedModsUnion(devExt));

    for (ULONG i = 0; i < KBDLAYER_MAX_KEY_OVERRIDE_COUNT; i++) {
        const KBDLAYER_KEY_OVERRIDE_ENTRY* entry = &devExt->KeyOverrides[i];
        UCHAR triggerKinds;
        BOOLEAN satisfied = TRUE;
        UCHAR replacementMods;

        if (!devExt->KeyOverrideUsed[i] || entry->TriggerScanCode != OutScanCode) {
            continue;
        }
        triggerKinds = ModifierKindsOf(entry->TriggerMods);
        for (ULONG k = 0; k < RTL_NUMBER_OF(kinds); k++) {
            if ((triggerKinds & kinds[k]) && !(down & kinds[k])) {
                satisfied = FALSE;
                break;
            }
        }
        if (!satisfied) {
            continue;
        }

        replacementMods = (entry->Replacement.Type == KBDLAYER_ACTION_MODDED_KEY) ? entry->Replacement.Param1 : 0;
        OutAction->Type = KBDLAYER_ACTION_INTERNAL_KEY_OVERRIDE;
        OutAction->Param1 = replacementMods;
        OutAction->Param2 = entry->Replacement.Param2;
        *OutSuppress = (UCHAR)(down & triggerKinds & ~replacementMods);
        return TRUE;
    }
    return FALSE;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
ResolveModdedKey(
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
    PKBDLAYER_HELD_KEY held;
    KBDLAYER_ACTION action;
    UCHAR sendMods = 0;
    BOOLEAN isOverride = FALSE;
    UCHAR suppress = 0;
    UCHAR previousSuppressed = 0;

    if ((InputDataEnd - InputDataStart) != 1) {
        return FALSE;
    }

    ev = InputDataStart;
    scanCode = EncodeScancode16(ev->MakeCode, ev->Flags);
    isBreak = (ev->Flags & KEY_BREAK) != 0;
    RtlZeroMemory(&action, sizeof(action));

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);

    held = HeldKeyFor(devExt, scanCode);

    // --- 修飾付きキー・LM・Key Overrideとして押下中のキー: マップのON/OFF・差し替えにかかわらず記録どおり ---
    if (held->State == KBDLAYER_HELD_ACTION && IsModdedActionType(held->Action.Type)) {
        action = held->Action;

        if (!isBreak) {
            // オートリピート。修飾付きキー（とKey Overrideの置き換え先）はキーのmakeだけを繰り返す
            // （修飾キーは押したまま）。別のキーを押して修飾キーを外した後（D-12、StripModdedKeyMods）
            // は、修飾キーなしで繰り返す。ただしハードウェアのリピートは最後に押したキーだけなので、
            // 実際にはほとんど起きない。LM・OSMは何もしない。
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            if (action.Type != KBDLAYER_ACTION_LAYER_MOD && action.Type != KBDLAYER_ACTION_ONESHOT_MOD) {
                SendDecodedEvent(devExt, action.Param2, KEY_MAKE);
            }
            if (SystemArgument3 != NULL) {
                *(PULONG)SystemArgument3 = 1;
            }
            return TRUE;
        }

        // 解放。記録を消してから、足した修飾キーのうち、他の記録にも普通の出力にも
        // 押されていないものだけを離す。
        {
            UCHAR added = held->AddedMods;
            // OSM: 押している間に別のキーを押さなかったら、修飾キーを次のキー1つのために押したままにする
            // （OneShotModsはAddedModsUnionに含まれるので、下のsendModsは0になりbreakは送らない）。
            // 別のキーを押していたら、普通の修飾キーとして戻す。
            // マップがOFFのとき（押している間にOFF・緊急解除された）は、One Shotにしない（実機試験D-2:
            // OFFの後に離したOSMが、次のキーまで修飾キーを持ち越していた）。
            BOOLEAN toOneShot = (action.Type == KBDLAYER_ACTION_ONESHOT_MOD &&
                                 devExt->LayerMapEnabled &&
                                 !devExt->OsmUsedWhileHeld && devExt->OsmHeldScanCode == scanCode);
            SetHeldAddedModsLocked(devExt, held, 0);
            SetHeldKeyLocked(devExt, scanCode, KBDLAYER_HELD_NONE, NULL);
            if (action.Type == KBDLAYER_ACTION_LAYER_MOD && action.Param1 < KBDLAYER_MAX_LAYERS) {
                devExt->ActiveLayerMask &= ~(1UL << action.Param1);
            }
            if (toOneShot) {
                devExt->OneShotMods |= action.Param1;
            }
            sendMods = (UCHAR)(added & ~(devExt->RealModsDown | AddedModsUnion(devExt)));
        }
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

        // 修飾付きキーでは、キーのbreakを送るForwardToClassの安全網が、足した修飾キーの
        // breakも続けて送る。下のSendAddedModifiersは、そのとき送り済みのビットを送らない
        // （OutputModsDownで判定）ので、LM・OSMのとき・念のための呼び出しになる。
        if (action.Type != KBDLAYER_ACTION_LAYER_MOD && action.Type != KBDLAYER_ACTION_ONESHOT_MOD) {
            SendDecodedEvent(devExt, action.Param2, KEY_BREAK);
        }
        SendAddedModifiers(devExt, sendMods, KEY_BREAK);
        // Key Override: この置き換えが一時的に離していた修飾キーのうち、まだ押されているものを戻す
        // （置き換え中に別のキーを押していれば、そのときに戻し済み。SuppressedModsはOutputLockで保護）。
        if (action.Type == KBDLAYER_ACTION_INTERNAL_KEY_OVERRIDE &&
            devExt->SuppressedMods != 0 && devExt->SuppressOwnerScanCode == action.Param2) {
            RestoreSuppressedModifiers(devExt);
        }
        if (SystemArgument3 != NULL) {
            *(PULONG)SystemArgument3 = 1;
        }
        return TRUE;
    }

    // --- 新しい押下 ---
    if (isBreak || held->State != KBDLAYER_HELD_NONE ||
        !devExt->LayerMapEnabled || devExt->LayerMap == NULL) {
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        return FALSE;
    }
    {
        PKBDLAYER_KEY_ENTRY keyEntry = FindKeyEntry(devExt->LayerMap, scanCode);
        PKBDLAYER_ACTION resolved = (keyEntry != NULL) ?
            ResolveEffectiveAction(devExt, devExt->LayerMap, keyEntry) : NULL;
        UCHAR mask;
        UCHAR downBefore;
        UCHAR added;

        if (resolved != NULL && resolved->Type == KBDLAYER_ACTION_ONESHOT_MOD &&
            (devExt->OneShotMods & resolved->Param1) == resolved->Param1) {
            // 同じOSMが次のキーを待っている間にもう一度タップした → 解除する（OneShotModsから外せば、
            // 安全網がbreakを送る。Alt・Winなら単独押しにならないようCtrlのタップも挟まれる）。
            // このキーの解放は飲み込む。
            devExt->OneShotMods &= (UCHAR)~resolved->Param1;
            SetHeldKeyLocked(devExt, scanCode, KBDLAYER_HELD_SWALLOWED, NULL);
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
            ReleaseLeakedModifiers(devExt);
            if (SystemArgument3 != NULL) {
                *(PULONG)SystemArgument3 = 1;
            }
            return TRUE;
        }
        if (resolved != NULL && IsModdedActionType(resolved->Type)) {
            action = *resolved;
            mask = (action.Type == KBDLAYER_ACTION_MODDED_KEY || action.Type == KBDLAYER_ACTION_ONESHOT_MOD)
                       ? action.Param1 : (UCHAR)(action.Param2 & 0xFF);
        } else {
            // Key Override: 解決した出力キー（KEYアクション。マップに無いキーは物理スキャンコード）が
            // 表のどれかに当たり、条件の修飾キーが押されていれば、置き換え先を修飾付きキーと同じ扱いで
            // 押す（記録の種別はINTERNAL_KEY_OVERRIDE）。条件の修飾キーはOS上で一時的に離す。
            USHORT outScanCode = 0;
            if (keyEntry == NULL) {
                outScanCode = scanCode;
            } else if (resolved != NULL && resolved->Type == KBDLAYER_ACTION_KEY) {
                outScanCode = resolved->Param2;
            }
            if (outScanCode == 0 || !MatchKeyOverrideLocked(devExt, outScanCode, &action, &suppress)) {
                KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
                return FALSE;
            }
            mask = action.Param1;
            isOverride = TRUE;
        }

        // 普通の出力として押されている修飾キーは足さない（離しもしない。QMKのweak mods相当）。
        // 他の記録が足している修飾キーは、こちらの記録にも含めるが送り直さない（両方が
        // 離れるまで押したままにするため）。
        downBefore = (UCHAR)((devExt->RealModsDown & ~devExt->SuppressedMods) | AddedModsUnion(devExt));
        added = (UCHAR)(mask & ~devExt->RealModsDown);
        SetHeldKeyLocked(devExt, scanCode, KBDLAYER_HELD_ACTION, &action);
        SetHeldAddedModsLocked(devExt, held, added);
        sendMods = (UCHAR)(added & ~downBefore);

        if (isOverride) {
            // 前の置き換えが離していた修飾キーは、この置き換えが離さないものなら戻す（下で送る）。
            previousSuppressed = devExt->SuppressedMods;
            devExt->SuppressedMods = suppress;
            devExt->SuppressOwnerScanCode = action.Param2;
        }

        if (action.Type == KBDLAYER_ACTION_LAYER_MOD) {
            if (action.Param1 < KBDLAYER_MAX_LAYERS) {
                devExt->ActiveLayerMask |= (1UL << action.Param1);
            }
        } else if (action.Type == KBDLAYER_ACTION_ONESHOT_MOD) {
            // OSMはキーを出さないので、OSLのワンショットは消費しない。離したときにOne Shotにするか
            // （押している間に別のキーを押さなかったか）を見るため、押したことを覚える。
            devExt->OsmHeldScanCode = scanCode;
            devExt->OsmUsedWhileHeld = FALSE;
        } else if (devExt->OneShotLayerPending) {
            // KEYと同じく、OSL(layer)発動後に最初に解決された実キーでワンショットを消費する。
            devExt->ActiveLayerMask &= ~devExt->OneShotLayerBit;
            devExt->OneShotLayerPending = FALSE;
        }
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    if (isOverride) {
        // 条件の修飾キーをOS上で離し（あるべき状態からSuppressedModsが外れたので、安全網がbreakを
        // 送る。Alt・Winの単独押しにならないようCtrlのタップも挟む）、前の置き換えが離していた修飾キーの
        // うち今回は離さないものを戻す。
        ReleaseLeakedModifiers(devExt);
        SendAddedModifiers(devExt, (UCHAR)(previousSuppressed & ~suppress & devExt->RealModsDown), KEY_MAKE);
    }
    SendAddedModifiers(devExt, sendMods, KEY_MAKE);
    if (action.Type != KBDLAYER_ACTION_LAYER_MOD && action.Type != KBDLAYER_ACTION_ONESHOT_MOD) {
        SendDecodedEvent(devExt, action.Param2, KEY_MAKE);
    }
    if (SystemArgument3 != NULL) {
        *(PULONG)SystemArgument3 = 1;
    }
    return TRUE;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
NeedsSingleEventDispatch(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    KIRQL oldIrql;
    BOOLEAN needs;
    BOOLEAN anyCombo = FALSE;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    // コンボはレイヤーマップの型ではなく別の表なので、NeedsSingleEventsには現れない。登録がある間
    // （機能がON）と、判定中・成立中・後始末中は1件ずつにする。分けないと、メンバーの解放と次のキーの
    // 押下が1つのバッチで届いたとき、ResolveComboが素通りして解放を見落とし、相方待ちのタイムアウトで
    // 復元のmakeだけが出る、または成立中の出力キー（足した修飾キーを含む）が押しっぱなしになる。
    if (devExt->EnabledFeatures & KBDLAYER_FEATURE_COMBO) {
        for (ULONG i = 0; i < KBDLAYER_MAX_COMBO_COUNT; i++) {
            if (devExt->ComboUsed[i]) {
                anyCombo = TRUE;
                break;
            }
        }
    }
    // Key Overrideの表がある間も1件ずつ（条件の修飾キーの判定と、一時的に離す処理を1件ずつ行うため）。
    needs = (devExt->ModdedHeldCount != 0) ||
            devExt->ComboPendingActive || devExt->ComboConfirmed || devExt->ComboDraining ||
            (devExt->LayerMapEnabled && devExt->LayerMap != NULL &&
             (devExt->LayerMap->NeedsSingleEvents || anyCombo ||
              (devExt->KeyOverrideCount != 0 && (devExt->EnabledFeatures & KBDLAYER_FEATURE_KEYOVERRIDE))));
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
    return needs;
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
// - KC_NO・全レイヤー透過・CUSTOM（ヘルパーイベント） → 何も送らない（CUSTOMはヘルパーへも
//   知らせない。生のスキャンコードを出すと、CUSTOMにしたキーが意図せず文字を打つため）
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
        if (action == NULL || action->Type == KBDLAYER_ACTION_NONE || action->Type == KBDLAYER_ACTION_CUSTOM) {
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
            // 修飾付きキーの出力で足していた修飾キーは、キーのbreakの後に安全網が離す。
            devExt->ComboAddedMods = 0;
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

            // MACRO(n)の出力は成立時に実行済み（押している間の状態は無い）。
            if (outputAction.Type != KBDLAYER_ACTION_MACRO && outputAction.Param2 != 0) {
                SendDecodedEvent(devExt, outputAction.Param2, KEY_BREAK);
            }
            ReleaseLeakedModifiers(devExt);
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
            // 相方到着 = コンボ成立。出力の種類ごとに（2026-10-02から KEY 以外も）:
            // - KEY: そのキーを押し、メンバーの解放で離す。
            // - MODDED_KEY: 普通の出力として押されていない修飾キーを足して（ComboAddedMods）からキーを押す。
            //   メンバーの解放でキーを離し、足した修飾キーは安全網が離す。
            // - MACRO: 成立した時点で1回実行する（マクロの機能がOFFなら何もしない）。
            KBDLAYER_ACTION outputAction = entry->OutputAction;
            KBDLAYER_TAP_OUTPUT macroOut;
            UCHAR addMods = 0;

            RtlZeroMemory(&macroOut, sizeof(macroOut));
            devExt->ComboPendingActive = FALSE;
            devExt->ComboConfirmed = TRUE;
            if (outputAction.Type == KBDLAYER_ACTION_MODDED_KEY) {
                addMods = (UCHAR)(outputAction.Param1 & ~devExt->RealModsDown);
                devExt->ComboAddedMods = addMods;
            } else if (outputAction.Type == KBDLAYER_ACTION_MACRO) {
                PrepareTapDanceTapLocked(devExt, &outputAction, &macroOut);
            }
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

            KeCancelTimer(&devExt->ComboTimer);
            if (outputAction.Type == KBDLAYER_ACTION_MACRO) {
                EmitTapDanceTap(devExt, &macroOut);
            } else {
                SendAddedModifiers(devExt, addMods, KEY_MAKE); // OSで押されていないビットだけ送る
                if (outputAction.Param2 != 0) {
                    SendDecodedEvent(devExt, outputAction.Param2, KEY_MAKE);
                }
            }
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
    // ただし、MT/LTの判定が決まっていない間は始めない。このキーはMT/LTにとっての「別のキー」なので、
    // 後段のResolveTapHoldに渡して、先に始まったMT/LTの判定に使わせる（ホールドの確定、または
    // PERMISSIVE_HOLD/TAP_PREFERREDで溜める）。ここでコンボを始めると、MT/LTの判定がこのキーを
    // 見ないまま進み、コンボの復元がMT/LTのタップより先に出て出力の順序が逆になっていた
    // （2026-09-30の外部レビューで指摘、2026-10-04に修正）。この押下は通常のキーとして解決される。
    // タップダンスの判定中の割り込みは、Driver.cのResolveAndForwardがここより前に確定させている。
    if (!isBreak && HeldKeyFor(devExt, scanCode)->State == KBDLAYER_HELD_NONE &&
        !(devExt->PendingActive && !devExt->HoldConfirmed)) {
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

// devExt->EngineLock保持中（とOutputLock保持中）に呼ぶこと。SingleHoldActionがLAYERTAP・LAYER_MODなら
// ActiveLayerMaskのビットをセットする（Phase 2のConfirmHoldLockedと同じ理屈）。MODDED_KEY・LAYER_MODなら、
// 普通の出力として押されていない修飾キーをTapDanceAddedModsに足す（送るのはEmitTapDanceHoldEventIfApplicable）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
ConfirmTapDanceHoldLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKBDLAYER_ACTION HoldAction
    )
{
    if ((HoldAction->Type == KBDLAYER_ACTION_LAYERTAP || HoldAction->Type == KBDLAYER_ACTION_LAYER_MOD) &&
        HoldAction->Param1 < KBDLAYER_MAX_LAYERS) {
        devExt->ActiveLayerMask |= (1UL << HoldAction->Param1);
    }
    if (HoldAction->Type == KBDLAYER_ACTION_MODDED_KEY) {
        devExt->TapDanceAddedMods = (UCHAR)(HoldAction->Param1 & ~devExt->RealModsDown);
    } else if (HoldAction->Type == KBDLAYER_ACTION_LAYER_MOD) {
        devExt->TapDanceAddedMods = (UCHAR)((HoldAction->Param2 & 0xFF) & ~devExt->RealModsDown);
    }
    devExt->TapDancePendingActive = FALSE;
    devExt->TapDanceHoldConfirmed = TRUE;
}

// devExt->EngineLock保持中（とOutputLock保持中）に呼ぶこと。SingleHoldActionがLAYERTAP・LAYER_MODなら
// ActiveLayerMaskのビットをクリアし、足していた修飾キーを0にする（breakは安全網が送る）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
ReleaseTapDanceHoldLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKBDLAYER_ACTION HoldAction
    )
{
    if ((HoldAction->Type == KBDLAYER_ACTION_LAYERTAP || HoldAction->Type == KBDLAYER_ACTION_LAYER_MOD) &&
        HoldAction->Param1 < KBDLAYER_MAX_LAYERS) {
        devExt->ActiveLayerMask &= ~(1UL << HoldAction->Param1);
    }
    devExt->TapDanceAddedMods = 0;
    devExt->TapDanceHoldConfirmed = FALSE;
}

// HoldActionの種別に応じてイベントを合成送出する。KEYならそのキー、MODTAPなら対応する
// 修飾キーを送る（EmitModTapEventIfApplicableと違いKEYも扱う）。2026-10-02から:
// - MODDED_KEY: 押下で足した修飾キー（TapDanceAddedMods）のmakeとキーのmake、解放でキーのbreak。
// - LAYER_MOD: 押下で足した修飾キーのmake（レイヤーはConfirmTapDanceHoldLockedで立てる）。
// - 解放では最後に安全網（ReleaseLeakedModifiers）を通し、足していた修飾キーのbreakを送る。
// LAYERTAPはActiveLayerMaskの操作のみで完結する。MACROは確定時にEmitTapDanceTapで実行する（ここでは扱わない）。
// devExt->EngineLockを保持していない状態（OutputLockは保持）で呼ぶこと。
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
    } else if (HoldAction->Type == KBDLAYER_ACTION_KEY && HoldAction->Param2 != 0) {
        SendDecodedEvent(devExt, HoldAction->Param2, BreakBit);
    } else if (HoldAction->Type == KBDLAYER_ACTION_MODDED_KEY || HoldAction->Type == KBDLAYER_ACTION_LAYER_MOD) {
        if (BreakBit == KEY_MAKE) {
            SendAddedModifiers(devExt, devExt->TapDanceAddedMods, KEY_MAKE); // OSで押されていないビットだけ送る
        }
        if (HoldAction->Type == KBDLAYER_ACTION_MODDED_KEY && HoldAction->Param2 != 0) {
            SendDecodedEvent(devExt, HoldAction->Param2, BreakBit);
        }
    }
    // NONE・KEYのスキャンコード0（長押しの動作なし）は何も送らない。
    if (BreakBit == KEY_BREAK) {
        ReleaseLeakedModifiers(devExt);
    }
}

// ScanCodeの押下が、今のレイヤーでタップダンスに解決されるなら、新しい保留サイクルを始めて
// TRUEを返す（*DueTimeにKeSetTimerへ渡す時間を入れる。KeSetTimerは呼び出し側がロック解放後に
// 呼ぶ）。押下記録のあるキー（オートリピート）は対象外。devExt->EngineLock保持中に呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN
TryStartTapDanceLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ USHORT ScanCode,
    _Out_ PLARGE_INTEGER DueTime
    )
{
    PKBDLAYER_KEY_ENTRY keyEntry;
    PKBDLAYER_ACTION action;

    DueTime->QuadPart = 0;
    if (devExt->LayerMap == NULL || HeldKeyFor(devExt, ScanCode)->State != KBDLAYER_HELD_NONE) {
        return FALSE;
    }
    keyEntry = FindKeyEntry(devExt->LayerMap, ScanCode);
    if (keyEntry == NULL) {
        return FALSE;
    }
    action = ResolveEffectiveAction(devExt, devExt->LayerMap, keyEntry);
    if (action == NULL || action->Type != KBDLAYER_ACTION_TAPDANCE ||
        action->Param1 >= KBDLAYER_MAX_TAPDANCE_COUNT || !devExt->TapDanceUsed[action->Param1]) {
        return FALSE;
    }

    devExt->TapDancePendingActive = TRUE;
    devExt->TapDanceKeyDown = TRUE;
    devExt->TapDanceTapCount = 0;
    devExt->TapDanceScanCode = ScanCode;
    devExt->TapDanceActiveId = action->Param1;
    *DueTime = TimerDueLocked(&devExt->TapDanceDeadline, devExt->TapDanceTermMs);
    return TRUE;
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
    KBDLAYER_TAP_OUTPUT tapOut;
    USHORT scanCode = 0;

    UNREFERENCED_PARAMETER(Dpc);
    UNREFERENCED_PARAMETER(SystemArgument1);
    UNREFERENCED_PARAMETER(SystemArgument2);

    if (devExt == NULL) {
        return;
    }

    RtlZeroMemory(&action, sizeof(action));
    RtlZeroMemory(&tapOut, sizeof(tapOut));

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
            if (action.Type == KBDLAYER_ACTION_MACRO) {
                PrepareTapDanceTapLocked(devExt, &action, &tapOut); // 長押しのマクロは確定時に1回実行
            }
        } else {
            ULONG finalCount = wasKeyDown ? (tapCountSoFar + 1) : tapCountSoFar;
            action = TapActionForCount(entry, finalCount);
            PrepareTapDanceTapLocked(devExt, &action, &tapOut);
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
            if (action.Type == KBDLAYER_ACTION_MACRO) {
                EmitTapDanceTap(devExt, &tapOut);
            } else {
                EmitTapDanceHoldEventIfApplicable(devExt, &action, KEY_MAKE);
            }
        } else {
            KBDLAYER_TRACE("LayerEngine: tapdance TIMEOUT tap confirmed sc=0x%X type=%u\n", scanCode, action.Type);
            EmitTapDanceTap(devExt, &tapOut);
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
                    KBDLAYER_TAP_OUTPUT tapOut;
                    PrepareTapDanceTapLocked(devExt, &tapAction, &tapOut);
                    devExt->TapDancePendingActive = FALSE;
                    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

                    KeCancelTimer(&devExt->TapDanceTimer);
                    EmitTapDanceTap(devExt, &tapOut);
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
        // タップダンスの結果を確定して送ってから、割り込んだキーは後段（ResolveMacro以降、
        // ResolveLayerActionまで）に渡す。ホールドを確定した後のレイヤーで解決され、押下記録も
        // 後段が通常どおり作る（D-6）。以前は割り込んだキーを生のスキャンコードのまま転送して
        // いた（ResolveTapHoldと同じ既知の制約。2026-09-30に解消）。
        {
            ULONG tapCountSoFar = devExt->TapDanceTapCount;
            BOOLEAN wasKeyDown = devExt->TapDanceKeyDown;
            BOOLEAN isHoldCase = (wasKeyDown && tapCountSoFar == 0);
            KBDLAYER_ACTION action;

            if (isHoldCase) {
                // 1回目の押下中の割り込み = SingleHold確定（Phase 2のHold On Other Key
                // Pressと同じ規律）。
                KBDLAYER_TAP_OUTPUT holdMacro;

                RtlZeroMemory(&holdMacro, sizeof(holdMacro));
                action = entry->SingleHoldAction;
                ConfirmTapDanceHoldLocked(devExt, &action);
                if (action.Type == KBDLAYER_ACTION_MACRO) {
                    PrepareTapDanceTapLocked(devExt, &action, &holdMacro); // 長押しのマクロは確定時に1回実行
                }
                KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

                KeCancelTimer(&devExt->TapDanceTimer);
                if (action.Type == KBDLAYER_ACTION_MACRO) {
                    EmitTapDanceTap(devExt, &holdMacro);
                } else {
                    EmitTapDanceHoldEventIfApplicable(devExt, &action, KEY_MAKE);
                }
                return FALSE;
            }

            // 2回目以降の押下中、または次のタップ待ち中の割り込み = その時点の
            // タップ回数を確定。押されたままなら解放をDrainingで無音に待つ。
            {
                ULONG finalCount = wasKeyDown ? (tapCountSoFar + 1) : tapCountSoFar;
                KBDLAYER_TAP_OUTPUT tapOut;
                LARGE_INTEGER dueTime;
                BOOLEAN restarted = FALSE;

                dueTime.QuadPart = 0;

                action = TapActionForCount(entry, finalCount);
                PrepareTapDanceTapLocked(devExt, &action, &tapOut);
                devExt->TapDancePendingActive = FALSE;
                if (wasKeyDown) {
                    devExt->TapDanceDraining = TRUE;
                } else if (!isBreak) {
                    // 次のタップ待ち中に、別のタップダンスキーが押された。確定したので、
                    // そのキーで新しい保留サイクルを始める（Drainingが無いので状態は1組で足りる）。
                    // 始められなければ後段へ渡す。
                    restarted = TryStartTapDanceLocked(devExt, scanCode, &dueTime);
                }
                KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

                KeCancelTimer(&devExt->TapDanceTimer);
                EmitTapDanceTap(devExt, &tapOut);

                if (restarted) {
                    KeSetTimer(&devExt->TapDanceTimer, dueTime, &devExt->TapDanceDpc);
                    if (SystemArgument3 != NULL) {
                        *(PULONG)SystemArgument3 = 1;
                    }
                    return TRUE;
                }
                return FALSE;
            }
        }
    }

    // --- Idle: このキー自体がTAPDANCEアクションに解決される押下イベントなら、
    //     新しい保留サイクルを開始する（押下時の記録があるキーのオートリピートは対象外） ---
    if (!isBreak) {
        LARGE_INTEGER dueTime;
        if (TryStartTapDanceLocked(devExt, scanCode, &dueTime)) {
            KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

            KeSetTimer(&devExt->TapDanceTimer, dueTime, &devExt->TapDanceDpc);

            if (SystemArgument3 != NULL) {
                *(PULONG)SystemArgument3 = 1;
            }
            return TRUE;
        }
    }

    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
    return FALSE;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
TapDanceInterruptedBy(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd
    )
{
    KIRQL oldIrql;
    BOOLEAN interrupted;
    USHORT scanCode;

    if ((InputDataEnd - InputDataStart) != 1 || (InputDataStart->Flags & KEY_BREAK) != 0) {
        return FALSE;
    }
    scanCode = EncodeScancode16(InputDataStart->MakeCode, InputDataStart->Flags);

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    interrupted = devExt->LayerMapEnabled && devExt->LayerMap != NULL &&
                  devExt->TapDancePendingActive && scanCode != devExt->TapDanceScanCode;
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
    return interrupted;
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
    // AbortTapHoldLockedが取り出した、判定中に溜めていたイベント（breakの送出後に流し直す）。
    ULONG ReplayCount;
    KEYBOARD_INPUT_DATA Replay[KBDLAYER_MAX_HOLD_BUFFER];
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

// Phase 2: MT/LT。判定中に溜めたイベント（PERMISSIVE_HOLD/TAP_PREFERRED）はReleases->Replayへ移し、
// SendAbortReleasesが、修飾キーのbreakの後に流し直す。打ち切りはOFF（と、その経路を使う緊急解除）
// なので、流し直した押下はマップを経ずにそのまま送られ、押下記録のあるキーの解放（判定より前から
// 押されていたキー）は記録どおりに処理される。捨てると、送り済みのmakeに対応するbreakが失われ、
// そのキーが押しっぱなしになる。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
AbortTapHoldLocked(
    _In_ PDEVICE_EXTENSION devExt,
    _Inout_ PKBDLAYER_ABORT_RELEASES Releases
    )
{
    Releases->ReplayCount = TakeHoldBufferLocked(devExt, Releases->Replay);
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
        // 修飾付きキーの出力で足していた修飾キーは、SendAbortReleasesの安全網が離す。MACROの出力は
        // 成立時に実行済みなので離すものは無い。
        if (entry->OutputAction.Type != KBDLAYER_ACTION_MACRO) {
            AppendAbortRelease(Releases, entry->OutputAction.Param2);
        }
        devExt->ComboAddedMods = 0;
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
        } else if (holdAction.Type == KBDLAYER_ACTION_KEY || holdAction.Type == KBDLAYER_ACTION_MODDED_KEY) {
            AppendAbortRelease(Releases, holdAction.Param2); // 0（動作なし）はAppendAbortReleaseが無視する
        }
        // LAYERTAP・LAYER_MODならレイヤーのビットを落とし、足していた修飾キーを0にする
        // （そのbreakはSendAbortReleasesの安全網が送る）。MACROは確定時に実行済み。
        ReleaseTapDanceHoldLocked(devExt, &holdAction);
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
    // MT/LTの判定中に溜めていたイベント（AbortTapHoldLocked参照）。打ち切った後の状態で流し直す。
    ReplayHoldBuffer(devExt, Releases->Replay, Releases->ReplayCount);
    // コンボ・タップダンスの長押しで足していた修飾キー（Abort*Lockedが0にしたもの）を離す。
    // キーのbreakを送っていない場合（LMの長押しなど）もあるので、ここで必ず同期する。
    ReleaseLeakedModifiers(devExt);
}

// ---------------------------------------------------------------------------
// IOCTLハンドラから呼ばれる設定関数（PASSIVE_LEVEL）
// ---------------------------------------------------------------------------

// devExt->PersistBindingに従って、レイヤーマップ等の永続化先サブキーパスを組み立てる
// （E-2: DeviceIdは一意でない前提で設計する。common/kbdlayer_ioctl.hのコメント参照）。
// - Global: "LayerMaps\G_All"（すべてのキーボードで共有。キーに依存しないので常に使える）
// - Instance: "LayerMaps\I_<InstanceKeyHex>"（この1台＝差し込み口だけ）
// - Model（既定）: "LayerMaps\M_<ModelKeyHex>"（同型番で共有）
// - 対応するキーが取得できなかった場合（InstanceKey/ModelKeyが0）は、旧形式
//   "LayerMaps\<DeviceIdHex>" にフォールバックする（MigrateLegacyPersistedSettingsIfNeededの
//   移行元と同じ形式でもある）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
BuildLayerMapsSubKeyPath(
    _In_ PDEVICE_EXTENSION devExt,
    _Out_writes_z_(PathLengthInChars) PWCHAR PathBuffer,
    _In_ ULONG PathLengthInChars
    )
{
    if (devExt->PersistBinding == KBDLAYER_BINDING_GLOBAL) {
        RtlStringCchPrintfW(PathBuffer, PathLengthInChars, L"LayerMaps\\G_All");
    } else if (devExt->PersistBinding == KBDLAYER_BINDING_INSTANCE && devExt->InstanceKey != 0) {
        RtlStringCchPrintfW(PathBuffer, PathLengthInChars, L"LayerMaps\\I_%08X", devExt->InstanceKey);
    } else if (devExt->ModelKey != 0) {
        RtlStringCchPrintfW(PathBuffer, PathLengthInChars, L"LayerMaps\\M_%08X", devExt->ModelKey);
    } else if (devExt->InstanceKey != 0) {
        // ModelKeyが取れずBinding=Modelのとき、InstanceKeyがあればそちらで代用する
        // （フォールバックより固有性が高い）。
        RtlStringCchPrintfW(PathBuffer, PathLengthInChars, L"LayerMaps\\I_%08X", devExt->InstanceKey);
    } else {
        RtlStringCchPrintfW(PathBuffer, PathLengthInChars, L"LayerMaps\\%08X", devExt->DeviceId);
    }
}

// CheckPersistResultはこの少し下で定義するが、MigrateLegacyPersistedSettingsIfNeededは
// それより前に使うので前方宣言する。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
CheckPersistResult(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PCWSTR ValueName,
    _In_ NTSTATUS Status
    );

// 旧形式（"LayerMaps\<DeviceIdHex>"。DeviceIdMode=0ならInstanceKeyと同じ値になる）から
// 新しいバインディング先へ、既存の永続化設定を1回だけコピーする。AddDeviceから、
// LoadPersistedLayerMapForDeviceの前に呼ぶ。
// - 新旧のパスが同じ文字列なら（例: DeviceIdMode=1かつBinding=Model、どちらもModelKey）
//   何もしない。
// - 新パスに既に"Migrated"マーカーがあれば何もしない（新パス側の変更を上書きしないため。
//   Model共有先は複数台が読み書きするので、2台目以降は毎回スキップする）。
// - 失敗はベストエフォート（個々の値のRegReadBinary/RegWriteBinary等の戻り値は見ない。
//   移行できなければ単に「初回として設定し直す」のと同じ状態になるだけ）。
// LayerMap/Enabled/.../Nicola の9種類の値を、srcPathからdstPathへコピーする
// （MigrateLegacyPersistedSettingsIfNeededとSetDeviceScopeForDeviceで共有）。
// - 可変長バイナリ（LayerMap/Nicola）は、srcに値があるときだけコピーする。
// - 固定長バイナリ（Macros/Combos/TapDances）は、期待どおりの長さで読めたときだけコピーする。
// - DWORD群は、srcに値が無ければ既定値がそのまま書かれるだけなので無条件でコピーする。
// - 失敗はベストエフォート（個々のRegRead/RegWriteの戻り値はCheckPersistResultでログするのみ）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
CopyPersistedSettings(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PCWSTR srcPath,
    _In_ PCWSTR dstPath
    )
{
    PVOID scratch;
    ULONG scratchBytes = KBDLAYER_MAX_MAP_BLOB_BYTES;
    ULONG actualLength;

    if (scratchBytes < KBDLAYER_MACRO_PERSIST_BLOB_BYTES) scratchBytes = KBDLAYER_MACRO_PERSIST_BLOB_BYTES;
    if (scratchBytes < KBDLAYER_COMBO_PERSIST_BLOB_BYTES) scratchBytes = KBDLAYER_COMBO_PERSIST_BLOB_BYTES;
    if (scratchBytes < KBDLAYER_TAPDANCE_PERSIST_BLOB_BYTES) scratchBytes = KBDLAYER_TAPDANCE_PERSIST_BLOB_BYTES;
    if (scratchBytes < KBDLAYER_KEY_OVERRIDE_PERSIST_BLOB_BYTES) scratchBytes = KBDLAYER_KEY_OVERRIDE_PERSIST_BLOB_BYTES;
    if (scratchBytes < KBDLAYER_MAX_NICOLA_BLOB_BYTES) scratchBytes = KBDLAYER_MAX_NICOLA_BLOB_BYTES;

    scratch = ExAllocatePool2(POOL_FLAG_PAGED, scratchBytes, KBDLAYER_REGTEMP_POOL_TAG);
    if (scratch == NULL) {
        return;
    }

    {
        static const PCWSTR variableNames[] = { KBDLAYER_MAP_VALUE_NAME, KBDLAYER_NICOLA_VALUE_NAME };
        for (ULONG i = 0; i < RTL_NUMBER_OF(variableNames); i++) {
            actualLength = 0;
            if (NT_SUCCESS(RegReadBinary(KBDLAYER_SERVICE_NAME, srcPath, variableNames[i],
                                          scratch, scratchBytes, &actualLength)) && actualLength > 0) {
                CheckPersistResult(devExt, variableNames[i],
                    RegWriteBinary(KBDLAYER_SERVICE_NAME, dstPath, variableNames[i], scratch, actualLength));
            }
        }
    }

    {
        // Macrosは旧サイズ（32個分）も受け付け、読んだバイト列のままコピーする
        // （読み込み側のLoadPersistedMacrosForDeviceも旧サイズを読めるため）。
        actualLength = 0;
        if (NT_SUCCESS(RegReadBinary(KBDLAYER_SERVICE_NAME, srcPath, KBDLAYER_MACROS_VALUE_NAME,
                                      scratch, scratchBytes, &actualLength)) &&
            MacroPersistCountFromLength(actualLength) > 0) {
            CheckPersistResult(devExt, KBDLAYER_MACROS_VALUE_NAME,
                RegWriteBinary(KBDLAYER_SERVICE_NAME, dstPath, KBDLAYER_MACROS_VALUE_NAME, scratch, actualLength));
        }
    }

    {
        static const PCWSTR fixedNames[] = { KBDLAYER_COMBOS_VALUE_NAME, KBDLAYER_TAPDANCES_VALUE_NAME,
                                             KBDLAYER_KEYOVERRIDES_VALUE_NAME };
        static const ULONG fixedSizes[] = { KBDLAYER_COMBO_PERSIST_BLOB_BYTES, KBDLAYER_TAPDANCE_PERSIST_BLOB_BYTES,
                                            KBDLAYER_KEY_OVERRIDE_PERSIST_BLOB_BYTES };
        for (ULONG i = 0; i < RTL_NUMBER_OF(fixedNames); i++) {
            actualLength = 0;
            if (NT_SUCCESS(RegReadBinary(KBDLAYER_SERVICE_NAME, srcPath, fixedNames[i],
                                          scratch, scratchBytes, &actualLength)) && actualLength == fixedSizes[i]) {
                CheckPersistResult(devExt, fixedNames[i],
                    RegWriteBinary(KBDLAYER_SERVICE_NAME, dstPath, fixedNames[i], scratch, fixedSizes[i]));
            }
        }
    }

    ExFreePoolWithTag(scratch, KBDLAYER_REGTEMP_POOL_TAG);

    {
        ULONG value = 0;
        RegReadDwordEx(KBDLAYER_SERVICE_NAME, srcPath, KBDLAYER_ENABLED_VALUE_NAME, &value, 0);
        CheckPersistResult(devExt, KBDLAYER_ENABLED_VALUE_NAME,
            RegWriteDword(KBDLAYER_SERVICE_NAME, dstPath, KBDLAYER_ENABLED_VALUE_NAME, value));

        RegReadDwordEx(KBDLAYER_SERVICE_NAME, srcPath, KBDLAYER_TAPTERM_VALUE_NAME, &value, KBDLAYER_DEFAULT_TAP_TERM_MS);
        CheckPersistResult(devExt, KBDLAYER_TAPTERM_VALUE_NAME,
            RegWriteDword(KBDLAYER_SERVICE_NAME, dstPath, KBDLAYER_TAPTERM_VALUE_NAME, value));

        RegReadDwordEx(KBDLAYER_SERVICE_NAME, srcPath, KBDLAYER_COMBOTERM_VALUE_NAME, &value, KBDLAYER_DEFAULT_COMBO_TERM_MS);
        CheckPersistResult(devExt, KBDLAYER_COMBOTERM_VALUE_NAME,
            RegWriteDword(KBDLAYER_SERVICE_NAME, dstPath, KBDLAYER_COMBOTERM_VALUE_NAME, value));

        RegReadDwordEx(KBDLAYER_SERVICE_NAME, srcPath, KBDLAYER_TAPDANCETERM_VALUE_NAME, &value, KBDLAYER_DEFAULT_TAPDANCE_TERM_MS);
        CheckPersistResult(devExt, KBDLAYER_TAPDANCETERM_VALUE_NAME,
            RegWriteDword(KBDLAYER_SERVICE_NAME, dstPath, KBDLAYER_TAPDANCETERM_VALUE_NAME, value));

        RegReadDwordEx(KBDLAYER_SERVICE_NAME, srcPath, KBDLAYER_FEATURES_VALUE_NAME, &value, KBDLAYER_FEATURE_ALL_V1);
        CheckPersistResult(devExt, KBDLAYER_FEATURES_VALUE_NAME,
            RegWriteDword(KBDLAYER_SERVICE_NAME, dstPath, KBDLAYER_FEATURES_VALUE_NAME, value));

        // EnabledFeaturesEx は、コピー元にあるときだけコピーする（無ければ読み込み側が旧値から決める）。
        RegReadDwordEx(KBDLAYER_SERVICE_NAME, srcPath, KBDLAYER_FEATURES_EX_VALUE_NAME, &value, 0xFFFFFFFFUL);
        if (value != 0xFFFFFFFFUL) {
            CheckPersistResult(devExt, KBDLAYER_FEATURES_EX_VALUE_NAME,
                RegWriteDword(KBDLAYER_SERVICE_NAME, dstPath, KBDLAYER_FEATURES_EX_VALUE_NAME, value));
        }

        RegReadDwordEx(KBDLAYER_SERVICE_NAME, srcPath, KBDLAYER_TAPHOLDMODE_VALUE_NAME, &value,
                       KBDLAYER_TAPHOLD_MODE_HOLD_ON_OTHER_KEY);
        CheckPersistResult(devExt, KBDLAYER_TAPHOLDMODE_VALUE_NAME,
            RegWriteDword(KBDLAYER_SERVICE_NAME, dstPath, KBDLAYER_TAPHOLDMODE_VALUE_NAME, value));

        RegReadDwordEx(KBDLAYER_SERVICE_NAME, srcPath, KBDLAYER_QUICKTAPTERM_VALUE_NAME, &value, 0);
        CheckPersistResult(devExt, KBDLAYER_QUICKTAPTERM_VALUE_NAME,
            RegWriteDword(KBDLAYER_SERVICE_NAME, dstPath, KBDLAYER_QUICKTAPTERM_VALUE_NAME, value));

        RegReadDwordEx(KBDLAYER_SERVICE_NAME, srcPath, KBDLAYER_TAPPINGTOGGLE_VALUE_NAME, &value,
                       KBDLAYER_DEFAULT_TAPPING_TOGGLE);
        CheckPersistResult(devExt, KBDLAYER_TAPPINGTOGGLE_VALUE_NAME,
            RegWriteDword(KBDLAYER_SERVICE_NAME, dstPath, KBDLAYER_TAPPINGTOGGLE_VALUE_NAME, value));
    }

    DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_INFO_LEVEL,
               "kbdlayer: copied persisted settings %ws -> %ws for DeviceId=0x%08X\n",
               srcPath, dstPath, devExt->DeviceId);
}

// staticにしない: Driver.cのAddDeviceから、LoadPersistedLayerMapForDeviceの前に呼ぶ。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
MigrateLegacyPersistedSettingsIfNeeded(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    WCHAR legacyPath[64];
    WCHAR newPath[64];
    ULONG migrated = 0;

    // Globalは、すべてのキーボードで1つの設定を共有する保存先。旧形式（キーボードごとの
    // "LayerMaps\<DeviceIdHex>"）からの移行先にはしない（1台の古い設定で全台の設定を上書きしないため）。
    if (devExt->PersistBinding == KBDLAYER_BINDING_GLOBAL) {
        return;
    }

    RtlStringCchPrintfW(legacyPath, RTL_NUMBER_OF(legacyPath), L"LayerMaps\\%08X", devExt->DeviceId);
    BuildLayerMapsSubKeyPath(devExt, newPath, RTL_NUMBER_OF(newPath));

    if (wcscmp(legacyPath, newPath) == 0) {
        return; // 移行先が旧パスと同じ（DeviceIdMode/Bindingの組み合わせ次第で起きる）
    }

    RegReadDwordEx(KBDLAYER_SERVICE_NAME, newPath, L"Migrated", &migrated, 0);
    if (migrated != 0) {
        return;
    }

    CopyPersistedSettings(devExt, legacyPath, newPath);

    CheckPersistResult(devExt, L"Migrated",
        RegWriteDword(KBDLAYER_SERVICE_NAME, newPath, L"Migrated", 1));
}

// GET_DEVICE_SCOPE の実体。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
GetDeviceScopeForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _Out_ PKBDLAYER_DEVICE_SCOPE OutScope
    )
{
    OutScope->InstanceKey = devExt->InstanceKey;
    OutScope->ModelKey = devExt->ModelKey;
    OutScope->Binding = devExt->PersistBinding;
}

// 保存先subKeyPathにレイヤーマップが保存されているか（Globalへ切り替えるとき、ほかのキーボードが
// すでに作った共有設定を上書きしないための判定）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static BOOLEAN
PersistedStoreHasLayerMap(
    _In_ PCWSTR subKeyPath
    )
{
    ULONG actualLength = 0;

    // Buffer=NULLなら、値の長さだけを調べる（RegReadBinaryはActualLengthを返す）。
    if (NT_SUCCESS(RegReadBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_MAP_VALUE_NAME,
                                 NULL, 0, &actualLength))) {
        return actualLength > 0;
    }
    return FALSE;
}

// SET_DEVICE_SCOPE の実体。Model<->Instanceの切替時、現在の保存先の内容を新しい
// 保存先へコピーしてから devExt->PersistBinding を確定させる（設定が消えたように
// 見えないようにするため）。Globalへの切替は、Global側にレイヤーマップがあればそれを使い
// （上書きしない）、無ければ現在の設定をコピーする。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetDeviceScopeForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG NewBinding
    )
{
    WCHAR oldPath[64];
    WCHAR newPath[64];
    WCHAR instanceKeyHex[16];

    if (NewBinding != KBDLAYER_BINDING_MODEL && NewBinding != KBDLAYER_BINDING_INSTANCE &&
        NewBinding != KBDLAYER_BINDING_GLOBAL) {
        return STATUS_INVALID_PARAMETER;
    }
    if (NewBinding == KBDLAYER_BINDING_INSTANCE && devExt->InstanceKey == 0) {
        // 差し込み口を特定できないデバイスは「この1台だけ」に切り替えられない。
        return STATUS_INVALID_DEVICE_STATE;
    }
    if (devExt->PersistBinding == NewBinding) {
        return STATUS_SUCCESS;
    }

    BuildLayerMapsSubKeyPath(devExt, oldPath, RTL_NUMBER_OF(oldPath));
    devExt->PersistBinding = NewBinding;
    BuildLayerMapsSubKeyPath(devExt, newPath, RTL_NUMBER_OF(newPath));

    if (wcscmp(oldPath, newPath) != 0) {
        // Globalは複数のキーボードが共有する。すでに設定があれば、それを使う（この1台の設定で上書きして、
        // ほかのキーボードの設定を消さない）。
        if (NewBinding != KBDLAYER_BINDING_GLOBAL || !PersistedStoreHasLayerMap(newPath)) {
            CopyPersistedSettings(devExt, oldPath, newPath);
        }
    }

    // Bindingそのものは差し込み口（InstanceKey）に紐づく設定として、
    // Parameters\DeviceBinding\<InstanceKeyHex> に永続化する。
    if (devExt->InstanceKey != 0) {
        RtlStringCchPrintfW(instanceKeyHex, RTL_NUMBER_OF(instanceKeyHex), L"%08X", devExt->InstanceKey);
        CheckPersistResult(devExt, L"DeviceBinding",
            RegWriteDword(KBDLAYER_SERVICE_NAME, L"DeviceBinding", instanceKeyHex, NewBinding));
    }

    return STATUS_SUCCESS;
}

// 設定の永続化（レジストリへの書き込み）の結果を確認する。ライブの設定は既に反映済みなので
// IOCTLは成功のまま返すが、失敗を黙って握りつぶさずにログする（Release版でもDebugView等で
// 見えるようDbgPrintExのエラーレベルで出す）。以前は戻り値を全て無視していたため、
// "LayerMaps"キーが作れずに永続化が一度も成功していないことに気付けなかった
// （2026-09-28、dev-notesチェックリストD-11）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
CheckPersistResult(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PCWSTR ValueName,
    _In_ NTSTATUS Status
    )
{
    if (!NT_SUCCESS(Status)) {
        DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL,
                   "kbdlayer: failed to persist %ws for DeviceId=0x%08X, status=0x%08X\n",
                   ValueName, devExt->DeviceId, Status);
    }
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

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    CheckPersistResult(devExt, KBDLAYER_MAP_VALUE_NAME,
        RegWriteBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_MAP_VALUE_NAME, Blob, BlobLength));

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

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    CheckPersistResult(devExt, KBDLAYER_MAP_VALUE_NAME,
        RegWriteBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_MAP_VALUE_NAME, NULL, 0));

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
    releases.ReplayCount = 0;

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
        // TTで固定したレイヤーとタップの回数も消す（押しているTTキーの解放は押下記録どおりに
        // 処理されるが、固定が無いのでビットを落とすだけになる）。
        devExt->TtToggledLayers = 0;
        devExt->TtTapCount = 0;
        devExt->TtKeyDown = FALSE;
        // OSMの待ち（次のキーのために押したままにしている修飾キー）も消す。breakは下の安全網が送る。
        // 押しているOSMは、離したときに普通の修飾キーとして戻す（One Shotにしない）。
        devExt->OneShotMods = 0;
        devExt->OsmUsedWhileHeld = TRUE;
    }
    KeReleaseSpinLockFromDpcLevel(&devExt->EngineLock);

    if (!Enabled) {
        KeCancelTimer(&devExt->HoldTimer);
        KeCancelTimer(&devExt->ComboTimer);
        KeCancelTimer(&devExt->TapDanceTimer);
        SendAbortReleases(devExt, &releases);
        ReleaseLeakedModifiers(devExt);
    }
    KeReleaseSpinLock(&devExt->OutputLock, outputIrql);

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    CheckPersistResult(devExt, KBDLAYER_ENABLED_VALUE_NAME,
        RegWriteDword(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_ENABLED_VALUE_NAME, Enabled ? 1 : 0));

    // ON/OFF自体と、OFF時のレイヤー状態のリセットを、待っているアプリへ知らせる。
    // （アプリ管理レイヤーはOFFでも消さない。持ち主のアプリが下ろす）
    LayerNotifyIfChanged(devExt);
    return STATUS_SUCCESS;
}

// ---------------------------------------------------------------------------
// 機能別ON/OFF（release-roadmap-priority.md 項番4。common/kbdlayer_ioctl.hのコメント参照）
// ---------------------------------------------------------------------------

_IRQL_requires_max_(PASSIVE_LEVEL)
ULONG
GetFeaturesForDevice(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    KIRQL oldIrql;
    ULONG features;

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    features = devExt->EnabledFeatures;
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
    return features;
}

// 無効化したビットの機能に保留中・確定中の状態があれば、SetLayerMapEnabledForDevice(FALSE)と
// 同じ考え方で打ち切り、送出済みのmakeにbreakを送る（押しっぱなしを残さない）。
// Macroは保留状態を持たないので対象外。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetFeaturesForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG Features
    )
{
    KIRQL outputIrql;
    WCHAR subKeyPath[64];
    KBDLAYER_ABORT_RELEASES releases;
    ULONG disabled;

    if (Features & ~KBDLAYER_FEATURE_ALL) {
        return STATUS_INVALID_PARAMETER;
    }

    releases.Count = 0;
    releases.ReplayCount = 0;

    KeAcquireSpinLock(&devExt->OutputLock, &outputIrql);
    KeAcquireSpinLockAtDpcLevel(&devExt->EngineLock);
    disabled = devExt->EnabledFeatures & ~Features;
    if (disabled & KBDLAYER_FEATURE_COMBO) {
        AbortComboLocked(devExt, &releases);
    }
    if (disabled & KBDLAYER_FEATURE_TAPDANCE) {
        AbortTapDanceLocked(devExt, &releases);
    }
    devExt->EnabledFeatures = Features;
    KeReleaseSpinLockFromDpcLevel(&devExt->EngineLock);

    if (disabled & KBDLAYER_FEATURE_COMBO) {
        KeCancelTimer(&devExt->ComboTimer);
    }
    if (disabled & KBDLAYER_FEATURE_TAPDANCE) {
        KeCancelTimer(&devExt->TapDanceTimer);
    }
    SendAbortReleases(devExt, &releases);
    KeReleaseSpinLock(&devExt->OutputLock, outputIrql);

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    // 旧値（3機能のビットだけ。旧ドライバに戻したときに読まれる）と、全ビットの値の両方を書く
    // （LoadPersistedLayerMapForDevice参照）。
    CheckPersistResult(devExt, KBDLAYER_FEATURES_VALUE_NAME,
        RegWriteDword(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_FEATURES_VALUE_NAME,
                      Features & KBDLAYER_FEATURE_ALL_V1));
    CheckPersistResult(devExt, KBDLAYER_FEATURES_EX_VALUE_NAME,
        RegWriteDword(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_FEATURES_EX_VALUE_NAME, Features));

    return STATUS_SUCCESS;
}

// ---------------------------------------------------------------------------
// 緊急時の解除手段（release-roadmap-priority.md 項番3。common/kbdlayer_ioctl.hの
// コメントと .plans/emergency-release.md 参照）
// ---------------------------------------------------------------------------

// ドライバ全体の設定（グローバル）。g_EmergencyConfigLockで保護する。CheckEmergencyRelease
// （devExt->OutputLock保持中、DISPATCH_LEVEL）から読むため、KSPIN_LOCKで保護する
// （PASSIVE_LEVELの書き込み側もこれを使う。ロック順序: OutputLock → g_EmergencyConfigLock、
// 一方向のみ）。
static KSPIN_LOCK g_EmergencyConfigLock;
static ULONG g_EmergencyKeyCount = 0;
static USHORT g_EmergencyScanCodes[KBDLAYER_EMERGENCY_MAX_KEYS];
static PIO_WORKITEM g_EmergencyWorkItem = NULL;
static LONG g_EmergencyReleaseInFlight = 0;

// 一度も設定していない場合の既定値: 左右Ctrl + 左右Shift（Enc16）。
static const USHORT g_DefaultEmergencyScanCodes[4] = { 0x001D, 0xE01D, 0x002A, 0x0036 };

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
GetEmergencyReleaseConfig(
    _Out_ PKBDLAYER_EMERGENCY_RELEASE_CONFIG OutConfig
    )
{
    KIRQL oldIrql;

    KeAcquireSpinLock(&g_EmergencyConfigLock, &oldIrql);
    OutConfig->KeyCount = g_EmergencyKeyCount;
    RtlCopyMemory(OutConfig->ScanCodes, g_EmergencyScanCodes, sizeof(OutConfig->ScanCodes));
    KeReleaseSpinLock(&g_EmergencyConfigLock, oldIrql);
}

// 緊急解除の組み合わせをライブの設定へ反映する。スピンロックを持つ区間なので、PAGEセクションに
// 置かない非ページのヘルパーに切り出している（A-1: PAGE関数の中でスピンロックを持たない）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
PublishEmergencyConfig(
    _In_ ULONG KeyCount,
    _In_reads_(KeyCount) const USHORT* ScanCodes
    )
{
    KIRQL oldIrql;

    KeAcquireSpinLock(&g_EmergencyConfigLock, &oldIrql);
    g_EmergencyKeyCount = KeyCount;
    RtlZeroMemory(g_EmergencyScanCodes, sizeof(g_EmergencyScanCodes));
    RtlCopyMemory(g_EmergencyScanCodes, ScanCodes, KeyCount * sizeof(USHORT));
    KeReleaseSpinLock(&g_EmergencyConfigLock, oldIrql);
}

#ifdef ALLOC_PRAGMA
#pragma alloc_text(PAGE, SetEmergencyReleaseConfig)
#pragma alloc_text(PAGE, InitializeEmergencyReleaseGlobal)
#pragma alloc_text(PAGE, TeardownEmergencyReleaseGlobal)
#endif

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetEmergencyReleaseConfig(
    _In_ PKBDLAYER_EMERGENCY_RELEASE_CONFIG Config
    )
{
    NTSTATUS status;
    NTSTATUS scStatus;

    PAGED_CODE();

    if (Config->KeyCount < KBDLAYER_EMERGENCY_MIN_KEYS || Config->KeyCount > KBDLAYER_EMERGENCY_MAX_KEYS) {
        return STATUS_INVALID_PARAMETER;
    }
    // 重複キーは拒否する（同じ物理キーが2回指定されると、全ビットが揃うことがなくなり
    // 実質的な無効化と同じになるため）。
    for (ULONG i = 0; i < Config->KeyCount; i++) {
        for (ULONG j = i + 1; j < Config->KeyCount; j++) {
            if (Config->ScanCodes[i] == Config->ScanCodes[j]) {
                return STATUS_INVALID_PARAMETER;
            }
        }
    }

    PublishEmergencyConfig(Config->KeyCount, Config->ScanCodes);

    status = RegWriteDword(KBDLAYER_SERVICE_NAME, KBDLAYER_EMERGENCY_SUBKEY,
                            KBDLAYER_EMERGENCY_KEYCOUNT_VALUE_NAME, Config->KeyCount);
    scStatus = RegWriteBinary(KBDLAYER_SERVICE_NAME, KBDLAYER_EMERGENCY_SUBKEY,
                               KBDLAYER_EMERGENCY_SCANCODES_VALUE_NAME,
                               Config->ScanCodes, Config->KeyCount * sizeof(USHORT));
    if (!NT_SUCCESS(status)) {
        DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL,
                   "kbdlayer: failed to persist %ws, status=0x%08X\n", KBDLAYER_EMERGENCY_KEYCOUNT_VALUE_NAME, status);
    }
    if (!NT_SUCCESS(scStatus)) {
        DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL,
                   "kbdlayer: failed to persist %ws, status=0x%08X\n", KBDLAYER_EMERGENCY_SCANCODES_VALUE_NAME, scStatus);
    }

    DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_WARNING_LEVEL,
               "kbdlayer: emergency release combo changed, KeyCount=%lu\n", Config->KeyCount);

    return STATUS_SUCCESS; // ライブの設定は既に反映済み（他のSet*と同じ方針）
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeEmergencyReleaseGlobal(
    _In_ PDEVICE_OBJECT ControlDeviceObject
    )
{
    ULONG keyCount = 0;
    USHORT scanCodes[KBDLAYER_EMERGENCY_MAX_KEYS] = {0};
    ULONG actualLength = 0;

    PAGED_CODE();

    KeInitializeSpinLock(&g_EmergencyConfigLock);

    RegReadDwordEx(KBDLAYER_SERVICE_NAME, KBDLAYER_EMERGENCY_SUBKEY,
                    KBDLAYER_EMERGENCY_KEYCOUNT_VALUE_NAME, &keyCount, 0);
    if (keyCount >= KBDLAYER_EMERGENCY_MIN_KEYS && keyCount <= KBDLAYER_EMERGENCY_MAX_KEYS) {
        NTSTATUS status = RegReadBinary(KBDLAYER_SERVICE_NAME, KBDLAYER_EMERGENCY_SUBKEY,
                                         KBDLAYER_EMERGENCY_SCANCODES_VALUE_NAME,
                                         scanCodes, sizeof(scanCodes), &actualLength);
        if (!NT_SUCCESS(status) || actualLength != keyCount * sizeof(USHORT)) {
            DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL,
                       "kbdlayer: EmergencyReleaseScanCodes missing/corrupt (status=0x%08X, length=%lu, expected=%lu); "
                       "falling back to the default combo\n", status, actualLength, keyCount * (ULONG)sizeof(USHORT));
            keyCount = 0;
        }
    } else {
        keyCount = 0;
    }

    if (keyCount == 0) {
        keyCount = RTL_NUMBER_OF(g_DefaultEmergencyScanCodes);
        RtlCopyMemory(scanCodes, g_DefaultEmergencyScanCodes, sizeof(g_DefaultEmergencyScanCodes));
    }

    PublishEmergencyConfig(keyCount, scanCodes);

    g_EmergencyWorkItem = IoAllocateWorkItem(ControlDeviceObject);
    if (g_EmergencyWorkItem == NULL) {
        DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL,
                   "kbdlayer: IoAllocateWorkItem failed; emergency release will not be able to act\n");
    }

    DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_INFO_LEVEL,
               "kbdlayer: emergency release armed, KeyCount=%lu\n", keyCount);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
TeardownEmergencyReleaseGlobal(
    VOID
    )
{
    PAGED_CODE();

    if (g_EmergencyWorkItem != NULL) {
        IoFreeWorkItem(g_EmergencyWorkItem);
        g_EmergencyWorkItem = NULL;
    }
}

// 接続中の全キーボードのレイヤーマップを一括でOFFにする実処理。PASSIVE_LEVELで動く
// ワークアイテムのルーチン（IoQueueWorkItem/DelayedWorkQueue）。EmergencyDpcRoutine
// （DISPATCH_LEVEL）から、SetLayerMapEnabledForDevice（PASSIVE_LEVEL・レジストリI/O）を
// 直接呼べないためにこの形にしている。
// 緊急解除の対象デバイスを集め、それぞれのremove lockを取って返す。g_listLockを持つ区間なので、
// PAGEセクションに置かない非ページのヘルパーにしている（A-1）。
// B-1: remove lockはg_listLockを保持したまま取得する（PnPによる削除との競合防止）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static ULONG
CollectEmergencyTargets(
    _Out_writes_to_(KBDLAYER_EMERGENCY_MAX_TARGETS, return) PDEVICE_EXTENSION* Targets
    )
{
    ULONG targetCount = 0;
    KIRQL oldIrql;
    PLIST_ENTRY entry;
    PDEVICE_EXTENSION devExt;

    KeAcquireSpinLock(&g_listLock, &oldIrql);
    for (entry = g_deviceList.Flink;
         entry != &g_deviceList && targetCount < KBDLAYER_EMERGENCY_MAX_TARGETS;
         entry = entry->Flink) {
        devExt = CONTAINING_RECORD(entry, DEVICE_EXTENSION, ListEntry);
        if (devExt->DeviceId == 0) {
            continue;
        }
        if (NT_SUCCESS(IoAcquireRemoveLock(&devExt->RemoveLock, NULL))) {
            Targets[targetCount++] = devExt;
        }
    }
    KeReleaseSpinLock(&g_listLock, oldIrql);
    return targetCount;
}

static IO_WORKITEM_ROUTINE EmergencyReleaseWorkRoutine;

#ifdef ALLOC_PRAGMA
#pragma alloc_text(PAGE, EmergencyReleaseWorkRoutine)
#endif

_Use_decl_annotations_
static VOID
EmergencyReleaseWorkRoutine(
    PDEVICE_OBJECT WorkItemDeviceObject,
    PVOID Context
    )
{
    PDEVICE_EXTENSION targets[KBDLAYER_EMERGENCY_MAX_TARGETS];
    ULONG targetCount;

    UNREFERENCED_PARAMETER(WorkItemDeviceObject);
    UNREFERENCED_PARAMETER(Context);
    PAGED_CODE();

    DbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_WARNING_LEVEL,
               "kbdlayer: EMERGENCY RELEASE triggered, disabling all layer maps\n");

    // 実際のSetLayerMapEnabledForDevice呼び出し（PASSIVE_LEVEL・レジストリI/O）は
    // g_listLockを解放してから行う（スピンロック保持中にPAGEDコードを呼ばない）。
    targetCount = CollectEmergencyTargets(targets);

    for (ULONG i = 0; i < targetCount; i++) {
        SetLayerMapEnabledForDevice(targets[i], FALSE);
        IoReleaseRemoveLock(&targets[i]->RemoveLock, NULL);
    }

    InterlockedExchange(&g_EmergencyReleaseInFlight, 0);
}

_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
QueueEmergencyReleaseWorkItem(
    VOID
    )
{
    if (g_EmergencyWorkItem == NULL) {
        return; // InitializeEmergencyReleaseGlobalでの割り当てに失敗している
    }
    if (InterlockedCompareExchange(&g_EmergencyReleaseInFlight, 1, 0) == 0) {
        IoQueueWorkItem(g_EmergencyWorkItem, EmergencyReleaseWorkRoutine, DelayedWorkQueue, NULL);
    }
}

// HoldTimer/ComboTimer/TapDanceTimerと同じKTIMER+KDPCの組。組み合わせの全キーが
// 揃った状態でKBDLAYER_EMERGENCY_HOLD_MS経過したときに起動する。
_IRQL_requires_max_(DISPATCH_LEVEL)
static VOID
EmergencyDpcRoutine(
    _In_ PKDPC Dpc,
    _In_opt_ PVOID DeferredContext,
    _In_opt_ PVOID SystemArgument1,
    _In_opt_ PVOID SystemArgument2
    )
{
    PDEVICE_EXTENSION devExt = (PDEVICE_EXTENSION)DeferredContext;
    ULONG keyCount;
    BOOLEAN shouldTrigger;

    UNREFERENCED_PARAMETER(Dpc);
    UNREFERENCED_PARAMETER(SystemArgument1);
    UNREFERENCED_PARAMETER(SystemArgument2);

    if (devExt == NULL) {
        return;
    }

    KeAcquireSpinLockAtDpcLevel(&devExt->OutputLock);
    keyCount = g_EmergencyKeyCount; // 単純な読み出し。SetのタイミングとDPCが競合しても
                                     // 「その瞬間のマスクと比べて古い/新しいKeyCount」を
                                     // 使うだけで、最悪もう一度押し直せば確実に発動する。
    shouldTrigger = (keyCount >= KBDLAYER_EMERGENCY_MIN_KEYS) &&
                    (devExt->EmergencyKeysDownMask == (UCHAR)((1U << keyCount) - 1)) &&
                    (KeQueryInterruptTime() >= devExt->EmergencyDeadline);
    KeReleaseSpinLockFromDpcLevel(&devExt->OutputLock);

    if (shouldTrigger) {
        QueueEmergencyReleaseWorkItem();
    }
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeEmergencyReleaseState(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    KeInitializeTimer(&devExt->EmergencyTimer);
    KeInitializeDpc(&devExt->EmergencyDpc, EmergencyDpcRoutine, devExt);
    devExt->EmergencyKeysDownMask = 0;
    devExt->EmergencyDeadline = 0;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
CheckEmergencyRelease(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd
    )
{
    ULONG keyCount;
    USHORT scanCodes[KBDLAYER_EMERGENCY_MAX_KEYS];
    UCHAR fullMask;
    KIRQL oldIrql;

    KeAcquireSpinLock(&g_EmergencyConfigLock, &oldIrql);
    keyCount = g_EmergencyKeyCount;
    RtlCopyMemory(scanCodes, g_EmergencyScanCodes, sizeof(scanCodes));
    KeReleaseSpinLock(&g_EmergencyConfigLock, oldIrql);

    if (keyCount < KBDLAYER_EMERGENCY_MIN_KEYS) {
        return; // 未初期化（InitializeEmergencyReleaseGlobal未呼び出し）
    }
    fullMask = (UCHAR)((1U << keyCount) - 1);

    for (PKEYBOARD_INPUT_DATA p = InputDataStart; p < InputDataEnd; p++) {
        USHORT sc = EncodeScancode16(p->MakeCode, p->Flags);
        BOOLEAN isBreak = (p->Flags & KEY_BREAK) != 0;

        for (ULONG i = 0; i < keyCount; i++) {
            if (scanCodes[i] != sc) {
                continue;
            }

            UCHAR bit = (UCHAR)(1U << i);
            if (isBreak) {
                if (devExt->EmergencyKeysDownMask & bit) {
                    devExt->EmergencyKeysDownMask &= (UCHAR)~bit;
                    KeCancelTimer(&devExt->EmergencyTimer);
                }
            } else if (!(devExt->EmergencyKeysDownMask & bit)) {
                devExt->EmergencyKeysDownMask |= bit;
                if (devExt->EmergencyKeysDownMask == fullMask) {
                    LARGE_INTEGER dueTime;
                    devExt->EmergencyDeadline = KeQueryInterruptTime() +
                        (ULONGLONG)KBDLAYER_EMERGENCY_HOLD_MS * 10000ULL;
                    dueTime.QuadPart = -(LONGLONG)KBDLAYER_EMERGENCY_HOLD_MS * 10000LL;
                    KeSetTimer(&devExt->EmergencyTimer, dueTime, &devExt->EmergencyDpc);
                }
            }
            break; // このイベントは組み合わせのうち1つのキーとしか一致しない
        }
    }
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
LoadPersistedKeyOverridesForDevice(
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

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));

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

    {
        // 判定方式・Quick Tap・TT（2026-10-01追加）。値が無ければ既定値（旧バージョンの永続化データも同じ）。
        // 範囲外の値（手で書き換えた等）は既定値に戻す。
        ULONG mode = KBDLAYER_TAPHOLD_MODE_HOLD_ON_OTHER_KEY;
        ULONG quick = 0;
        ULONG toggle = KBDLAYER_DEFAULT_TAPPING_TOGGLE;
        RegReadDwordEx(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_TAPHOLDMODE_VALUE_NAME, &mode,
                       KBDLAYER_TAPHOLD_MODE_HOLD_ON_OTHER_KEY);
        RegReadDwordEx(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_QUICKTAPTERM_VALUE_NAME, &quick, 0);
        RegReadDwordEx(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_TAPPINGTOGGLE_VALUE_NAME, &toggle,
                       KBDLAYER_DEFAULT_TAPPING_TOGGLE);
        devExt->TapHoldMode = (mode <= KBDLAYER_TAPHOLD_MODE_MAX) ? mode : KBDLAYER_TAPHOLD_MODE_HOLD_ON_OTHER_KEY;
        devExt->QuickTapTermMs = quick;
        devExt->TappingToggle = (toggle >= 1 && toggle <= KBDLAYER_MAX_TAPPING_TOGGLE) ? toggle
                                                                                      : KBDLAYER_DEFAULT_TAPPING_TOGGLE;
    }

    {
        // 機能別ON/OFF（項番4）。値が無ければ全機能有効（既定、旧バージョンの永続化データも同じ）。
        // 2026-10-01以降のドライバは、全ビットをEnabledFeaturesExにも書く。旧ドライバが書いた
        // EnabledFeaturesだけがある場合は、その後に増えた機能（Key Override）を有効として扱う
        // （旧値の7=「全部有効」で、新しい機能が無効になってしまわないように）。
        ULONG features = KBDLAYER_FEATURE_ALL;
        ULONG featuresEx = 0xFFFFFFFFUL; // 値が無いことの印
        RegReadDwordEx(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_FEATURES_EX_VALUE_NAME,
                        &featuresEx, 0xFFFFFFFFUL);
        if (featuresEx != 0xFFFFFFFFUL) {
            features = featuresEx;
        } else {
            RegReadDwordEx(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_FEATURES_VALUE_NAME,
                            &features, KBDLAYER_FEATURE_ALL);
            features = (features & KBDLAYER_FEATURE_ALL_V1) | (KBDLAYER_FEATURE_ALL & ~KBDLAYER_FEATURE_ALL_V1);
        }
        devExt->EnabledFeatures = features & KBDLAYER_FEATURE_ALL;
    }

    LoadPersistedMacrosForDevice(devExt);
    LoadPersistedCombosForDevice(devExt);
    LoadPersistedTapDancesForDevice(devExt);
    LoadPersistedKeyOverridesForDevice(devExt);
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

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    CheckPersistResult(devExt, KBDLAYER_TAPTERM_VALUE_NAME,
        RegWriteDword(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_TAPTERM_VALUE_NAME, effective));

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

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    CheckPersistResult(devExt, KBDLAYER_COMBOTERM_VALUE_NAME,
        RegWriteDword(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_COMBOTERM_VALUE_NAME, effective));

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

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    CheckPersistResult(devExt, KBDLAYER_TAPDANCETERM_VALUE_NAME,
        RegWriteDword(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_TAPDANCETERM_VALUE_NAME, effective));

    return STATUS_SUCCESS;
}

// SET_CONFIG v2: 判定方式・Quick Tap・TTの回数を変え、永続化する。範囲外ならSTATUS_INVALID_PARAMETER
// （何も変えない）。TappingToggle=0は既定値。方式を変えたときに判定中のMT/LTがあっても、その判定は
// 新しい方式で続く（溜めたイベントは、確定したときに流し直される）。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetTapHoldOptionsForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG TapHoldMode,
    _In_ ULONG QuickTapTermMs,
    _In_ ULONG TappingToggle
    )
{
    KIRQL oldIrql;
    WCHAR subKeyPath[64];
    ULONG toggle = (TappingToggle != 0) ? TappingToggle : KBDLAYER_DEFAULT_TAPPING_TOGGLE;

    if (TapHoldMode > KBDLAYER_TAPHOLD_MODE_MAX || toggle > KBDLAYER_MAX_TAPPING_TOGGLE) {
        return STATUS_INVALID_PARAMETER;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    devExt->TapHoldMode = TapHoldMode;
    devExt->QuickTapTermMs = QuickTapTermMs;
    devExt->TappingToggle = toggle;
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    CheckPersistResult(devExt, KBDLAYER_TAPHOLDMODE_VALUE_NAME,
        RegWriteDword(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_TAPHOLDMODE_VALUE_NAME, TapHoldMode));
    CheckPersistResult(devExt, KBDLAYER_QUICKTAPTERM_VALUE_NAME,
        RegWriteDword(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_QUICKTAPTERM_VALUE_NAME, QuickTapTermMs));
    CheckPersistResult(devExt, KBDLAYER_TAPPINGTOGGLE_VALUE_NAME,
        RegWriteDword(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_TAPPINGTOGGLE_VALUE_NAME, toggle));

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
    devExt->TapHoldMode = KBDLAYER_TAPHOLD_MODE_HOLD_ON_OTHER_KEY;
    devExt->QuickTapTermMs = 0;
    devExt->TappingToggle = KBDLAYER_DEFAULT_TAPPING_TOGGLE;
    devExt->HoldBufferCount = 0;
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

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    CheckPersistResult(devExt, KBDLAYER_MACROS_VALUE_NAME,
        RegWriteBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_MACROS_VALUE_NAME,
                        buffer, KBDLAYER_MACRO_PERSIST_BLOB_BYTES));

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

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    status = RegReadBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_MACROS_VALUE_NAME,
                            buffer, KBDLAYER_MACRO_PERSIST_BLOB_BYTES, &actualLength);
    // 旧サイズ（32個分）なら先頭32個として読む（MacroPersistCountFromLength参照）。
    ULONG persistedCount = NT_SUCCESS(status) ? MacroPersistCountFromLength(actualLength) : 0;
    if (persistedCount > 0) {
        for (ULONG i = 0; i < persistedCount; i++) {
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
    // actualLengthが受け付けるサイズでない場合（値が存在しない、または将来の
    // 互換性が壊れた場合）は何もしない。AddDeviceで
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

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    CheckPersistResult(devExt, KBDLAYER_COMBOS_VALUE_NAME,
        RegWriteBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_COMBOS_VALUE_NAME,
                        buffer, KBDLAYER_COMBO_PERSIST_BLOB_BYTES));

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
        // 出力はKEY・MODDED_KEY（修飾子マスクとキーが0でない）・MACRO（IDが範囲内）。2026-10-02にKEYのみから拡張
        // （common/kbdlayer_ioctl.h参照）。
        switch (Entry->OutputAction.Type) {
        case KBDLAYER_ACTION_KEY:
            break;
        case KBDLAYER_ACTION_MODDED_KEY:
            if (Entry->OutputAction.Param1 == 0 || Entry->OutputAction.Param2 == 0) {
                return STATUS_INVALID_PARAMETER;
            }
            break;
        case KBDLAYER_ACTION_MACRO:
            if (Entry->OutputAction.Param1 >= KBDLAYER_MAX_MACRO_COUNT || Entry->OutputAction.Param2 != 0) {
                return STATUS_INVALID_PARAMETER;
            }
            break;
        default:
            return STATUS_INVALID_PARAMETER;
        }
    }

    releases.Count = 0;
    releases.ReplayCount = 0;

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

// ---------------------------------------------------------------------------
// Key Override（2026-10-01）の表の設定・取得・永続化。押下時の処理はResolveModdedKey。
// ---------------------------------------------------------------------------

// devExt->KeyOverrides/KeyOverrideUsed全体を1つのblobへシリアライズしてレジストリへ書き込む
// （PersistAllCombosと同じ。ベストエフォート）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
PersistAllKeyOverrides(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    WCHAR subKeyPath[64];
    PKBDLAYER_KEY_OVERRIDE_PERSIST_ENTRY buffer;
    KIRQL oldIrql;

    buffer = (PKBDLAYER_KEY_OVERRIDE_PERSIST_ENTRY)ExAllocatePool2(
        POOL_FLAG_NON_PAGED, KBDLAYER_KEY_OVERRIDE_PERSIST_BLOB_BYTES, KBDLAYER_REGTEMP_POOL_TAG);
    if (buffer == NULL) {
        return;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    for (ULONG i = 0; i < KBDLAYER_MAX_KEY_OVERRIDE_COUNT; i++) {
        buffer[i].Used = devExt->KeyOverrideUsed[i] ? 1 : 0;
        buffer[i].Entry = devExt->KeyOverrides[i];
    }
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    CheckPersistResult(devExt, KBDLAYER_KEYOVERRIDES_VALUE_NAME,
        RegWriteBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_KEYOVERRIDES_VALUE_NAME,
                        buffer, KBDLAYER_KEY_OVERRIDE_PERSIST_BLOB_BYTES));

    ExFreePoolWithTag(buffer, KBDLAYER_REGTEMP_POOL_TAG);
}

// Key Overrideの1件が正しい形か（F-3）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static BOOLEAN
IsValidKeyOverrideEntry(
    _In_ const KBDLAYER_KEY_OVERRIDE_ENTRY* Entry
    )
{
    if (Entry->TriggerMods == 0 || Entry->Flags != 0 || Entry->TriggerScanCode == 0) {
        return FALSE;
    }
    switch (Entry->Replacement.Type) {
    case KBDLAYER_ACTION_KEY:
        return Entry->Replacement.Param2 != 0;
    case KBDLAYER_ACTION_MODDED_KEY:
        return Entry->Replacement.Param1 != 0 && Entry->Replacement.Param2 != 0;
    default:
        return FALSE;
    }
}

// SET_KEY_OVERRIDE: Entry=NULLならそのIdをクリアする。置き換え中のキーは、押下記録に置き換え先を
// 持っているので、表を書き換えても解放は記録どおりに処理される（打ち切りは不要）。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetKeyOverrideForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG Id,
    _In_opt_ PKBDLAYER_KEY_OVERRIDE_ENTRY Entry
    )
{
    KIRQL outputIrql;
    ULONG count = 0;

    if (Id >= KBDLAYER_MAX_KEY_OVERRIDE_COUNT) {
        return STATUS_INVALID_PARAMETER;
    }
    if (Entry != NULL && !IsValidKeyOverrideEntry(Entry)) {
        return STATUS_INVALID_PARAMETER;
    }

    // KeyOverrides/KeyOverrideCountはOutputLockとEngineLockの両方を保持して書く。
    KeAcquireSpinLock(&devExt->OutputLock, &outputIrql);
    KeAcquireSpinLockAtDpcLevel(&devExt->EngineLock);
    if (Entry != NULL) {
        devExt->KeyOverrides[Id] = *Entry;
        devExt->KeyOverrideUsed[Id] = TRUE;
    } else {
        RtlZeroMemory(&devExt->KeyOverrides[Id], sizeof(devExt->KeyOverrides[Id]));
        devExt->KeyOverrideUsed[Id] = FALSE;
    }
    for (ULONG i = 0; i < KBDLAYER_MAX_KEY_OVERRIDE_COUNT; i++) {
        if (devExt->KeyOverrideUsed[i]) {
            count++;
        }
    }
    devExt->KeyOverrideCount = count;
    KeReleaseSpinLockFromDpcLevel(&devExt->EngineLock);
    KeReleaseSpinLock(&devExt->OutputLock, outputIrql);

    PersistAllKeyOverrides(devExt);
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
GetKeyOverrideForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG Id,
    _Out_ PKBDLAYER_KEY_OVERRIDE_ENTRY OutEntry,
    _Out_ PBOOLEAN OutUsed
    )
{
    KIRQL oldIrql;

    if (Id >= KBDLAYER_MAX_KEY_OVERRIDE_COUNT) {
        return STATUS_INVALID_PARAMETER;
    }

    KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
    *OutEntry = devExt->KeyOverrides[Id];
    *OutUsed = devExt->KeyOverrideUsed[Id];
    KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
    return STATUS_SUCCESS;
}

// AddDeviceから呼ばれる: レジストリの"KeyOverrides"値を読む（LoadPersistedCombosForDeviceと同じ理屈で、
// ロック無しに直接書き込む）。形の正しくないエントリ（手で書き換えた等）は読み込まない。
_IRQL_requires_max_(PASSIVE_LEVEL)
static VOID
LoadPersistedKeyOverridesForDevice(
    _In_ PDEVICE_EXTENSION devExt
    )
{
    WCHAR subKeyPath[64];
    PKBDLAYER_KEY_OVERRIDE_PERSIST_ENTRY buffer;
    ULONG actualLength = 0;
    NTSTATUS status;

    buffer = (PKBDLAYER_KEY_OVERRIDE_PERSIST_ENTRY)ExAllocatePool2(
        POOL_FLAG_PAGED, KBDLAYER_KEY_OVERRIDE_PERSIST_BLOB_BYTES, KBDLAYER_REGTEMP_POOL_TAG);
    if (buffer == NULL) {
        return;
    }
    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    status = RegReadBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_KEYOVERRIDES_VALUE_NAME,
                            buffer, KBDLAYER_KEY_OVERRIDE_PERSIST_BLOB_BYTES, &actualLength);
    if (NT_SUCCESS(status) && actualLength == KBDLAYER_KEY_OVERRIDE_PERSIST_BLOB_BYTES) {
        ULONG count = 0;
        for (ULONG i = 0; i < KBDLAYER_MAX_KEY_OVERRIDE_COUNT; i++) {
            BOOLEAN used = (buffer[i].Used != 0) && IsValidKeyOverrideEntry(&buffer[i].Entry);
            devExt->KeyOverrideUsed[i] = used;
            if (used) {
                devExt->KeyOverrides[i] = buffer[i].Entry;
                count++;
            } else {
                RtlZeroMemory(&devExt->KeyOverrides[i], sizeof(devExt->KeyOverrides[i]));
            }
        }
        devExt->KeyOverrideCount = count;
    }
    ExFreePoolWithTag(buffer, KBDLAYER_REGTEMP_POOL_TAG);
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
        BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
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

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
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

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    CheckPersistResult(devExt, KBDLAYER_TAPDANCES_VALUE_NAME,
        RegWriteBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_TAPDANCES_VALUE_NAME,
                        buffer, KBDLAYER_TAPDANCE_PERSIST_BLOB_BYTES));

    ExFreePoolWithTag(buffer, KBDLAYER_REGTEMP_POOL_TAG);
}

// タップダンスのタップ側（SingleTap/DoubleTap/TripleTap）に置けるアクションか。
// NONE・KEY（スキャンコード0は動作なし）、MODDED_KEY（修飾子マスクが0でない）、MACRO（IDが範囲内）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static BOOLEAN
IsValidTapDanceTapAction(
    _In_ const KBDLAYER_ACTION* Action
    )
{
    switch (Action->Type) {
    case KBDLAYER_ACTION_NONE: // 動作なし（SingleTapのNONEはSET時に「クリア」の意味になるので、
                               // ツールはSingleTapの動作なしをKEY・スキャンコード0で送る）
    case KBDLAYER_ACTION_KEY:  // スキャンコード0（KC_NO）は動作なし
        return TRUE;
    case KBDLAYER_ACTION_MODDED_KEY:
        return Action->Param1 != 0;
    case KBDLAYER_ACTION_MACRO:
        return Action->Param1 < KBDLAYER_MAX_MACRO_COUNT && Action->Param2 == 0;
    default:
        return FALSE;
    }
}

// タップダンスの長押し（SingleHold）に置けるアクションか（F-3）。
// NONE・KEY（スキャンコード0は動作なし）・MODTAP・LAYERTAP、2026-10-02から MODDED_KEY（修飾子マスクが0でない）・
// LAYER_MOD（レイヤーが範囲内、修飾子マスクが0でない）・MACRO（IDが範囲内）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static BOOLEAN
IsValidTapDanceHoldAction(
    _In_ const KBDLAYER_ACTION* Action
    )
{
    switch (Action->Type) {
    case KBDLAYER_ACTION_NONE:
    case KBDLAYER_ACTION_KEY:
    case KBDLAYER_ACTION_MODTAP:
    case KBDLAYER_ACTION_LAYERTAP:
        return TRUE;
    case KBDLAYER_ACTION_MODDED_KEY:
        return Action->Param1 != 0;
    case KBDLAYER_ACTION_LAYER_MOD:
        return Action->Param1 < KBDLAYER_MAX_LAYERS && (Action->Param2 & 0xFF00) == 0 && (Action->Param2 & 0x00FF) != 0;
    case KBDLAYER_ACTION_MACRO:
        return Action->Param1 < KBDLAYER_MAX_MACRO_COUNT && Action->Param2 == 0;
    default:
        return FALSE;
    }
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
        // SingleTap/DoubleTap/TripleTapはKEY/MODDED_KEY/MACRO、SingleHoldはKEY/MODTAP/LAYERTAPを
        // 許可する（common/kbdlayer_ioctl.h参照。F-3: 引数も検証する）。
        if (!IsValidTapDanceTapAction(&Entry->SingleTapAction) ||
            !IsValidTapDanceTapAction(&Entry->DoubleTapAction) ||
            !IsValidTapDanceTapAction(&Entry->TripleTapAction)) {
            return STATUS_INVALID_PARAMETER;
        }
        // SingleHoldのNONE（とKEYのスキャンコード0）は「長押しの動作なし」: 長押ししても何も出さない。
        if (!IsValidTapDanceHoldAction(&Entry->SingleHoldAction)) {
            return STATUS_INVALID_PARAMETER;
        }
    }

    releases.Count = 0;
    releases.ReplayCount = 0;

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
        BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
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

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
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
    Out->TapHoldMode = devExt->TapHoldMode;
    Out->QuickTapTermMs = devExt->QuickTapTermMs;
    Out->TappingToggle = devExt->TappingToggle;
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
    // 物理キーではなく、実際に送った出力で見る（ForwardToClass参照）。
    // レイヤーマップでCtrlにしたキーやMTのホールド、修飾付きキー・LMで足した修飾キーも
    // 対象になり、物理Ctrlを別のキーにした場合は対象外になる。ServiceCallbackはOutputLockを
    // 保持しているので読める（AddedModCountはEngineLockでも保護されている）。
    if (kind != NICOLA_KEY_OTHER && (devExt->RealModsDown | AddedModsUnion(devExt)) != 0) {
        kind = NICOLA_KEY_OTHER;
    }
    // 修飾付きキー・LMとして押されているキーの解放・オートリピートは、NICOLAが取らずに
    // 後段（ResolveModdedKey）へ渡す（足した修飾キーを必ず戻すため）。
    {
        PKBDLAYER_HELD_KEY held = HeldKeyFor(devExt, scanCode);
        if (held->State == KBDLAYER_HELD_ACTION && IsModdedActionType(held->Action.Type)) {
            kind = NICOLA_KEY_OTHER;
        }
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
    devExt->RealModsDown = 0;
    devExt->OutputModsDown = 0;
    devExt->LastOutputWasModifier = FALSE;
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

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
    CheckPersistResult(devExt, KBDLAYER_NICOLA_VALUE_NAME,
        RegWriteBinary(KBDLAYER_SERVICE_NAME, subKeyPath, KBDLAYER_NICOLA_VALUE_NAME,
                        BlobLength > 0 ? Blob : NULL, BlobLength));

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

    BuildLayerMapsSubKeyPath(devExt, subKeyPath, RTL_NUMBER_OF(subKeyPath));
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

    // このバッチ・DPCでCUSTOMキー（ヘルパーイベント）が積まれていれば、待っているアプリへ渡す。
    // LayerNotifyIfChangedは、キーを処理しうるすべての経路の末尾（ロックの外）で呼ばれるので、
    // ここに相乗りする。
    HelperNotifyFlushIfNeeded();

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

    // レイヤー数は常にKBDLAYER_MAX_LAYERS（LayerCountフィールド廃止）なので、0〜15の
    // どのレイヤーも常に指定できる（ヘルパーアプリがVIAL的に動的切替する対象を、静的な
    // キーマップの定義済みレイヤー数に制限しないための望ましい副作用でもある）。
    layerCount = KBDLAYER_MAX_LAYERS;
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
