// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

/*
  common/kbdlayer_ioctl.h

  kbdlayer.sys のコントロールデバイス（\\.\KbdLayerControl）とやり取りするための
  IOCTL定義・データ構造。カーネル（kbdlayer/ControlDevice.c, kbdlayer/LayerEngine.c）と
  ユーザーモード（KeymapTool、第三者アプリ）の両方から include できるように、
  プラットフォーム依存を最小限にしてある。

  kbdaddid の common/kbdaddid_ioctl.h（1:1スキャンコード置換、Windows標準の
  Scancode Map とバイト互換）とは別物。こちらは「物理スキャンコード →
  レイヤーごとのアクション配列」を表現するための新しいワイヤーフォーマット。
  設計の経緯は .plans/perplexity-c-users-applet-documents-git-parallel-mochi.md を参照。
*/

#ifndef _KBDLAYER_COMMON_IOCTL_H_
#define _KBDLAYER_COMMON_IOCTL_H_

#if defined(_KERNEL_MODE) || defined(_NTDDK_)
#include <ntddk.h>
#else
#include <windows.h>
#include <winioctl.h>
#endif

// ユーザーモードから CreateFileW するためのパス。
#define KBDLAYER_CONTROL_DEVICE_PATH L"\\\\.\\KbdLayerControl"
// カーネル側の名前付きデバイス・シンボリックリンク（ControlDevice.c で使用）。
#define KBDLAYER_CONTROL_DEVICE_NT_NAME   L"\\Device\\KbdLayerControl"
#define KBDLAYER_CONTROL_SYMLINK_NT_NAME  L"\\DosDevices\\KbdLayerControl"

#pragma pack(push, 1)

// 1キー分・1レイヤー分のアクション（4バイト固定長）。
typedef struct _KBDLAYER_ACTION {
    UCHAR  Type;     // KBDLAYER_ACTION_*
    UCHAR  Param1;   // レイヤー番号 / 修飾子ビット等（Typeに依存）
    USHORT Param2;   // 送出先スキャンコード（Enc16形式）等（Typeに依存）
} KBDLAYER_ACTION, *PKBDLAYER_ACTION;

// KBDLAYER_ACTION.Type
#define KBDLAYER_ACTION_NONE          0  // 何もしない（KC_NO）
#define KBDLAYER_ACTION_TRANSPARENT   1  // 下位レイヤーへフォールスルー（KC_TRNS）
#define KBDLAYER_ACTION_KEY           2  // 単純キー: Param2=送出スキャンコード(Enc16)
#define KBDLAYER_ACTION_MOMENTARY     3  // MO(layer): Param1=レイヤー番号
#define KBDLAYER_ACTION_TOGGLE        4  // TG(layer): Param1=レイヤー番号
#define KBDLAYER_ACTION_DEFAULT       5  // DF(layer): Param1=レイヤー番号
#define KBDLAYER_ACTION_ONESHOT_LAYER 6  // OSL(layer): Param1=レイヤー番号
#define KBDLAYER_ACTION_ONESHOT_MOD   7  // OSM(mod): Param1=修飾子ビット (Phase 2以降)
#define KBDLAYER_ACTION_MODTAP        8  // MT(mod,kc): Param1=修飾子ビット, Param2=タップ時スキャンコード (Phase 2)
#define KBDLAYER_ACTION_LAYERTAP      9  // LT(layer,kc): Param1=レイヤー番号, Param2=タップ時スキャンコード (Phase 2)
#define KBDLAYER_ACTION_MACRO         10 // MACRO(id): Param1=マクロID(0..KBDLAYER_MAX_MACRO_COUNT-1) (Phase 3)
#define KBDLAYER_ACTION_CUSTOM        11 // 将来の拡張フック点として予約のみ。詳細設計は先送り (Phase 3)
#define KBDLAYER_ACTION_TAPDANCE      12 // TAPDANCE(id): Param1=タップダンスID(0..KBDLAYER_MAX_TAPDANCE_COUNT-1) (Phase 5)

#define KBDLAYER_MAX_LAYERS 8   // VIA実務上の上限(4)に余裕を持たせる
#define KBDLAYER_MAX_KEYS   256 // 既存 KBDADDID_MAX_REMAP_ENTRIES を踏襲

// 1物理キー分のエントリ: 物理スキャンコード(Enc16) + レイヤー数分のアクション。
typedef struct _KBDLAYER_KEY_ENTRY {
    USHORT SourceScanCode;                       // キャリブレーションで得た物理スキャンコード（Enc16形式）
    KBDLAYER_ACTION Actions[KBDLAYER_MAX_LAYERS]; // Actions[0]=レイヤー0(基底)用 ... Actions[N-1]
} KBDLAYER_KEY_ENTRY, *PKBDLAYER_KEY_ENTRY;

