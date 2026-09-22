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
    ULONG TapTermMs; // 既定200ms。0を渡すと既定値にリセットする。
} KBDLAYER_SET_CONFIG_INPUT, *PKBDLAYER_SET_CONFIG_INPUT;

#pragma pack(pop)

// IOCTL_INTERNAL_KEYBOARD_CONNECT 等の既存IOCTLと衝突しないよう、
// カスタムのデバイスタイプを使う（"user-defined"範囲: 0x8000〜0xFFFF）。
// kbdaddidの FILE_DEVICE_KBDADDID (0x8010) とも重ならない値を選ぶ。
#define FILE_DEVICE_KBDLAYER 0x00008020

#define IOCTL_KBDLAYER_LIST_DEVICES \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x800, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KBDLAYER_GET_LAYERMAP \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x801, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KBDLAYER_SET_LAYERMAP \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x802, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KBDLAYER_CLEAR_LAYERMAP \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x803, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KBDLAYER_SET_LAYERMAP_ENABLED \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x804, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KBDLAYER_CAPTURE_NEXT_KEY \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x805, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_KBDLAYER_SET_CONFIG \
    CTL_CODE(FILE_DEVICE_KBDLAYER, 0x806, METHOD_BUFFERED, FILE_ANY_ACCESS)

#endif // _KBDLAYER_COMMON_IOCTL_H_
