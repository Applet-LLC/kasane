// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

/*
  kbdlayer/LayerEngine.h

  レイヤーエンジンの中核ロジック:
  - Phase 1: 基本キーコード + レイヤー切替 MO/TG/DF/OSL + 修飾キー
  - Phase 2: Mod-Tap(MT)/Layer-Tap(LT)（Hold On Other Key Press方式、
    ResolveTapHold。phase2-spikeブランチでの実機検証を踏まえた実装）
  - blob（KBDLAYER_MAP_HEADER形式、common/kbdlayer_ioctl.h）の検証・
    内部形式（KBDLAYER_MAP_TABLE, DeviceExtension.h）への変換、および逆変換
  - ServiceCallbackのホットパスで呼ばれる ResolveTapHold / ResolveLayerAction
  - コントロールデバイスのIOCTLハンドラ（ControlDevice.c）から呼ばれる
    Set/Clear/SetEnabled/Get/SetConfig
  - AddDeviceから呼ばれるレジストリからの永続化ロードと、タイマー/DPCの初期化
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
// （キャリブレーション用のキャプチャ記録はResolveCalibrationが担当する。）
// IRQL: <= DISPATCH_LEVEL。非ページメモリのみ使用、ブロッキング無し。
_IRQL_requires_max_(DISPATCH_LEVEL)
PKEYBOARD_INPUT_DATA
ResolveLayerAction(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd
    );

// キャリブレーション（CAPTURE_NEXT_KEY / SET_CAPTURE_SUPPRESS）の処理。
// Driver.cのServiceCallbackで、ResolveCombo等のどのResolve*よりも先に呼ぶこと
// （レイヤーマップON中にMT/コンボ等がイベントを横取りしても、キャプチャ記録を
// 取りこぼさないため）。
// - devExt->CaptureActive==TRUEなら最初の押下イベントのスキャンコードを記録して
//   CaptureEventをセットする。E1シーケンス（Pause）の2イベント目と、E0付きの
//   疑似Shift（E0 2A / E0 36）は記録対象から外す。
// - devExt->SuppressActive==TRUEなら押下イベントを捨て、それと対になる解放も捨てる
//   （抑止開始前から押されていたキーの解放は通す）。アイドルタイムアウトの判定もここで行う。
// 戻り値は捨てた分を詰めた新しいEnd（何も捨てなければInputDataEndのまま。
// 全件捨てた場合はInputDataStart）。
// IRQL: <= DISPATCH_LEVEL。
_IRQL_requires_max_(DISPATCH_LEVEL)
PKEYBOARD_INPUT_DATA
ResolveCalibration(
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

// Phase 2: Mod-Tap(MT)/Layer-Tap(LT)のタップ/ホールド判定（Hold On Other Key
// Press方式）。Driver.cのServiceCallbackから、ResolveLayerActionより前に呼ぶこと。
// 単一イベントのバッチのみを対象とする（複数件バッチは素通しに倒す。理由は
// LayerEngine.cの実装コメント参照）。
// 戻り値:
//   TRUE  = このイベントバッチはこの関数が処理済み。呼び出し元は通常の
//           ResolveLayerAction/OriginalClassService呼び出しを行わずそのまま
//           ServiceCallbackから戻ってよい。
//   FALSE = このイベントバッチは対象外（LayerMap未設定/無効、MT/LT以外の
//           通常キー、複数件バッチ等）。呼び出し元は通常通り処理を続ける。
// IRQL: <= DISPATCH_LEVEL。EngineLock保持中はOriginalClassServiceを呼ばない
// （既存のホットパスの規律を踏襲）。
_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
ResolveTapHold(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _Inout_opt_ PVOID SystemArgument3
    );

// AddDeviceから呼ばれる: devExt->HoldTimer/HoldDpcを初期化し、TapTermMsを
// 既定値にする（レジストリからの永続化読み込みはLoadPersistedLayerMapForDevice側で
// 上書きする）。devExtがまだg_deviceListに公開されていない前提。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeTapHoldState(
    _In_ PDEVICE_EXTENSION devExt
    );

// Phase 3: マクロ(MACRO(id))の実行。Driver.cのServiceCallbackから、
// ResolveTapHoldの後・ResolveLayerActionの前に呼ぶこと。MT/LTのような時間的な
// 曖昧さが無いため、押下イベントを受け取ったその場でマクロの各ステップぶん
// OriginalClassServiceを逐次呼ぶ。単一イベントのバッチのみを対象とする
// （ResolveTapHoldと同じ制約）。
// 戻り値: ResolveTapHoldと同じ意味（TRUE=処理済み、FALSE=対象外）。
_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
ResolveMacro(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _Inout_opt_ PVOID SystemArgument3
    );

// Phase 4: コンボ（2キー限定 v1）。Driver.cのServiceCallbackから、
// ResolveTapHold/ResolveMacroより前に呼ぶこと（物理キー単位の判定を、レイヤーや
// タップ/ホールドの解釈より先に済ませるため）。単一イベントのバッチのみを対象と
// する（ResolveTapHold/ResolveMacroと同じ制約）。
// 戻り値: ResolveTapHoldと同じ意味（TRUE=処理済み、FALSE=対象外）。
// 割り込みキーによる不成立の場合、保留していたキーのpressだけをこの関数内で
// 復元送出した上でFALSEを返す（呼び出し元の今回のイベント自体は通常経路に委ねる）。
_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
ResolveCombo(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _Inout_opt_ PVOID SystemArgument3
    );

// AddDeviceから呼ばれる: devExt->ComboTimer/ComboDpcを初期化し、ComboTermMsを
// 既定値にする（レジストリからの永続化読み込みはLoadPersistedLayerMapForDevice側で
// 上書きする）。devExtがまだg_deviceListに公開されていない前提。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeComboState(
    _In_ PDEVICE_EXTENSION devExt
    );

// Phase 5: タップダンス（v1: タップ回数3回まで、ホールドは1回目の押下のみ）。
// Driver.cのServiceCallbackから、ResolveTapHoldの後・ResolveMacroの前に呼ぶこと
// （TapDanceは1つの物理キーの振る舞いを扱うため、MT/LT・Macroと同じグループに
// 位置づける）。単一イベントのバッチのみを対象とする（他のResolve*と同じ制約）。
// 戻り値: ResolveTapHoldと同じ意味（TRUE=処理済み、FALSE=対象外）。
// 他キー割り込みによる確定の場合、Phase 2のHold On Other Key Pressと同じ方式
// （確定イベントを合成送出したあと、割り込みバッチをそのまま生スキャンコードで
// 転送する）を踏襲する。
_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
ResolveTapDance(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _Inout_opt_ PVOID SystemArgument3
    );

// AddDeviceから呼ばれる: devExt->TapDanceTimer/TapDanceDpcを初期化し、TapDanceTermMsを
// 既定値にする（レジストリからの永続化読み込みはLoadPersistedLayerMapForDevice側で
// 上書きする）。devExtがまだg_deviceListに公開されていない前提。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeTapDanceState(
    _In_ PDEVICE_EXTENSION devExt
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

// SET_CONFIG: devExt->TapTermMsを変更し、レジストリの"TapTermMs"値へ永続化する。
// TapTermMs=0を渡すと既定値(KBDLAYER_DEFAULT_TAP_TERM_MS)にリセットする。
// LayerMapEnabledの状態に関わらず常に成功する（TapTermMsはMT/LTの判定パラメータで
// あり、レイヤーマップ本体とは独立した設定のため）。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetTapTermForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG TapTermMs
    );

// SET_CONFIG (Phase 4分): devExt->ComboTermMsを変更し、レジストリの"ComboTermMs"値へ
// 永続化する。ComboTermMs=0を渡すと既定値(KBDLAYER_DEFAULT_COMBO_TERM_MS)にリセットする。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetComboTermForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG ComboTermMs
    );

// SET_CONFIG (Phase 5分): devExt->TapDanceTermMsを変更し、レジストリの
// "TapDanceTermMs"値へ永続化する。TapDanceTermMs=0を渡すと既定値
// (KBDLAYER_DEFAULT_TAPDANCE_TERM_MS)にリセットする。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetTapDanceTermForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG TapDanceTermMs
    );

// SET_MACRO: 指定したMacroIdのステップ列を設定する。StepCount=0はそのマクロIDの
// クリアを意味する。LayerMapEnabledの状態に関わらず常に成功する（TapTermMsと同じ
// 理由）。ライブ反映後、レジストリの"Macros"値へ永続化する（全マクロスロットを
// まとめて1つのblobとして保存する）。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetMacroForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG MacroId,
    _In_reads_opt_(StepCount) PKBDLAYER_MACRO_STEP Steps,
    _In_ ULONG StepCount
    );

// GET_MACRO: 指定したMacroIdの現在のステップ列（OutBuffer、KBDLAYER_MACRO_STEPの
// 生配列のみ。ヘッダは呼び出し元のControlDevice.cが組み立てる）とステップ数
// （OutStepCount）を返す。未設定の場合は *BytesReturned=0, *OutStepCount=0で成功する。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
GetMacroForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG MacroId,
    _Out_writes_bytes_to_opt_(OutBufferLength, *BytesReturned) PVOID OutBuffer,
    _In_ ULONG OutBufferLength,
    _Out_ PULONG BytesReturned,
    _Out_ PULONG OutStepCount
    );

// SET_COMBO: 指定したComboIdのメンバー2キー+出力アクションを設定する。
// Entry=NULLはそのComboIdのクリアを意味する。バリデーション（メンバー2キーが
// 異なること・出力がKEY型のみ・他のコンボと物理スキャンコードが重複しないこと）は
// この関数内で行う。ライブ反映後、レジストリの"Combos"値へ永続化する（全コンボ
// スロットをまとめて1つのblobとして保存する、マクロと同じ方式）。
// 設定変更と整合しない状態を残さないよう、ライブのコンボ判定状態
// （ComboPendingActive等）は常にリセットする。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetComboForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG ComboId,
    _In_opt_ PKBDLAYER_COMBO_ENTRY Entry
    );

// GET_COMBO: 指定したComboIdの現在の内容(*OutEntry)と使用中かどうか(*OutUsed)を返す。
// 未設定の場合は*OutUsed=FALSEで成功する（*OutEntryの内容は不定）。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
GetComboForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG ComboId,
    _Out_ PKBDLAYER_COMBO_ENTRY OutEntry,
    _Out_ PBOOLEAN OutUsed
    );

// SET_TAPDANCE: 指定したTapDanceIdのSingleTap/SingleHold/DoubleTap/TripleTapアクションを
// 設定する。Entry=NULLはそのTapDanceIdのクリアを意味する。バリデーション
// （SingleTap/DoubleTap/TripleTapはKEY型のみ・SingleHoldはKEY/MODTAP/LAYERTAPのみ）は
// この関数内で行う。ライブ反映後、レジストリの"TapDances"値へ永続化する（全スロットを
// まとめて1つのblobとして保存する、Macro/Comboと同じ方式）。設定変更と整合しない
// 状態を残さないよう、ライブのタップダンス判定状態は常にリセットする。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetTapDanceForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG TapDanceId,
    _In_opt_ PKBDLAYER_TAPDANCE_ENTRY Entry
    );

// GET_TAPDANCE: 指定したTapDanceIdの現在の内容(*OutEntry)と使用中かどうか(*OutUsed)を返す。
// 未設定の場合は*OutUsed=FALSEで成功する（*OutEntryの内容は不定）。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
GetTapDanceForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG TapDanceId,
    _Out_ PKBDLAYER_TAPDANCE_ENTRY OutEntry,
    _Out_ PBOOLEAN OutUsed
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

// SET_CAPTURE_SUPPRESS: 入力抑止の有効化/解除。OwnerはIOCTLを発行したハンドルの
// FILE_OBJECT（IRP_MJ_CLEANUP時にReleaseCaptureSuppressForOwnerで一括解除するための鍵）。
// 別のOwnerが抑止中のデバイスに対する有効化はSTATUS_DEVICE_BUSY、解除は
// STATUS_ACCESS_DENIED。既に解除済みのデバイスに対する解除は冪等に成功する。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetCaptureSuppressForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ BOOLEAN Enable,
    _In_ PVOID Owner
    );

// コントロールデバイスのハンドルがクローズされた（IRP_MJ_CLEANUP）ときに呼ぶ。
// そのハンドルが有効化した全デバイスの入力抑止を解除する。
_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
ReleaseCaptureSuppressForOwner(
    _In_ PVOID Owner
    );

// GET_CONFIG: TapTermMs/ComboTermMs/TapDanceTermMsの現在の実効値を返す。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
GetConfigForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _Out_ PKBDLAYER_GET_CONFIG_OUTPUT Out
    );

#endif // _KBDLAYER_LAYERENGINE_H_
