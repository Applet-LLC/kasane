// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

/*
  KeymapTool/KeycodeTable.h

  QMKキーコード文字列（"KC_A", "MO(1)", "LT(2,KC_ENT)" 等）を、カーネルへ送る
  KBDLAYER_ACTION（common/kbdlayer_ioctl.h）へ変換するユーザーモード専用の
  静的テーブル。カーネル側には一切持ち込まない
  （.plans/perplexity-...md の「ユーザーモード設計」節を参照）。

  基本キー（KC_A〜KC_Z, 数字, 記号, ファンクションキー, ナビゲーションクラスタ,
  テンキー, 主要メディアキー）と、レイヤーアクション（MO/TG/DF/OSL, MT/LT[Phase 2],
  MACRO(id)[Phase 3], TAPDANCE(id)[Phase 5], HLP(id)[ヘルパーイベント]）を扱う。カバーしていないキーコードは
  Resolve()がfalseを返すので、呼び出し側でエラー表示すること。
*/

#ifndef _KEYMAPTOOL_KEYCODETABLE_H_
#define _KEYMAPTOOL_KEYCODETABLE_H_

#include <windows.h>
#include <string>
#include "..\\common\\kbdlayer_ioctl.h"

// KeymapTool内部で使う、1トークン分の解決結果。
struct KeycodeMapping {
    UCHAR ActionType = KBDLAYER_ACTION_NONE;
    UCHAR Param1 = 0;
    USHORT Param2 = 0;
};

class KeycodeTable {
public:
    // token（例: "KC_A", "KC_TRNS", "MO(1)", "LT(2,KC_ENT)"）を解決する。
    // 既知でない場合はfalseを返す（*outは不定）。
    // 修飾付きキー（LCTL(KC_C)等）はKBDLAYER_ACTION_MODDED_KEY（Param1=修飾子マスク）、
    // LM(layer, MOD_xxx|...)はKBDLAYER_ACTION_LAYER_MOD（Param2=修飾子マスク）に解決する。
    static bool Resolve(const std::string& token, KeycodeMapping* out);

    // "MOD_LCTL|MOD_LSFT" を修飾子マスク（KBDLAYER_MOD_*のOR）にする。
    static bool ResolveModMask(const std::string& token, UCHAR* outMask);

    // 修飾子マスクのビット番号（0=LCTL ... 7=RGUI）から、その修飾キーのスキャンコード（Enc16）。
    // driver/kbdlayer/LayerEngine.cのs_ModifierScanCodesと同じ。
    static USHORT ModifierScanCode(int bit);

private:
    // 単純キー（修飾子含む）のみを解決する。MO/TG/DF/OSL/MT/LTのパラメータとして
    // 使われる「タップ時キーコード」の解決にも使う。
    static bool ResolveBaseKey(const std::string& token, USHORT* outScanCode16);

    // "MOD_LCTL"等の修飾子名をビット番号(0-7)へ変換する（MT()用）。
    static bool ResolveModifierBit(const std::string& token, UCHAR* outBit);
};

#endif // _KEYMAPTOOL_KEYCODETABLE_H_
