// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: Apache-2.0

/*
  KeymapTool/InfoJson.h

  QMKリポジトリ形式のinfo.json（keyboard.json）から、matrix.rows/cols と
  layouts[<name>].layout（VIAエディタでの見た目の並び = keymap.jsonの
  フラット配列の並びと一致する）を読み取る。
*/

#ifndef _KEYMAPTOOL_INFOJSON_H_
#define _KEYMAPTOOL_INFOJSON_H_

#include <string>
#include <vector>

struct LayoutKeyPosition {
    int Row = 0;
    int Col = 0;
};

class InfoJson {
public:
    // filePathを読み込み、layoutName（空文字なら"layouts"直下の最初のキー）の
    // layout配列をoutPositionsへ格納する。outPositions[i]がkeymap.jsonの各レイヤー
    // 配列のi番目の要素に対応するmatrix(row,col)になる。
    static bool Load(
        const std::string& filePath,
        const std::string& layoutName,
        std::vector<LayoutKeyPosition>* outPositions,
        int* outRows,
        int* outCols,
        std::string* error);
};

#endif // _KEYMAPTOOL_INFOJSON_H_
