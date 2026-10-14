// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

/*
  kbdlayer/DeviceExtension.h

  DEVICE_EXTENSION（per-キーボード フィルタFDOの拡張）と、per-UniqIDの
  レイヤーマップテーブルの内部表現を、Driver.c / LayerEngine.c / ControlDevice.c の
  複数の翻訳単位から共有するための内部ヘッダ（kbdlayerプロジェクト内専用。
  common/ の共有ヘッダとは異なり、他プロジェクトからはincludeしない）。

  kbdaddid/DeviceExtension.h からのフォーク。RemapTable/RemapLock/RemapEnabled を
  LayerMap/EngineLock/LayerMapEnabled 系に置き換え、レイヤー状態（ActiveLayerMask/
  DefaultLayer）とキャリブレーションウィザード用のキャプチャ状態を追加した。
*/

#ifndef _KBDLAYER_DEVICE_EXTENSION_H_
#define _KBDLAYER_DEVICE_EXTENSION_H_

#include <ntddk.h>
#include <wdm.h>
#include <kbdmou.h>
#include "..\\common\\kbdlayer_ioctl.h"
#include "NicolaCore.h"

// カーネル内部専用の可変長レイヤーマップテーブル（1回のプール割り当てで確保する）。
// ワイヤー/レジストリ形式（KBDLAYER_MAP_HEADER, common/kbdlayer_ioctl.h）と
// フィールド構成は同じだが、こちらは内部表現として単独で扱う。
typedef struct _KBDLAYER_MAP_TABLE {
    ULONG KeyCount;
    // TRUE = MODDED_KEY/LAYER_MOD/MODTAP/LAYERTAP/TAPDANCEを含む。ServiceCallbackは複数件バッチを
    // 1件ずつに分けて流す（押下と解放が別のバッチ形で届いても足した修飾キーを必ず戻すため、また
    // タップ/ホールドの判定中に届いたイベントを1件ずつ判定・溜めるため）。ParseLayerMapBlobで決める。
    BOOLEAN NeedsSingleEvents;
    KBDLAYER_KEY_ENTRY Entries[ANYSIZE_ARRAY];
} KBDLAYER_MAP_TABLE, *PKBDLAYER_MAP_TABLE;

#define KBDLAYER_MAP_POOL_TAG 'mLdK'
#define KBDLAYER_NICOLA_POOL_TAG 'nLdK'

// 押下中の物理キーごとの「押下（make）をどう処理したか」の記録（QMKのsource layers
// cache相当）。解放（break）とオートリピートは、その時点のレイヤー状態で解決し直さず、
// この記録どおりに処理する。押している間にレイヤーが変わっても、押下と解放が別のキーに
// ならないようにするため（詳細はLayerEngine.cのLayerEngineShouldDropEntry参照）。
#define KBDLAYER_HELD_NONE      0 // 記録なし（押されていない、またはレイヤーエンジンを通らなかった）
#define KBDLAYER_HELD_RAW       1 // 押下を生のスキャンコードのまま転送した
#define KBDLAYER_HELD_ACTION    2 // 押下をActionで処理した（レイヤーマップで解決した結果）
#define KBDLAYER_HELD_SWALLOWED 3 // 押下を飲み込んだ（マクロ実行等）。解放も飲み込む

// インデックス = MakeCode(下位8bit) + プレフィックス無し:0 / E0:0x100 / E1:0x200。
#define KBDLAYER_HELD_KEY_SLOTS 0x300

// 押下記録（KBDLAYER_HELD_KEY.Action.Type）にだけ使う、ドライバ内部のアクション種別。レイヤーマップ
// には現れない（KBDLAYER_ACTION_TYPE_MAXより大きいので、ParseLayerMapBlobが拒否する）。
// Key Overrideで置き換えたキー: Param1=置き換え先の修飾子マスク（KEYなら0）、Param2=置き換え先の
// スキャンコード（Enc16）。MODDED_KEYと同じ扱い（足した修飾キー・1件ずつの処理・記録どおりの解放）に、
// 一時的に離した修飾キー（devExt->SuppressedMods）を解放時に戻す処理が加わる。
#define KBDLAYER_ACTION_INTERNAL_KEY_OVERRIDE 0x80

