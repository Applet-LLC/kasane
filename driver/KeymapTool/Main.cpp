// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

/*
  KeymapTool/Main.cpp

  kbdlayer.sys 用のCLIツール。addid/DriverTestApp/DriverTestApp.cpp の
  --list/--get/--remap/--clear/--on/--off 相当のコマンドに加え、VIA/VIAL
  keymap.jsonの読み込みとキャリブレーションウィザードを提供する
  （.plans/perplexity-...md の「ユーザーモード設計」節、「検証方法」節）。
*/

#include <windows.h>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#include "..\\common\\kbdlayer_ioctl.h"
#include "IoctlClient.h"
#include "InfoJson.h"
#include "KeymapJson.h"
#include "CalibrationProfile.h"
#include "BlobBuilder.h"
#include "KeycodeTable.h"

namespace {

bool ParseHexU32(const char* s, ULONG* out) {
    if (!s || !*s) return false;
    char* end = nullptr;
    unsigned long v = strtoul(s, &end, 16);
    if (end == s || *end != '\0') return false;
    *out = (ULONG)v;
    return true;
}

void PrintLayerMapBlob(const unsigned char* blob, size_t blobLength) {
    if (blobLength < sizeof(KBDLAYER_MAP_HEADER)) {
        printf("  (no layer map set)\n");
        return;
    }
    const KBDLAYER_MAP_HEADER* header = (const KBDLAYER_MAP_HEADER*)blob;
    const KBDLAYER_KEY_ENTRY* entries =
        (const KBDLAYER_KEY_ENTRY*)(blob + sizeof(KBDLAYER_MAP_HEADER));

    printf("  LayerCount=%lu KeyCount=%lu\n", header->LayerCount, header->KeyCount);
    for (ULONG i = 0; i < header->KeyCount; i++) {
        printf("    [%lu] SourceScanCode=0x%04X\n", i, entries[i].SourceScanCode);
        for (ULONG L = 0; L < header->LayerCount; L++) {
            const KBDLAYER_ACTION& a = entries[i].Actions[L];
            printf("      L%lu: Type=%u Param1=%u Param2=0x%04X\n", L, a.Type, a.Param1, a.Param2);
        }
    }
}

// --suppress指定時、キャプチャ待機の既定タイムアウト（10秒）がこの回数だけ連続したら
// キャリブレーションを中断する（約60秒。ドライバ側のアイドルタイムアウトと同じ長さ）。
const int kSuppressMaxConsecutiveTimeouts = 6;

// --suppress（オフモード）: スコープの間だけ対象キーボードの入力をWindowsへ渡さない。
// デストラクタで解除する。プロセスが異常終了した場合も、コントロールデバイスの
// ハンドルが閉じられた時点でドライバ側が自動的に解除する（IRP_MJ_CLEANUP）。
class CaptureSuppressGuard {
public:
    CaptureSuppressGuard(IoctlClient* client, ULONG uniqId, bool enable)
        : client_(client), uniqId_(uniqId), requested_(enable) {}
    ~CaptureSuppressGuard() {
        if (active_) client_->SetCaptureSuppress(uniqId_, false);
    }
    bool Begin() {
        if (!requested_) return true;
        if (!client_->SetCaptureSuppress(uniqId_, true)) return false;
        active_ = true;
        printf("--suppress: key presses on this keyboard are NOT sent to Windows until this command ends.\n"
               "            To abort, close this console window with the mouse, or stop pressing keys\n"
               "            for about 60 seconds.\n");
        return true;
    }
private:
    IoctlClient* client_;
    ULONG uniqId_;
    bool requested_;
    bool active_ = false;
};

int CmdList(IoctlClient* client) {
    std::vector<DeviceListEntry> devices;
    if (!client->ListDevices(&devices)) return 1;

    printf("DeviceCount=%zu\n", devices.size());
    for (const auto& d : devices) {
        printf("  UniqId=%08X HasLayerMap=%d Enabled=%d ActiveLayerMask=0x%02X HardwareId=%ls\n",
               d.UniqId, d.HasLayerMap ? 1 : 0, d.Enabled ? 1 : 0, d.ActiveLayerMask, d.HardwareId.c_str());
    }
    return 0;
}

int CmdGet(IoctlClient* client, ULONG uniqId) {
    std::vector<unsigned char> blob;
    if (!client->GetLayerMap(uniqId, &blob)) return 1;

    printf("UniqId=%08X BlobLength=%zu\n", uniqId, blob.size());
    PrintLayerMapBlob(blob.data(), blob.size());
    return 0;
}

int CmdClear(IoctlClient* client, ULONG uniqId) {
    if (!client->ClearLayerMap(uniqId)) return 1;
    printf("CLEAR_LAYERMAP ok. UniqId=%08X\n", uniqId);
    return 0;
}

int CmdSetEnabled(IoctlClient* client, ULONG uniqId, bool enabled) {
    if (!client->SetLayerMapEnabled(uniqId, enabled)) return 1;
    printf("SET_LAYERMAP_ENABLED ok. UniqId=%08X Enabled=%d\n", uniqId, enabled ? 1 : 0);
    return 0;
}

int CmdCapture(IoctlClient* client, ULONG uniqId, ULONG timeoutMs, bool suppress) {
    CaptureSuppressGuard guard(client, uniqId, suppress);
    if (!guard.Begin()) return 1;

    USHORT scanCode = 0;
    bool timedOut = false;
    if (!client->CaptureNextKey(uniqId, timeoutMs, &scanCode, &timedOut)) return 1;
    if (timedOut) {
        printf("timed out, no key detected\n");
    } else {
        printf("captured scancode 0x%04X\n", scanCode);
    }
    return 0;
}

int CmdSetConfig(IoctlClient* client, ULONG uniqId, ULONG tapTermMs, ULONG comboTermMs, ULONG tapDanceTermMs) {
    if (!client->SetConfig(uniqId, tapTermMs, comboTermMs, tapDanceTermMs)) return 1;
    printf("SET_CONFIG ok. UniqId=%08X TapTermMs=%lu%s ComboTermMs=%lu%s TapDanceTermMs=%lu%s\n", uniqId,
           tapTermMs != 0 ? tapTermMs : 200UL,
           tapTermMs == 0 ? " (default)" : "",
           comboTermMs != 0 ? comboTermMs : 50UL,
           comboTermMs == 0 ? " (default)" : "",
           tapDanceTermMs != 0 ? tapDanceTermMs : 200UL,
           tapDanceTermMs == 0 ? " (default)" : "");
    return 0;
}

int CmdGetConfig(IoctlClient* client, ULONG uniqId) {
    KBDLAYER_GET_CONFIG_OUTPUT config;
    if (!client->GetConfig(uniqId, &config)) return 1;
    printf("UniqId=%08X TapTermMs=%lu ComboTermMs=%lu TapDanceTermMs=%lu\n",
           uniqId, config.TapTermMs, config.ComboTermMs, config.TapDanceTermMs);
    return 0;
}

// --set-macro <UniqIdHex> <MacroId> [KC_xxx KC_yyy ...]
// 各キーコードトークンをタップ（押下+解放）1回分のステップとして展開する。
// 引数無し（キーコードを1つも渡さない）場合はそのマクロIDをクリアする。
int CmdSetMacro(IoctlClient* client, ULONG uniqId, ULONG macroId, int argc, char** argv) {
    std::vector<KBDLAYER_MACRO_STEP> steps;
    for (int i = 0; i < argc; i++) {
        KeycodeMapping mapping;
        if (!KeycodeTable::Resolve(argv[i], &mapping) || mapping.ActionType != KBDLAYER_ACTION_KEY) {
            fprintf(stderr, "--set-macro: \"%s\" is not a plain key (macros only support KC_* keys, not layer actions)\n", argv[i]);
            return 1;
        }
        if (steps.size() + 2 > KBDLAYER_MAX_MACRO_STEPS) {
            fprintf(stderr, "--set-macro: too many steps (max %d key taps = %d steps)\n",
                    KBDLAYER_MAX_MACRO_STEPS / 2, KBDLAYER_MAX_MACRO_STEPS);
            return 1;
        }
        KBDLAYER_MACRO_STEP press{ mapping.Param2, 0, 0 };
        KBDLAYER_MACRO_STEP release{ mapping.Param2, 1, 0 };
        steps.push_back(press);
        steps.push_back(release);
    }

    if (!client->SetMacro(uniqId, macroId, steps)) return 1;
    if (steps.empty()) {
        printf("SET_MACRO ok. UniqId=%08X MacroId=%lu cleared.\n", uniqId, macroId);
    } else {
        printf("SET_MACRO ok. UniqId=%08X MacroId=%lu, %zu steps (%d key taps).\n",
               uniqId, macroId, steps.size(), argc);
    }
    return 0;
}

int CmdGetMacro(IoctlClient* client, ULONG uniqId, ULONG macroId) {
    std::vector<KBDLAYER_MACRO_STEP> steps;
    if (!client->GetMacro(uniqId, macroId, &steps)) return 1;

    printf("UniqId=%08X MacroId=%lu StepCount=%zu\n", uniqId, macroId, steps.size());
    for (size_t i = 0; i < steps.size(); i++) {
        printf("  [%zu] ScanCode=0x%04X %s\n", i, steps[i].ScanCode,
               steps[i].IsBreak ? "release" : "press");
    }
    return 0;
}

// --set-combo <UniqIdHex> <ComboId> <KC_member1> <KC_member2> <KC_output>
// v1制限（common/kbdlayer_ioctl.h参照）: メンバーは常に2つ、出力は単純キーのみ。
// KC_*トークンは、そのキーの標準スキャンコード（Enc16。ほとんどのキーボードで
// 物理位置と一致する）として解決される。非標準な配列のキーボードでは
// --captureで実際のスキャンコードを確認した上で、KeymapTool側の対応キーコード名を
// 使うこと。引数無し（キーコードを1つも渡さない）場合はそのコンボIDをクリアする。
int CmdSetCombo(IoctlClient* client, ULONG uniqId, ULONG comboId, int argc, char** argv) {
    if (argc == 0) {
        if (!client->SetCombo(uniqId, comboId, nullptr)) return 1;
        printf("SET_COMBO ok. UniqId=%08X ComboId=%lu cleared.\n", uniqId, comboId);
        return 0;
    }
    if (argc != 3) {
        fprintf(stderr, "--set-combo: expected 2 member keys + 1 output key "
                "(KC_member1 KC_member2 KC_output), or none to clear\n");
        return 1;
    }

    KeycodeMapping member1, member2, output;
    if (!KeycodeTable::Resolve(argv[0], &member1) || member1.ActionType != KBDLAYER_ACTION_KEY) {
        fprintf(stderr, "--set-combo: \"%s\" is not a plain key\n", argv[0]);
        return 1;
    }
    if (!KeycodeTable::Resolve(argv[1], &member2) || member2.ActionType != KBDLAYER_ACTION_KEY) {
        fprintf(stderr, "--set-combo: \"%s\" is not a plain key\n", argv[1]);
        return 1;
    }
    if (member1.Param2 == member2.Param2) {
        fprintf(stderr, "--set-combo: the two member keys must be different\n");
        return 1;
    }
    if (!KeycodeTable::Resolve(argv[2], &output) || output.ActionType != KBDLAYER_ACTION_KEY) {
        fprintf(stderr, "--set-combo: output \"%s\" is not a plain key "
                "(v1 only supports a KC_* output)\n", argv[2]);
        return 1;
    }

    KBDLAYER_COMBO_ENTRY entry;
    entry.ScanCode1 = member1.Param2;
    entry.ScanCode2 = member2.Param2;
    entry.OutputAction.Type = KBDLAYER_ACTION_KEY;
    entry.OutputAction.Param1 = 0;
    entry.OutputAction.Param2 = output.Param2;

    if (!client->SetCombo(uniqId, comboId, &entry)) return 1;
    printf("SET_COMBO ok. UniqId=%08X ComboId=%lu: %s + %s -> %s\n",
           uniqId, comboId, argv[0], argv[1], argv[2]);
    return 0;
}

int CmdGetCombo(IoctlClient* client, ULONG uniqId, ULONG comboId) {
    KBDLAYER_COMBO_ENTRY entry;
    bool used = false;
    if (!client->GetCombo(uniqId, comboId, &entry, &used)) return 1;

    if (!used) {
        printf("UniqId=%08X ComboId=%lu: (not set)\n", uniqId, comboId);
    } else {
        printf("UniqId=%08X ComboId=%lu: ScanCode1=0x%04X ScanCode2=0x%04X OutputScanCode=0x%04X\n",
               uniqId, comboId, entry.ScanCode1, entry.ScanCode2, entry.OutputAction.Param2);
    }
    return 0;
}

// --set-tapdance <UniqIdHex> <TapDanceId> <SingleTap> <SingleHold> <DoubleTap> <TripleTap>
// v1制限（common/kbdlayer_ioctl.h参照）: タップ回数は3回まで区別。SingleTap/DoubleTap/
// TripleTapは単純キー(KC_xxx)のみ許可。SingleHoldはKC_xxx（押している間その生キーを
// 送る）に加え、MT(mod,kc)/LT(layer,kc)トークンも受け付ける（タップ時キーコード引数は
// 無視される。ホールド時のmod/layer部分だけがSingleHoldActionとして使われる。
// 例: --set-tapdance <id> KC_ESC "LT(1,KC_NO)" KC_TAB KC_CAPS）。
// 引数無し（4つとも渡さない）場合はそのタップダンスIDをクリアする。
int CmdSetTapDance(IoctlClient* client, ULONG uniqId, ULONG tapDanceId, int argc, char** argv) {
    if (argc == 0) {
        if (!client->SetTapDance(uniqId, tapDanceId, nullptr)) return 1;
        printf("SET_TAPDANCE ok. UniqId=%08X TapDanceId=%lu cleared.\n", uniqId, tapDanceId);
        return 0;
    }
    if (argc != 4) {
        fprintf(stderr, "--set-tapdance: expected 4 actions (SingleTap SingleHold DoubleTap TripleTap), "
                "or none to clear\n");
        return 1;
    }

    KeycodeMapping singleTap, singleHold, doubleTap, tripleTap;
    if (!KeycodeTable::Resolve(argv[0], &singleTap) || singleTap.ActionType != KBDLAYER_ACTION_KEY) {
        fprintf(stderr, "--set-tapdance: SingleTap \"%s\" is not a plain key\n", argv[0]);
        return 1;
    }
    if (!KeycodeTable::Resolve(argv[1], &singleHold) ||
        (singleHold.ActionType != KBDLAYER_ACTION_KEY &&
         singleHold.ActionType != KBDLAYER_ACTION_MODTAP &&
         singleHold.ActionType != KBDLAYER_ACTION_LAYERTAP)) {
        fprintf(stderr, "--set-tapdance: SingleHold \"%s\" must be a plain key, MT(mod,kc), or LT(layer,kc)\n", argv[1]);
        return 1;
    }
    if (!KeycodeTable::Resolve(argv[2], &doubleTap) || doubleTap.ActionType != KBDLAYER_ACTION_KEY) {
        fprintf(stderr, "--set-tapdance: DoubleTap \"%s\" is not a plain key\n", argv[2]);
        return 1;
    }
    if (!KeycodeTable::Resolve(argv[3], &tripleTap) || tripleTap.ActionType != KBDLAYER_ACTION_KEY) {
        fprintf(stderr, "--set-tapdance: TripleTap \"%s\" is not a plain key\n", argv[3]);
        return 1;
    }

    KBDLAYER_TAPDANCE_ENTRY entry;
    entry.SingleTapAction = { singleTap.ActionType, singleTap.Param1, singleTap.Param2 };
    entry.SingleHoldAction = { singleHold.ActionType, singleHold.Param1, singleHold.Param2 };
    entry.DoubleTapAction = { doubleTap.ActionType, doubleTap.Param1, doubleTap.Param2 };
    entry.TripleTapAction = { tripleTap.ActionType, tripleTap.Param1, tripleTap.Param2 };

    if (!client->SetTapDance(uniqId, tapDanceId, &entry)) return 1;
    printf("SET_TAPDANCE ok. UniqId=%08X TapDanceId=%lu: tap=%s hold=%s double=%s triple=%s\n",
           uniqId, tapDanceId, argv[0], argv[1], argv[2], argv[3]);
    return 0;
}

int CmdGetTapDance(IoctlClient* client, ULONG uniqId, ULONG tapDanceId) {
    KBDLAYER_TAPDANCE_ENTRY entry;
    bool used = false;
    if (!client->GetTapDance(uniqId, tapDanceId, &entry, &used)) return 1;

    if (!used) {
        printf("UniqId=%08X TapDanceId=%lu: (not set)\n", uniqId, tapDanceId);
    } else {
        printf("UniqId=%08X TapDanceId=%lu:\n", uniqId, tapDanceId);
        printf("  SingleTap:  Type=%u Param1=%u Param2=0x%04X\n",
               entry.SingleTapAction.Type, entry.SingleTapAction.Param1, entry.SingleTapAction.Param2);
        printf("  SingleHold: Type=%u Param1=%u Param2=0x%04X\n",
               entry.SingleHoldAction.Type, entry.SingleHoldAction.Param1, entry.SingleHoldAction.Param2);
        printf("  DoubleTap:  Type=%u Param1=%u Param2=0x%04X\n",
               entry.DoubleTapAction.Type, entry.DoubleTapAction.Param1, entry.DoubleTapAction.Param2);
        printf("  TripleTap:  Type=%u Param1=%u Param2=0x%04X\n",
               entry.TripleTapAction.Type, entry.TripleTapAction.Param1, entry.TripleTapAction.Param2);
    }
    return 0;
}

// info.jsonを使うキャリブレーション: --calibrate <UniqIdHex> <info.json> <out-profile.json> [layoutName]
int CmdCalibrate(IoctlClient* client, ULONG uniqId, const std::string& infoJsonPath,
                  const std::string& outProfilePath, const std::string& layoutName, bool suppress) {
    std::vector<LayoutKeyPosition> positions;
    int rows = 0, cols = 0;
    std::string error;
    if (!InfoJson::Load(infoJsonPath, layoutName, &positions, &rows, &cols, &error)) {
        fprintf(stderr, "failed to load info.json: %s\n", error.c_str());
        return 1;
    }

    CalibrationProfile profile;
    {
        CaptureSuppressGuard guard(client, uniqId, suppress);
        if (!guard.Begin()) return 1;
        if (!RunCalibrationWizard(client, uniqId, positions, &profile,
                                  suppress ? kSuppressMaxConsecutiveTimeouts : 0)) {
            return 1;
        }
    }

    if (!profile.Save(outProfilePath, &error)) {
        fprintf(stderr, "failed to save profile: %s\n", error.c_str());
        return 1;
    }
    printf("Calibration profile saved to %s\n", outProfilePath.c_str());
    return 0;
}

// info.json無しの簡易モード: 物理キーをkeyCount個、列挙順にそのまま案内する。
// row/colは使わず、単純にインデックス i を row=0, col=i として保存する
// （Find(row,col)による後段の突き合わせロジックをそのまま再利用するための表現）。
int CmdCalibrateSimple(IoctlClient* client, ULONG uniqId, int keyCount, const std::string& outProfilePath,
                       bool suppress) {
    std::vector<LayoutKeyPosition> positions(keyCount);
    for (int i = 0; i < keyCount; i++) {
        positions[i].Row = 0;
        positions[i].Col = i;
    }

    CalibrationProfile profile;
    {
        CaptureSuppressGuard guard(client, uniqId, suppress);
        if (!guard.Begin()) return 1;
        if (!RunCalibrationWizard(client, uniqId, positions, &profile,
                                  suppress ? kSuppressMaxConsecutiveTimeouts : 0)) {
            return 1;
        }
    }

    std::string error;
    if (!profile.Save(outProfilePath, &error)) {
        fprintf(stderr, "failed to save profile: %s\n", error.c_str());
        return 1;
    }
    printf("Calibration profile saved to %s\n", outProfilePath.c_str());
    return 0;
}

// info.json + keymap.json + calibration profile を突き合わせてSET_LAYERMAPする。
int CmdApply(IoctlClient* client, ULONG uniqId, const std::string& infoJsonPath,
             const std::string& keymapJsonPath, const std::string& profilePath,
             const std::string& layoutName) {
    std::string error;

    std::vector<LayoutKeyPosition> positions;
    int rows = 0, cols = 0;
    if (!InfoJson::Load(infoJsonPath, layoutName, &positions, &rows, &cols, &error)) {
        fprintf(stderr, "failed to load info.json: %s\n", error.c_str());
        return 1;
    }

    std::vector<std::vector<std::string>> layers;
    if (!KeymapJson::Load(keymapJsonPath, &layers, &error)) {
        fprintf(stderr, "failed to load keymap.json: %s\n", error.c_str());
        return 1;
    }

    CalibrationProfile profile;
    if (!profile.Load(profilePath, &error)) {
        fprintf(stderr, "failed to load calibration profile: %s\n", error.c_str());
        return 1;
    }

    if (!layers.empty() && layers[0].size() != positions.size()) {
        fprintf(stderr, "keymap.json layer size (%zu) does not match info.json layout key count (%zu)\n",
                layers[0].size(), positions.size());
        return 1;
    }

    std::vector<USHORT> scanCodesByIndex(positions.size());
    for (size_t i = 0; i < positions.size(); i++) {
        const CalibratedKey* k = profile.Find(positions[i].Row, positions[i].Col);
        if (k == nullptr) {
            fprintf(stderr, "calibration profile has no entry for matrix (row=%d, col=%d) "
                    "(re-run --calibrate against the current info.json layout)\n",
                    positions[i].Row, positions[i].Col);
            return 1;
        }
        scanCodesByIndex[i] = k->ScanCode16;
    }

    std::vector<unsigned char> blob;
    if (!BuildLayerMapBlob(layers, scanCodesByIndex, &blob, &error)) {
        fprintf(stderr, "failed to build layer map: %s\n", error.c_str());
        return 1;
    }

    if (!client->SetLayerMap(uniqId, blob)) return 1;
    printf("SET_LAYERMAP ok. UniqId=%08X, %zu keys x %zu layers. "
           "Note: still OFF until you run --on %08X.\n",
           uniqId, scanCodesByIndex.size(), layers.size(), uniqId);
    return 0;
}

// 簡易モード適用: info.json無し、キャリブレーションで得たインデックス順の
// スキャンコード列とkeymap.jsonの各レイヤーのフラット配列を、そのまま
// 同じ並び順として突き合わせる。
int CmdApplySimple(IoctlClient* client, ULONG uniqId, const std::string& keymapJsonPath,
                    const std::string& profilePath) {
    std::string error;

    std::vector<std::vector<std::string>> layers;
    if (!KeymapJson::Load(keymapJsonPath, &layers, &error)) {
        fprintf(stderr, "failed to load keymap.json: %s\n", error.c_str());
        return 1;
    }

    CalibrationProfile profile;
    if (!profile.Load(profilePath, &error)) {
        fprintf(stderr, "failed to load calibration profile: %s\n", error.c_str());
        return 1;
    }

    std::vector<USHORT> scanCodesByIndex(profile.Keys.size());
    for (size_t i = 0; i < profile.Keys.size(); i++) {
        // CmdCalibrateSimpleはrow=0, col=iで保存しているので、そのままインデックス順に並べる。
        const CalibratedKey* k = profile.Find(0, (int)i);
        if (k == nullptr) {
            fprintf(stderr, "calibration profile missing index %zu (was it produced by --calibrate-simple?)\n", i);
            return 1;
        }
        scanCodesByIndex[i] = k->ScanCode16;
    }

    std::vector<unsigned char> blob;
    if (!BuildLayerMapBlob(layers, scanCodesByIndex, &blob, &error)) {
        fprintf(stderr, "failed to build layer map: %s\n", error.c_str());
        return 1;
    }

    if (!client->SetLayerMap(uniqId, blob)) return 1;
    printf("SET_LAYERMAP ok (simple mode). UniqId=%08X, %zu keys x %zu layers. "
           "Note: still OFF until you run --on %08X.\n",
           uniqId, scanCodesByIndex.size(), layers.size(), uniqId);
    return 0;
}

void PrintUsage() {
    printf(
        "Usage:\n"
        "  KeymapTool.exe --list\n"
        "      Show UniqId of every connected keyboard the driver has identified.\n"
        "  KeymapTool.exe --get <UniqIdHex>\n"
        "      Show the layer map currently loaded for a device.\n"
        "  KeymapTool.exe --clear <UniqIdHex>\n"
        "      Remove the layer map (must be OFF first).\n"
        "  KeymapTool.exe --on <UniqIdHex> / --off <UniqIdHex>\n"
        "      Enable/disable the layer map (map contents are kept when OFF).\n"
        "  KeymapTool.exe --capture <UniqIdHex> [timeoutMs] [--suppress]\n"
        "      One-shot test of IOCTL_KBDLAYER_CAPTURE_NEXT_KEY.\n"
        "  KeymapTool.exe --calibrate <UniqIdHex> <info.json> <out-profile.json> [layoutName] [--suppress]\n"
        "      Walk every key in info.json's layout, prompting you to press it,\n"
        "      and save the physical scancode mapping to out-profile.json.\n"
        "  KeymapTool.exe --calibrate-simple <UniqIdHex> <keyCount> <out-profile.json> [--suppress]\n"
        "      Same, but without an info.json: just walks keyCount keys in enumeration order\n"
        "      (for boards with no published info.json).\n"
        "      --suppress: while calibrating/capturing, key presses on that keyboard are not\n"
        "      sent to Windows (so Enter, Win, etc. do nothing). Released automatically when\n"
        "      the command ends, the console is closed, or no key is pressed for ~60 seconds.\n"
        "  KeymapTool.exe --apply <UniqIdHex> <info.json> <keymap.json> <profile.json> [layoutName]\n"
        "      Build a layer map from a VIA-exported keymap.json + info.json + a calibration\n"
        "      profile, and SET it on the device (still requires --on to activate).\n"
        "  KeymapTool.exe --apply-simple <UniqIdHex> <keymap.json> <profile.json>\n"
        "      Same, but matches keymap.json's flat per-layer arrays to the profile by\n"
        "      enumeration order (paired with --calibrate-simple).\n"
        "  KeymapTool.exe --set-config <UniqIdHex> <TapTermMs> [ComboTermMs] [TapDanceTermMs]\n"
        "      Set the Mod-Tap/Layer-Tap tap-vs-hold threshold, the combo match window,\n"
        "      and the tap-dance threshold, in milliseconds (0 resets any one of them to\n"
        "      its driver default: TapTermMs=200ms, ComboTermMs=50ms, TapDanceTermMs=200ms;\n"
        "      an omitted trailing argument defaults to 0).\n"
        "  KeymapTool.exe --get-config <UniqIdHex>\n"
        "      Show the current TapTermMs / ComboTermMs / TapDanceTermMs values.\n"
        "  KeymapTool.exe --set-macro <UniqIdHex> <MacroId> [KC_xxx KC_yyy ...]\n"
        "      Define macro <MacroId> (0-31) as a sequence of key taps, played back\n"
        "      via MACRO(<MacroId>) in a keymap.json. Omit the keycodes to clear it.\n"
        "  KeymapTool.exe --get-macro <UniqIdHex> <MacroId>\n"
        "      Show the raw step list currently stored for a macro (debugging).\n"
        "  KeymapTool.exe --set-combo <UniqIdHex> <ComboId> [KC_member1 KC_member2 KC_output]\n"
        "      Define combo <ComboId> (0-15): pressing member1+member2 together within\n"
        "      ComboTermMs sends KC_output instead. v1 limits: exactly 2 members, plain-key\n"
        "      output only, and a given key may not belong to more than one combo.\n"
        "      Omit the keycodes to clear it.\n"
        "  KeymapTool.exe --get-combo <UniqIdHex> <ComboId>\n"
        "      Show the current definition of a combo (debugging).\n"
        "  KeymapTool.exe --set-tapdance <UniqIdHex> <TapDanceId> [<SingleTap> <SingleHold> <DoubleTap> <TripleTap>]\n"
        "      Define tap-dance <TapDanceId> (0-15), played back via TAPDANCE(<TapDanceId>)\n"
        "      in a keymap.json. v1 limits: up to 3 taps are distinguished; SingleTap/\n"
        "      DoubleTap/TripleTap must be plain keys; SingleHold may be a plain key,\n"
        "      MT(mod,kc), or LT(layer,kc) (the tap-kc argument in MT/LT is ignored here).\n"
        "      Omit all 4 actions to clear it.\n"
        "  KeymapTool.exe --get-tapdance <UniqIdHex> <TapDanceId>\n"
        "      Show the current definition of a tap-dance (debugging).\n"
        "\n"
        "  UniqIdHex is the value shown by --list (e.g. A1B20000).\n");
}

} // namespace

