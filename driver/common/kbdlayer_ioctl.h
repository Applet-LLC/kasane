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
// OSM(mod)（One Shot Mod、2026-10-02実装）: Param1=修飾子マスク（KBDLAYER_MOD_*のOR、0以外）、Param2=0。
// 押している間は修飾キーとして働く。別のキーを押さずに離すと、次に押す（修飾キーでない）キー1つにだけ
// その修飾キーが掛かる。同じOSMをもう一度タップすると解除する。ロックと時間切れは無し。
#define KBDLAYER_ACTION_ONESHOT_MOD   7
#define KBDLAYER_ACTION_MODTAP        8  // MT(mod,kc): Param1=修飾子ビット, Param2=タップ時スキャンコード (Phase 2)
#define KBDLAYER_ACTION_LAYERTAP      9  // LT(layer,kc): Param1=レイヤー番号, Param2=タップ時スキャンコード (Phase 2)
#define KBDLAYER_ACTION_MACRO         10 // MACRO(id): Param1=マクロID(0..KBDLAYER_MAX_MACRO_COUNT-1) (Phase 3)
// CUSTOM（ヘルパーイベント、2026-10-08実装。QMKのRaw HIDに近い）: Param1=0（予約。0以外は拒否）、
// Param2=ヘルパー動作のID（0〜65535）。キーは出力せず、押下・オートリピート・解放を
// IOCTL_KBDLAYER_WAIT_HELPER_EVENT で待っているアプリ（kasane-tray）へ知らせる。動作の中身
// （アプリの起動・文字列の入力など）はアプリ側で決める。置けるのはレイヤーマップだけ
// （コンボ・タップダンス・キー置換の出力には置けない）。これより前のドライバは、CUSTOMを
// 何もしないキーとして扱う（キーは出ない）。
#define KBDLAYER_ACTION_CUSTOM        11
#define KBDLAYER_ACTION_TAPDANCE      12 // TAPDANCE(id): Param1=タップダンスID(0..KBDLAYER_MAX_TAPDANCE_COUNT-1) (Phase 5)
// 修飾付きキー（QMKのLCTL(kc)等に相当）: Param1=修飾子マスク（0以外）, Param2=送出スキャンコード(Enc16)。
// 押下で、まだ押されていない修飾キーを足してからキーを送り、解放でキーを離してから足した分を戻す。
#define KBDLAYER_ACTION_MODDED_KEY    13
// レイヤー＋修飾（QMKのLM(layer,mod)に相当）: Param1=レイヤー番号, Param2の下位8bit=修飾子マスク（0以外）,
// Param2の上位8bitは0。押している間、レイヤーをONにし、修飾キーを足す。
#define KBDLAYER_ACTION_LAYER_MOD     14
// TO(layer)（QMKのTO相当）: Param1=レイヤー番号。押すと、そのレイヤーだけを有効にし、キーボード側の
// 他のレイヤー（MO/TG/OSL/LT/TTで有効にしたもの）をすべて無効にする。デフォルトレイヤー（DF）と
// アプリ管理レイヤーはそのまま。
#define KBDLAYER_ACTION_LAYER_TO      15
// TT(layer)（QMKのTT相当）: Param1=レイヤー番号。押している間はMOと同じ。続けて
// TappingToggle回（既定KBDLAYER_DEFAULT_TAPPING_TOGGLE）タップするとTGと同じくON/OFFを切り替え、そのままにする。
#define KBDLAYER_ACTION_LAYER_TAP_TOGGLE 16
// ParseLayerMapBlobが受け付けるTypeの上限（古いドライバは、これより大きいTypeを含むマップを拒否する）。
#define KBDLAYER_ACTION_TYPE_MAX      KBDLAYER_ACTION_LAYER_TAP_TOGGLE

// TT(layer)でレイヤーを切り替えるのに必要な連続タップ回数の既定値（QMKの既定値と同じ）。
// SET_CONFIGのTappingToggleで変えられる（1〜KBDLAYER_MAX_TAPPING_TOGGLE）。
#define KBDLAYER_DEFAULT_TAPPING_TOGGLE 5
#define KBDLAYER_MAX_TAPPING_TOGGLE     32

// MT/LTのタップ／ホールドの判定方式（SET_CONFIGのTapHoldMode）。タップダンスの長押しの判定は
// 対象外（常にHOLD_ON_OTHER_KEYと同じ）。QMKの同名の設定の外部仕様（docs.qmk.fm）を参考にした。
// - HOLD_ON_OTHER_KEY: 判定中に別のキーが押されたら、すぐにホールドに確定する（既定。2026-09まではこれだけ）。
// - PERMISSIVE_HOLD: 判定中に別のキーを押して離したら（そのキーのタップが判定中に終わったら）ホールド。
//   別のキーを押しただけで、先にMT/LTを離せばタップ（＋そのキー）。
// - TAP_PREFERRED: 判定時間内にMT/LTを離せば、間に別のキーを押して離していてもタップ。判定時間が
//   過ぎたらホールド（QMKの既定に近い）。
// PERMISSIVE_HOLDとTAP_PREFERREDでは、判定中に押された別のキーのイベントをドライバが溜め、判定が
// 決まってから順に流し直す（KBDLAYER_MAX_HOLD_BUFFER件まで。あふれたらホールドに確定する）。
#define KBDLAYER_TAPHOLD_MODE_HOLD_ON_OTHER_KEY 0
#define KBDLAYER_TAPHOLD_MODE_PERMISSIVE_HOLD   1
#define KBDLAYER_TAPHOLD_MODE_TAP_PREFERRED     2
#define KBDLAYER_TAPHOLD_MODE_MAX               KBDLAYER_TAPHOLD_MODE_TAP_PREFERRED
#define KBDLAYER_MAX_HOLD_BUFFER 8

// Quick Tap（QMKのQuick Tap Term相当）を使わない、を表すQuickTapTermMsの値。
#define KBDLAYER_QUICK_TAP_TERM_DISABLED 0xFFFFFFFFUL

// 修飾子マスク（MODDED_KEY/LAYER_MODで使う）のビット。**MODTAPのParam1（ビット番号0〜7）とは
// 表し方が違う**ので注意（MODTAPはビット番号、こちらはビットのOR）。並びは同じ。
#define KBDLAYER_MOD_LCTL 0x01
#define KBDLAYER_MOD_LSFT 0x02
#define KBDLAYER_MOD_LALT 0x04
#define KBDLAYER_MOD_LGUI 0x08
#define KBDLAYER_MOD_RCTL 0x10
#define KBDLAYER_MOD_RSFT 0x20
#define KBDLAYER_MOD_RALT 0x40
#define KBDLAYER_MOD_RGUI 0x80

