// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: Apache-2.0

/*
  KeymapTool/NicolaTables.h

  NICOLA（親指シフト）設定JSON（samples/nicola.json）を読み、ドライバへ送る
  KBDLAYER_NICOLA_HEADER + entries のblob（common/kbdlayer_ioctl.h）を組み立てる。

  かな → スキャンコード列の変換はここで行う（カーネルはかなを知らない）。
  変換方式は2種類:
    "romaji"   : ローマ字入力のIME向け。「か」→ K,A のタップ列
    "jis-kana" : かな入力のIME向け。JISかな配列のキー（+Shift）。濁音は清音+゛の2打
  どちらでも表現できない文字（ローマ字の単独の ゛/゜ など）は、警告を出して
  そのプレーンを空（何も送らない）にする。
*/

#ifndef _KEYMAPTOOL_NICOLATABLES_H_
#define _KEYMAPTOOL_NICOLATABLES_H_

#include <windows.h>
#include <string>
#include <vector>
#include "..\\common\\kbdlayer_ioctl.h"

class NicolaTables {
public:
    // jsonPathのNICOLA設定を読み、blobを組み立てる。変換できなかった文字などは
    // *warningsへ積む（失敗扱いにはしない）。形式エラーはfalse + *error。
    static bool BuildBlob(const std::string& jsonPath,
                          std::vector<unsigned char>* outBlob,
                          std::vector<std::string>* warnings,
                          std::string* error);

    // blobの内容を人が読める形で表示する（--get-nicola / --dump-nicola 用）。
    static void PrintBlob(const unsigned char* blob, size_t blobLength);
};

#endif // _KEYMAPTOOL_NICOLATABLES_H_
