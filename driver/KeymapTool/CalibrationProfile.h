// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: Apache-2.0

/*
  KeymapTool/CalibrationProfile.h

  実機のmatrix (row,col) <-> 物理スキャンコードの対応付け（.plans/...md の
  「キャリブレーションウィザード」節）。IOCTL_KBDLAYER_CAPTURE_NEXT_KEYで
  取得した対応をUniqId単位でローカルJSONファイルに保存し、同一物理キーボードの
  再キャリブレーションを不要にする。
*/

#ifndef _KEYMAPTOOL_CALIBRATIONPROFILE_H_
#define _KEYMAPTOOL_CALIBRATIONPROFILE_H_

#include <windows.h>
#include <string>
#include <vector>
#include "InfoJson.h"
#include "IoctlClient.h"

struct CalibratedKey {
    int Row = 0;
    int Col = 0;
    USHORT ScanCode16 = 0;
};

class CalibrationProfile {
public:
    std::vector<CalibratedKey> Keys;

    bool Save(const std::string& filePath, std::string* error) const;
    bool Load(const std::string& filePath, std::string* error);
    const CalibratedKey* Find(int row, int col) const;
};

// positions[i] の物理キーを順番に案内し、IOCTL_KBDLAYER_CAPTURE_NEXT_KEYで
// 実際のスキャンコードを取得してoutProfileへ格納する。標準出力に案内を表示し、
// 標準入力から改行待ちを行う（対話型CLI）。キー押下が既定タイムアウト内に
// 検出できない場合は同じキーについて再試行を促す。
// maxConsecutiveTimeouts > 0 の場合、その回数だけ連続でタイムアウトしたら
// 中断してfalseを返す（入力抑止中にユーザーが離席しても、キーボードが
// Windowsへ戻らないまま待ち続けないようにするため）。0なら無制限に再試行する。
bool RunCalibrationWizard(
    IoctlClient* client,
    ULONG uniqId,
    const std::vector<LayoutKeyPosition>& positions,
    CalibrationProfile* outProfile,
    int maxConsecutiveTimeouts = 0);

#endif // _KEYMAPTOOL_CALIBRATIONPROFILE_H_