// ワイヤー/レジストリ形式のブロブヘッダ。続けて KeyCount 個の KBDLAYER_KEY_ENTRY が続く。
typedef struct _KBDLAYER_MAP_HEADER {
    ULONG Header;      // 予約。常に0。
    ULONG LayerCount;  // 実際に使用するレイヤー数（1 <= LayerCount <= KBDLAYER_MAX_LAYERS）
    ULONG KeyCount;    // 続く KBDLAYER_KEY_ENTRY の数（<= KBDLAYER_MAX_KEYS）
    // 続けて KeyCount 個の KBDLAYER_KEY_ENTRY
} KBDLAYER_MAP_HEADER, *PKBDLAYER_MAP_HEADER;

// blob全体（ヘッダ+エントリ列）の最大バイト数。
// 256 keys * (2 + 8*4) bytes = 256 * 34 = 8704 bytes 程度。
#define KBDLAYER_MAX_MAP_BLOB_BYTES \
    (sizeof(KBDLAYER_MAP_HEADER) + (KBDLAYER_MAX_KEYS * sizeof(KBDLAYER_KEY_ENTRY)))

// 操作対象の物理キーボードを指定する。UniqId は devExt->DeviceId
// （kbdaddidと同じ、ExtraInformation上位16bitの値そのもの）。
typedef struct _KBDLAYER_TARGET {
    ULONG UniqId;
} KBDLAYER_TARGET, *PKBDLAYER_TARGET;

// IOCTL_KBDLAYER_SET_LAYERMAP の入力バッファ。
// 続く BlobLength バイトが KBDLAYER_MAP_HEADER + entries そのもの。
typedef struct _KBDLAYER_SET_MAP_INPUT {
    KBDLAYER_TARGET Target;
    ULONG BlobLength;
    // 続けて BlobLength バイトの KBDLAYER_MAP_HEADER + entries
} KBDLAYER_SET_MAP_INPUT, *PKBDLAYER_SET_MAP_INPUT;

// IOCTL_KBDLAYER_GET_LAYERMAP / IOCTL_KBDLAYER_CLEAR_LAYERMAP の入力は KBDLAYER_TARGET のみ。

// IOCTL_KBDLAYER_GET_LAYERMAP の出力バッファ。
// SETで渡したのと同じバイト列（KBDLAYER_MAP_HEADER + entries）がそのまま返る。
// マップが設定されていないデバイスに対しては BlobLength=0 で成功を返す。
typedef struct _KBDLAYER_GET_MAP_OUTPUT {
    ULONG BlobLength;
    // 続けて BlobLength バイトの KBDLAYER_MAP_HEADER + entries
} KBDLAYER_GET_MAP_OUTPUT, *PKBDLAYER_GET_MAP_OUTPUT;

// IOCTL_KBDLAYER_SET_LAYERMAP_ENABLED の入力バッファ。
// マップ内容（blob）には一切触れず、ON/OFFのみを切り替える。
typedef struct _KBDLAYER_SET_ENABLED_INPUT {
    KBDLAYER_TARGET Target;
    ULONG Enabled;   // 0 = OFF（マップは保持したまま素通しに戻す）, 1 = ON
} KBDLAYER_SET_ENABLED_INPUT, *PKBDLAYER_SET_ENABLED_INPUT;

// IOCTL_KBDLAYER_LIST_DEVICES の出力に含まれる1台分の情報。
typedef struct _KBDLAYER_DEVICE_INFO {
    ULONG UniqId;
    ULONG HasLayerMap;     // マップが設定済みかどうか（内容の有無）
    ULONG Enabled;         // 現在ONになっているかどうか（HasLayerMap=0のときは無意味）
    ULONG ActiveLayerMask; // 現在アクティブなレイヤーのビットマスク（DefaultLayerビット含む）
    WCHAR HardwareId[256];
} KBDLAYER_DEVICE_INFO, *PKBDLAYER_DEVICE_INFO;

typedef struct _KBDLAYER_LIST_DEVICES_OUTPUT {
    ULONG DeviceCount;
    // 続けて DeviceCount 個の KBDLAYER_DEVICE_INFO
} KBDLAYER_LIST_DEVICES_OUTPUT, *PKBDLAYER_LIST_DEVICES_OUTPUT;