// VIA実務上の上限(4)に余裕を持たせる。2026-09-29に8→16へ引き上げ（ヘルパーアプリが
// アプリごとのレイヤーをSET_LAYER_STATEで切り替える用途に備えて）。レイヤーのビットマスク
// （ActiveLayerMask等）はULONGなので32まで入るが、Actions[]の容量が比例して増えるため16に留める。
// 変えるときは kasane の KbdLayerClient.MaxLayers も合わせること。
#define KBDLAYER_MAX_LAYERS 16
#define KBDLAYER_MAX_KEYS   256 // 既存 KBDADDID_MAX_REMAP_ENTRIES を踏襲

// 1物理キー分のエントリ: 物理スキャンコード(Enc16) + レイヤー数分のアクション。
typedef struct _KBDLAYER_KEY_ENTRY {
    USHORT SourceScanCode;                       // キャリブレーションで得た物理スキャンコード（Enc16形式）
    KBDLAYER_ACTION Actions[KBDLAYER_MAX_LAYERS]; // Actions[0]=レイヤー0(基底)用 ... Actions[N-1]
} KBDLAYER_KEY_ENTRY, *PKBDLAYER_KEY_ENTRY;

// ワイヤー/レジストリ形式のブロブヘッダ。続けて KeyCount 個の KBDLAYER_KEY_ENTRY が続く。
// レイヤー数は常に KBDLAYER_MAX_LAYERS（可変ではない。2026-09-29に LayerCount フィールドを
// 廃止した。1キーあたりの Actions[] は元々 KBDLAYER_MAX_LAYERS 分固定で確保されており、
// LayerCount は「実際に使う数」を示すだけで容量には影響していなかったため、削っても
// blobの1キーあたりのバイト数は変わらない。未使用レイヤー分は呼び出し側が
// KBDLAYER_ACTION_TRANSPARENT で埋める）。
typedef struct _KBDLAYER_MAP_HEADER {
    ULONG Header;      // 予約。常に0。
    ULONG KeyCount;    // 続く KBDLAYER_KEY_ENTRY の数（<= KBDLAYER_MAX_KEYS）
    // 続けて KeyCount 個の KBDLAYER_KEY_ENTRY
} KBDLAYER_MAP_HEADER, *PKBDLAYER_MAP_HEADER;

