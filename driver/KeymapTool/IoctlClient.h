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

    // SET_CONFIG v2で追加した項目（kbdlayer_ioctl.hのKBDLAYER_SET_CONFIG_INPUT参照）。
    struct TapHoldOptions {
        ULONG TapHoldMode;    // KBDLAYER_TAPHOLD_MODE_*
        ULONG QuickTapTermMs; // 0 = TapTermMsと同じ、KBDLAYER_QUICK_TAP_TERM_DISABLED = 使わない
        ULONG TappingToggle;  // 0 = 既定(5)
    };

    // Phase 2/4/5: TapTermMs（Mod-Tap/Layer-Tap判定閾値）・ComboTermMs（コンボ判定閾値）・
    // TapDanceTermMs（タップダンス判定閾値、いずれもミリ秒）を設定する。0を渡すとその項目だけ
    // ドライバ側既定値にリセットする (TapTermMs=200ms, ComboTermMs=50ms, TapDanceTermMs=200ms)。
    // tapHoldを渡すと判定方式・Quick Tap・TTの回数も設定する（v2。古いドライバは無視する）。
    // nullptrなら旧サイズで送り、それらは変えない。
    bool SetConfig(ULONG uniqId, ULONG tapTermMs, ULONG comboTermMs, ULONG tapDanceTermMs,
                   const TapHoldOptions* tapHold = nullptr);
    // 現在の実効値を取得する。古いドライバは先頭の3項目だけを返す。そのときは追加した項目を
    // 既定値にし、*outHasTapHoldOptionsをfalseにする。
    bool GetConfig(ULONG uniqId, KBDLAYER_GET_CONFIG_OUTPUT* out, bool* outHasTapHoldOptions = nullptr);

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

    // Key Override（2026-10-01）。entry=nullptrはそのidをクリアする。
    bool SetKeyOverride(ULONG uniqId, ULONG id, const KBDLAYER_KEY_OVERRIDE_ENTRY* entry);
    bool GetKeyOverride(ULONG uniqId, ULONG id, KBDLAYER_KEY_OVERRIDE_ENTRY* outEntry, bool* outUsed);

    // Phase 5: タップダンス（v1: タップ回数3回まで）。entry=nullptrはそのtapDanceIdをクリアする。
    bool SetTapDance(ULONG uniqId, ULONG tapDanceId, const KBDLAYER_TAPDANCE_ENTRY* entry);
    bool GetTapDance(ULONG uniqId, ULONG tapDanceId, KBDLAYER_TAPDANCE_ENTRY* outEntry, bool* outUsed);

    // Phase 6: NICOLA。blobが空なら設定をクリアする。
    bool SetNicola(ULONG uniqId, const std::vector<unsigned char>& blob);
    bool GetNicola(ULONG uniqId, std::vector<unsigned char>* outBlob);

    // レイヤー制御の双方向通知。いずれもFILE_READ_ACCESSなので、昇格していなくても使える。
    bool GetLayerState(ULONG uniqId, KBDLAYER_LAYER_STATE* out);
    // レイヤー状態のSeqがlastSeqと変わるまで待つ（同期呼び出しなので、このスレッドは戻るまで止まる）。
    // *outCancelled = キャンセル・デバイスの取り外しで戻った。
    bool WaitLayerChange(ULONG uniqId, ULONG lastSeq, KBDLAYER_LAYER_STATE* out, bool* outCancelled);
    // アプリ管理レイヤーの操作。このハンドル（IoctlClient）を閉じると、立てたレイヤーは下ろされる。
    bool SetLayerState(ULONG uniqId, ULONG op, ULONG layer, ULONG mask, ULONG flags, KBDLAYER_LAYER_STATE* out);

    // E-2: DeviceIdは一意でない前提で設計する（common/kbdlayer_ioctl.hのコメント参照）。
    // GET/SET_DEVICE_SCOPE: このキーボードのレイヤーマップ等を「同型番で共有」
    // (KBDLAYER_BINDING_MODEL) と「この1台だけ」(KBDLAYER_BINDING_INSTANCE) の
    // どちらで永続化するか。GET_DEVICE_SCOPEはFILE_READ_ACCESSなので昇格していなくても使える。
    bool GetDeviceScope(ULONG uniqId, KBDLAYER_DEVICE_SCOPE* out);
    bool SetDeviceScope(ULONG uniqId, ULONG binding, KBDLAYER_DEVICE_SCOPE* out);

    // 緊急時の解除手段（release-roadmap-priority.md 項番3）。ドライバ全体の設定なので
    // UniqIdを取らない。GET_EMERGENCY_RELEASEはFILE_READ_ACCESSなので昇格していなくても使える。
    bool GetEmergencyRelease(KBDLAYER_EMERGENCY_RELEASE_CONFIG* out);
    bool SetEmergencyRelease(const KBDLAYER_EMERGENCY_RELEASE_CONFIG& config);

    // i8042prtの書き換え設定（ドライバ全体）。SETは全体置換。GETは昇格していなくても使える。
    bool GetI8042Config(KBDLAYER_I8042_CONFIG* out);
    bool SetI8042Config(const KBDLAYER_I8042_CONFIG& config);

    // 機能別ON/OFF（項番4）。features は KBDLAYER_FEATURE_* のOR。
    bool GetFeatures(ULONG uniqId, ULONG* outFeatures);
    bool SetFeatures(ULONG uniqId, ULONG features);

    ~IoctlClient() { Close(); }

private:
    bool RequireWriteAccess(const char* operation);

    HANDLE handle_ = INVALID_HANDLE_VALUE;
    bool readOnly_ = false;
};

#endif // _KEYMAPTOOL_IOCTLCLIENT_H_