// IOCTL_KBDLAYER_CAPTURE_NEXT_KEY の入力バッファ（KeymapTool のキャリブレーションウィザード専用）。
// TimeoutMs: 待機の上限（ミリ秒）。0 は既定値（KeymapTool/ControlDevice.c の
// KBDLAYER_CAPTURE_DEFAULT_TIMEOUT_MS）を使うことを表す。
// KBDLAYER_CAPTURE_MAX_TIMEOUT_MS を超える値は、その上限に切り詰める。
// 待機中にプロセスが終了したりハンドルを閉じたりすると、IRPはSTATUS_CANCELLEDで即座に戻る。
#define KBDLAYER_CAPTURE_MAX_TIMEOUT_MS 60000
typedef struct _KBDLAYER_CAPTURE_NEXT_KEY_INPUT {
    KBDLAYER_TARGET Target;
    ULONG TimeoutMs;
} KBDLAYER_CAPTURE_NEXT_KEY_INPUT, *PKBDLAYER_CAPTURE_NEXT_KEY_INPUT;

// IOCTL_KBDLAYER_CAPTURE_NEXT_KEY の出力バッファ。
// ScanCode: 押下されたキーの物理スキャンコード（Enc16形式）。
// TimedOut: TimeoutMs内にキー押下が無かった場合は1、取得できた場合は0。
typedef struct _KBDLAYER_CAPTURE_NEXT_KEY_OUTPUT {
    USHORT ScanCode;
    USHORT TimedOut;
} KBDLAYER_CAPTURE_NEXT_KEY_OUTPUT, *PKBDLAYER_CAPTURE_NEXT_KEY_OUTPUT;

// IOCTL_KBDLAYER_SET_CONFIG の入力バッファ（Phase 2: Tap-Hold設定。Phase 1では未使用だが
// 構造体自体は先に定義しておく）。
typedef struct _KBDLAYER_SET_CONFIG_INPUT {
    KBDLAYER_TARGET Target;
    ULONG TapTermMs;      // 既定200ms。0を渡すと既定値にリセットする。
    ULONG ComboTermMs;    // 既定50ms。0を渡すと既定値にリセットする。(Phase 4: コンボ)
    ULONG TapDanceTermMs; // 既定200ms。0を渡すと既定値にリセットする。(Phase 5: タップダンス)
} KBDLAYER_SET_CONFIG_INPUT, *PKBDLAYER_SET_CONFIG_INPUT;

// IOCTL_KBDLAYER_GET_CONFIG の入力は KBDLAYER_TARGET のみ。出力は現在の実効値
// （未設定・0指定で既定値に戻っている項目は既定値そのもの）。
typedef struct _KBDLAYER_GET_CONFIG_OUTPUT {
    ULONG TapTermMs;
    ULONG ComboTermMs;
    ULONG TapDanceTermMs;
} KBDLAYER_GET_CONFIG_OUTPUT, *PKBDLAYER_GET_CONFIG_OUTPUT;

// IOCTL_KBDLAYER_SET_CAPTURE_SUPPRESS の入力バッファ（キャリブレーション用の「オフモード」）。
// Enable=1 の間、対象デバイスの押下イベントをOSへ渡さずに捨てる（CAPTURE_NEXT_KEYの
// 記録は通常通り行われる）。抑止開始前から押されていたキーの解放はそのまま通す。
// 抑止は有効化したハンドルに紐付き、次のいずれかで自動的に解除される:
//   - そのハンドルのクローズ（プロセス終了・クラッシュを含む。IRP_MJ_CLEANUP）
//   - KBDLAYER_CAPTURE_SUPPRESS_IDLE_TIMEOUT_MS の間、CAPTURE_NEXT_KEYの待機も
//     SET_CAPTURE_SUPPRESSの呼び出しも無かったとき
// 別のハンドルが既に抑止中のデバイスに対するEnable=1はSTATUS_DEVICE_BUSYで失敗する。
typedef struct _KBDLAYER_SET_CAPTURE_SUPPRESS_INPUT {
    KBDLAYER_TARGET Target;
    ULONG Enable; // 0 = 解除, 1 = 有効化
} KBDLAYER_SET_CAPTURE_SUPPRESS_INPUT, *PKBDLAYER_SET_CAPTURE_SUPPRESS_INPUT;

#define KBDLAYER_CAPTURE_SUPPRESS_IDLE_TIMEOUT_MS 60000

// --- Phase 3: マクロ ---

#define KBDLAYER_MAX_MACRO_COUNT 32  // KBDLAYER_ACTION_MACROのParam1(UCHAR)が指せる範囲を実務的に制限
#define KBDLAYER_MAX_MACRO_STEPS 64  // 1マクロあたりのステップ数上限