int main(int rawArgc, char** rawArgv) {
    // --suppress はどの位置に書いてもよいオプションとして先に取り除き、
    // 残りを従来通りの位置引数として解釈する。
    bool suppress = false;
    std::vector<char*> args;
    for (int i = 0; i < rawArgc; i++) {
        if (i > 0 && strcmp(rawArgv[i], "--suppress") == 0) {
            suppress = true;
        } else {
            args.push_back(rawArgv[i]);
        }
    }
    args.push_back(nullptr);
    int argc = (int)args.size() - 1;
    char** argv = args.data();

    if (argc <= 1) {
        PrintUsage();
        return 1;
    }

    const char* cmd = argv[1];

    IoctlClient client;
    if (!client.Open()) return 1;

    if (strcmp(cmd, "--list") == 0) {
        return CmdList(&client);
    }

    if (strcmp(cmd, "--get") == 0) {
        ULONG uniqId;
        if (argc < 3 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        return CmdGet(&client, uniqId);
    }

    if (strcmp(cmd, "--clear") == 0) {
        ULONG uniqId;
        if (argc < 3 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        return CmdClear(&client, uniqId);
    }

    if (strcmp(cmd, "--on") == 0 || strcmp(cmd, "--off") == 0) {
        ULONG uniqId;
        if (argc < 3 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        return CmdSetEnabled(&client, uniqId, strcmp(cmd, "--on") == 0);
    }

    if (strcmp(cmd, "--set-config") == 0) {
        ULONG uniqId;
        if (argc < 4 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        ULONG tapTermMs = (ULONG)strtoul(argv[3], nullptr, 10);
        ULONG comboTermMs = (argc >= 5) ? (ULONG)strtoul(argv[4], nullptr, 10) : 0;
        ULONG tapDanceTermMs = (argc >= 6) ? (ULONG)strtoul(argv[5], nullptr, 10) : 0;
        return CmdSetConfig(&client, uniqId, tapTermMs, comboTermMs, tapDanceTermMs);
    }

    if (strcmp(cmd, "--get-config") == 0) {
        ULONG uniqId;
        if (argc < 3 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        return CmdGetConfig(&client, uniqId);
    }

    if (strcmp(cmd, "--set-combo") == 0) {
        ULONG uniqId;
        if (argc < 4 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        ULONG comboId = (ULONG)strtoul(argv[3], nullptr, 10);
        return CmdSetCombo(&client, uniqId, comboId, argc - 4, argv + 4);
    }

    if (strcmp(cmd, "--get-combo") == 0) {
        ULONG uniqId;
        if (argc < 4 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        ULONG comboId = (ULONG)strtoul(argv[3], nullptr, 10);
        return CmdGetCombo(&client, uniqId, comboId);
    }

    if (strcmp(cmd, "--set-tapdance") == 0) {
        ULONG uniqId;
        if (argc < 4 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        ULONG tapDanceId = (ULONG)strtoul(argv[3], nullptr, 10);
        return CmdSetTapDance(&client, uniqId, tapDanceId, argc - 4, argv + 4);
    }

    if (strcmp(cmd, "--get-tapdance") == 0) {
        ULONG uniqId;
        if (argc < 4 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        ULONG tapDanceId = (ULONG)strtoul(argv[3], nullptr, 10);
        return CmdGetTapDance(&client, uniqId, tapDanceId);
    }

    if (strcmp(cmd, "--set-macro") == 0) {
        ULONG uniqId;
        if (argc < 4 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        ULONG macroId = (ULONG)strtoul(argv[3], nullptr, 10);
        return CmdSetMacro(&client, uniqId, macroId, argc - 4, argv + 4);
    }

    if (strcmp(cmd, "--get-macro") == 0) {
        ULONG uniqId;
        if (argc < 4 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        ULONG macroId = (ULONG)strtoul(argv[3], nullptr, 10);
        return CmdGetMacro(&client, uniqId, macroId);
    }

    if (strcmp(cmd, "--capture") == 0) {
        ULONG uniqId;
        if (argc < 3 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        ULONG timeoutMs = (argc >= 4) ? (ULONG)strtoul(argv[3], nullptr, 10) : 0;
        return CmdCapture(&client, uniqId, timeoutMs, suppress);
    }

    if (strcmp(cmd, "--calibrate") == 0) {
        ULONG uniqId;
        if (argc < 5 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        std::string layoutName = (argc >= 6) ? argv[5] : std::string();
        return CmdCalibrate(&client, uniqId, argv[3], argv[4], layoutName, suppress);
    }

    if (strcmp(cmd, "--calibrate-simple") == 0) {
        ULONG uniqId;
        if (argc < 5 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        int keyCount = atoi(argv[3]);
        if (keyCount <= 0) { PrintUsage(); return 1; }
        return CmdCalibrateSimple(&client, uniqId, keyCount, argv[4], suppress);
    }

    if (strcmp(cmd, "--apply") == 0) {
        ULONG uniqId;
        if (argc < 6 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        std::string layoutName = (argc >= 7) ? argv[6] : std::string();
        return CmdApply(&client, uniqId, argv[3], argv[4], argv[5], layoutName);
    }

    if (strcmp(cmd, "--apply-simple") == 0) {
        ULONG uniqId;
        if (argc < 5 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        return CmdApplySimple(&client, uniqId, argv[3], argv[4]);
    }

    PrintUsage();
    return 1;
}
