// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

#include <ntddk.h>
#include <wdm.h>
#include <ntstrsafe.h>
#include "DeviceExtension.h"
#include "LayerEngine.h"
#include "..\\common\\regutil.h"

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
#define KBDLAYER_REGTEMP_POOL_TAG 'tLdK'

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
            if (type > KBDLAYER_ACTION_LAYERTAP) {
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
        // OSM/MT/LT等はPhase 2以降で実装する。Phase 1のエンジンに来た場合は
        // 安全側に倒して「何もしない」として扱う（ParseLayerMapBlobで型自体は
        // 許可しているため、KeymapTool側が未対応アクションを送ってきても
        // クラッシュしない）。
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
    devExt->TapTermMs = KBDLAYER_DEFAULT_TAP_TERM_MS;
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
