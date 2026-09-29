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
// - devExt->CaptureActive==TRUEなら最初の押下イベントのスキャンコードを記録し、
//   保留中のCAPTURE_NEXT_KEYのIRPを（ロック解放後に）完了させる。E1シーケンス（Pause）の
//   2イベント目と、E0付きの疑似Shift（E0 2A / E0 36）は記録対象から外す。
// - devExt->SuppressActive==TRUEなら押下イベントを捨て、それと対になる解放も捨てる
//   （抑止開始前から押されていたキーの解放は通す。押下を捨てたキーの解放は、抑止の解除後に
//   届いても捨てる）。アイドルタイムアウトの判定もここで行う。
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

// Phase 6: NICOLA（親指シフト）の同時打鍵判定。Driver.cのServiceCallbackから、
// ResolveComboより前に呼ぶこと（判定が有効な間、文字キー・親指キーは物理キー単位で
// ここが先に受け取る）。単一イベントのバッチのみを対象とする。判定が有効なときの
// 複数件バッチは、呼び出し元が1件ずつに分けてから渡すこと（NicolaIsActive参照）。
// 戻り値: ResolveTapHoldと同じ意味（TRUE=処理済み、FALSE=対象外）。
// 対象外のキーが保留中の判定に割り込んだ場合は、保留分を確定送出した上でFALSEを返す
// （今回のイベント自体は通常経路に委ねる）。
_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
ResolveNicola(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _Inout_opt_ PVOID SystemArgument3
    );

// kbdclassへバッチを転送する直前に呼ぶ。送る修飾キーの押下状態をdevExt->OutputModifierDownに
// 反映する（OSへ修飾キーを押下中はNICOLA判定を迂回するため）。合成イベント
// （SendSyntheticEvent）は内部で同じ追跡をしている。devExt->OutputLock保持中に呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
TrackOutputModifiers(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd
    );

// NICOLA判定が現在有効か（設定あり・Enabled・レイヤーマップON・対象レイヤーがアクティブ）。
_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
NicolaIsActive(
    _In_ PDEVICE_EXTENSION devExt
    );

// 判定の有効/無効が前回から変わっていれば、OnEnterKey/OnExitKeyを送出し、無効化時は
// 保留中の判定を確定させてから状態を初期化する。レイヤー状態を変えうる処理の後
// （ServiceCallbackの末尾）と、ResolveNicolaの先頭で呼ぶ。
_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
NicolaSyncActivation(
    _In_ PDEVICE_EXTENSION devExt
    );

// NicolaSyncActivationと同じだが、SendImeKeys=FALSEならOnEnterKey/OnExitKeyを送らない
// （アプリ管理レイヤーの変更など、IME側が起点の切り替え用）。devExt->OutputLock保持中に呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
NicolaSyncActivationEx(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ BOOLEAN SendImeKeys
    );

// --- アプリ管理レイヤーとレイヤー変化の通知 ---

// レイヤー状態（実効マスク・DefaultLayer・マップのON/OFF・NICOLAの有効）が前回から
// 変わっていればSeqを進め、WAIT_LAYER_CHANGEで待っているIRPを完了させる。
// レイヤーを変えうる処理の後（ServiceCallbackの末尾、Hold/TapDanceのDPC、各IOCTL）で呼ぶ。
// EngineLock・OutputLockを保持していない状態で呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
LayerNotifyIfChanged(
    _In_ PDEVICE_EXTENSION devExt
    );

// 現在のレイヤー状態を返す（Seqは最新に更新してから返す）。
_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
GetLayerStateForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _Out_ PKBDLAYER_LAYER_STATE Out
    );

// SET_LAYER_STATE: アプリ管理レイヤーを操作し、Ownerを所有者にする。NICOLAの有効/無効が
// 切り替わった場合は即座に反映する（Flagsに KBDLAYER_LAYER_STATE_FLAG_NO_NICOLA_KEYS が
// あればIMEのオン/オフキーを送らない）。Outが非NULLなら変更後の状態を返す。
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
    );

// コントロールデバイスのハンドルがクローズされた（IRP_MJ_CLEANUP）ときに呼ぶ。そのハンドルが
// 所有するアプリ管理レイヤーを全デバイスで下ろす（IMEのオン/オフキーは送らない）。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
ReleaseAppLayersForOwner(
    _In_ PVOID Owner
    );