// マクロ1ステップ分。単純なキー押下/解放のみ（遅延指定は将来拡張、Phase 3では未対応）。
typedef struct _KBDLAYER_MACRO_STEP {
    USHORT ScanCode;  // 送出先スキャンコード（Enc16形式）
    UCHAR  IsBreak;   // 0 = 押下(make), 1 = 解放(break)
    UCHAR  Reserved;
} KBDLAYER_MACRO_STEP, *PKBDLAYER_MACRO_STEP;

// IOCTL_KBDLAYER_SET_MACRO の入力バッファ。
// StepCount=0 は該当マクロIDのクリア（未設定に戻す）を意味する。
typedef struct _KBDLAYER_SET_MACRO_INPUT {
    KBDLAYER_TARGET Target;
    ULONG MacroId;     // 0 <= MacroId < KBDLAYER_MAX_MACRO_COUNT
    ULONG StepCount;   // 0 <= StepCount <= KBDLAYER_MAX_MACRO_STEPS
    // 続けて StepCount 個の KBDLAYER_MACRO_STEP
} KBDLAYER_SET_MACRO_INPUT, *PKBDLAYER_SET_MACRO_INPUT;

// IOCTL_KBDLAYER_GET_MACRO の入力バッファ。
typedef struct _KBDLAYER_GET_MACRO_INPUT {
    KBDLAYER_TARGET Target;
    ULONG MacroId;
} KBDLAYER_GET_MACRO_INPUT, *PKBDLAYER_GET_MACRO_INPUT;

// IOCTL_KBDLAYER_GET_MACRO の出力バッファ。未設定のマクロIDに対してはStepCount=0で成功する。
typedef struct _KBDLAYER_GET_MACRO_OUTPUT {
    ULONG StepCount;
    // 続けて StepCount 個の KBDLAYER_MACRO_STEP
} KBDLAYER_GET_MACRO_OUTPUT, *PKBDLAYER_GET_MACRO_OUTPUT;

// --- Phase 4: コンボ ---
//
// v1のスコープ限定（.plans/...md 検討時の合意事項）:
//   - メンバーキーは常に2つ（3キー以上の一般化は将来検討）
//   - 同一の物理スキャンコードが複数のコンボに属することは禁止（Set時に検証）
//   - 出力アクションはKBDLAYER_ACTION_KEYのみ許可（MO/TG等への拡張は将来検討）
//   - 出力の解除(release)は、2つのメンバーキーのどちらかが離された時点で行う
//     （両方が離されるまで待たない）
// これらの制約により、Phase 2のPendingActive等と同じ「単一保留スロット」の
// 仕組みをそのまま再利用でき、失敗時のリプレイ（DPCからの自発呼び出しを含む）も
// 常に1イベント分で済む（詳細はLayerEngine.cのResolveCombo参照）。

#define KBDLAYER_MAX_COMBO_COUNT 16

// コンボ1件分。ScanCode1/ScanCode2は物理スキャンコード(Enc16形式。KeymapTool側では
// KC_*トークンを標準スキャンコードとして解決したものをそのまま使う想定）。
typedef struct _KBDLAYER_COMBO_ENTRY {
    USHORT ScanCode1;
    USHORT ScanCode2;
    KBDLAYER_ACTION OutputAction; // v1: Type == KBDLAYER_ACTION_KEY のみ許可
} KBDLAYER_COMBO_ENTRY, *PKBDLAYER_COMBO_ENTRY;

// IOCTL_KBDLAYER_SET_COMBO の入力バッファ。
// Entry.OutputAction.Type == KBDLAYER_ACTION_NONE を渡すと、そのComboIdのクリアを意味する。
typedef struct _KBDLAYER_SET_COMBO_INPUT {
    KBDLAYER_TARGET Target;
    ULONG ComboId; // 0 <= ComboId < KBDLAYER_MAX_COMBO_COUNT
    KBDLAYER_COMBO_ENTRY Entry;
} KBDLAYER_SET_COMBO_INPUT, *PKBDLAYER_SET_COMBO_INPUT;

// IOCTL_KBDLAYER_GET_COMBO の入力バッファ。
typedef struct _KBDLAYER_GET_COMBO_INPUT {
    KBDLAYER_TARGET Target;
    ULONG ComboId;
} KBDLAYER_GET_COMBO_INPUT, *PKBDLAYER_GET_COMBO_INPUT;

// IOCTL_KBDLAYER_GET_COMBO の出力バッファ。Used=0の場合、指定ComboIdは未設定
// （Entryの内容は不定として扱うこと）。
typedef struct _KBDLAYER_GET_COMBO_OUTPUT {
    ULONG Used;
    KBDLAYER_COMBO_ENTRY Entry;
} KBDLAYER_GET_COMBO_OUTPUT, *PKBDLAYER_GET_COMBO_OUTPUT;