typedef struct _KBDLAYER_HELD_KEY {
    UCHAR State;            // KBDLAYER_HELD_*
    KBDLAYER_ACTION Action; // State==KBDLAYER_HELD_ACTIONのときの、押下時に使ったアクション
    // MODDED_KEY/LAYER_MODの押下で足した修飾キー（KBDLAYER_MOD_*のOR）。押下時点で
    // 普通の出力として押されていた修飾キー（RealModsDown）は含まない。0 = 足していない。
    // 変更はSetHeldAddedModsLocked経由で行い、devExt->AddedModCount[]と整合させる。
    UCHAR AddedMods;
} KBDLAYER_HELD_KEY, *PKBDLAYER_HELD_KEY;

#define KBDLAYER_DEFAULT_TAP_TERM_MS 200
#define KBDLAYER_DEFAULT_COMBO_TERM_MS 50
#define KBDLAYER_DEFAULT_TAPDANCE_TERM_MS 200

typedef struct _DEVICE_EXTENSION {
    PDEVICE_OBJECT LowerDevice;
    ULONG DeviceId; // アドレッシング用ID（Target.UniqId、ExtraInformation刻印）。DeviceIdModeで決まる。
    LIST_ENTRY ListEntry;
    WCHAR HardwareId[256]; // HardwareId キャッシュ
    ULONG License; // 0 = disabled, 1 = enabled (read at AddDevice)

    // --- 永続化スコープ（E-2: DeviceIdは一意でない前提で設計する） ---
    // DeviceIdとは独立に、常に両方を計算して持つ（common/kbdlayer_ioctl.hのコメント参照）。
    // AddDeviceで一度だけ設定し、以後は不変（デバイスの生存期間中）。
    ULONG InstanceKey;   // DeviceInstanceIdのハッシュ。0 = 取得できなかった
    ULONG ModelKey;      // HardwareIdのハッシュ。0 = 取得できなかった
    ULONG PersistBinding; // KBDLAYER_BINDING_*。レイヤーマップ等をどちらのキーで永続化するか
    // ClassService hook: IOCTL_INTERNAL_KEYBOARD_CONNECT で設定
    PDEVICE_OBJECT OriginalClassDeviceObject;
    PSERVICE_CALLBACK_ROUTINE OriginalClassService;
    IO_REMOVE_LOCK RemoveLock;

    // --- 出力の直列化 ---
    // 「状態の決定（EngineLock内）→ ロック解放 → OriginalClassServiceで送出」を、
    // CPUをまたいで一続きにするためのロック。ServiceCallback全体・送出するタイマーDPC
    // （Hold/Combo/TapDance/Nicola）・状態をリセットまたは送出するIOCTL経路が保持する。
    // これが無いと、例えばMTのホールド確定DPC（Ctrlのmake）と、別CPUで処理されたMTキーの
    // 解放（Ctrlのbreak）の送出順が逆転し、Ctrlが押しっぱなしになる。
    // ロック順序は OutputLock → EngineLock の一方向のみ（EngineLock保持中に取らない）。
    // EngineLockと違い、保持したままOriginalClassServiceを呼んでよい（上位のサービス
    // コールバックがkbdlayerへ再入することはないため）。
    KSPIN_LOCK OutputLock;

    // --- レイヤーエンジン本体（Phase 1） ---
    KSPIN_LOCK EngineLock;              // LayerMap・レイヤー状態全体を保護
    PKBDLAYER_MAP_TABLE LayerMap;       // NULL = 未設定（素通し）
    BOOLEAN LayerMapEnabled;            // FALSE = マップは保持したまま素通し（ON/OFF切替用）
    ULONG ActiveLayerMask;              // bit0..15 = 各レイヤーのON/OFF（MO/TG由来。DefaultLayerは含まない）
    ULONG DefaultLayer;                 // DF(layer)で変更される基底レイヤー（常時ON扱い）
    BOOLEAN OneShotLayerPending;        // OSL(layer)発動中、次のKEYアクション解決でOFFに戻す
    ULONG OneShotLayerBit;              // OneShotLayerPending==TRUE時に、解除すべきビット
    // TT(layer)の状態（LayerEngine.cのHandleTapToggleLocked参照）。OutputLockとEngineLockの両方を
    // 保持して書き（ResolveLayerAction・OFF）、どちらか一方の保持中に読む。
    USHORT TtScanCode;                  // 最後に押したTTキー（物理スキャンコード、Enc16）
    BOOLEAN TtKeyDown;                  // TtScanCodeのTTキーを押している間TRUE
    BOOLEAN TtInterrupted;              // TTキーを押している間に別のキーが押された（タップにならない）
    ULONG TtTapCount;                   // 続けてタップした回数（別のキー・時間切れで0に戻す）
    ULONGLONG TtPressTime;              // TTキーを押した時刻（KeQueryInterruptTime()値）
    ULONGLONG TtLastTapTime;            // 最後にタップが成立した時刻
    ULONG TtToggledLayers;              // TTのタップで固定したレイヤーのビット（ActiveLayerMaskにも立っている）
    // 押下中キーの処理記録（KBDLAYER_HELD_*）。レイヤーマップのOFF・差し替え・CLEAR後も
    // 保持し、記録のあるキーの解放は記録どおりに処理する（押しっぱなしを残さないため）。
    // アクションそのものを持つので、マップが無くなっても解放を変換できる。
    KBDLAYER_HELD_KEY HeldKeys[KBDLAYER_HELD_KEY_SLOTS];
    ULONG HeldKeyCount;                 // State!=NONEの記録数（0ならOFF中の走査を省く）
    // --- 修飾付きキー（MODDED_KEY）とLM（LAYER_MOD） ---
    // OSから見た修飾キーの押下状態は RealModsDown | AddedModsUnion(devExt)。
    // - RealModsDown（下のOutput節）: 普通の出力（素通し・KEYで置き換えたキー・MTのホールド・
    //   マクロ・コンボ・タップダンス・NICOLA）で押している修飾キー。
    // - AddedModCount[bit]: その修飾キーを足している押下記録（HeldKeys[].AddedMods）の数。
    // 両方を見て、足した修飾キーのmake/breakと、普通の出力の修飾キーのbreakを出し分ける
    // （ForwardToClass・ResolveModdedKey参照）。OutputLockとEngineLockの両方を保持して書き、
    // どちらか一方の保持中に読む。
    UCHAR AddedModCount[8];
    ULONG ModdedHeldCount;              // Action.TypeがMODDED_KEY/LAYER_MOD/INTERNAL_KEY_OVERRIDEの押下記録の数

    // --- Key Override（2026-10-01。LayerEngine.cのResolveModdedKey参照） ---
    // 表（SET_KEY_OVERRIDE）。OutputLockとEngineLockの両方を保持して書き、どちらか一方の保持中に読む。
    KBDLAYER_KEY_OVERRIDE_ENTRY KeyOverrides[KBDLAYER_MAX_KEY_OVERRIDE_COUNT];
    BOOLEAN KeyOverrideUsed[KBDLAYER_MAX_KEY_OVERRIDE_COUNT];
    ULONG KeyOverrideCount;             // KeyOverrideUsedがTRUEの数
    // 置き換え中に、OS上で一時的に離している修飾キー（KBDLAYER_MOD_*）。普通の出力として押されて
    // いても（RealModsDown）、OSへは送らない（ForwardToClass・ReleaseLeakedModifiers参照）。
    // SuppressOwnerScanCodeは、その置き換え先のキー（Enc16。このキー自身のmakeでは戻さない）。
    // OutputLockで保護する（書くのはすべてOutputLock保持中）。
    UCHAR SuppressedMods;
    USHORT SuppressOwnerScanCode;

    // --- One Shot Mod（OSM、2026-10-02。LayerEngine.cのResolveModdedKey参照） ---
    // OneShotMods: OSMをタップした（別のキーを押さずに離した）後、次のキーのために押したままにしている
    // 修飾キー（KBDLAYER_MOD_*）。AddedModsUnionに含まれる（OS上では押されたまま）。修飾キーでないキーの
    // makeを送ったら（ForwardToClass）0に戻し、安全網がbreakを送る。
    // OsmHeldScanCode/OsmUsedWhileHeld: 最後に押したOSMキーと、それを押している間に別のキーのmakeを
    // 送ったか（送ったなら、離したときは普通の修飾キーとして戻す）。
    // いずれもOutputLockで保護する（書くのはすべてOutputLock保持中）。
    UCHAR OneShotMods;
    USHORT OsmHeldScanCode;
    BOOLEAN OsmUsedWhileHeld;

    // コンボの出力・タップダンスの長押しが修飾付きキー（とLM）のとき、押している間足している修飾キー
    // （2026-10-02。押下記録ではなく、それぞれの判定状態に結び付く）。AddedModsUnionに含まれる。
    // 解除・打ち切りで0にし、安全網（ReleaseLeakedModifiers）がbreakを送る。OutputLockで保護する。
    UCHAR ComboAddedMods;
    UCHAR TapDanceAddedMods;

    // --- アプリ管理レイヤーとレイヤー変化の通知（common/kbdlayer_ioctl.h参照） ---
    // EngineLock保持中に読み書きすること。
    ULONG AppLayerMask;                 // アプリが立てたレイヤー（実効マスクにORで合成する）
    PVOID AppLayerOwner;                // 最後にSET_LAYER_STATEを呼んだハンドルのFILE_OBJECT（比較専用）
    ULONG LayerStateSeq;                // レイヤー状態が変わるたびに増える
    // 最後にSeqを進めた時点の状態（変化の検出用）
    ULONG LastActiveLayerMask;
    ULONG LastAppLayerMask;
    ULONG LastDefaultLayer;
    BOOLEAN LastLayerMapEnabled;
    BOOLEAN LastNicolaActive;

    // TRUE = IRP_MN_REMOVE_DEVICEの処理を始めた。以後、IRPの保留（CAPTURE_NEXT_KEY・
    // WAIT_LAYER_CHANGE）を受け付けない。保留IRPを完了させてからIoReleaseRemoveLockAndWait
    // するまでの間に新しい保留IRPが入り、削除がその完了まで待たされるのを防ぐ。
    // EngineLock保持中に読み書きすること。
    BOOLEAN RemovePending;

    // --- Phase 2 (Tap-Hold: MT/LT) ---
    //
    // Hold On Other Key Press方式。phase2-spikeブランチでの実機検証で得た教訓
    // （オートリピート対応・未飲み込みの解放は素通し・状態は独立フラグで管理）を
    // 反映済み。DeviceExtension.hのこのコメント自体は元々スパイクの教訓置き場
    // だったもので、以下がその教訓を踏まえた本実装。
    KTIMER HoldTimer;             // TapTermMs後にHoldDpcを起動するタイマー
    KDPC HoldDpc;                 // タイムアウト時にホールド確定を行うDPC（HoldTapDpcRoutine, LayerEngine.c）
    ULONG TapTermMs;              // タップ/ホールドの閾値（既定200ms、IOCTL_KBDLAYER_SET_CONFIGで変更可）
    BOOLEAN PendingActive;        // TRUE = MT/LTキー押下中、タップ/ホールド未確定
    BOOLEAN HoldConfirmed;        // TRUE = ホールド確定済み、PendingScanCodeの解放待ち
    USHORT PendingScanCode;       // 保留中/ホールド中のMT/LTキーの物理スキャンコード（Enc16）
    KBDLAYER_ACTION PendingAction; // 保留中/ホールド中のMT/LTアクション本体（Type/Param1/Param2）
    LARGE_INTEGER PendingPressTime; // デバッグ用（現状ロジックはKeSetTimerの相対時間で完結し未使用）
    // HoldTimerを張ったときの期限（KeQueryInterruptTime()値）。EngineLock内で記録する。
    // HoldDpcは期限前なら何もしない（満了済みでキュー待ちの古いDPCが、その後に始まった
    // 新しい保留サイクルを即座にホールド確定させないため。CaptureDeadlineと同じ手法）。
    ULONGLONG HoldDeadline;
    // Quick Tap（QMKのQuick Tap Term相当）: MT/LTをタップした直後に同じキーをもう一度押したら、
    // 判定せずにタップ側のキーとして押す（押し続ければそのキーのリピートになる）。最後に
    // タップが確定したMT/LTキーと、その時刻（KeQueryInterruptTime()値）。LastTapScanCode==0は
    // 記録なし。EngineLockで保護する。期間はv1ではTapTermMsと同じ。
    USHORT LastTapScanCode;
    ULONGLONG LastTapTime;
    // 判定方式と、その設定（IOCTL_KBDLAYER_SET_CONFIG v2。EngineLockで保護）。
    ULONG TapHoldMode;             // KBDLAYER_TAPHOLD_MODE_*
    ULONG QuickTapTermMs;          // 0 = TapTermMsと同じ、KBDLAYER_QUICK_TAP_TERM_DISABLED = 使わない
    ULONG TappingToggle;           // TT(layer)の回数（1〜KBDLAYER_MAX_TAPPING_TOGGLE）
    // PERMISSIVE_HOLD/TAP_PREFERREDで、判定中（PendingActive）に届いた別のキーのイベント。判定が
    // 決まったら、MT/LT自身の出力の後に、届いた順にResolveAndForwardTailへ流し直す
    // （LayerEngine.cのReplayHoldBuffer参照）。EngineLockで保護する。
    KEYBOARD_INPUT_DATA HoldBuffer[KBDLAYER_MAX_HOLD_BUFFER];
    ULONG HoldBufferCount;

    // --- 機能別ON/OFF（release-roadmap-priority.md 項番4。common/kbdlayer_ioctl.hの
    // コメント参照） ---
    // 対象はMacro/Combo/TapDanceの3つ（MT/LT・NICOLAは対象外）。既定は全機能有効
    // （KBDLAYER_FEATURE_ALL）。ServiceCallback（Driver.cのResolveAndForward）が、
    // 無効な機能のResolve*呼び出しをスキップするのに使う。書き換えはOutputLockと
    // EngineLockの両方を保持して行い（SetFeaturesForDevice）、読むときはどちらか一方を
    // 保持していればよい（ResolveAndForwardはOutputLock、GET_FEATURESはEngineLock）。
    ULONG EnabledFeatures;

    // --- Phase 3: マクロ ---
    // 固定サイズで埋め込み（32 * (4 + 64*4) = 8320バイト程度。動的確保・解放の
    // ライフサイクル管理を避けるため、DEVICE_EXTENSION内に直接持つ）。
    // MacroStepCounts[i]==0 はマクロID iが未設定であることを表す。
    // EngineLock保持中に読み書きすること（LayerMap等と同じ規律）。
    ULONG MacroStepCounts[KBDLAYER_MAX_MACRO_COUNT];
    KBDLAYER_MACRO_STEP Macros[KBDLAYER_MAX_MACRO_COUNT][KBDLAYER_MAX_MACRO_STEPS];

    // --- Phase 4: コンボ ---
    // v1は2キー限定・重複禁止（common/kbdlayer_ioctl.hのコメント参照）。この
    // スコープ限定により、保留スロットはPhase 2のPendingActive等と同様に1つで足りる
    // （Combos[]の中で「どのコンボを待っているか」が一意に定まるため）。
    // EngineLock保持中に読み書きすること。
    KBDLAYER_COMBO_ENTRY Combos[KBDLAYER_MAX_COMBO_COUNT];
    BOOLEAN ComboUsed[KBDLAYER_MAX_COMBO_COUNT];

    KTIMER ComboTimer;             // ComboTermMs後にComboDpcを起動するタイマー
    KDPC ComboDpc;                 // タイムアウト時に「不成立」処理を行うDPC（ComboDpcRoutine, LayerEngine.c）
    ULONG ComboTermMs;             // 最初のメンバー押下から相方到着までの許容時間（既定50ms）
    BOOLEAN ComboPendingActive;    // TRUE = 最初のメンバー押下中、相方待ち
    BOOLEAN ComboConfirmed;        // TRUE = コンボ成立済み、出力キー押下中
    BOOLEAN ComboDraining;         // TRUE = 出力は解除済み、もう片方の物理キー解放を待つだけ
    USHORT ComboPendingScanCode;   // 保留中の最初のメンバーキーの物理スキャンコード（Enc16）
    // コンボ不成立時に復元する押下の内容。保留を始めた時点（押下時点）のレイヤーで解決しておく。
    // KBDLAYER_HELD_ACTION = ComboPendingActionのKEY出力を送る / KBDLAYER_HELD_RAW = 生のスキャンコード
    // （KEY以外のアクションのとき） / KBDLAYER_HELD_SWALLOWED = 何も送らない（KC_NO・全レイヤー透過）。
    UCHAR ComboPendingRestore;
    KBDLAYER_ACTION ComboPendingAction;
    ULONG ComboActiveId;           // 保留/成立/ドレイン中のコンボID（Combos[]のインデックス）
    // Draining中に、まだ押されているメンバーの物理スキャンコード（Enc16）。
    // 設定変更・OFFでDrainingを打ち切るとき、その解放だけを飲み込むために使う。
    USHORT ComboDrainingScanCode;
    ULONGLONG ComboDeadline;       // ComboTimerの期限（HoldDeadlineと同じ用途）

    // --- Phase 5: タップダンス ---
    // v1は3回までのタップ区別・ホールドは1回目の押下のみ（common/kbdlayer_ioctl.hの
    // コメント参照）。TapDanceは1つの物理キーの振る舞いを表すため、Phase 1〜3と同じく
    // KBDLAYER_ACTION_TAPDANCE(id)としてレイヤーアクション表に埋め込み、実体はここに
    // 別テーブルとして持つ（Macroと同じ方式）。EngineLock保持中に読み書きすること。
    KBDLAYER_TAPDANCE_ENTRY TapDances[KBDLAYER_MAX_TAPDANCE_COUNT];
    BOOLEAN TapDanceUsed[KBDLAYER_MAX_TAPDANCE_COUNT];

    KTIMER TapDanceTimer;            // TapDanceTermMs後にTapDanceDpcを起動するタイマー
    KDPC TapDanceDpc;                // タイムアウト時に確定処理を行うDPC（TapDanceDpcRoutine, LayerEngine.c）
    ULONG TapDanceTermMs;            // タップ間・ホールド判定の閾値（既定200ms）
    BOOLEAN TapDancePendingActive;   // TRUE = 保留サイクル進行中（押下中 or 次のタップ待ち）
    BOOLEAN TapDanceKeyDown;         // TRUE = 現在の物理キーが押されたまま（まだ離されていない）
    BOOLEAN TapDanceHoldConfirmed;   // TRUE = SingleHold確定済み、対応する解放待ち
    BOOLEAN TapDanceDraining;        // TRUE = N-tap(N>1)確定済みだが、押されたままの解放を無音で待つだけ
    USHORT TapDanceScanCode;         // 対象の物理キーのスキャンコード（Enc16）
    ULONG TapDanceActiveId;          // 対象のタップダンスID（TapDances[]のインデックス）
    ULONG TapDanceTapCount;          // これまでに完了した（press+release）タップの回数
    ULONGLONG TapDanceDeadline;      // TapDanceTimerの期限（HoldDeadlineと同じ用途）

    // --- Phase 6: NICOLA（親指シフト） ---
    // 判定アルゴリズム本体はNicolaCore.c（カーネル非依存）。ここにはその状態と、
    // 設定（ワイヤー形式のblobをそのままNonPagedに複製したもの）を持つ。
    // EngineLock保持中に読み書きすること。
    PKBDLAYER_NICOLA_HEADER NicolaTable; // NULL = 未設定。続けてKeyCount個のエントリ
    ULONG NicolaTableBytes;              // NicolaTableのバイト数（GET/永続化用）
    NICOLA_CORE_STATE NicolaState;
    KTIMER NicolaTimer;                  // NicolaState.Deadlineで満了するタイマー
    KDPC NicolaDpc;                      // タイムアウト時の確定処理（NicolaDpcRoutine, LayerEngine.c）
    BOOLEAN NicolaWasActive;             // 直前に判定が有効だったか（OnEnter/OnExitキーの送出判定用）
    // 普通の出力としてOSへ送った修飾キーのうち、押下中のもの（KBDLAYER_MOD_*のビット。
    // s_ModifierScanCodesと同じ並び）。修飾付きキー・LMで足した分は含まない（AddedModCount参照）。
    // NICOLAは RealModsDown | AddedModsUnion(devExt) が0でない間、判定を迂回する。
    // 物理キーではなく、実際にkbdclassへ送った出力で追跡する（レイヤーマップでCtrlにした
    // キーやMTのホールドも含み、逆に物理Ctrlを別のキーにした場合は含まない）。送出はすべて
    // OutputLock保持中にForwardToClassを通るので、OutputLockで保護する。
    UCHAR RealModsDown;
    // OSへ実際にmakeを送り、まだbreakを送っていない修飾キー（普通の出力と足した分の両方）。
    // 正しく動いていれば RealModsDown | AddedModsUnion(devExt) と一致する。はみ出したビット
    // （足した記録がbreakを送らずに消えた等）は、ForwardToClassの後始末がbreakを送って直す
    // （のどかのflushPendingModifiersIfIdleに相当する安全網）。SendAddedModifiersはこれを見て、
    // 送り済みのmake・breakを二重に送らない。OutputLockで保護する。
    UCHAR OutputModsDown;
    // 直近にOSへ送ったmakeが修飾キーだったか。足したAlt・Winを、間に何も押さずに離すと
    // メニューバー・スタートメニューが開くので、そのときはCtrlのタップを挟む
    // （SendAddedModifiers参照）。OutputLockで保護する。
    BOOLEAN LastOutputWasModifier;

    // --- キャリブレーションウィザード (IOCTL_KBDLAYER_CAPTURE_NEXT_KEY) 用 ---
    // CAPTURE_NEXT_KEYのIRPは保留（STATUS_PENDING）にしてCaptureIrpに持ち、
    // キー到着（ResolveCalibration）・タイムアウト（CaptureDpc）・キャンセル（プロセス終了等）・
    // ハンドルのクローズ（IRP_MJ_CLEANUP）・デバイス削除のいずれかで完了させる。
    // 同期待機にしていた頃は、待機中にツールをkillしてもタイムアウトまでIRPが返らず、
    // その間は入力抑止も解けなかった。EngineLock保持中に読み書きすること。
    BOOLEAN CaptureActive;   // TRUE の間、ServiceCallbackは次の押下イベントのスキャンコードを記録する
    BOOLEAN CaptureDone;     // TRUE = 記録済み。ResolveCalibrationがロック解放後にIRPを完了させる
    USHORT CapturedScanCode; // 記録されたスキャンコード（Enc16形式）
    PIRP CaptureIrp;         // 保留中のCAPTURE_NEXT_KEYのIRP（NULL = 無し）
    ULONGLONG CaptureDeadline; // タイムアウト時刻（KeQueryInterruptTime()値）
    KTIMER CaptureTimer;     // タイムアウト用
    KDPC CaptureDpc;         // CaptureDpcRoutine（LayerEngine.c）
    // TRUE = 直前の押下がE1プレフィックス付き（Pauseの{1D,E1}）だったので、次の押下
    // （同じシーケンスの2イベント目{45}）はキャプチャ対象から外す。CaptureActiveの
    // 状態に関わらず次の押下1件で必ず消費する（2イベント目が別バッチ・別のキャプチャ
    // 待機中に届いても、NumLockや次のスロットとして誤記録しないため）。
    BOOLEAN CaptureSkipAfterE1;
    // TRUE = 今の捕捉待機は疑似Shift（E0 2A / E0 36）も記録する（KBDLAYER_CAPTURE_FLAG_ALLOW_FAKE_SHIFT）。
    // StartCaptureNextKeyが毎回設定する。EngineLock保持中に読み書きする。
    BOOLEAN CaptureAllowFakeShift;

    // --- キーイベントの通知（WAIT_KEY_EVENT、KeyStream.c） ---
    // TRUE = 直前の観測したエントリがE1プレフィックス付き（Pauseの{E1 1D}）だったので、次の
    // エントリ（同じシーケンスの2イベント目{45}。E1フラグは付かず、NumLockのスキャンコードと
    // 同じに見える）は出さずに捨てる。解放側（{E1 9D}{C5}）も同じ。バッチをまたいでも効くよう、
    // デバイスごとに持つ。KeyStreamObserveがOutputLock保持中に読み書きする。
    BOOLEAN KeyStreamSkipAfterE1;

    // --- 緊急時の解除手段（release-roadmap-priority.md 項番3） ---
    // グローバル設定（g_EmergencyKeyCount/g_EmergencyScanCodes、LayerEngine.c）のうち、
    // このデバイスで現在押されている物理キーのビットマスク。全ビットが揃ってから
    // KBDLAYER_EMERGENCY_HOLD_MS 経過したら、接続中の全キーボードを一括でOFFにする
    // （EmergencyDpcRoutine→ワークアイテム。common/kbdlayer_ioctl.hのコメント参照）。
    // devExt->OutputLock保持中に読み書きすること（Hold/Combo/TapDanceと同じKTIMER+KDPCの組）。
    UCHAR EmergencyKeysDownMask;
    KTIMER EmergencyTimer;
    KDPC EmergencyDpc;
    ULONGLONG EmergencyDeadline;

    // --- キャリブレーション用の入力抑止（IOCTL_KBDLAYER_SET_CAPTURE_SUPPRESS）---
    // ResolveCalibration（LayerEngine.c）がEngineLock保持中に参照・更新する。
    BOOLEAN SuppressActive;         // TRUE = 押下イベントをOSへ渡さずに捨てる
    PVOID SuppressOwner;            // 有効化したハンドルのFILE_OBJECT（比較専用。参照は保持しない）
    ULONGLONG SuppressLastActivity; // KeQueryInterruptTime()値。アイドルタイムアウト判定用
    // 抑止中に押下を捨てたキーのビットマップ（解放も対で捨てるため）。
    // インデックス = MakeCode(下位8bit) + プレフィックス無し:0 / E0:0x100 / E1:0x200。
    // 抑止を解除しても消さない。解除後に届いた解放も1回だけ捨ててビットを落とす
    // （押下を捨てたキーの解放だけがOSに漏れないようにするため）。
    UCHAR SuppressedDown[96];
    ULONG SuppressedDownCount;      // SuppressedDownで立っているビットの数（0ならホットパスで素通し）
} DEVICE_EXTENSION, *PDEVICE_EXTENSION;

#define KBDLAYER_POOL_TAG 'diLk'

// Driver.c で定義されるグローバル（AddDevice/DispatchPnp/Unload で管理される
// per-キーボード DEVICE_EXTENSION のリスト）。ControlDevice.c / LayerEngine.c から
// UniqIdによるデバイス検索のために参照する。
extern LIST_ENTRY g_deviceList;
extern KSPIN_LOCK g_listLock;

// g_listLock を保護しつつ g_deviceList を走査し、DeviceId が一致する
// DEVICE_EXTENSION を返す（見つからない・削除中ならNULL）。ControlDevice.c から使う。
// 返したdevExtのRemoveLockをIrpで取得済みなので、呼び出し元は使い終えたら
// IoReleaseRemoveLock(&devExt->RemoveLock, Irp) を呼ぶこと。
_IRQL_requires_max_(DISPATCH_LEVEL)
PDEVICE_EXTENSION
FindDeviceExtensionByUniqId(
    _In_ ULONG UniqId,
    _In_ PIRP Irp
    );

#endif // _KBDLAYER_DEVICE_EXTENSION_H_