// --- WAIT_LAYER_CHANGEの保留IRP（LayerNotify.c） ---

// DriverEntryから呼ぶ。保留IRPのキャンセル安全キューを初期化する。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeLayerNotify(
    VOID
    );

// WAIT_LAYER_CHANGEのIRPを保留にする。成功時はSTATUS_PENDINGを返し、以後の完了
// （出力の書き込み・IoReleaseRemoveLock・IoCompleteRequest）はLayerNotify.cが行う。
// 呼び出し元はFindDeviceExtensionByUniqId(…, Irp)でremove lockを取っておくこと（保留中は
// IRPがそのremove lockを持ち続ける）。STATUS_PENDING以外を返したときは、IRPの完了と
// remove lockの解放は呼び出し元が行う。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
StartWaitLayerChange(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PIRP Irp,
    _In_ ULONG LastSeq
    );

// devExtを待っている保留IRPを、現在の状態で完了させる（LayerNotifyIfChangedから呼ばれる）。
_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
LayerNotifyCompleteWaiters(
    _In_ PDEVICE_EXTENSION devExt
    );

// devExtを待っている保留IRPをStatusで完了させる（デバイス削除時。DispatchPnpが
// IoReleaseRemoveLockAndWaitの前に呼ぶ。保留IRPがremove lockを握っているため）。
_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
LayerNotifyAbortDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ NTSTATUS Status
    );

// ハンドルのクローズ（IRP_MJ_CLEANUP）時に、そのハンドルの保留IRPをSTATUS_CANCELLEDで完了させる。
_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
LayerNotifyCleanupOwner(
    _In_ PFILE_OBJECT FileObject
    );

// AddDeviceから呼ばれる: devExt->NicolaTimer/NicolaDpcとNicolaStateを初期化する。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeNicolaState(
    _In_ PDEVICE_EXTENSION devExt
    );

// --- IOCTLハンドラ（ControlDevice.c）から呼ばれる、PASSIVE_LEVEL専用 ---

// SET_NICOLA: NICOLA設定blob（KBDLAYER_NICOLA_HEADER + entries）を検証して差し替える。
// Blob=NULL/BlobLength=0 は設定のクリア。LayerMapEnabledの状態に関わらず成功する。
// 進行中の判定は確定させてから破棄し、ライブ反映後にレジストリの"Nicola"値へ永続化する。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetNicolaForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_reads_bytes_opt_(BlobLength) PVOID Blob,
    _In_ ULONG BlobLength
    );

// GET_NICOLA: SETで渡したのと同じblobを返す。未設定なら *BytesReturned=0 で成功する。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
GetNicolaForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _Out_writes_bytes_to_opt_(OutBufferLength, *BytesReturned) PVOID OutBuffer,
    _In_ ULONG OutBufferLength,
    _Out_ PULONG BytesReturned
    );

// デバイス削除・アンロード時: NicolaTableを解放する（タイマーのキャンセルと
// KeFlushQueuedDpcsは呼び出し元で済ませておくこと）。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
FreeNicolaForDevice(
    _In_ PDEVICE_EXTENSION devExt
    );

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

// AddDeviceから、devExt->InstanceKey/ModelKey/PersistBindingを設定した後・
// LoadPersistedLayerMapForDeviceの前に呼ぶ: 旧形式のパスから、新しいバインディング先へ
// 既存の永続化設定を1回だけコピーする（E-2: DeviceIdは一意でない前提で設計する。
// common/kbdlayer_ioctl.hのコメント参照）。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
MigrateLegacyPersistedSettingsIfNeeded(
    _In_ PDEVICE_EXTENSION devExt
    );

// GET_DEVICE_SCOPE: devExtのInstanceKey/ModelKey/PersistBindingをそのまま返す。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
GetDeviceScopeForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _Out_ PKBDLAYER_DEVICE_SCOPE OutScope
    );