// blob全体（ヘッダ+エントリ列）の最大バイト数。
// 256 keys * (2 + 16*4) bytes = 256 * 66 = 16896 bytes 程度。
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
// Flags（旧クライアントはこの項目を送らない＝入力が8バイト。その場合は0として扱う）:
//   KBDLAYER_CAPTURE_FLAG_ALLOW_FAKE_SHIFT: 疑似Shift（E0 2A / E0 36）も捕捉の対象にする。
//   既定（0）では、PrintScreen等の前置きの疑似Shiftと区別できないため除外している。
//   右ShiftをE0 36で出す一部のキーボードを実測するときだけ指定する（Shiftのスロット専用。
//   PrintScreenのスロットで指定すると、前置きのE0 2Aが先に捕捉される）。
#define KBDLAYER_CAPTURE_FLAG_ALLOW_FAKE_SHIFT 0x00000001
#define KBDLAYER_CAPTURE_FLAGS_MASK            0x00000001
// 旧クライアントの入力サイズ（Flagsを含まない）。
#define KBDLAYER_CAPTURE_NEXT_KEY_INPUT_V1_SIZE (sizeof(KBDLAYER_TARGET) + sizeof(ULONG))
typedef struct _KBDLAYER_CAPTURE_NEXT_KEY_INPUT {
    KBDLAYER_TARGET Target;
    ULONG TimeoutMs;
    ULONG Flags;
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
// 2026-10-01に末尾へTapHoldMode/QuickTapTermMs/TappingToggleを追加した。ドライバは旧サイズ
// （KBDLAYER_SET_CONFIG_INPUT_V1_SIZE）の入力も受け付け、その場合は追加した項目を変えない。
// 古いドライバは入力が長くても先頭の項目だけを使う（長さは下限しか検査しない）。
typedef struct _KBDLAYER_SET_CONFIG_INPUT {
    KBDLAYER_TARGET Target;
    ULONG TapTermMs;      // 既定200ms。0を渡すと既定値にリセットする。
    ULONG ComboTermMs;    // 既定50ms。0を渡すと既定値にリセットする。(Phase 4: コンボ)
    ULONG TapDanceTermMs; // 既定200ms。0を渡すと既定値にリセットする。(Phase 5: タップダンス)
    // --- 以下 v2（2026-10-01） ---
    ULONG TapHoldMode;    // KBDLAYER_TAPHOLD_MODE_*。範囲外はSTATUS_INVALID_PARAMETER。
    ULONG QuickTapTermMs; // 0 = TapTermMsと同じ（既定）。KBDLAYER_QUICK_TAP_TERM_DISABLED = 使わない。
    ULONG TappingToggle;  // TT(layer)の回数。0 = 既定(5)。KBDLAYER_MAX_TAPPING_TOGGLEを超えたらSTATUS_INVALID_PARAMETER。
} KBDLAYER_SET_CONFIG_INPUT, *PKBDLAYER_SET_CONFIG_INPUT;
#define KBDLAYER_SET_CONFIG_INPUT_V1_SIZE ((ULONG)FIELD_OFFSET(KBDLAYER_SET_CONFIG_INPUT, TapHoldMode)) // カーネルのFIELD_OFFSETはLONG

// IOCTL_KBDLAYER_GET_CONFIG の入力は KBDLAYER_TARGET のみ。出力は現在の実効値
// （未設定・0指定で既定値に戻っている項目は既定値そのもの。QuickTapTermMsだけは設定値のまま返す）。
// 出力バッファが旧サイズ（KBDLAYER_GET_CONFIG_OUTPUT_V1_SIZE）なら、先頭の3項目だけを返す。
typedef struct _KBDLAYER_GET_CONFIG_OUTPUT {
    ULONG TapTermMs;
    ULONG ComboTermMs;
    ULONG TapDanceTermMs;
    // --- 以下 v2（2026-10-01） ---
    ULONG TapHoldMode;
    ULONG QuickTapTermMs;
    ULONG TappingToggle;
} KBDLAYER_GET_CONFIG_OUTPUT, *PKBDLAYER_GET_CONFIG_OUTPUT;
#define KBDLAYER_GET_CONFIG_OUTPUT_V1_SIZE ((ULONG)FIELD_OFFSET(KBDLAYER_GET_CONFIG_OUTPUT, TapHoldMode))

// --- 機能別ON/OFF（release-roadmap-priority.md 項番4） ---
//
// 一般ユーザーにとって使わないことが多いMacro/Combo/TapDanceを、それぞれ無効化できるように
// する。無効化しても、キーの置き換えとレイヤー（MO/TG/DF/OSL）・MT/LT・NICOLAはそのまま使える。
// 無効な機能のキー（MACRO(id)/TAPDANCE(id)のアクション、Comboのメンバーキー）は、押しても
// 何も出力しない（KC_NOと同じ扱い）。設定（SET_MACRO/SET_COMBO/SET_TAPDANCE）自体は無効中でも
// 受け付ける（保持するだけで、有効化するまで効かない。レイヤーマップのOFF中にSET_LAYERMAPが
// できるのと同じ考え方）。キーボードごとの設定で、永続化する（既定は全機能有効）。

#define KBDLAYER_FEATURE_MACRO    0x00000001
#define KBDLAYER_FEATURE_COMBO    0x00000002
#define KBDLAYER_FEATURE_TAPDANCE 0x00000004
#define KBDLAYER_FEATURE_KEYOVERRIDE 0x00000008 // 2026-10-01追加
// 2026-09（機能別ON/OFFの導入時）の3機能。永続化の旧値（EnabledFeatures）はこのビットだけを持つ。
#define KBDLAYER_FEATURE_ALL_V1 \
    (KBDLAYER_FEATURE_MACRO | KBDLAYER_FEATURE_COMBO | KBDLAYER_FEATURE_TAPDANCE)
#define KBDLAYER_FEATURE_ALL \
    (KBDLAYER_FEATURE_ALL_V1 | KBDLAYER_FEATURE_KEYOVERRIDE)

// IOCTL_KBDLAYER_SET_FEATURES の入力バッファ。Featuresに未知のビットが含まれていたら
// STATUS_INVALID_PARAMETER。
typedef struct _KBDLAYER_SET_FEATURES_INPUT {
    KBDLAYER_TARGET Target;
    ULONG Features; // KBDLAYER_FEATURE_* のOR
} KBDLAYER_SET_FEATURES_INPUT, *PKBDLAYER_SET_FEATURES_INPUT;

// IOCTL_KBDLAYER_GET_FEATURES の入力は KBDLAYER_TARGET のみ。
typedef struct _KBDLAYER_GET_FEATURES_OUTPUT {
    ULONG Features; // KBDLAYER_FEATURE_* のOR
} KBDLAYER_GET_FEATURES_OUTPUT, *PKBDLAYER_GET_FEATURES_OUTPUT;

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

// KBDLAYER_ACTION_MACROのParam1(UCHAR)が指せる範囲を実務的に制限。2026-09-30に32→128へ
// 引き上げた（ドライバはレジストリの旧サイズ（32個分）の"Macros"値も読める）。
// 変えるときは kasane の KbdLayerClient.MaxMacroCount も合わせること。
#define KBDLAYER_MAX_MACRO_COUNT 128
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

// --- Key Override（2026-10-01。QMKのKey Overridesの外部仕様を参考にした） ---
//
// 「指定した修飾キーを押しながら、指定したキーを押す」と、別のキー（修飾付きキーも可）に置き換える。
// 例: Shift+2 → @（US配列の刻印のキーボードをJIS設定のWindowsで使う）、Shift+BS → Delete。
//   - TriggerScanCodeは、レイヤーで解決した後の出力キー（KEYアクションのスキャンコード。レイヤー
//     マップに無いキーは物理スキャンコード）。
//   - TriggerModsは KBDLAYER_MOD_* のOR。左右は区別せず、Ctrl・Shift・Alt・Winの種類ごとに、
//     指定した種類が**全部**押されているときに発動する（左右どちらでもよい）。指定していない修飾キーが
//     押されていても発動する（その修飾キーは置き換え先にもそのまま掛かる）。
//   - 発動すると、TriggerModsの種類の修飾キーをOS上で一時的に離し、Replacementを押す。置き換え先の
//     キーを離したときに、まだ押されている修飾キーを戻す。
//   - 置き換え中に条件の修飾キーを離しても、置き換えはキーを離すまで続く（押下と解放を同じ出力にする）。
//   - 置き換え中に別のキーを押すと、そのキーの前に一時的に離していた修飾キーを戻す（置き換えは続く）。
//   - ReplacementはKEY、MODDED_KEY、MACRO（2026-10-15）。
//   - ReplacementがMACRO（Param1=マクロID）のときは、キーを押した時点でマクロを1回送る。送る間だけ、
//     TriggerModsの種類の修飾キー（押されている分）をOS上で離し、送り終えたら、まだ押されているものを
//     戻す（マクロの出力に条件の修飾キーが掛からない）。キーの解放は飲み込む。マクロの機能
//     （KBDLAYER_FEATURE_MACRO）がOFFのときは、この置き換えは発動しない（キーはそのまま通る）。
//     オートリピートでは、既定では何もしない。KBDLAYER_KEY_OVERRIDE_FLAG_REPEATを立てたときだけ、
//     リピートのたびに同じマクロを再実行する（マクロキー MACRO(n) のオートリピートと同じ）。
//   - 機能別ON/OFFのKBDLAYER_FEATURE_KEYOVERRIDEで無効にできる。
#define KBDLAYER_MAX_KEY_OVERRIDE_COUNT 32

// KBDLAYER_KEY_OVERRIDE_ENTRY.Flags のビット。ReplacementがMACROのときだけ指定できる。
#define KBDLAYER_KEY_OVERRIDE_FLAG_REPEAT 0x01 // オートリピートでマクロを繰り返す

typedef struct _KBDLAYER_KEY_OVERRIDE_ENTRY {
    UCHAR TriggerMods;            // KBDLAYER_MOD_* のOR（0以外）
    UCHAR Flags;                  // KBDLAYER_KEY_OVERRIDE_FLAG_*のOR。未定義のビットは0
    USHORT TriggerScanCode;       // Enc16（0以外）
    KBDLAYER_ACTION Replacement;  // KEY / MODDED_KEY / MACRO
} KBDLAYER_KEY_OVERRIDE_ENTRY, *PKBDLAYER_KEY_OVERRIDE_ENTRY;

// IOCTL_KBDLAYER_SET_KEY_OVERRIDE の入力バッファ。
// Entry.Replacement.Type == KBDLAYER_ACTION_NONE を渡すと、そのIdのクリアを意味する。
typedef struct _KBDLAYER_SET_KEY_OVERRIDE_INPUT {
    KBDLAYER_TARGET Target;
    ULONG Id; // 0 <= Id < KBDLAYER_MAX_KEY_OVERRIDE_COUNT
    KBDLAYER_KEY_OVERRIDE_ENTRY Entry;
} KBDLAYER_SET_KEY_OVERRIDE_INPUT, *PKBDLAYER_SET_KEY_OVERRIDE_INPUT;

// IOCTL_KBDLAYER_GET_KEY_OVERRIDE の入力バッファ。
typedef struct _KBDLAYER_GET_KEY_OVERRIDE_INPUT {
    KBDLAYER_TARGET Target;
    ULONG Id;
} KBDLAYER_GET_KEY_OVERRIDE_INPUT, *PKBDLAYER_GET_KEY_OVERRIDE_INPUT;

// IOCTL_KBDLAYER_GET_KEY_OVERRIDE の出力バッファ。Used=0なら未設定。
typedef struct _KBDLAYER_GET_KEY_OVERRIDE_OUTPUT {
    ULONG Used;
    KBDLAYER_KEY_OVERRIDE_ENTRY Entry;
} KBDLAYER_GET_KEY_OVERRIDE_OUTPUT, *PKBDLAYER_GET_KEY_OVERRIDE_OUTPUT;

// --- Phase 4: コンボ ---
//
// v1のスコープ限定（.plans/...md 検討時の合意事項）:
//   - メンバーキーは常に2つ（3キー以上の一般化は将来検討）
//   - 同一の物理スキャンコードが複数のコンボに属することは禁止（Set時に検証）
//   - 出力アクションはKEY・MODDED_KEY・MACRO（2026-10-02にKEYのみから拡張。MO/TG等のレイヤー操作は未対応）
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
    // KEY・MODDED_KEY（押している間、修飾キーを足す）・MACRO（成立時に1回実行）を許可
    // （2026-10-02にKEYのみから拡張。古いドライバはKEY以外をSTATUS_INVALID_PARAMETERで拒否する）。
    KBDLAYER_ACTION OutputAction;
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
//   - SingleHoldActionは KEY/MODTAP/LAYERTAP（Phase 2のPendingActionと同じ扱いを再利用）と、
//     2026-10-02から MODDED_KEY/LAYER_MOD/MACRO を許可する。SingleTap/DoubleTap/TripleTapは KEY・MODDED_KEY（修飾付きキー。
//     修飾子マスクは0以外）・MACRO（IDは範囲内、Param2は0）を許可する（2026-09-30にKEYのみから拡張。
//     タップ側は確定した時点で押下と解放を続けて送るので、押している間の状態を持たない）。
//     古いドライバはKEY以外のタップ側をSTATUS_INVALID_PARAMETERで拒否する。
//   - 他キー割り込みによる確定は、Phase 2のHold On Other Key Pressと同じ規律を踏襲する
//     （即座に確定し、割り込んだキーは確定後のレイヤーで通常どおり解決する。2026-09-30までは
//     生スキャンコードのまま転送していた）。
// 詳細な状態遷移はLayerEngine.cのResolveTapDance参照。
#define KBDLAYER_MAX_TAPDANCE_COUNT 16
#define KBDLAYER_MAX_TAP_COUNT 3 // これ以上は3回タップと同じ扱い

// タップダンス1件分。SingleTapAction.Type == KBDLAYER_ACTION_NONE はそのIDが未設定
// であることの合図として、Set時に「クリア」を意味する（common実装ではなく
// ControlDevice.c/LayerEngine.cの規約）。
typedef struct _KBDLAYER_TAPDANCE_ENTRY {
    // 「動作なし」は、KEYのスキャンコード0（KC_NO）で表す。SingleHoldとDouble/TripleTapはNONEも可
    // （SingleTapのNONEはSET時に「クリア」の意味になるため、KEY・スキャンコード0を使うこと）。
    KBDLAYER_ACTION SingleTapAction;  // KEY/MODDED_KEY/MACROを許可
    // NONE/KEY/MODTAP/LAYERTAP、2026-10-02からMODDED_KEY/LAYER_MOD（押している間、修飾キーを足す）/
    // MACRO（長押しの確定時に1回実行）を許可。
    KBDLAYER_ACTION SingleHoldAction;
    KBDLAYER_ACTION DoubleTapAction;  // NONE/KEY/MODDED_KEY/MACROを許可
    KBDLAYER_ACTION TripleTapAction;  // NONE/KEY/MODDED_KEY/MACROを許可
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
//   （プロセスのクラッシュを含む）、アプリ管理レイヤーは0に戻る。このときは、NICOLAの
//   ON/OFFが切り替わってもOnEnterKey/OnExitKeyを送らない（SET時のFlagsに関わらず。
//   持ち主が落ちた時点のIMEの状態は分からず、トグル型のキーだと逆にIMEを反転させるため）。
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

// --- ヘルパーイベント（CUSTOMアクションの通知、2026-10-08） ---
//
// レイヤーマップのCUSTOM(id)を割り当てたキーの押下・オートリピート・解放を、ドライバ全体で
// 1本のキュー（KBDLAYER_HELPER_EVENT_QUEUE_SIZE件のリング）に積み、WAIT_HELPER_EVENTで
// 待っているアプリへ渡す。キー自体は出力しない。
// - キーボードを指定しない（全キーボードのイベントが届く。UniqIdで区別する）。
// - 押下と解放の対応は、ほかのアクションと同じく押下時の記録（HeldKeys）に従う。押している間に
//   レイヤーが変わっても、マップをOFFにしても、解放は同じCustomIdで届く。
// - 複数のアプリ（ユーザーごとのトレイなど）が待っていれば、全員に同じイベントを渡す。
//   どのアプリが動作を実行するか（アクティブなセッションのものだけ、など）はアプリ側で決める。
// - 読み出しが追いつかずにリングから押し出されたイベントは、Droppedで件数だけを知らせる。

// イベント1件（20バイト）。
typedef struct _KBDLAYER_HELPER_EVENT {
    ULONG  Seq;       // ドライバ全体で単調に増える番号（0とKBDLAYER_HELPER_SEQ_LATESTは使わない）
    ULONG  UniqId;    // 押されたキーボードのDeviceId（KBDLAYER_TARGET.UniqIdと同じ値）
    USHORT CustomId;  // CUSTOMアクションのParam2
    UCHAR  IsBreak;   // 0 = 押下（オートリピートを含む）、1 = 解放
    UCHAR  Flags;     // KBDLAYER_HELPER_EVENT_FLAG_*
    UCHAR  Modifiers; // そのとき普通の出力として押されていた修飾キー（KBDLAYER_MOD_*のOR）
    UCHAR  Reserved[3];
    ULONG  TimeMs;    // KeQueryInterruptTime（起動からの時間）のミリ秒。古いイベントの判定用
} KBDLAYER_HELPER_EVENT, *PKBDLAYER_HELPER_EVENT;

// KBDLAYER_HELPER_EVENT.Flags
#define KBDLAYER_HELPER_EVENT_FLAG_REPEAT 0x01 // 押下中のキーのオートリピート

// ドライバ全体のキューの件数。
#define KBDLAYER_HELPER_EVENT_QUEUE_SIZE 64

// WAIT_HELPER_EVENTのLastSeqに渡すと、「今より後のイベント」を待つ（起動直後のアプリが、
// 過去のイベントを実行しないため）。
#define KBDLAYER_HELPER_SEQ_LATEST 0xFFFFFFFFUL

// IOCTL_KBDLAYER_WAIT_HELPER_EVENT の入力。
// Seq が LastSeq より新しいイベントがあれば、すぐに完了する。無ければIRPを保留する。
// 次のいずれかで完了する: イベント（STATUS_SUCCESS）、キャンセル・ハンドルのクローズ
// （STATUS_CANCELLED）。初回は LastSeq = KBDLAYER_HELPER_SEQ_LATEST で呼び、以後は出力のSeqを渡す。
typedef struct _KBDLAYER_WAIT_HELPER_EVENT_INPUT {
    ULONG LastSeq;
    ULONG Flags;   // 予約（0）
} KBDLAYER_WAIT_HELPER_EVENT_INPUT, *PKBDLAYER_WAIT_HELPER_EVENT_INPUT;

// IOCTL_KBDLAYER_WAIT_HELPER_EVENT の出力。続けてCount件のKBDLAYER_HELPER_EVENT。
// 出力バッファは、少なくともこの構造体＋イベント1件分が必要（足りなければSTATUS_BUFFER_TOO_SMALL）。
// 入りきらなかったイベントは、次の呼び出し（LastSeq = 出力のSeq）で返る。
typedef struct _KBDLAYER_WAIT_HELPER_EVENT_OUTPUT {
    ULONG Seq;      // 返した最後のイベントのSeq（次のLastSeqに渡す）
    ULONG Count;    // 続くイベントの件数（1以上）
    ULONG Dropped;  // LastSeqより後に起きたが、キューから押し出されて返せなかった件数
    ULONG Reserved;
    // KBDLAYER_HELPER_EVENT Events[Count];
} KBDLAYER_WAIT_HELPER_EVENT_OUTPUT, *PKBDLAYER_WAIT_HELPER_EVENT_OUTPUT;

// ドライバ全体で同時に保留できるWAIT_HELPER_EVENTの数。超えるとSTATUS_INSUFFICIENT_RESOURCES。
// （Everyoneが呼べるIOCTLなので、非ページプールを使い切らせる攻撃を防ぐ。通常はユーザーごとの
// トレイが1つずつ待つだけ。）
#define KBDLAYER_MAX_PENDING_HELPER_WAITS 16

// --- キーイベントの継続的な通知（IOCTL_KBDLAYER_WAIT_KEY_EVENT、2026-10-14） ---
//
// キーボードから届いた生の物理キー（レイヤーマップなどで置き換える前のスキャンコード）の
// 押下・解放を、ドライバ全体で1本のキュー（KBDLAYER_KEY_EVENT_QUEUE_SIZE件のリング）に積み、
// WAIT_KEY_EVENTで待っているアプリへ渡す。設定画面の「入力確認」「同時押しを試す」が使う
// （LLフックは、キーボードレイアウトDLLの影響で右Shiftや日本語キーの解放を取りこぼす）。
// 構造はWAIT_HELPER_EVENTと同じ（保留IRP、LastSeq、Dropped）。違い:
//  - **キー入力が丸見えになる**（キーロガーになりうる）ので、FILE_WRITE_ACCESS（管理者のみ。
//    制御デバイスのSDDLが、Everyoneには読み取りしか与えない）。CAPTURE_NEXT_KEYと同じ方針。
//  - 積むのは、このIOCTLで待ち始めたハンドル（所有者）が1つ以上あるときだけ。所有者は、ハンドルの
//    クローズ（プロセスの終了・クラッシュを含む）で外れる。WAITの合間も、所有者でいる間は積む。
//  - 疑似Shift（E0 2A / E0 36）は出さない。Pause/BreakはE11Dの押下・解放だけを出す（2イベント目の {45}/{C5} は出さない）。
//  - オートリピートの押下も、そのまま届く（区別しない）。

// イベント1件（16バイト）。
typedef struct _KBDLAYER_KEY_EVENT {
    ULONG  Seq;       // ドライバ全体で単調に増える番号（0とKBDLAYER_KEY_SEQ_LATESTは使わない）
    ULONG  UniqId;    // 押されたキーボードのDeviceId（KBDLAYER_TARGET.UniqIdと同じ値）
    USHORT ScanCode;  // Enc16（上位バイト: 0 / 0xE0 / 0xE1、下位バイト: MakeCode）
    UCHAR  IsBreak;   // 0 = 押下（オートリピートを含む）、1 = 解放
    UCHAR  Reserved;
    ULONG  TimeMs;    // KeQueryInterruptTime（起動からの時間）のミリ秒
} KBDLAYER_KEY_EVENT, *PKBDLAYER_KEY_EVENT;

// ドライバ全体のキューの件数。
#define KBDLAYER_KEY_EVENT_QUEUE_SIZE 256

// WAIT_KEY_EVENTのLastSeqに渡すと、「今より後のイベント」を待つ。
#define KBDLAYER_KEY_SEQ_LATEST 0xFFFFFFFFUL

// IOCTL_KBDLAYER_WAIT_KEY_EVENT の入力。完了の条件と使い方は WAIT_HELPER_EVENT と同じ
// （初回は LastSeq = KBDLAYER_KEY_SEQ_LATEST、以後は出力のSeq）。
typedef struct _KBDLAYER_WAIT_KEY_EVENT_INPUT {
    ULONG LastSeq;
    ULONG Flags;   // 予約（0）
} KBDLAYER_WAIT_KEY_EVENT_INPUT, *PKBDLAYER_WAIT_KEY_EVENT_INPUT;

// IOCTL_KBDLAYER_WAIT_KEY_EVENT の出力。続けてCount件のKBDLAYER_KEY_EVENT。
// 出力バッファは、少なくともこの構造体＋イベント1件分が必要（足りなければSTATUS_BUFFER_TOO_SMALL）。
typedef struct _KBDLAYER_WAIT_KEY_EVENT_OUTPUT {
    ULONG Seq;      // 返した最後のイベントのSeq（次のLastSeqに渡す）
    ULONG Count;    // 続くイベントの件数（1以上）
    ULONG Dropped;  // LastSeqより後に起きたが、キューから押し出されて返せなかった件数
    ULONG Reserved;
    // KBDLAYER_KEY_EVENT Events[Count];
} KBDLAYER_WAIT_KEY_EVENT_OUTPUT, *PKBDLAYER_WAIT_KEY_EVENT_OUTPUT;

// 同時に保留できるWAIT_KEY_EVENTの数、所有者（待ち始めたハンドル）の数。超えるとSTATUS_INSUFFICIENT_RESOURCES。
#define KBDLAYER_MAX_PENDING_KEY_WAITS  4
#define KBDLAYER_MAX_KEY_STREAM_OWNERS  4

// --- デバイスの永続化スコープ（E-2: DeviceIdは一意でない前提で設計する） ---
//
// devExt->DeviceId（Target.UniqIdで指定するアドレッシング用ID。ExtraInformationへの刻印にも
// 使う）は、従来どおりDeviceIdModeで決まる（0=InstanceId、1=HardwareIdのハッシュ）。
// これとは別に、ドライバは常に以下の2つを計算して持つ:
//   - InstanceKey: DeviceInstanceIdのハッシュ。差し込み口ごとに一意。
//   - ModelKey:    HardwareIdのハッシュ。同型番のキーボードは同じ値になる。
// レイヤーマップ等の永続化設定（LayerMap/Enabled/TapTermMs/.../Nicola）は、次のどれを
// 保存先にするかを選べる（KBDLAYER_BINDING_*）。
//   - Model（既定）: 同型番のキーボードは差し込み口が変わっても同じ設定を共有する。
//   - Instance:      この1台（この差し込み口）だけの設定にする。
//   - Global:        すべてのキーボードで1つの設定を共有する（型番も差し込み口も区別しない。
//                    2026-10-04追加。後から接続したキーボードにも、Globalにしていれば同じ設定が効く）。
// いずれも保存先の選択だけで、すでに接続中の別のキーボードへは、そのキーボードを接続し直す
// （AddDevice）まで反映されない。キーマップは物理スキャンコードで決まるので、配列の違う
// キーボード（JISとテンキー等）をGlobalで共有すると、意図しない割り当てになりうる。
// 詳細は .plans/device-id-scope.md 参照。

// KBDLAYER_DEVICE_SCOPE.Binding / KBDLAYER_SET_DEVICE_SCOPE_INPUT.Binding
#define KBDLAYER_BINDING_MODEL    0 // 同型番で共有（既定）
#define KBDLAYER_BINDING_INSTANCE 1 // この1台（差し込み口）だけ
#define KBDLAYER_BINDING_GLOBAL   2 // すべてのキーボードで共有

// IOCTL_KBDLAYER_GET_DEVICE_SCOPE の入力は KBDLAYER_TARGET のみ。出力は KBDLAYER_DEVICE_SCOPE。
typedef struct _KBDLAYER_DEVICE_SCOPE {
    ULONG InstanceKey;
    ULONG ModelKey;
    ULONG Binding; // KBDLAYER_BINDING_*
} KBDLAYER_DEVICE_SCOPE, *PKBDLAYER_DEVICE_SCOPE;

// IOCTL_KBDLAYER_SET_DEVICE_SCOPE の入力バッファ。
// 切り替えるときは、現在の保存先にある設定を、切替先へコピーしてから切り替える（設定が消えたように
// 見えないようにするため）。切替先にすでにある設定は、コピーで上書きされる。たとえばGlobalやInstanceから
// Modelへ戻すと、そのModelKeyの保存先（同じ型番のキーボードで共有している設定）が、この1台の設定で
// 置き換わる。例外はGlobalへの切替だけで、Global側にすでにレイヤーマップがあれば、コピーせずにそれを使う
// （複数のキーボードが共有する保存先を、1台の設定で上書きしないため）。切替直後のこの1台のライブの設定は
// 変わらず、次のAddDeviceで新しい保存先から読む。出力バッファがKBDLAYER_DEVICE_SCOPE以上あれば、
// 変更後の状態を返す（任意）。古いドライバはBinding=2を知らず、Modelとして扱う。
typedef struct _KBDLAYER_SET_DEVICE_SCOPE_INPUT {
    KBDLAYER_TARGET Target;
    ULONG Binding; // KBDLAYER_BINDING_*
} KBDLAYER_SET_DEVICE_SCOPE_INPUT, *PKBDLAYER_SET_DEVICE_SCOPE_INPUT;

// --- 緊急時の解除手段（release-roadmap-priority.md 項番3） ---
//
// 設定ミスで入力できなくなったときの逃げ道。指定した物理キーの組み合わせ（ScanCodes、
// Enc16形式）を KBDLAYER_EMERGENCY_HOLD_MS 以上同時に押し続けると、その時点で接続中の
// 全キーボードのレイヤーマップを一括でOFFにする（マップ自体は消さない。既存の
// IOCTL_KBDLAYER_SET_LAYERMAP_ENABLED(Enabled=0) と同じ効果を全台へ適用し、永続化する）。
// 判定はレイヤーマップ解決より前の生の物理スキャンコード段階で行うため、マップ・マクロ・
// コンボの内容がどれだけ壊れていても必ず動く。ドライバ全体で1つの設定（キーボードごとでは
// ない）なので、これらのIOCTLは KBDLAYER_TARGET を取らない。
// 詳細: .plans/emergency-release.md

#define KBDLAYER_EMERGENCY_MIN_KEYS 2
#define KBDLAYER_EMERGENCY_MAX_KEYS 6
#define KBDLAYER_EMERGENCY_HOLD_MS  3000 // 固定（IOCTLでは変更できない）

// IOCTL_KBDLAYER_GET/SET_EMERGENCY_RELEASE の入出力。
// KeyCount は常に KBDLAYER_EMERGENCY_MIN_KEYS〜KBDLAYER_EMERGENCY_MAX_KEYS
// （0は許可しない。安全網そのものを無効化させないため）。ScanCodesはKeyCount個が有効。
typedef struct _KBDLAYER_EMERGENCY_RELEASE_CONFIG {
    ULONG KeyCount;
    USHORT ScanCodes[KBDLAYER_EMERGENCY_MAX_KEYS];
} KBDLAYER_EMERGENCY_RELEASE_CONFIG, *PKBDLAYER_EMERGENCY_RELEASE_CONFIG;

// --- i8042prt の Parameters を、キーボードの接続時に書き換える／初期化する（2026-10） ---
//
// PS/2(ACPI)キーボードのレイアウトは HKLM\SYSTEM\CurrentControlSet\Services\i8042prt\Parameters の
// OverrideKeyboardType/Subtype(DWORD)・OverrideKeyboardIdentifier・LayerDriver JPN・LayerDriver KOR
// (SZ) でしか指定できない。HIDキーボードは、これらが残っていると個別指定が無効になる。
// Enabled=1 のとき、AddDevice（キーボードの接続時）が次のように i8042prt を書き換える:
//   - そのキーボードのModelKeyのプロファイルがある → Flagsで指定された値を書き、指定の無い値は削除する。
//   - プロファイルが無い（未指定のキーボード）      → 上の5値をすべて削除する（OSの既定へ戻す）。
// Enabled=0（既定）なら何もしない。ドライバ全体で1つの設定なので KBDLAYER_TARGET は取らない。
// SETは全体置換。変更は次のキーボード接続（AddDevice）から効く。接続中のキーボードへは即時適用しない。
// LayerDriver の値はパスではなくレイアウトDLL名なので、\ / : などの文字を含むものは拒否する。

#define KBDLAYER_I8042_MAX_PROFILES 16
#define KBDLAYER_I8042_STR_CHARS    32 // 終端のNULを含む。PCAT_106KEY / kbd106.dll などが収まる長さ

// KBDLAYER_I8042_PROFILE.Flags: 立っているビットの値だけを書く（無いものは削除）
#define KBDLAYER_I8042_HAS_TYPE       0x00000001
#define KBDLAYER_I8042_HAS_SUBTYPE    0x00000002
#define KBDLAYER_I8042_HAS_IDENTIFIER 0x00000004
#define KBDLAYER_I8042_HAS_LAYER_JPN  0x00000008
#define KBDLAYER_I8042_HAS_LAYER_KOR  0x00000010
#define KBDLAYER_I8042_HAS_ALL        0x0000001F

typedef struct _KBDLAYER_I8042_PROFILE {
    ULONG ModelKey; // KBDLAYER_DEVICE_SCOPE.ModelKey（HardwareIdのハッシュ）。0は不可
    ULONG Flags;    // KBDLAYER_I8042_HAS_*
    ULONG Type;     // OverrideKeyboardType（0〜0xFFFF）
    ULONG Subtype;  // OverrideKeyboardSubtype（0〜0xFFFF）
    WCHAR Identifier[KBDLAYER_I8042_STR_CHARS]; // OverrideKeyboardIdentifier（例: PCAT_106KEY）
    WCHAR LayerJpn[KBDLAYER_I8042_STR_CHARS];   // "LayerDriver JPN"（例: kbd106.dll）
    WCHAR LayerKor[KBDLAYER_I8042_STR_CHARS];   // "LayerDriver KOR"
} KBDLAYER_I8042_PROFILE, *PKBDLAYER_I8042_PROFILE;

// IOCTL_KBDLAYER_GET/SET_I8042_CONFIG の入出力（常にこのサイズ全体）。
// Profiles は ProfileCount 個が有効。
typedef struct _KBDLAYER_I8042_CONFIG {
    ULONG Enabled;      // 0 / 1
    ULONG ProfileCount; // 0〜KBDLAYER_I8042_MAX_PROFILES
    KBDLAYER_I8042_PROFILE Profiles[KBDLAYER_I8042_MAX_PROFILES];
} KBDLAYER_I8042_CONFIG, *PKBDLAYER_I8042_CONFIG;

// --- キー送出（IOCTL_KBDLAYER_SEND_KEY、2026-11-01。1.01 から Release のドライバも処理する） ---
//
// 修飾キー（Mods）を押したまま ScanCode のキーを1回押して離す。ヘルパーのSequenceが、SendInputでは
// 管理者のウィンドウへ確実に届かない（vk-admin-first-key-results.md）ので、ドライバ経由で送れるか
// 試すためのもの。リマップ・タップホールド・コンボなどの解釈は通さず、OSへそのまま出す（修飾キーの
// 押下状態の記録だけ更新する）。make と break を、1つのOutputLockの区間で送り切る。
// 1.00 までのドライバにはこのIOCTLが無い（STATUS_INVALID_DEVICE_REQUEST＝Win32のERROR_INVALID_FUNCTION）。
// クライアントはそれを「未対応」とみなし、SendInputへ戻ること。
// ライセンスが実効でないときは、STATUS_ACCESS_DENIEDで失敗する（リマップなどと同じ扱い）。これも、SendInputへ戻ること。
// 入力:
//   Target   送出先のキーボード（LIST_DEVICESのUniqId）。
//   ScanCode Enc16形式（上位がE0なら拡張キー）。0とE1プレフィックスは不可。
//   Mods     KBDLAYER_MOD_*（左右の区別あり）。立てたビットのうち、OSでまだ押されていないものだけを
//            押し、キーを離したあとに離す（物理的に押されている修飾キーは、押し直さず、離さない）。
//   Flags    予約。0であること。
typedef struct _KBDLAYER_SEND_KEY_INPUT {
    KBDLAYER_TARGET Target;
    USHORT ScanCode;
    UCHAR Mods;
    UCHAR Flags;
} KBDLAYER_SEND_KEY_INPUT, *PKBDLAYER_SEND_KEY_INPUT;

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
// （kasane の KbdLayerClient.cs は値を自前で計算しているので、そちらも合わせる）。
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
// GET_DEVICE_SCOPEは読み出しのみ。SET_DEVICE_SCOPEは永続化設定の保存先を切り替える
// （Model→Instanceでは設定のコピーも行う）ので、SET_*/CLEARと同じくFILE_WRITE_ACCESS。
#define IOCTL_KBDLAYER_GET_DEVICE_SCOPE \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x814, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_KBDLAYER_SET_DEVICE_SCOPE \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x815, METHOD_BUFFERED, FILE_WRITE_ACCESS)
// 緊急解除の組み合わせを変更できるのは管理者のみ（変更を悪用されると解除手段そのものを
// 無力化されうるため、SET_LAYERMAP等の書き込み系と同じ扱いにする）。読み出しは誰でもよい。
#define IOCTL_KBDLAYER_GET_EMERGENCY_RELEASE \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x816, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_KBDLAYER_SET_EMERGENCY_RELEASE \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x817, METHOD_BUFFERED, FILE_WRITE_ACCESS)
// 機能別ON/OFF（項番4）。設定の書き換えなので、他のSET_*と同じくFILE_WRITE_ACCESS。
#define IOCTL_KBDLAYER_GET_FEATURES \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x818, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_KBDLAYER_SET_FEATURES \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x819, METHOD_BUFFERED, FILE_WRITE_ACCESS)
// Key Override（2026-10-01）。設定の書き換えは他のSET_*と同じくFILE_WRITE_ACCESS。
#define IOCTL_KBDLAYER_SET_KEY_OVERRIDE \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x81A, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_KBDLAYER_GET_KEY_OVERRIDE \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x81B, METHOD_BUFFERED, FILE_READ_ACCESS)
// i8042prt の書き換え設定（2026-10）。レジストリのシステム設定を書き換えさせる設定なので、
// SET側は管理者のみ（FILE_WRITE_ACCESS）。
#define IOCTL_KBDLAYER_GET_I8042_CONFIG     CTL_CODE(FILE_DEVICE_KBDLAYER, 0x81C, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_KBDLAYER_SET_I8042_CONFIG     CTL_CODE(FILE_DEVICE_KBDLAYER, 0x81D, METHOD_BUFFERED, FILE_WRITE_ACCESS)
// ヘルパーイベント（CUSTOMアクションの通知、2026-10-08）。WAIT_LAYER_CHANGEと同じく、ユーザー自身が
// 設定したCUSTOMキーの押下を知らせるだけで、任意の打鍵は読めない（キーロガーにならない）ので
// FILE_READ_ACCESSにする。昇格していないトレイから呼ぶ。
#define IOCTL_KBDLAYER_WAIT_HELPER_EVENT \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x81E, METHOD_BUFFERED, FILE_READ_ACCESS)

// キーイベントの継続的な通知。キー入力が丸見えになるので FILE_WRITE_ACCESS（管理者のみ）。
#define IOCTL_KBDLAYER_WAIT_KEY_EVENT \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x81F, METHOD_BUFFERED, FILE_WRITE_ACCESS)

// ライセンスの再検証（2026-10-28）。ライセンスの状態はドライバ全体で1つ。LicenseTokenを書いた・消した
// 直後と、サービスの起動時にサービスが送る。ライセンスが無効でも、各キーボードのDeviceIdと永続設定は
// AddDeviceで算出・復元済みなので、再検証が成功すればキーボードを接続し直さずに有効になる。
// ライセンスの設定そのものを変えるので FILE_WRITE_ACCESS（管理者・SYSTEMのみ）。
#define IOCTL_KBDLAYER_REVALIDATE_LICENSE \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x820, METHOD_BUFFERED, FILE_WRITE_ACCESS)

// キー送出（2026-11-01）。任意のキーの注入になるので FILE_WRITE_ACCESS（管理者・SYSTEMのみ）。
// 1.01以降のドライバが処理する。入出力は KBDLAYER_SEND_KEY_INPUT（出力なし）。
#define IOCTL_KBDLAYER_SEND_KEY \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x821, METHOD_BUFFERED, FILE_WRITE_ACCESS)

// ライセンスの状態（KBDLAYER_REVALIDATE_LICENSE_OUTPUT::State）。
#define KBDLAYER_LICENSE_STATE_INVALID      0UL // 無効
#define KBDLAYER_LICENSE_STATE_PROVISIONAL  1UL // 暫定有効。前回は有効だった。起動直後の検証が済むまで、期限つきで有効
#define KBDLAYER_LICENSE_STATE_VALID        2UL // 有効（検証成功）

// 再検証の扱い（KBDLAYER_REVALIDATE_LICENSE_INPUT::Mode）。
// DEFINITIVE: 検証の結果を確定として扱う。失敗なら、暫定有効の途中でも即座に無効にする
//             （LicenseTokenを書いた・消した直後。ユーザーが操作した結果）。
// SOFT:       成功なら有効にするが、失敗しても状態を変えない（サービスの起動時。サービスも
//             ブート直後に起動するので、そこでの失敗を確定としてドライバ自身の再試行を潰さない）。
#define KBDLAYER_REVALIDATE_DEFINITIVE 0UL
#define KBDLAYER_REVALIDATE_SOFT       1UL

typedef struct _KBDLAYER_REVALIDATE_LICENSE_INPUT {
    ULONG Mode; // KBDLAYER_REVALIDATE_*
} KBDLAYER_REVALIDATE_LICENSE_INPUT, *PKBDLAYER_REVALIDATE_LICENSE_INPUT;

typedef struct _KBDLAYER_REVALIDATE_LICENSE_OUTPUT {
    ULONG State; // 再検証の後のKBDLAYER_LICENSE_STATE_*
} KBDLAYER_REVALIDATE_LICENSE_OUTPUT, *PKBDLAYER_REVALIDATE_LICENSE_OUTPUT;

#endif // _KBDLAYER_COMMON_IOCTL_H_
