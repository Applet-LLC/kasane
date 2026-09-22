// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

/*
  KeymapTool/KeymapJson.h

  VIAエディタの「Export Keymap」で出力されるkeymap.jsonを読み取る。
  実際のファイルは概ね次の形:
    {
      "version": 1,
      "notes": "...",
      "keyboard": "...",
      "layout": [
        ["KC_A", "KC_B", ...],   // レイヤー0。info.jsonのlayoutと同じ並び順のフラット配列
        ["KC_TRNS", "MO(2)", ...] // レイヤー1
      ]
    }
  "layout"の代わりに"layers"キーを使うエクスポートも見られるため、両対応する。
*/

#ifndef _KEYMAPTOOL_KEYMAPJSON_H_
#define _KEYMAPTOOL_KEYMAPJSON_H_

#include <string>
#include <vector>

class KeymapJson {
public:
    // filePathを読み込み、レイヤーごとのキーコード文字列配列をoutLayersへ格納する。
    // outLayers[L][i] が info.json の layout配列の i番目の物理キー位置に対応する。
    static bool Load(
        const std::string& filePath,
        std::vector<std::vector<std::string>>* outLayers,
        std::string* error);
};

#endif // _KEYMAPTOOL_KEYMAPJSON_H_
