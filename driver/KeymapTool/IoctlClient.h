// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

/*
  KeymapTool/IoctlClient.h

  \\.\KbdLayerControl (common/kbdlayer_ioctl.h) への薄いIOCTLラッパー。
  addid/DriverTestApp/DriverTestApp.cpp の --list/--get/--remap/... 相当の
  操作を、kbdlayerのIOCTLセットに合わせて実装したもの
  （.plans/...md の「検証方法」節: 回帰確認用コマンドの新IOCTL版）。
*/

#ifndef _KEYMAPTOOL_IOCTLCLIENT_H_
#define _KEYMAPTOOL_IOCTLCLIENT_H_

#include <windows.h>
#include <string>
#include <vector>
#include "..\\common\\kbdlayer_ioctl.h"

struct DeviceListEntry {
    ULONG UniqId;
    bool HasLayerMap;
    bool Enabled;
    ULONG ActiveLayerMask;
    std::wstring HardwareId;
};

class IoctlClient {
public:
    // \\.\KbdLayerControl を開く。失敗したらfalseを返しエラーをstderrへ表示する。
    bool Open();
    void Close();
    bool IsOpen() const { return handle_ != INVALID_HANDLE_VALUE; }

    bool ListDevices(std::vector<DeviceListEntry>* out);
    bool GetLayerMap(ULONG uniqId, std::vector<unsigned char>* outBlob);
    bool SetLayerMap(ULONG uniqId, const std::vector<unsigned char>& blob);
    bool ClearLayerMap(ULONG uniqId);
    bool SetLayerMapEnabled(ULONG uniqId, bool enabled);

    // timeoutMs=0はドライバ側既定値(KBDLAYER_CAPTURE_DEFAULT_TIMEOUT_MS)を使う。
    // 戻り値: true=IOCTL自体は成功（*outTimedOutで実際に捕捉できたか判定）。
    bool CaptureNextKey(ULONG uniqId, ULONG timeoutMs, USHORT* outScanCode16, bool* outTimedOut);

    // Phase 2: Mod-Tap/Layer-TapのTapTermMs（タップ/ホールド判定閾値、ミリ秒）を設定する。
    // tapTermMs=0はドライバ側既定値(KBDLAYER_DEFAULT_TAP_TERM_MS=200ms)にリセットする。
    bool SetConfig(ULONG uniqId, ULONG tapTermMs);

    // Phase 3: マクロ。stepsが空ならそのmacroIdをクリアする。
    bool SetMacro(ULONG uniqId, ULONG macroId, const std::vector<KBDLAYER_MACRO_STEP>& steps);
    bool GetMacro(ULONG uniqId, ULONG macroId, std::vector<KBDLAYER_MACRO_STEP>* outSteps);

    ~IoctlClient() { Close(); }

private:
    HANDLE handle_ = INVALID_HANDLE_VALUE;
};

#endif // _KEYMAPTOOL_IOCTLCLIENT_H_