// --- Phase 5: タップダンス ---
//
// v1のスコープ限定（プラン検討時の合意事項）:
//   - タップ回数は3回まで区別する（SingleTap/DoubleTap/TripleTap）。4回目以降は
//     3回目と同じ扱い（上限到達時点で即確定）。
//   - ホールド判定は「1回目の押下」にのみ存在する（SingleHold）。2回目以降を
//     押しっぱなしにしても専用のHoldアクションは無く、その時点のタップ回数の
//     タップアクションが確定するだけ（DoubleHold/TripleHold相当は非対応）。
//   - SingleHoldActionのみ KEY/MODTAP/LAYERTAP を許可する（Phase 2のPendingActionと
//     同じ扱いを再利用するため）。SingleTap/DoubleTap/TripleTapは KEY のみ許可。
//   - 他キー割り込みによる確定は、Phase 2のHold On Other Key Pressと同じ規律
//     （即座に確定し、割り込みバッチは生スキャンコードのまま転送する）を踏襲する。
// 詳細な状態遷移はLayerEngine.cのResolveTapDance参照。
#define KBDLAYER_MAX_TAPDANCE_COUNT 16
#define KBDLAYER_MAX_TAP_COUNT 3 // これ以上は3回タップと同じ扱い

// タップダンス1件分。SingleTapAction.Type == KBDLAYER_ACTION_NONE はそのIDが未設定
// であることの合図として、Set時に「クリア」を意味する（common実装ではなく
// ControlDevice.c/LayerEngine.cの規約）。
typedef struct _KBDLAYER_TAPDANCE_ENTRY {
    KBDLAYER_ACTION SingleTapAction;  // v1: KEYのみ許可
    KBDLAYER_ACTION SingleHoldAction; // v1: KEY/MODTAP/LAYERTAPを許可
    KBDLAYER_ACTION DoubleTapAction;  // v1: KEYのみ許可
    KBDLAYER_ACTION TripleTapAction;  // v1: KEYのみ許可
} KBDLAYER_TAPDANCE_ENTRY, *PKBDLAYER_TAPDANCE_ENTRY;

// IOCTL_KBDLAYER_SET_TAPDANCE の入力バッファ。
// Entry.SingleTapAction.Type == KBDLAYER_ACTION_NONE を渡すと、そのTapDanceIdの
// クリアを意味する。
typedef struct _KBDLAYER_SET_TAPDANCE_INPUT {
    KBDLAYER_TARGET Target;
    ULONG TapDanceId; // 0 <= TapDanceId < KBDLAYER_MAX_TAPDANCE_COUNT
    KBDLAYER_TAPDANCE_ENTRY Entry;
} KBDLAYER_SET_TAPDANCE_INPUT, *PKBDLAYER_SET_TAPDANCE_INPUT;

// IOCTL_KBDLAYER_GET_TAPDANCE の入力バッファ。
typedef struct _KBDLAYER_GET_TAPDANCE_INPUT {
    KBDLAYER_TARGET Target;
    ULONG TapDanceId;
} KBDLAYER_GET_TAPDANCE_INPUT, *PKBDLAYER_GET_TAPDANCE_INPUT;

// IOCTL_KBDLAYER_GET_TAPDANCE の出力バッファ。Used=0の場合、指定TapDanceIdは未設定
// （Entryの内容は不定として扱うこと）。
typedef struct _KBDLAYER_GET_TAPDANCE_OUTPUT {
    ULONG Used;
    KBDLAYER_TAPDANCE_ENTRY Entry;
} KBDLAYER_GET_TAPDANCE_OUTPUT, *PKBDLAYER_GET_TAPDANCE_OUTPUT;

// --- Phase 6: NICOLA（親指シフト） ---
//
// NICOLA配列規格書（http://nicola.sunicom.co.jp/spec/kikaku.htm）の同時打鍵判定を
// カーネル内で行う。カーネルは「かな」を一切知らない: 物理キー × 3プレーン
// （単独 / 親指左 / 親指右）ごとに、送出するスキャンコード列（マクロと同じ
// KBDLAYER_MACRO_STEP）を持つだけ。ローマ字列にするかJISかな入力のキーにするかは
// KeymapTool側がこの表を組み立てる段階で決める。
//
// 判定はHeader.Layerのレイヤーがアクティブ（ActiveLayerMask|DefaultLayerビット）な
// 間だけ行う。判定アルゴリズムの詳細はkbdlayer/NicolaCore.c参照。

