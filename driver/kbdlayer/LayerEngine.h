// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

/*
  kbdlayer/LayerEngine.h

  レイヤーエンジンの中核ロジック（Phase 1: 基本キーコード + レイヤー切替
  MO/TG/DF/OSL + 修飾キー。Mod-Tap/Layer-Tapは Phase 2 で追加）:
  - blob（KBDLAYER_MAP_HEADER形式、common/kbdlayer_ioctl.h）の検証・
    内部形式（KBDLAYER_MAP_TABLE, DeviceExtension.h）への変換、および逆変換
  - ServiceCallbackのホットパスで呼ばれる ResolveLayerAction
  - コントロールデバイスのIOCTLハンドラ（ControlDevice.c）から呼ばれる
    Set/Clear/SetEnabled/Get
  - AddDeviceから呼ばれるレジストリからの永続化ロード
  - キャリブレーションウィザード用のキー捕捉（Start/Cancel/Consume）

  kbdaddid/RemapTable.c からのフォーク。1:1スキャンコード置換は「レイヤー0での
  KEYアクション」の特殊ケースとして表現できるため、独立機能としては残さず
  こちらへ一本化した（.plans/perplexity-c-users-applet-documents-git-parallel-mochi.md
  の「統合・再設計」節を参照）。CompactInputDataArray のようなホットパスの
  配列詰め直し構造はそのままコピーして再利用している。
*/

#ifndef _KBDLAYER_LAYERENGINE_H_
#define _KBDLAYER_LAYERENGINE_H_

#include "DeviceExtension.h"

// --- ホットパス（ServiceCallbackから呼ばれる） ---

// [InputDataStart, InputDataEnd) を devExt->LayerMap / devExt->LayerMapEnabled /
// devExt->ActiveLayerMask / devExt->DefaultLayer に従ってその場で書き換え、
// MO/TG/DF/OSLのように出力すべきでないエントリを詰めた新しいEndポインタを返す。
// OFF・未設定時は何もせずInputDataEndをそのまま返す。
// このタイミングでActiveLayerMask/DefaultLayer/OneShotLayerPendingも更新される。
// また、devExt->CaptureActive==TRUEの間は最初に見つかった押下イベントの
// スキャンコードをdevExt->CapturedScanCodeへ記録し、devExt->CaptureEventをセットする
// （キャリブレーションウィザード用。レイヤーマップの適用有無とは独立に動作する）。
// IRQL: <= DISPATCH_LEVEL。非ページメモリのみ使用、ブロッキング無し。
_IRQL_requires_max_(DISPATCH_LEVEL)
PKEYBOARD_INPUT_DATA
ResolveLayerAction(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd
    );

// 汎用の「配列詰め直し」ヘルパー。ShouldDropAndTransformがTRUEを返した
// エントリを配列から取り除き、残りを前方に詰める。戻り値は新しいEnd。
// kbdaddid/RemapTable.c の CompactInputDataArray と同一実装（Phase 3の
// コンボ機能等でも同じ仕組みを再利用する想定）。
typedef
_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
(*PKBDLAYER_SHOULD_DROP_ENTRY)(
    _In_ PDEVICE_EXTENSION devExt,
    _Inout_ PKEYBOARD_INPUT_DATA Entry,
    _In_opt_ PVOID Context
    );

_IRQL_requires_max_(DISPATCH_LEVEL)
PKEYBOARD_INPUT_DATA
CompactInputDataArray(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _In_ PKBDLAYER_SHOULD_DROP_ENTRY ShouldDropAndTransform,
    _In_opt_ PVOID Context
    );

// --- IOCTLハンドラ（ControlDevice.c）から呼ばれる、PASSIVE_LEVEL専用 ---

// SET_LAYERMAP: devExt->LayerMapEnabled == FALSE のときだけ成功する
// （ONの間はSTATUS_INVALID_DEVICE_STATE）。成功してもLayerMapEnabledは変更しない。
// ライブ反映後、レジストリの"LayerMap"値へ永続化する。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetLayerMapForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_reads_bytes_(BlobLength) PVOID Blob,
    _In_ ULONG BlobLength
    );

// CLEAR_LAYERMAP: devExt->LayerMapEnabled == FALSE のときだけ成功する
// （ONの間はSTATUS_INVALID_DEVICE_STATE）。LayerMapをNULLにし、
// レジストリの"LayerMap"値を削除する。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
ClearLayerMapForDevice(
    _In_ PDEVICE_EXTENSION devExt
    );

// SET_LAYERMAP_ENABLED: Enabled=TRUEはLayerMap != NULLのときだけ成功する
// （マップ未設定ならSTATUS_INVALID_DEVICE_STATE）。Enabled=FALSEは常に成功する。
// どちらも冪等。ライブ反映後、レジストリの"Enabled"値へ永続化する。
// OFFへ切り替える際はActiveLayerMask/DefaultLayer/OneShotLayerPendingもリセットする
// （素通しに戻すときにレイヤー状態が残ってしまうと、次にONにしたときの挙動が
// 直前の状態に依存してしまうため）。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetLayerMapEnabledForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ BOOLEAN Enabled
    );

// GET_LAYERMAP: 現在のblob（SETで渡したのと同じバイト列）とEnabled状態を返す。
// マップ未設定の場合は *BytesReturned=0, *OutEnabled=FALSE で成功する。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
GetLayerMapForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _Out_writes_bytes_to_opt_(OutBufferLength, *BytesReturned) PVOID OutBuffer,
    _In_ ULONG OutBufferLength,
    _Out_ PULONG BytesReturned,
    _Out_opt_ PBOOLEAN OutEnabled
    );

// AddDeviceから呼ばれる: レジストリの"LayerMap"/"Enabled"を読み、
// devExt->LayerMap / devExt->LayerMapEnabled を初期化する（devExtはまだ
// g_deviceListに公開されておらず、他スレッドからの競合が無い前提でロック無しに
// 直接書き込む）。失敗時はLayerMap=NULL・LayerMapEnabled=FALSEのままでよい
// （ベストエフォート）。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
LoadPersistedLayerMapForDevice(
    _In_ PDEVICE_EXTENSION devExt
    );

// --- キャリブレーションウィザード（ControlDevice.cのCAPTURE_NEXT_KEYハンドラから） ---

// 捕捉待機を開始する。既に別の捕捉が進行中ならSTATUS_DEVICE_BUSYを返す。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
BeginCaptureNextKey(
    _In_ PDEVICE_EXTENSION devExt
    );

// 捕捉待機を中止する（タイムアウト時にControlDevice.cから呼ばれる）。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
CancelCaptureNextKey(
    _In_ PDEVICE_EXTENSION devExt
    );

// 捕捉されたスキャンコードを取り出す（CaptureEventがシグナルされた後に呼ぶこと）。
_IRQL_requires_max_(PASSIVE_LEVEL)
USHORT
ConsumeCapturedScanCode(
    _In_ PDEVICE_EXTENSION devExt
    );

#endif // _KBDLAYER_LAYERENGINE_H_
