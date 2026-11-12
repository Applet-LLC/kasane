// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: Apache-2.0

/*
  kbdlayer/NicolaCore.h

  NICOLA（親指シフト）の同時打鍵判定エンジン。NICOLA配列規格書
  （http://nicola.sunicom.co.jp/spec/kikaku.htm）の5章・6.1節に従う。

  カーネル非依存の純粋な状態機械として書いてある（ロック・タイマー・送出は
  呼び出し側 = LayerEngine.cのResolveNicolaが担う）。同じソースを
  driver/NicolaCoreTest（ユーザーモードのテストEXE）でもビルドし、規格の図1〜7の
  打鍵パターンを時刻付きで再現して検証している。

  呼び出し規約:
  - キーイベントごとに NicolaCoreOnKey、タイマー満了時に NicolaCoreOnTimeout を呼ぶ。
  - 時刻はミリ秒の単調増加カウンタ（カーネルではKeQueryInterruptTime()/10000）。
  - 結果（NICOLA_CORE_RESULT）の Items を先頭から順に処理し、Deadline が0でなければ
    その時刻にタイマーを張り直す（0ならタイマーを止める）。
  - Items の末尾が NICOLA_OUT_PASS なら、今回の生イベントを通常経路へ流す。
*/

#ifndef _KBDLAYER_NICOLA_CORE_H_
#define _KBDLAYER_NICOLA_CORE_H_

#include "..\\common\\kbdlayer_ioctl.h"

// 呼び出し側が物理キーを分類して渡す。
typedef enum _NICOLA_KEY_KIND {
    NICOLA_KEY_OTHER = 0,       // NICOLA表に無いキー、または修飾キー押下中のため判定を迂回するキー
    NICOLA_KEY_CHAR,            // NICOLA表にある文字キー
    NICOLA_KEY_THUMB_LEFT,      // 親指左キー
    NICOLA_KEY_THUMB_RIGHT,     // 親指右キー
} NICOLA_KEY_KIND;

#define NICOLA_SIDE_LEFT  0
#define NICOLA_SIDE_RIGHT 1

// 出力の種類
#define NICOLA_OUT_CHAR       1 // 文字キー ScanCode の Plane を送出する
#define NICOLA_OUT_TAP_MAKE   2 // 親指キー Side の単独打鍵キーを押下する
#define NICOLA_OUT_TAP_BREAK  3 // 親指キー Side の単独打鍵キーを解放する
#define NICOLA_OUT_PASS       4 // 今回の生イベントを通常経路へ流す（常に末尾）

typedef struct _NICOLA_CORE_OUTPUT {
    UCHAR  Type;     // NICOLA_OUT_*
    UCHAR  Arg;      // CHAR: Plane (KBDLAYER_NICOLA_PLANE_*) / TAP_*: Side (NICOLA_SIDE_*)
    USHORT ScanCode; // CHAR: 文字キーの物理スキャンコード（Enc16）
} NICOLA_CORE_OUTPUT, *PNICOLA_CORE_OUTPUT;

#define NICOLA_CORE_MAX_OUTPUTS 12

typedef struct _NICOLA_CORE_RESULT {
    ULONG Count;
    NICOLA_CORE_OUTPUT Items[NICOLA_CORE_MAX_OUTPUTS];
    ULONGLONG Deadline; // 次にNicolaCoreOnTimeoutを呼ぶべき時刻（ms）。0 = タイマー不要
} NICOLA_CORE_RESULT, *PNICOLA_CORE_RESULT;

typedef struct _NICOLA_CORE_PARAMS {
    ULONG SimulTermMs;         // 0は呼び出し側で既定値に置き換えておくこと
    ULONG OverlapRatioPercent; // 同上
    ULONG MinOverlapMs;
    ULONG Flags;               // KBDLAYER_NICOLA_FLAG_*
} NICOLA_CORE_PARAMS, *PNICOLA_CORE_PARAMS;

typedef struct _NICOLA_CORE_STATE {
    // 判定保留中の文字キー（押されたまま、単独か同時かが未確定）
    BOOLEAN   CharPending;
    USHORT    CharScan;
    ULONGLONG CharTime;

    // 判定保留中の親指キー（押されたまま、単独か同時かが未確定）
    BOOLEAN   ThumbPending;
    UCHAR     ThumbSide;
    ULONGLONG ThumbTime;

    // 文字と親指の両方が保留中のとき、文字が先に押されたか（規格の図5/図4の区別）
    BOOLEAN   CharFirst;

    // 親指キーの物理状態
    BOOLEAN   ThumbDown[2];
    ULONGLONG ThumbDownTime[2];
    BOOLEAN   ThumbUsed[2];    // 今回の押下でシフトとして使った（離しても単独打鍵にしない）
    BOOLEAN   ThumbTapHeld[2]; // 単独打鍵キーを押下送出済み（離したときに解放を送る）

    // キーリピート用: 最後に確定した文字キー
    BOOLEAN   LastCharDown;
    USHORT    LastCharScan;
    UCHAR     LastCharPlane;

    // このエンジンが押下を飲み込んだキー（解放も対で飲み込む）。
    // インデックス = MakeCode + プレフィックス無し:0 / E0:0x100 / E1:0x200。
    UCHAR     OwnedDown[96];

    ULONGLONG Deadline;
} NICOLA_CORE_STATE, *PNICOLA_CORE_STATE;

VOID
NicolaCoreInit(
    _Out_ PNICOLA_CORE_STATE State
    );

VOID
NicolaCoreOnKey(
    _Inout_ PNICOLA_CORE_STATE State,
    _In_ const NICOLA_CORE_PARAMS* Params,
    _In_ NICOLA_KEY_KIND Kind,
    _In_ USHORT ScanCode,
    _In_ BOOLEAN IsBreak,
    _In_ ULONGLONG NowMs,
    _Out_ PNICOLA_CORE_RESULT Result
    );

VOID
NicolaCoreOnTimeout(
    _Inout_ PNICOLA_CORE_STATE State,
    _In_ const NICOLA_CORE_PARAMS* Params,
    _In_ ULONGLONG NowMs,
    _Out_ PNICOLA_CORE_RESULT Result
    );

// 判定を打ち切る（レイヤーを抜けた・設定変更など）。保留中のキーを確定させ、
// 押下送出済みの単独打鍵キーを解放してから、状態を初期化する。
VOID
NicolaCoreReset(
    _Inout_ PNICOLA_CORE_STATE State,
    _Out_ PNICOLA_CORE_RESULT Result
    );

#endif // _KBDLAYER_NICOLA_CORE_H_
