// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

/*
  KeymapTool/BlobBuilder.h

  keymap.jsonのレイヤー配列 + キャリブレーションで得た物理スキャンコード列から、
  IOCTL_KBDLAYER_SET_LAYERMAPへ送るワイヤーフォーマットのblob
  （KBDLAYER_MAP_HEADER + KBDLAYER_KEY_ENTRY[]、common/kbdlayer_ioctl.h）を
  組み立てる。info.jsonモード・簡易モードのどちらも、最終的に「キー位置i ->
  物理スキャンコード」の配列へ正規化してから同じロジックで処理する。
*/

#ifndef _KEYMAPTOOL_BLOBBUILDER_H_
#define _KEYMAPTOOL_BLOBBUILDER_H_

#include <windows.h>
#include <string>
#include <vector>

// layers[L][i] = レイヤーLのキー位置iのQMKキーコード文字列。
// scanCodesByIndex[i] = キー位置iの物理スキャンコード（Enc16形式）。
// layers[L].size() は全レイヤーで scanCodesByIndex.size() と一致していること。
bool BuildLayerMapBlob(
    const std::vector<std::vector<std::string>>& layers,
    const std::vector<USHORT>& scanCodesByIndex,
    std::vector<unsigned char>* outBlob,
    std::string* error);

#endif // _KEYMAPTOOL_BLOBBUILDER_H_
