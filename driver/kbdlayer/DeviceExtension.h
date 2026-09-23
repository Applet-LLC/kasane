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

// カーネル内部専用の可変長レイヤーマップテーブル（1回のプール割り当てで確保する）。
// ワイヤー/レジストリ形式（KBDLAYER_MAP_HEADER, common/kbdlayer_ioctl.h）と
// フィールド構成は同じだが、こちらは内部表現として単独で扱う。
typedef struct _KBDLAYER_MAP_TABLE {
    ULONG LayerCount;
    ULONG KeyCount;
    KBDLAYER_KEY_ENTRY Entries[ANYSIZE_ARRAY];
} KBDLAYER_MAP_TABLE, *PKBDLAYER_MAP_TABLE;

#define KBDLAYER_MAP_POOL_TAG 'mLdK'

#define KBDLAYER_DEFAULT_TAP_TERM_MS 200
#define KBDLAYER_DEFAULT_COMBO_TERM_MS 50

typedef struct _DEVICE_EXTENSION {
    PDEVICE_OBJECT LowerDevice;
    ULONG DeviceId;
    LIST_ENTRY ListEntry;
    WCHAR HardwareId[256]; // HardwareId キャッシュ
    ULONG License; // 0 = disabled, 1 = enabled (read at AddDevice)
    // ClassService hook: IOCTL_INTERNAL_KEYBOARD_CONNECT で設定
    PDEVICE_OBJECT OriginalClassDeviceObject;
    PSERVICE_CALLBACK_ROUTINE OriginalClassService;
    IO_REMOVE_LOCK RemoveLock;

    // --- レイヤーエンジン本体（Phase 1） ---
    KSPIN_LOCK EngineLock;              // LayerMap・レイヤー状態全体を保護
    PKBDLAYER_MAP_TABLE LayerMap;       // NULL = 未設定（素通し）
    BOOLEAN LayerMapEnabled;            // FALSE = マップは保持したまま素通し（ON/OFF切替用）
    ULONG ActiveLayerMask;              // bit0..7 = 各レイヤーのON/OFF（MO/TG由来。DefaultLayerは含まない）
    ULONG DefaultLayer;                 // DF(layer)で変更される基底レイヤー（常時ON扱い）
    BOOLEAN OneShotLayerPending;        // OSL(layer)発動中、次のKEYアクション解決でOFFに戻す
    ULONG OneShotLayerBit;              // OneShotLayerPending==TRUE時に、解除すべきビット

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
    ULONG ComboActiveId;           // 保留/成立/ドレイン中のコンボID（Combos[]のインデックス）

    // --- キャリブレーションウィザード (IOCTL_KBDLAYER_CAPTURE_NEXT_KEY) 用 ---
    // ServiceCallback（DISPATCH_LEVEL、EngineLock保持中）と、PASSIVE_LEVELで
    // KeWaitForSingleObjectするIOCTLハンドラ（ControlDevice.c）との受け渡しに使う。
    BOOLEAN CaptureActive;   // TRUE の間、ServiceCallbackは次の押下イベントのスキャンコードを記録する
    USHORT CapturedScanCode; // 記録されたスキャンコード（Enc16形式）
    KEVENT CaptureEvent;     // 記録完了をIOCTLハンドラへ通知する
} DEVICE_EXTENSION, *PDEVICE_EXTENSION;

#define KBDLAYER_POOL_TAG 'diLk'

// Driver.c で定義されるグローバル（AddDevice/DispatchPnp/Unload で管理される
// per-キーボード DEVICE_EXTENSION のリスト）。ControlDevice.c / LayerEngine.c から
// UniqIdによるデバイス検索のために参照する。
extern LIST_ENTRY g_deviceList;
extern KSPIN_LOCK g_listLock;

// g_listLock を保護しつつ g_deviceList を走査し、DeviceId が一致する
// DEVICE_EXTENSION を返す（見つからなければNULL）。ControlDevice.c から使う。
_IRQL_requires_max_(DISPATCH_LEVEL)
PDEVICE_EXTENSION
FindDeviceExtensionByUniqId(
    _In_ ULONG UniqId
    );

#endif // _KBDLAYER_DEVICE_EXTENSION_H_