#define KBDLAYER_NICOLA_MAX_KEYS    64
#define KBDLAYER_NICOLA_MAX_STEPS   12 // 1文字あたり。ローマ字"xtsu"+Shift程度まで収まる
#define KBDLAYER_NICOLA_PLANE_COUNT 3

// Planes[]のインデックス
#define KBDLAYER_NICOLA_PLANE_NONE        0 // 単独打鍵
#define KBDLAYER_NICOLA_PLANE_THUMB_LEFT  1 // 親指左キーとの同時打鍵
#define KBDLAYER_NICOLA_PLANE_THUMB_RIGHT 2 // 親指右キーとの同時打鍵

#define KBDLAYER_NICOLA_VERSION 1

// KBDLAYER_NICOLA_HEADER.Flags
// 規格6.1「緩慢な打鍵操作」: 親指キー押下中はタイムアウトせず、押している間の
// 文字キーを常にシフトする（連続シフト）。親指キーの単独打鍵は、文字キーと組まずに
// 離したときにだけ確定する。
#define KBDLAYER_NICOLA_FLAG_CONTINUOUS_SHIFT 0x00000001

#define KBDLAYER_NICOLA_DEFAULT_SIMUL_TERM_MS     100 // 規格5.2: 実験的に50〜200msが妥当
#define KBDLAYER_NICOLA_DEFAULT_OVERLAP_RATIO_PCT 100 // 規格5.5.2の n (=1.0)

typedef struct _KBDLAYER_NICOLA_OUTPUT {
    UCHAR StepCount; // 0 = 何も送出しない
    UCHAR Reserved[3];
    KBDLAYER_MACRO_STEP Steps[KBDLAYER_NICOLA_MAX_STEPS];
} KBDLAYER_NICOLA_OUTPUT, *PKBDLAYER_NICOLA_OUTPUT;

typedef struct _KBDLAYER_NICOLA_KEY_ENTRY {
    USHORT SourceScanCode; // 物理スキャンコード（Enc16）
    USHORT Reserved;
    KBDLAYER_NICOLA_OUTPUT Planes[KBDLAYER_NICOLA_PLANE_COUNT];
} KBDLAYER_NICOLA_KEY_ENTRY, *PKBDLAYER_NICOLA_KEY_ENTRY;

// ワイヤー/レジストリ形式のブロブヘッダ。続けて KeyCount 個の KBDLAYER_NICOLA_KEY_ENTRY。
typedef struct _KBDLAYER_NICOLA_HEADER {
    ULONG  Version;             // KBDLAYER_NICOLA_VERSION
    ULONG  Enabled;             // 0 = 設定は保持したまま判定しない
    ULONG  Layer;               // このレイヤーがアクティブな間だけ判定する
    USHORT ThumbLeftScanCode;   // 親指左キーの物理スキャンコード（Enc16）
    USHORT ThumbRightScanCode;  // 親指右キーの物理スキャンコード（Enc16）
    USHORT ThumbLeftTapKey;     // 親指左の単独打鍵で送るキー（Enc16、0 = 何も送らない）
    USHORT ThumbRightTapKey;    // 親指右の単独打鍵で送るキー（Enc16、0 = 何も送らない）
    USHORT OnEnterKey;          // 判定が有効になったときに1回タップするキー（IMEオン等、0 = なし）
    USHORT OnExitKey;           // 判定が無効になったときに1回タップするキー（IMEオフ等、0 = なし）
    ULONG  SimulTermMs;         // 同時打鍵判定時間。0 = 既定値
    ULONG  OverlapRatioPercent; // 規格5.5.2の n を百分率で。0 = 既定値
    ULONG  MinOverlapMs;        // 規格5.5.2: 重なりがこれ未満なら逐次打鍵とみなす（0 = 無効）
    ULONG  Flags;               // KBDLAYER_NICOLA_FLAG_*
    ULONG  KeyCount;            // 続く KBDLAYER_NICOLA_KEY_ENTRY の数（<= KBDLAYER_NICOLA_MAX_KEYS）
} KBDLAYER_NICOLA_HEADER, *PKBDLAYER_NICOLA_HEADER;

#define KBDLAYER_MAX_NICOLA_BLOB_BYTES \
    (sizeof(KBDLAYER_NICOLA_HEADER) + (KBDLAYER_NICOLA_MAX_KEYS * sizeof(KBDLAYER_NICOLA_KEY_ENTRY)))

// IOCTL_KBDLAYER_SET_NICOLA の入力バッファ。BlobLength=0 は設定のクリアを意味する。
typedef struct _KBDLAYER_SET_NICOLA_INPUT {
    KBDLAYER_TARGET Target;
    ULONG BlobLength;
    // 続けて BlobLength バイトの KBDLAYER_NICOLA_HEADER + entries
} KBDLAYER_SET_NICOLA_INPUT, *PKBDLAYER_SET_NICOLA_INPUT;

