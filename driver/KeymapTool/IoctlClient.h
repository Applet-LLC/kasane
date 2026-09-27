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
    // 昇格していない場合は読み取り専用で開き（IsReadOnly()==true）、書き込み系の
    // メソッド（SetLayerMap/ClearLayerMap/CaptureNextKey/SetConfig/SetCaptureSuppress/
    // SetMacro/SetCombo/SetTapDance/SetNicola）は管理者権限が必要な旨を表示して失敗する。
    bool Open();
    void Close();
    bool IsOpen() const { return handle_ != INVALID_HANDLE_VALUE; }
    bool IsReadOnly() const { return readOnly_; }

    bool ListDevices(std::vector<DeviceListEntry>* out);
    bool GetLayerMap(ULONG uniqId, std::vector<unsigned char>* outBlob);
    bool SetLayerMap(ULONG uniqId, const std::vector<unsigned char>& blob);
    bool ClearLayerMap(ULONG uniqId);
    bool SetLayerMapEnabled(ULONG uniqId, bool enabled);

    // timeoutMs=0はドライバ側既定値(KBDLAYER_CAPTURE_DEFAULT_TIMEOUT_MS)を使う。
    // 戻り値: true=IOCTL自体は成功（*outTimedOutで実際に捕捉できたか判定）。
    bool CaptureNextKey(ULONG uniqId, ULONG timeoutMs, USHORT* outScanCode16, bool* outTimedOut);

    // Phase 2/4/5: TapTermMs（Mod-Tap/Layer-Tap判定閾値）・ComboTermMs（コンボ判定閾値）・
    // TapDanceTermMs（タップダンス判定閾値、いずれもミリ秒）を設定する。0を渡すとその項目だけ
    // ドライバ側既定値にリセットする (TapTermMs=200ms, ComboTermMs=50ms, TapDanceTermMs=200ms)。
    bool SetConfig(ULONG uniqId, ULONG tapTermMs, ULONG comboTermMs, ULONG tapDanceTermMs);
    // 上記3項目の現在の実効値を取得する。
    bool GetConfig(ULONG uniqId, KBDLAYER_GET_CONFIG_OUTPUT* out);

    // キャリブレーション用の入力抑止（オフモード）。有効中は対象デバイスのキー入力が
    // Windowsへ届かない。このハンドル（IoctlClient）をCloseすると（プロセス終了・
    // クラッシュ時を含む）ドライバ側で自動的に解除される。
    bool SetCaptureSuppress(ULONG uniqId, bool enable);

    // Phase 3: マクロ。stepsが空ならそのmacroIdをクリアする。
    bool SetMacro(ULONG uniqId, ULONG macroId, const std::vector<KBDLAYER_MACRO_STEP>& steps);
    bool GetMacro(ULONG uniqId, ULONG macroId, std::vector<KBDLAYER_MACRO_STEP>* outSteps);

    // Phase 4: コンボ（2キー限定v1）。entry=nullptrはそのcomboIdをクリアする。
    bool SetCombo(ULONG uniqId, ULONG comboId, const KBDLAYER_COMBO_ENTRY* entry);
    bool GetCombo(ULONG uniqId, ULONG comboId, KBDLAYER_COMBO_ENTRY* outEntry, bool* outUsed);

    // Phase 5: タップダンス（v1: タップ回数3回まで）。entry=nullptrはそのtapDanceIdをクリアする。
    bool SetTapDance(ULONG uniqId, ULONG tapDanceId, const KBDLAYER_TAPDANCE_ENTRY* entry);
    bool GetTapDance(ULONG uniqId, ULONG tapDanceId, KBDLAYER_TAPDANCE_ENTRY* outEntry, bool* outUsed);

    // Phase 6: NICOLA。blobが空なら設定をクリアする。
    bool SetNicola(ULONG uniqId, const std::vector<unsigned char>& blob);
    bool GetNicola(ULONG uniqId, std::vector<unsigned char>* outBlob);

    ~IoctlClient() { Close(); }

private:
    bool RequireWriteAccess(const char* operation);

    HANDLE handle_ = INVALID_HANDLE_VALUE;
    bool readOnly_ = false;
};

#endif // _KEYMAPTOOL_IOCTLCLIENT_H_