// SET_DEVICE_SCOPE: devExt->PersistBindingを切り替える。
// Model→Instanceのときは、現在Model側にある設定をInstance側へコピーしてから切り替える
// （設定が消えたように見えないようにするため）。Instance→Modelはビットを書き替えるだけ。
// NewBindingがKBDLAYER_BINDING_*以外ならSTATUS_INVALID_PARAMETER。InstanceKeyが
// 取得できていないデバイスをInstanceへ切り替えようとした場合もSTATUS_INVALID_DEVICE_STATE。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetDeviceScopeForDevice(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ ULONG NewBinding
    );

// --- 緊急時の解除手段（release-roadmap-priority.md 項番3。common/kbdlayer_ioctl.hの
// コメントと .plans/emergency-release.md 参照） ---

// DriverEntryから、CreateControlDevice成功後に1回だけ呼ぶ: レジストリの
// Parameters\EmergencyRelease から設定を読み込む（無ければ既定の組み合わせ
// [左右Ctrl+左右Shift]を使う）。ワークアイテムをControlDeviceObjectへ紐づけて確保する。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeEmergencyReleaseGlobal(
    _In_ PDEVICE_OBJECT ControlDeviceObject
    );

// TeardownControlDeviceから、IoDeleteDeviceの前に呼ぶ: ワークアイテムを解放する。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
TeardownEmergencyReleaseGlobal(
    VOID
    );

// AddDeviceから呼ばれる: devExt->EmergencyTimer/EmergencyDpcを初期化する。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeEmergencyReleaseState(
    _In_ PDEVICE_EXTENSION devExt
    );

// ServiceCallbackの先頭（ResolveCalibrationより前、OutputLock保持中）から、生の
// 物理スキャンコードのバッチをそのまま渡して呼ぶ。イベント自体は書き換えない
// （観測のみ）。組み合わせが揃ってKBDLAYER_EMERGENCY_HOLD_MS経過すると、
// devExt->EmergencyDpcが接続中の全キーボードを一括でOFFにする処理を起動する。
_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
CheckEmergencyRelease(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd
    );

// GET/SET_EMERGENCY_RELEASE の実体。ドライバ全体の設定なので特定のdevExtを取らない。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
GetEmergencyReleaseConfig(
    _Out_ PKBDLAYER_EMERGENCY_RELEASE_CONFIG OutConfig
    );

// KeyCountがKBDLAYER_EMERGENCY_MIN_KEYS〜MAX_KEYS以外、または重複したScanCodesが
// あればSTATUS_INVALID_PARAMETER。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetEmergencyReleaseConfig(
    _In_ PKBDLAYER_EMERGENCY_RELEASE_CONFIG Config
    );

// --- キャリブレーションウィザード（ControlDevice.cのCAPTURE_NEXT_KEYハンドラから） ---

// AddDeviceから呼ばれる: CaptureTimer/CaptureDpcを初期化する。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeCaptureState(
    _In_ PDEVICE_EXTENSION devExt
    );

// 捕捉待機を開始し、Irpを保留にする。成功時はSTATUS_PENDINGを返し、以後Irpの完了
// （出力バッファの書き込み・IoReleaseRemoveLock・IoCompleteRequest）はこのモジュールが
// 行う。完了のきっかけは、キー到着・TimeoutMsの経過・キャンセル・ハンドルのクローズ
// （ReleaseCaptureSuppressForOwner）・デバイス削除（AbortCaptureNextKey）のいずれか。
// 呼び出し元はIoAcquireRemoveLock(&devExt->RemoveLock, Irp)を済ませておくこと。
// STATUS_PENDING以外（既に別の捕捉が進行中ならSTATUS_DEVICE_BUSY、既にキャンセル済みなら
// STATUS_CANCELLED）を返したときは、Irpの完了とremove lockの解放は呼び出し元が行う。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
StartCaptureNextKey(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PIRP Irp,
    _In_ ULONG TimeoutMs
    );

// 保留中の捕捉があれば、Statusで即座に完了させる（デバイス削除時。DispatchPnpが
// IoReleaseRemoveLockAndWaitの前に呼ぶ。保留IRPがremove lockを握っているため）。
_IRQL_requires_max_(DISPATCH_LEVEL)
VOID
AbortCaptureNextKey(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ NTSTATUS Status
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
// そのハンドルが有効化した全デバイスの入力抑止を解除し、そのハンドルで保留中の
// CAPTURE_NEXT_KEYをSTATUS_CANCELLEDで完了させる。
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