// IOCTL_KBDLAYER_GET_NICOLA の入力は KBDLAYER_TARGET のみ。
// 出力は SET で渡したのと同じバイト列。未設定なら BlobLength=0 で成功する。
typedef struct _KBDLAYER_GET_NICOLA_OUTPUT {
    ULONG BlobLength;
    // 続けて BlobLength バイトの KBDLAYER_NICOLA_HEADER + entries
} KBDLAYER_GET_NICOLA_OUTPUT, *PKBDLAYER_GET_NICOLA_OUTPUT;

// --- レイヤー制御の双方向通知（Raw HID相当） ---
//
// アプリからレイヤーを立てる「アプリ管理レイヤー」と、レイヤー状態の取得・変化の通知。
// 設計の経緯は .plans/layer-notify-and-ime-agent.md（IME連動エージェントがこの上に乗る）。
//
// アプリ管理レイヤー（AppLayerMask）は、キーボード側のレイヤー（ActiveLayerMask。MO/TG/OSL/LT）
// とは別に持ち、実効レイヤー = ActiveLayerMask | AppLayerMask | DefaultLayerビット で合成する。
// - 最後にSET_LAYER_STATEを呼んだハンドルが所有者になる。そのハンドルが閉じられると
//   （プロセスのクラッシュを含む）、アプリ管理レイヤーは0に戻る。
// - キーボードのMOを離す・--off（SET_LAYERMAP_ENABLED=0）では消えない。
// - ORで合成するので、アプリが立てたレイヤーはキーボードのTGでは消せない。
// - 永続化しない。

// レイヤー状態のスナップショット。
typedef struct _KBDLAYER_LAYER_STATE {
    ULONG Seq;                // レイヤー状態が変わるたびに増える（WAIT_LAYER_CHANGEのLastSeqに渡す）
    ULONG ActiveLayerMask;    // キーボード側のレイヤー（MO/TG/OSL/LT。DefaultLayerを含まない）
    ULONG AppLayerMask;       // アプリ管理レイヤー
    ULONG DefaultLayer;       // DF(layer)で決まる基底レイヤー
    ULONG EffectiveLayerMask; // ActiveLayerMask | AppLayerMask | (1 << DefaultLayer)
    ULONG LayerMapEnabled;    // レイヤーマップがONか
    ULONG NicolaActive;       // NICOLA判定が有効か
} KBDLAYER_LAYER_STATE, *PKBDLAYER_LAYER_STATE;

// IOCTL_KBDLAYER_GET_LAYER_STATE の入力は KBDLAYER_TARGET のみ。出力は KBDLAYER_LAYER_STATE。

// IOCTL_KBDLAYER_WAIT_LAYER_CHANGE の入力。出力は KBDLAYER_LAYER_STATE。
// Seq != LastSeq になるまでIRPを保留する（すでに違えば即座に完了する）。
// 次のいずれかで完了する: レイヤー状態の変化（STATUS_SUCCESS）、キャンセル・ハンドルの
// クローズ（STATUS_CANCELLED）、キーボードの取り外し（STATUS_DEVICE_REMOVED）。
// 初回は GET_LAYER_STATE の Seq を LastSeq に渡す。
typedef struct _KBDLAYER_WAIT_LAYER_CHANGE_INPUT {
    KBDLAYER_TARGET Target;
    ULONG LastSeq;
} KBDLAYER_WAIT_LAYER_CHANGE_INPUT, *PKBDLAYER_WAIT_LAYER_CHANGE_INPUT;

// ドライバ全体で同時に保留できるWAIT_LAYER_CHANGEの数。超えるとSTATUS_INSUFFICIENT_RESOURCES。
#define KBDLAYER_MAX_PENDING_LAYER_WAITS 64

// KBDLAYER_SET_LAYER_STATE_INPUT.Op
#define KBDLAYER_LAYER_OP_ON       1 // Layerのビットを立てる
#define KBDLAYER_LAYER_OP_OFF      2 // Layerのビットを下ろす
#define KBDLAYER_LAYER_OP_TOGGLE   3 // Layerのビットを反転する
#define KBDLAYER_LAYER_OP_SET_MASK 4 // AppLayerMask を Mask にする

// KBDLAYER_SET_LAYER_STATE_INPUT.Flags
// この変更でNICOLA判定のON/OFFが切り替わっても、OnEnterKey/OnExitKeyを送らない
// （IME連動エージェントのように、IME側の変化を起点にレイヤーを変える場合に使う。
// IMEのオン/オフキーを送り返すとループするため）。
#define KBDLAYER_LAYER_STATE_FLAG_NO_NICOLA_KEYS 0x00000001

// IOCTL_KBDLAYER_SET_LAYER_STATE の入力。アプリ管理レイヤーだけを操作する。
// 出力バッファが KBDLAYER_LAYER_STATE 以上あれば、変更後の状態を返す（任意）。
// Layer / Mask のビットは、レイヤーマップのLayerCount（未設定ならKBDLAYER_MAX_LAYERS）
// 未満でなければSTATUS_INVALID_PARAMETER。
typedef struct _KBDLAYER_SET_LAYER_STATE_INPUT {
    KBDLAYER_TARGET Target;
    ULONG Op;    // KBDLAYER_LAYER_OP_*
    ULONG Layer; // OP_ON / OP_OFF / OP_TOGGLE で使う
    ULONG Mask;  // OP_SET_MASK で使う
    ULONG Flags; // KBDLAYER_LAYER_STATE_FLAG_*
} KBDLAYER_SET_LAYER_STATE_INPUT, *PKBDLAYER_SET_LAYER_STATE_INPUT;

#pragma pack(pop)

// IOCTL_INTERNAL_KEYBOARD_CONNECT 等の既存IOCTLと衝突しないよう、
// カスタムのデバイスタイプを使う（"user-defined"範囲: 0x8000〜0xFFFF）。
// kbdaddidの FILE_DEVICE_KBDADDID (0x8010) とも重ならない値を選ぶ。
#define FILE_DEVICE_KBDLAYER 0x00008020

// アクセス権の分け方（.plans/ioctl-access-control-review.md の案2）:
// - FILE_READ_ACCESS: 一覧・設定の読み出しと、設定済みマップのON/OFF。
//   制御デバイスのSDDLはEveryoneにGRだけを与えるので、一般ユーザーのプロセスは
//   GENERIC_READで開いてこれらを呼べる。
// - FILE_WRITE_ACCESS: キー入力の注入になり得る設定の書き換え（SET_*/CLEAR）と、
//   キーロガーになり得る CAPTURE_NEXT_KEY / SET_CAPTURE_SUPPRESS。
//   GENERIC_WRITEで開けるのはSYSTEMとAdministrators（昇格済み）だけ。
// I/Oマネージャが、ハンドルの許可アクセスとIOCTLのアクセスビットを照合して拒否する
// （ドライバに届く前にSTATUS_ACCESS_DENIEDになる）。
// アクセスビットはIOCTLのコード値の一部なので、変えるとクライアントの再ビルドが要る
// （kbdlayer-keymap-gui の KbdLayerClient.cs は値を自前で計算しているので、そちらも合わせる）。
#define IOCTL_KBDLAYER_LIST_DEVICES \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x800, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_KBDLAYER_GET_LAYERMAP \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x801, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_KBDLAYER_SET_LAYERMAP \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x802, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_KBDLAYER_CLEAR_LAYERMAP \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x803, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_KBDLAYER_SET_LAYERMAP_ENABLED \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x804, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_KBDLAYER_CAPTURE_NEXT_KEY \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x805, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_KBDLAYER_SET_CONFIG \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x806, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_KBDLAYER_SET_MACRO \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x807, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_KBDLAYER_GET_MACRO \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x808, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_KBDLAYER_SET_COMBO \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x809, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_KBDLAYER_GET_COMBO \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x80A, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_KBDLAYER_SET_TAPDANCE \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x80B, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_KBDLAYER_GET_TAPDANCE \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x80C, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_KBDLAYER_SET_CAPTURE_SUPPRESS \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x80D, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_KBDLAYER_GET_CONFIG \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x80E, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_KBDLAYER_SET_NICOLA \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x80F, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_KBDLAYER_GET_NICOLA \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x810, METHOD_BUFFERED, FILE_READ_ACCESS)
// レイヤー制御の双方向通知。SET_LAYER_STATEは、SET_LAYERMAP_ENABLED（マップのON/OFF）と
// 同じく「ユーザー自身の設定の範囲内での切り替え」なのでFILE_READ_ACCESSにする。任意の
// キー入力の注入にはならず、昇格していないIME連動エージェントからも呼べる。
#define IOCTL_KBDLAYER_GET_LAYER_STATE \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x811, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_KBDLAYER_WAIT_LAYER_CHANGE \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x812, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_KBDLAYER_SET_LAYER_STATE \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x813, METHOD_BUFFERED, FILE_READ_ACCESS)

#endif // _KBDLAYER_COMMON_IOCTL_H_
