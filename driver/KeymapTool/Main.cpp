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
#include <cstring>
#include <string>
#include <vector>

#include "..\\common\\kbdlayer_ioctl.h"
#include "IoctlClient.h"
#include "InfoJson.h"
#include "KeymapJson.h"
#include "CalibrationProfile.h"
#include "BlobBuilder.h"
#include "KeycodeTable.h"
#include "NicolaTables.h"

namespace {

// KBDLAYER_BINDING_*の表示名（--list / --get-scope）。
const char* BindingName(ULONG binding) {
    switch (binding) {
    case KBDLAYER_BINDING_INSTANCE: return "instance";
    case KBDLAYER_BINDING_GLOBAL: return "global";
    default: return "model";
    }
}

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

    // レイヤー数は常にKBDLAYER_MAX_LAYERS。レイヤー1以上のTRANSPARENT（未使用レイヤーの
    // 埋め草を含む）は表示を省く。
    printf("  Layers=%d KeyCount=%lu (layer 1+ entries that are KC_TRNS are omitted)\n",
           KBDLAYER_MAX_LAYERS, header->KeyCount);
    for (ULONG i = 0; i < header->KeyCount; i++) {
        printf("    [%lu] SourceScanCode=0x%04X\n", i, entries[i].SourceScanCode);
        for (ULONG L = 0; L < KBDLAYER_MAX_LAYERS; L++) {
            const KBDLAYER_ACTION& a = entries[i].Actions[L];
            if (L > 0 && a.Type == KBDLAYER_ACTION_TRANSPARENT) {
                continue;
            }
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
        printf("  UniqId=%08X HasLayerMap=%d Enabled=%d ActiveLayerMask=0x%04X HardwareId=%ls\n",
               d.UniqId, d.HasLayerMap ? 1 : 0, d.Enabled ? 1 : 0, d.ActiveLayerMask, d.HardwareId.c_str());
        // E-2: 永続化スコープ（--get-scope/--set-scope参照）。LIST_DEVICESの固定長構造体
        // （kasaneが独自にオフセット計算している）は変えず、別IOCTLで補う。
        KBDLAYER_DEVICE_SCOPE scope;
        if (client->GetDeviceScope(d.UniqId, &scope)) {
            printf("    InstanceKey=%08X ModelKey=%08X Binding=%s\n",
                   scope.InstanceKey, scope.ModelKey,
                   BindingName(scope.Binding));
        }
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

int CmdCapture(IoctlClient* client, ULONG uniqId, ULONG timeoutMs, bool suppress, bool allowFakeShift) {
    CaptureSuppressGuard guard(client, uniqId, suppress);
    if (!guard.Begin()) return 1;

    USHORT scanCode = 0;
    bool timedOut = false;
    if (!client->CaptureNextKey(uniqId, timeoutMs, &scanCode, &timedOut, allowFakeShift)) return 1;
    if (timedOut) {
        printf("timed out, no key detected\n");
    } else {
        printf("captured scancode 0x%04X\n", scanCode);
    }
    return 0;
}

static const char* TapHoldModeName(ULONG mode) {
    switch (mode) {
    case KBDLAYER_TAPHOLD_MODE_HOLD_ON_OTHER_KEY: return "hold";
    case KBDLAYER_TAPHOLD_MODE_PERMISSIVE_HOLD:   return "permissive";
    case KBDLAYER_TAPHOLD_MODE_TAP_PREFERRED:     return "tap";
    default:                                       return "?";
    }
}

// --set-config の key=value 引数（mode= / quicktap= / ttcount=）を解釈して options に反映する。
static bool ParseTapHoldOption(const char* arg, IoctlClient::TapHoldOptions* options) {
    const char* eq = strchr(arg, '=');
    if (eq == nullptr) return false;
    std::string key(arg, eq - arg);
    std::string value(eq + 1);
    char* end = nullptr;
    if (key == "mode") {
        if (value == "hold") options->TapHoldMode = KBDLAYER_TAPHOLD_MODE_HOLD_ON_OTHER_KEY;
        else if (value == "permissive") options->TapHoldMode = KBDLAYER_TAPHOLD_MODE_PERMISSIVE_HOLD;
        else if (value == "tap") options->TapHoldMode = KBDLAYER_TAPHOLD_MODE_TAP_PREFERRED;
        else return false;
        return true;
    }
    if (key == "quicktap") {
        if (value == "off") { options->QuickTapTermMs = KBDLAYER_QUICK_TAP_TERM_DISABLED; return true; }
        if (value == "default") { options->QuickTapTermMs = 0; return true; }
        unsigned long v = strtoul(value.c_str(), &end, 10);
        if (value.empty() || *end != '\0' || v > 10000) return false;
        options->QuickTapTermMs = v;
        return true;
    }
    if (key == "ttcount") {
        unsigned long v = strtoul(value.c_str(), &end, 10);
        if (value.empty() || *end != '\0' || v > KBDLAYER_MAX_TAPPING_TOGGLE) return false;
        options->TappingToggle = v;
        return true;
    }
    return false;
}

// --set-config <UniqIdHex> <TapTermMs> [ComboTermMs] [TapDanceTermMs] [mode=..] [quicktap=..] [ttcount=..]
// key=value を1つも書かなければ旧形式（v1）で送り、判定方式・Quick Tap・TTの回数は変えない。
// 書いた場合は、今の値を読んでから、書いた項目だけを変えて送る（v2）。
int CmdSetConfig(IoctlClient* client, ULONG uniqId, ULONG tapTermMs, ULONG comboTermMs, ULONG tapDanceTermMs,
                 int optionCount, char** options) {
    IoctlClient::TapHoldOptions tapHold = {};
    bool useTapHold = optionCount > 0;
    if (useTapHold) {
        KBDLAYER_GET_CONFIG_OUTPUT current;
        bool hasV2 = false;
        if (!client->GetConfig(uniqId, &current, &hasV2)) return 1;
        if (!hasV2) {
            fprintf(stderr, "--set-config: this kbdlayer driver does not support mode=/quicktap=/ttcount= "
                    "(update the driver)\n");
            return 1;
        }
        tapHold.TapHoldMode = current.TapHoldMode;
        tapHold.QuickTapTermMs = current.QuickTapTermMs;
        tapHold.TappingToggle = current.TappingToggle;
        for (int i = 0; i < optionCount; i++) {
            if (!ParseTapHoldOption(options[i], &tapHold)) {
                fprintf(stderr, "--set-config: bad option \"%s\" (expected mode=hold|permissive|tap, "
                        "quicktap=<ms>|off|default, ttcount=<1-%d>)\n", options[i], KBDLAYER_MAX_TAPPING_TOGGLE);
                return 1;
            }
        }
    }
    if (!client->SetConfig(uniqId, tapTermMs, comboTermMs, tapDanceTermMs, useTapHold ? &tapHold : nullptr)) return 1;
    printf("SET_CONFIG ok. UniqId=%08X TapTermMs=%lu%s ComboTermMs=%lu%s TapDanceTermMs=%lu%s\n", uniqId,
           tapTermMs != 0 ? tapTermMs : 200UL,
           tapTermMs == 0 ? " (default)" : "",
           comboTermMs != 0 ? comboTermMs : 50UL,
           comboTermMs == 0 ? " (default)" : "",
           tapDanceTermMs != 0 ? tapDanceTermMs : 200UL,
           tapDanceTermMs == 0 ? " (default)" : "");
    if (useTapHold) {
        printf("  mode=%s quicktap=%s ttcount=%lu\n", TapHoldModeName(tapHold.TapHoldMode),
               tapHold.QuickTapTermMs == KBDLAYER_QUICK_TAP_TERM_DISABLED ? "off" :
               tapHold.QuickTapTermMs == 0 ? "default(=TapTermMs)" : std::to_string(tapHold.QuickTapTermMs).c_str(),
               tapHold.TappingToggle != 0 ? tapHold.TappingToggle : (ULONG)KBDLAYER_DEFAULT_TAPPING_TOGGLE);
    }
    return 0;
}

int CmdGetConfig(IoctlClient* client, ULONG uniqId) {
    KBDLAYER_GET_CONFIG_OUTPUT config;
    bool hasV2 = false;
    if (!client->GetConfig(uniqId, &config, &hasV2)) return 1;
    printf("UniqId=%08X TapTermMs=%lu ComboTermMs=%lu TapDanceTermMs=%lu\n",
           uniqId, config.TapTermMs, config.ComboTermMs, config.TapDanceTermMs);
    if (hasV2) {
        std::string quick = config.QuickTapTermMs == KBDLAYER_QUICK_TAP_TERM_DISABLED ? "off" :
                            config.QuickTapTermMs == 0 ? "default(=TapTermMs)" : std::to_string(config.QuickTapTermMs);
        printf("  mode=%s quicktap=%s ttcount=%lu\n", TapHoldModeName(config.TapHoldMode),
               quick.c_str(), config.TappingToggle);
    } else {
        printf("  (this driver does not report mode/quicktap/ttcount)\n");
    }
    return 0;
}

// --set-macro <UniqIdHex> <MacroId> [KC_xxx KC_yyy ...]
// 各キーコードトークンをタップ（押下+解放）1回分のステップとして展開する。
// 引数無し（キーコードを1つも渡さない）場合はそのマクロIDをクリアする。
int CmdSetMacro(IoctlClient* client, ULONG uniqId, ULONG macroId, int argc, char** argv) {
    std::vector<KBDLAYER_MACRO_STEP> steps;
    for (int i = 0; i < argc; i++) {
        KeycodeMapping mapping;
        if (!KeycodeTable::Resolve(argv[i], &mapping) ||
            (mapping.ActionType != KBDLAYER_ACTION_KEY && mapping.ActionType != KBDLAYER_ACTION_MODDED_KEY)) {
            fprintf(stderr, "--set-macro: \"%s\" is not a key (macros support KC_* keys and modified keys such as LCTL(KC_C), not layer actions)\n", argv[i]);
            return 1;
        }
        // LCTL(KC_C) は「Ctrl押す, C押す, C離す, Ctrl離す」に展開する（修飾キーは逆順で離す）。
        UCHAR mods = (mapping.ActionType == KBDLAYER_ACTION_MODDED_KEY) ? mapping.Param1 : 0;
        std::vector<KBDLAYER_MACRO_STEP> tap;
        for (int bit = 0; bit < 8; bit++) {
            if (mods & (1u << bit)) tap.push_back(KBDLAYER_MACRO_STEP{ KeycodeTable::ModifierScanCode(bit), 0, 0 });
        }
        tap.push_back(KBDLAYER_MACRO_STEP{ mapping.Param2, 0, 0 });
        tap.push_back(KBDLAYER_MACRO_STEP{ mapping.Param2, 1, 0 });
        for (int bit = 7; bit >= 0; bit--) {
            if (mods & (1u << bit)) tap.push_back(KBDLAYER_MACRO_STEP{ KeycodeTable::ModifierScanCode(bit), 1, 0 });
        }
        if (steps.size() + tap.size() > KBDLAYER_MAX_MACRO_STEPS) {
            fprintf(stderr, "--set-macro: too many steps (max %d steps; a plain key uses 2, each modifier adds 2)\n",
                    KBDLAYER_MAX_MACRO_STEPS);
            return 1;
        }
        steps.insert(steps.end(), tap.begin(), tap.end());
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
    if (!KeycodeTable::Resolve(argv[2], &output) ||
        (output.ActionType != KBDLAYER_ACTION_KEY && output.ActionType != KBDLAYER_ACTION_MODDED_KEY &&
         output.ActionType != KBDLAYER_ACTION_MACRO)) {
        fprintf(stderr, "--set-combo: output \"%s\" must be a plain key, a modified key (LCTL(kc)), or MACRO(n)\n", argv[2]);
        return 1;
    }

    KBDLAYER_COMBO_ENTRY entry;
    entry.ScanCode1 = member1.Param2;
    entry.ScanCode2 = member2.Param2;
    // 出力の種類と引数をそのまま渡す（KEY・修飾付きキー・MACRO。2026-10-03の実機試験で、ここがKEY固定のままで
    // 修飾子マスクやマクロIDが落ちていたのを修正）。
    entry.OutputAction = { output.ActionType, output.Param1, output.Param2 };

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
        printf("UniqId=%08X ComboId=%lu: ScanCode1=0x%04X ScanCode2=0x%04X Output Type=%u Param1=0x%02X Param2=0x%04X\n",
               uniqId, comboId, entry.ScanCode1, entry.ScanCode2,
               entry.OutputAction.Type, entry.OutputAction.Param1, entry.OutputAction.Param2);
    }
    return 0;
}

// タップダンスの引数を解決する。空文字列とKC_NOは「動作なし」（ActionType=NONE）。
static bool ResolveTapDanceArg(const char* token, KeycodeMapping* out) {
    if (token[0] == '\0') {
        *out = KeycodeMapping{};
        out->ActionType = KBDLAYER_ACTION_NONE;
        return true;
    }
    return KeycodeTable::Resolve(token, out);
}

// タップダンスのタップ側（SingleTap/DoubleTap/TripleTap）に置けるか: 単純キー(KC_xxx)、
// 修飾付きキー(LCTL(KC_C)等)、MACRO(n)、動作なし（KC_NO・空）。動作なしはKEY・スキャンコード0に
// 置き換える（SingleTapのNONEはドライバ側で「クリア」の意味になるため）。
static bool NormalizeTapDanceTapAction(KeycodeMapping* m) {
    if (m->ActionType == KBDLAYER_ACTION_NONE) {
        m->ActionType = KBDLAYER_ACTION_KEY;
        m->Param1 = 0;
        m->Param2 = 0;
        return true;
    }
    return m->ActionType == KBDLAYER_ACTION_KEY ||
           m->ActionType == KBDLAYER_ACTION_MODDED_KEY ||
           m->ActionType == KBDLAYER_ACTION_MACRO;
}

// --set-tapdance <UniqIdHex> <TapDanceId> <SingleTap> <SingleHold> <DoubleTap> <TripleTap>
// 制限（common/kbdlayer_ioctl.h参照）: タップ回数は3回まで区別。SingleTap/DoubleTap/
// TripleTapは単純キー(KC_xxx)・修飾付きキー(LCTL(KC_C)等)・MACRO(n)を許可。SingleHoldは
// KC_xxx（押している間その生キーを送る）に加え、MT(mod,kc)/LT(layer,kc)トークンも受け付ける
// （タップ時キーコード引数は無視される。ホールド時のmod/layer部分だけがSingleHoldActionとして
// 使われる。例: --set-tapdance <id> KC_ESC "LT(1,KC_NO)" "LCTL(KC_C)" "MACRO(0)"）。
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
    if (!ResolveTapDanceArg(argv[0], &singleTap) || !NormalizeTapDanceTapAction(&singleTap)) {
        fprintf(stderr, "--set-tapdance: SingleTap \"%s\" must be a key, a modified key (LCTL(kc)), MACRO(n), or KC_NO\n", argv[0]);
        return 1;
    }
    if (!ResolveTapDanceArg(argv[1], &singleHold) ||
        (singleHold.ActionType != KBDLAYER_ACTION_NONE &&
         singleHold.ActionType != KBDLAYER_ACTION_KEY &&
         singleHold.ActionType != KBDLAYER_ACTION_MODTAP &&
         singleHold.ActionType != KBDLAYER_ACTION_LAYERTAP &&
         singleHold.ActionType != KBDLAYER_ACTION_MODDED_KEY &&
         singleHold.ActionType != KBDLAYER_ACTION_LAYER_MOD &&
         singleHold.ActionType != KBDLAYER_ACTION_MACRO)) {
        fprintf(stderr, "--set-tapdance: SingleHold \"%s\" must be a plain key, MT(mod,kc), LT(layer,kc), "
                "a modified key (LCTL(kc)), LM(layer,mod), MACRO(n), or KC_NO\n", argv[1]);
        return 1;
    }
    if (!ResolveTapDanceArg(argv[2], &doubleTap) || !NormalizeTapDanceTapAction(&doubleTap)) {
        fprintf(stderr, "--set-tapdance: DoubleTap \"%s\" must be a key, a modified key (LCTL(kc)), MACRO(n), or KC_NO\n", argv[2]);
        return 1;
    }
    if (!ResolveTapDanceArg(argv[3], &tripleTap) || !NormalizeTapDanceTapAction(&tripleTap)) {
        fprintf(stderr, "--set-tapdance: TripleTap \"%s\" must be a key, a modified key (LCTL(kc)), MACRO(n), or KC_NO\n", argv[3]);
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

// NICOLA設定JSONからblobを組み立てる。警告はstderrへ出すだけで失敗扱いにしない。
bool BuildNicolaBlobOrReport(const char* jsonPath, std::vector<unsigned char>* blob) {
    std::vector<std::string> warnings;
    std::string error;
    if (!NicolaTables::BuildBlob(jsonPath, blob, &warnings, &error)) {
        fprintf(stderr, "failed to load NICOLA json: %s\n", error.c_str());
        return false;
    }
    for (const std::string& w : warnings) {
        fprintf(stderr, "warning: %s\n", w.c_str());
    }
    return true;
}

// --set-nicola <UniqIdHex> <nicola.json>
int CmdSetNicola(IoctlClient* client, ULONG uniqId, const char* jsonPath) {
    std::vector<unsigned char> blob;
    if (!BuildNicolaBlobOrReport(jsonPath, &blob)) return 1;
    if (!client->SetNicola(uniqId, blob)) return 1;

    const KBDLAYER_NICOLA_HEADER* h = (const KBDLAYER_NICOLA_HEADER*)blob.data();
    printf("SET_NICOLA ok. UniqId=%08X, %lu keys. Active while layer %lu is on "
           "(and the layer map is ON).\n", uniqId, h->KeyCount, h->Layer);
    return 0;
}

int CmdGetNicola(IoctlClient* client, ULONG uniqId) {
    std::vector<unsigned char> blob;
    if (!client->GetNicola(uniqId, &blob)) return 1;
    printf("UniqId=%08X BlobLength=%zu\n", uniqId, blob.size());
    NicolaTables::PrintBlob(blob.data(), blob.size());
    return 0;
}

int CmdClearNicola(IoctlClient* client, ULONG uniqId) {
    if (!client->SetNicola(uniqId, std::vector<unsigned char>())) return 1;
    printf("SET_NICOLA ok. UniqId=%08X cleared.\n", uniqId);
    return 0;
}

void PrintLayerState(const KBDLAYER_LAYER_STATE& s) {
    printf("Seq=%lu Effective=0x%04lX (Active=0x%04lX App=0x%04lX Default=%lu) LayerMap=%s NICOLA=%s\n",
           s.Seq, s.EffectiveLayerMask, s.ActiveLayerMask, s.AppLayerMask, s.DefaultLayer,
           s.LayerMapEnabled ? "ON" : "OFF", s.NicolaActive ? "ON" : "OFF");
    // --watch-layerの出力をファイルへリダイレクトしても、強制終了で行が失われないように。
    fflush(stdout);
}

int CmdGetLayerState(IoctlClient* client, ULONG uniqId) {
    KBDLAYER_LAYER_STATE state;
    if (!client->GetLayerState(uniqId, &state)) return 1;
    printf("UniqId=%08X ", uniqId);
    PrintLayerState(state);
    return 0;
}

void PrintDeviceScope(const KBDLAYER_DEVICE_SCOPE& s) {
    printf("InstanceKey=%08X ModelKey=%08X Binding=%s\n",
           s.InstanceKey, s.ModelKey,
           BindingName(s.Binding));
}

// --get-scope <UniqIdHex>: このキーボードの永続化スコープ（E-2）を表示する。
int CmdGetScope(IoctlClient* client, ULONG uniqId) {
    KBDLAYER_DEVICE_SCOPE scope;
    if (!client->GetDeviceScope(uniqId, &scope)) return 1;
    printf("UniqId=%08X ", uniqId);
    PrintDeviceScope(scope);
    return 0;
}

// --set-scope <UniqIdHex> model|instance|global: レイヤーマップ等の永続化先を切り替える。
// 切り替えるときは、現在の設定を切替先へコピーしてから切り替える（設定が消えたように見えないため。
// 切替先の既存の設定は上書きされる。たとえばmodelへ戻すと、その型番で共有している設定が置き換わる）。
// globalだけは、すべてのキーボードで1つの設定を共有するので、すでに共有の設定があればコピーせずにそれを使う。
int CmdSetScope(IoctlClient* client, ULONG uniqId, const char* bindingName) {
    ULONG binding;
    if (strcmp(bindingName, "model") == 0) {
        binding = KBDLAYER_BINDING_MODEL;
    } else if (strcmp(bindingName, "instance") == 0) {
        binding = KBDLAYER_BINDING_INSTANCE;
    } else if (strcmp(bindingName, "global") == 0) {
        binding = KBDLAYER_BINDING_GLOBAL;
    } else {
        fprintf(stderr, "--set-scope: binding must be model / instance / global\n");
        return 1;
    }
    KBDLAYER_DEVICE_SCOPE scope;
    if (!client->SetDeviceScope(uniqId, binding, &scope)) return 1;
    printf("SET_DEVICE_SCOPE ok. UniqId=%08X ", uniqId);
    PrintDeviceScope(scope);
    return 0;
}

void PrintFeatures(ULONG uniqId, ULONG f) {
    printf("UniqId=%08X Macro=%d Combo=%d TapDance=%d KeyOverride=%d\n", uniqId,
           (f & KBDLAYER_FEATURE_MACRO) ? 1 : 0,
           (f & KBDLAYER_FEATURE_COMBO) ? 1 : 0,
           (f & KBDLAYER_FEATURE_TAPDANCE) ? 1 : 0,
           (f & KBDLAYER_FEATURE_KEYOVERRIDE) ? 1 : 0);
}

// --get-features <UniqIdHex>: 機能別ON/OFF（項番4）の現在の状態を表示する。
int CmdGetFeatures(IoctlClient* client, ULONG uniqId) {
    ULONG features = 0;
    if (!client->GetFeatures(uniqId, &features)) return 1;
    PrintFeatures(uniqId, features);
    return 0;
}

// --set-features <UniqIdHex> <macro:0|1> <combo:0|1> <tapdance:0|1> [keyoverride:0|1]
// （--set-configの位置引数と同じスタイル）。keyoverrideを省略したら、今の設定のままにする。
int CmdSetFeatures(IoctlClient* client, ULONG uniqId, const char* macro, const char* combo, const char* tapDance,
                   const char* keyOverride) {
    const char* args[4] = { macro, combo, tapDance, keyOverride };
    const ULONG bits[4] = { KBDLAYER_FEATURE_MACRO, KBDLAYER_FEATURE_COMBO, KBDLAYER_FEATURE_TAPDANCE,
                            KBDLAYER_FEATURE_KEYOVERRIDE };
    ULONG features = 0;
    if (keyOverride == nullptr) {
        ULONG current = 0;
        if (!client->GetFeatures(uniqId, &current)) return 1;
        features |= current & KBDLAYER_FEATURE_KEYOVERRIDE;
    }
    for (int i = 0; i < 4; i++) {
        if (args[i] == nullptr) continue;
        if (strcmp(args[i], "1") == 0) {
            features |= bits[i];
        } else if (strcmp(args[i], "0") != 0) {
            fprintf(stderr, "--set-features: each flag must be 0 or 1 (got \"%s\")\n", args[i]);
            return 1;
        }
    }
    if (!client->SetFeatures(uniqId, features)) return 1;
    printf("SET_FEATURES ok. ");
    PrintFeatures(uniqId, features);
    return 0;
}

// --set-key-override <UniqIdHex> <Id> [<TriggerMods> <TriggerKey> <Replacement> [repeat]]
// 引数無しならそのIdをクリアする。TriggerModsはMOD_*を|でつないだもの、TriggerKeyは単純キー(KC_xxx)、
// Replacementは単純キー、修飾付きキー（LCTL(KC_Z)等）、またはMACRO(n)。repeatは、ReplacementがMACRO(n)のとき、
// オートリピートでマクロを繰り返す（既定は繰り返さない）。
int CmdSetKeyOverride(IoctlClient* client, ULONG uniqId, ULONG id, int argc, char** argv) {
    if (argc == 0) {
        if (!client->SetKeyOverride(uniqId, id, nullptr)) return 1;
        printf("SET_KEY_OVERRIDE ok. UniqId=%08X Id=%lu cleared.\n", uniqId, id);
        return 0;
    }
    if (argc != 3 && argc != 4) {
        fprintf(stderr, "--set-key-override: expected <TriggerMods> <TriggerKey> <Replacement> [repeat], or none to clear\n");
        return 1;
    }
    const bool repeat = (argc == 4);
    if (repeat && _stricmp(argv[3], "repeat") != 0) {
        fprintf(stderr, "--set-key-override: the 4th argument must be \"repeat\" (got \"%s\")\n", argv[3]);
        return 1;
    }

    UCHAR mods = 0;
    if (!KeycodeTable::ResolveModMask(argv[0], &mods) || mods == 0) {
        fprintf(stderr, "--set-key-override: TriggerMods \"%s\" must be MOD_xxx[|MOD_yyy...]\n", argv[0]);
        return 1;
    }
    KeycodeMapping trigger, replacement;
    if (!KeycodeTable::Resolve(argv[1], &trigger) || trigger.ActionType != KBDLAYER_ACTION_KEY || trigger.Param2 == 0) {
        fprintf(stderr, "--set-key-override: TriggerKey \"%s\" must be a plain key (KC_xxx)\n", argv[1]);
        return 1;
    }
    if (!KeycodeTable::Resolve(argv[2], &replacement) ||
        (replacement.ActionType != KBDLAYER_ACTION_KEY && replacement.ActionType != KBDLAYER_ACTION_MODDED_KEY &&
         replacement.ActionType != KBDLAYER_ACTION_MACRO) ||
        (replacement.ActionType != KBDLAYER_ACTION_MACRO && replacement.Param2 == 0)) {
        fprintf(stderr, "--set-key-override: Replacement \"%s\" must be a plain key, a modified key (LCTL(kc)) or MACRO(n)\n", argv[2]);
        return 1;
    }
    if (repeat && replacement.ActionType != KBDLAYER_ACTION_MACRO) {
        fprintf(stderr, "--set-key-override: \"repeat\" is only valid when Replacement is MACRO(n)\n");
        return 1;
    }

    KBDLAYER_KEY_OVERRIDE_ENTRY entry = {};
    entry.TriggerMods = mods;
    entry.TriggerScanCode = trigger.Param2;
    entry.Flags = repeat ? KBDLAYER_KEY_OVERRIDE_FLAG_REPEAT : 0;
    entry.Replacement = { replacement.ActionType, replacement.Param1, replacement.Param2 };
    if (!client->SetKeyOverride(uniqId, id, &entry)) return 1;
    printf("SET_KEY_OVERRIDE ok. UniqId=%08X Id=%lu: %s + %s -> %s%s\n", uniqId, id, argv[0], argv[1], argv[2], repeat ? " (repeat)" : "");
    return 0;
}

int CmdGetKeyOverride(IoctlClient* client, ULONG uniqId, ULONG id) {
    KBDLAYER_KEY_OVERRIDE_ENTRY entry = {};
    bool used = false;
    if (!client->GetKeyOverride(uniqId, id, &entry, &used)) return 1;
    if (!used) {
        printf("UniqId=%08X Id=%lu: (not set)\n", uniqId, id);
    } else {
        printf("UniqId=%08X Id=%lu: TriggerMods=0x%02X Flags=0x%02X TriggerScanCode=0x%04X Replacement Type=%u Param1=0x%02X Param2=0x%04X\n",
               uniqId, id, entry.TriggerMods, entry.Flags, entry.TriggerScanCode,
               entry.Replacement.Type, entry.Replacement.Param1, entry.Replacement.Param2);
    }
    return 0;
}

void PrintEmergencyReleaseConfig(const KBDLAYER_EMERGENCY_RELEASE_CONFIG& c) {
    printf("KeyCount=%lu HoldMs=%u ScanCodes=", c.KeyCount, KBDLAYER_EMERGENCY_HOLD_MS);
    for (ULONG i = 0; i < c.KeyCount; i++) {
        printf("%s0x%04X", (i > 0) ? "," : "", c.ScanCodes[i]);
    }
    printf("\n");
}

// --get-emergency-release: 緊急時の解除手段（項番3）の現在の組み合わせを表示する。
int CmdGetEmergencyRelease(IoctlClient* client) {
    KBDLAYER_EMERGENCY_RELEASE_CONFIG config;
    if (!client->GetEmergencyRelease(&config)) return 1;
    PrintEmergencyReleaseConfig(config);
    return 0;
}

// --set-emergency-release KC_xxx KC_yyy [...]: 2〜6個の異なるキーを、保持し続けたときの
// 組み合わせとして登録する。押し続けるとKBDLAYER_EMERGENCY_HOLD_MS後に、接続中の
// 全キーボードのレイヤーマップが一括でOFFになる。
int CmdSetEmergencyRelease(IoctlClient* client, int argc, char** argv) {
    KBDLAYER_EMERGENCY_RELEASE_CONFIG config;
    RtlZeroMemory(&config, sizeof(config));

    if (argc < (int)KBDLAYER_EMERGENCY_MIN_KEYS || argc > (int)KBDLAYER_EMERGENCY_MAX_KEYS) {
        fprintf(stderr, "--set-emergency-release: need %d-%d KC_* keys (got %d)\n",
                KBDLAYER_EMERGENCY_MIN_KEYS, KBDLAYER_EMERGENCY_MAX_KEYS, argc);
        return 1;
    }
    config.KeyCount = (ULONG)argc;
    for (int i = 0; i < argc; i++) {
        KeycodeMapping mapping;
        if (!KeycodeTable::Resolve(argv[i], &mapping) || mapping.ActionType != KBDLAYER_ACTION_KEY) {
            fprintf(stderr, "--set-emergency-release: \"%s\" is not a plain key\n", argv[i]);
            return 1;
        }
        config.ScanCodes[i] = mapping.Param2;
    }

    if (!client->SetEmergencyRelease(config)) return 1;
    printf("SET_EMERGENCY_RELEASE ok. ");
    PrintEmergencyReleaseConfig(config);
    return 0;
}

void PrintI8042Config(const KBDLAYER_I8042_CONFIG& c) {
    printf("Enabled=%lu ProfileCount=%lu\n", c.Enabled, c.ProfileCount);
    for (ULONG i = 0; i < c.ProfileCount && i < KBDLAYER_I8042_MAX_PROFILES; i++) {
        const KBDLAYER_I8042_PROFILE& p = c.Profiles[i];
        printf("  ModelKey=%08X", p.ModelKey);
        if (p.Flags & KBDLAYER_I8042_HAS_TYPE) printf(" type=%lu", p.Type);
        if (p.Flags & KBDLAYER_I8042_HAS_SUBTYPE) printf(" subtype=%lu", p.Subtype);
        if (p.Flags & KBDLAYER_I8042_HAS_IDENTIFIER) printf(" id=%ls", p.Identifier);
        if (p.Flags & KBDLAYER_I8042_HAS_LAYER_JPN) printf(" jpn=%ls", p.LayerJpn);
        if (p.Flags & KBDLAYER_I8042_HAS_LAYER_KOR) printf(" kor=%ls", p.LayerKor);
        printf("\n");
    }
}

// --get-i8042: i8042prt書き換え設定（ドライバ全体）を表示する。
int CmdGetI8042(IoctlClient* client) {
    KBDLAYER_I8042_CONFIG config;
    if (!client->GetI8042Config(&config)) return 1;
    PrintI8042Config(config);
    return 0;
}

// --set-i8042-enabled 0|1
int CmdSetI8042Enabled(IoctlClient* client, const char* arg) {
    if (strcmp(arg, "0") != 0 && strcmp(arg, "1") != 0) {
        fprintf(stderr, "--set-i8042-enabled: need 0 or 1\n");
        return 1;
    }
    KBDLAYER_I8042_CONFIG config;
    if (!client->GetI8042Config(&config)) return 1;
    config.Enabled = (arg[0] == '1') ? 1 : 0;
    if (!client->SetI8042Config(config)) return 1;
    printf("SET_I8042_CONFIG ok. ");
    PrintI8042Config(config);
    return 0;
}

// 文字列引数(ASCII)を固定長のWCHAR配列へ。長すぎる・空・ASCII以外ならfalse。
static bool CopyAsciiToWide(const char* src, WCHAR* dst) {
    size_t n = strlen(src);
    if (n == 0 || n >= KBDLAYER_I8042_STR_CHARS) return false;
    for (size_t i = 0; i < n; i++) {
        if ((unsigned char)src[i] > 0x7E) return false;
        dst[i] = (WCHAR)(unsigned char)src[i];
    }
    dst[n] = 0;
    return true;
}

// --set-i8042-profile <ModelKeyHex> [type=N] [subtype=N] [id=STR] [jpn=DLL] [kor=DLL]
// 指定したModelKeyのプロファイルを、指定した値だけで置き換える（無ければ追加する）。
// 指定しなかった値は、そのキーボードの接続時にi8042prtから削除される。
int CmdSetI8042Profile(IoctlClient* client, int argc, char** argv) {
    ULONG modelKey = 0;
    if (argc < 1 || !ParseHexU32(argv[0], &modelKey) || modelKey == 0) {
        fprintf(stderr, "--set-i8042-profile: need a non-zero ModelKeyHex (see --list / --get-scope)\n");
        return 1;
    }

    KBDLAYER_I8042_PROFILE p;
    RtlZeroMemory(&p, sizeof(p));
    p.ModelKey = modelKey;
    for (int i = 1; i < argc; i++) {
        const char* a = argv[i];
        if (strncmp(a, "type=", 5) == 0) {
            p.Flags |= KBDLAYER_I8042_HAS_TYPE;
            p.Type = (ULONG)strtoul(a + 5, nullptr, 10);
        } else if (strncmp(a, "subtype=", 8) == 0) {
            p.Flags |= KBDLAYER_I8042_HAS_SUBTYPE;
            p.Subtype = (ULONG)strtoul(a + 8, nullptr, 10);
        } else if (strncmp(a, "id=", 3) == 0) {
            if (!CopyAsciiToWide(a + 3, p.Identifier)) { fprintf(stderr, "--set-i8042-profile: bad id\n"); return 1; }
            p.Flags |= KBDLAYER_I8042_HAS_IDENTIFIER;
        } else if (strncmp(a, "jpn=", 4) == 0) {
            if (!CopyAsciiToWide(a + 4, p.LayerJpn)) { fprintf(stderr, "--set-i8042-profile: bad jpn\n"); return 1; }
            p.Flags |= KBDLAYER_I8042_HAS_LAYER_JPN;
        } else if (strncmp(a, "kor=", 4) == 0) {
            if (!CopyAsciiToWide(a + 4, p.LayerKor)) { fprintf(stderr, "--set-i8042-profile: bad kor\n"); return 1; }
            p.Flags |= KBDLAYER_I8042_HAS_LAYER_KOR;
        } else {
            fprintf(stderr, "--set-i8042-profile: unknown argument \"%s\"\n", a);
            return 1;
        }
    }

    KBDLAYER_I8042_CONFIG config;
    if (!client->GetI8042Config(&config)) return 1;
    ULONG index = config.ProfileCount;
    for (ULONG i = 0; i < config.ProfileCount && i < KBDLAYER_I8042_MAX_PROFILES; i++) {
        if (config.Profiles[i].ModelKey == modelKey) { index = i; break; }
    }
    if (index >= KBDLAYER_I8042_MAX_PROFILES) {
        fprintf(stderr, "--set-i8042-profile: too many profiles (max %d)\n", KBDLAYER_I8042_MAX_PROFILES);
        return 1;
    }
    config.Profiles[index] = p;
    if (index == config.ProfileCount) config.ProfileCount++;
    if (!client->SetI8042Config(config)) return 1;
    printf("SET_I8042_CONFIG ok. ");
    PrintI8042Config(config);
    return 0;
}

// --del-i8042-profile <ModelKeyHex>
int CmdDelI8042Profile(IoctlClient* client, const char* arg) {
    ULONG modelKey = 0;
    if (!ParseHexU32(arg, &modelKey) || modelKey == 0) {
        fprintf(stderr, "--del-i8042-profile: need a non-zero ModelKeyHex\n");
        return 1;
    }
    KBDLAYER_I8042_CONFIG config;
    if (!client->GetI8042Config(&config)) return 1;
    ULONG out = 0;
    for (ULONG i = 0; i < config.ProfileCount && i < KBDLAYER_I8042_MAX_PROFILES; i++) {
        if (config.Profiles[i].ModelKey != modelKey) {
            config.Profiles[out++] = config.Profiles[i];
        }
    }
    if (out == config.ProfileCount) {
        fprintf(stderr, "--del-i8042-profile: no such profile\n");
        return 1;
    }
    for (ULONG i = out; i < KBDLAYER_I8042_MAX_PROFILES; i++) {
        RtlZeroMemory(&config.Profiles[i], sizeof(config.Profiles[i]));
    }
    config.ProfileCount = out;
    if (!client->SetI8042Config(config)) return 1;
    printf("SET_I8042_CONFIG ok. ");
    PrintI8042Config(config);
    return 0;
}

// --watch-layer <UniqIdHex>: レイヤー状態が変わるたびに1行表示する（Ctrl+Cで終了）。
int CmdWatchLayer(IoctlClient* client, ULONG uniqId) {
    KBDLAYER_LAYER_STATE state;
    if (!client->GetLayerState(uniqId, &state)) return 1;
    printf("Watching UniqId=%08X (Ctrl+C to stop)\n", uniqId);
    PrintLayerState(state);
    for (;;) {
        bool cancelled = false;
        ULONG lastSeq = state.Seq;
        if (!client->WaitLayerChange(uniqId, lastSeq, &state, &cancelled)) {
            return cancelled ? 0 : 1;
        }
        PrintLayerState(state);
    }
}

// --watch-helper: ヘルパーイベント（HLP(id)＝CUSTOMアクションのキーの押下・解放）を1件ずつ表示する
// （Ctrl+Cで終了）。全キーボードのイベントが届く。起動より前のイベントは表示しない。
int CmdWatchHelper(IoctlClient* client) {
    ULONG lastSeq = KBDLAYER_HELPER_SEQ_LATEST;
    printf("Watching helper events (HLP keys) on all keyboards (Ctrl+C to stop)\n");
    fflush(stdout);
    for (;;) {
        KBDLAYER_WAIT_HELPER_EVENT_OUTPUT header;
        std::vector<KBDLAYER_HELPER_EVENT> events;
        bool cancelled = false;
        bool unsupported = false;
        if (!client->WaitHelperEvent(lastSeq, &header, &events, &cancelled, &unsupported)) {
            return cancelled ? 0 : 1;
        }
        if (header.Dropped != 0) {
            printf("(%lu event(s) were dropped: the queue overflowed)\n", header.Dropped);
        }
        for (const KBDLAYER_HELPER_EVENT& e : events) {
            printf("Seq=%lu UniqId=%08X HLP(%u) %s%s Mods=0x%02X TimeMs=%lu\n",
                   e.Seq, e.UniqId, e.CustomId, e.IsBreak ? "up" : "down",
                   (e.Flags & KBDLAYER_HELPER_EVENT_FLAG_REPEAT) ? " (repeat)" : "",
                   e.Modifiers, e.TimeMs);
        }
        // 出力をファイルへリダイレクトしても、強制終了で行が失われないように（試験のT-23）。
        fflush(stdout);
        lastSeq = header.Seq;
    }
}

// --watch-keys: 生の物理キー（レイヤーマップなどで置き換える前）の押下・解放を1件ずつ表示する
// （Ctrl+Cで終了）。全キーボードのイベントが届く。管理者権限が要る。起動より前のイベントは表示しない。
// ScanCodeはEnc16（拡張キーは0xE0xx、Pause/Breakは0xE11D）。
int CmdWatchKeys(IoctlClient* client) {
    ULONG lastSeq = KBDLAYER_KEY_SEQ_LATEST;
    printf("Watching raw key events on all keyboards (Ctrl+C to stop; needs Administrator)\n");
    fflush(stdout);
    for (;;) {
        KBDLAYER_WAIT_KEY_EVENT_OUTPUT header;
        std::vector<KBDLAYER_KEY_EVENT> events;
        bool cancelled = false;
        bool unsupported = false;
        bool denied = false;
        if (!client->WaitKeyEvent(lastSeq, &header, &events, &cancelled, &unsupported, &denied)) {
            return cancelled ? 0 : 1;
        }
        if (header.Dropped != 0) {
            printf("(%lu event(s) were dropped: the queue overflowed)\n", header.Dropped);
        }
        for (const KBDLAYER_KEY_EVENT& e : events) {
            printf("Seq=%lu UniqId=%08X Scan=0x%04X %s TimeMs=%lu\n",
                   e.Seq, e.UniqId, e.ScanCode, e.IsBreak ? "up" : "down", e.TimeMs);
        }
        fflush(stdout);
        lastSeq = header.Seq;
    }
}

// --set-layer <UniqIdHex> on|off|toggle <layer> / mask <hexMask> [--no-nicola-keys]
// アプリ管理レイヤーは、立てたハンドルを閉じると下ろされる。そのため、Enterを押すまで
// ハンドルを開いたまま待つ（そのままKeymapToolを強制終了すると、ハンドルのクローズで
// 下ろされることも確認できる）。
int CmdSetLayer(IoctlClient* client, ULONG uniqId, const char* opName, const char* value, bool noNicolaKeys) {
    ULONG op;
    ULONG layer = 0;
    ULONG mask = 0;

    if (strcmp(opName, "on") == 0) {
        op = KBDLAYER_LAYER_OP_ON;
    } else if (strcmp(opName, "off") == 0) {
        op = KBDLAYER_LAYER_OP_OFF;
    } else if (strcmp(opName, "toggle") == 0) {
        op = KBDLAYER_LAYER_OP_TOGGLE;
    } else if (strcmp(opName, "mask") == 0) {
        op = KBDLAYER_LAYER_OP_SET_MASK;
    } else {
        fprintf(stderr, "--set-layer: operation must be on / off / toggle / mask\n");
        return 1;
    }
    if (op == KBDLAYER_LAYER_OP_SET_MASK) {
        if (!ParseHexU32(value, &mask)) {
            fprintf(stderr, "--set-layer mask: \"%s\" is not a hex mask (e.g. 02)\n", value);
            return 1;
        }
    } else {
        char* end = nullptr;
        layer = (ULONG)strtoul(value, &end, 10);
        if (end == value || *end != '\0') {
            fprintf(stderr, "--set-layer: \"%s\" is not a layer number\n", value);
            return 1;
        }
    }

    KBDLAYER_LAYER_STATE state;
    ULONG flags = noNicolaKeys ? KBDLAYER_LAYER_STATE_FLAG_NO_NICOLA_KEYS : 0;
    if (!client->SetLayerState(uniqId, op, layer, mask, flags, &state)) return 1;
    printf("SET_LAYER_STATE ok. UniqId=%08X ", uniqId);
    PrintLayerState(state);
    printf("The app-managed layers stay on while this command runs. Press Enter to release them and exit.\n");
    (void)getchar();
    return 0;
}

// --dump-nicola <nicola.json>: ドライバに送らず、展開結果だけを表示する。
int CmdDumpNicola(const char* jsonPath) {
    std::vector<unsigned char> blob;
    if (!BuildNicolaBlobOrReport(jsonPath, &blob)) return 1;
    printf("BlobLength=%zu (steps: <scancode>v = press, <scancode>^ = release)\n", blob.size());
    NicolaTables::PrintBlob(blob.data(), blob.size());
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
        "  KeymapTool.exe --capture <UniqIdHex> [timeoutMs] [--suppress] [--allow-fake-shift]\n"
        "      One-shot test of IOCTL_KBDLAYER_CAPTURE_NEXT_KEY.\n"
        "      --allow-fake-shift also captures E0 2A / E0 36 (for keyboards whose right Shift\n"
        "      is E0 36). Use it only for Shift keys; PrintScreen would capture its E0 2A prefix.\n"
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
        "      Optional key=value arguments after the numbers (others keep their current value):\n"
        "        mode=hold|permissive|tap  MT/LT tap-vs-hold decision (tap-dance always uses hold):\n"
        "             hold       = another key's press confirms hold at once (default)\n"
        "             permissive = another key pressed AND released while held confirms hold\n"
        "             tap        = only the TapTermMs timeout confirms hold\n"
        "        quicktap=<ms>|off|default  re-press after a tap within this time repeats the\n"
        "             tap key (default = TapTermMs)\n"
        "        ttcount=<1-32>  taps needed for TT(layer) to toggle (0 = default 5)\n"
        "  KeymapTool.exe --get-config <UniqIdHex>\n"
        "      Show the current TapTermMs / ComboTermMs / TapDanceTermMs and mode / quicktap / ttcount.\n"
        "  KeymapTool.exe --set-macro <UniqIdHex> <MacroId> [KC_xxx KC_yyy ...]\n"
        "      Define macro <MacroId> (0-127) as a sequence of key taps, played back\n"
        "      via MACRO(<MacroId>) in a keymap.json. Modified keys are allowed:\n"
        "      LCTL(KC_C) taps Ctrl+C. Up to 64 steps (a key uses 2, each modifier +2).\n"
        "      Omit the keycodes to clear it.\n"
        "  KeymapTool.exe --get-macro <UniqIdHex> <MacroId>\n"
        "      Show the raw step list currently stored for a macro (debugging).\n"
        "  KeymapTool.exe --set-combo <UniqIdHex> <ComboId> [KC_member1 KC_member2 KC_output]\n"
        "      Define combo <ComboId> (0-15): pressing member1+member2 together within\n"
        "      ComboTermMs sends KC_output instead. The output may be a plain key, a modified\n"
        "      key (LCTL(KC_Z), held while the combo is held) or MACRO(n) (played once).\n"
        "      Limits: exactly 2 members, and a given key may not belong to more than one combo.\n"
        "      Omit the keycodes to clear it.\n"
        "  KeymapTool.exe --get-combo <UniqIdHex> <ComboId>\n"
        "      Show the current definition of a combo (debugging).\n"
        "  KeymapTool.exe --set-tapdance <UniqIdHex> <TapDanceId> [<SingleTap> <SingleHold> <DoubleTap> <TripleTap>]\n"
        "      Define tap-dance <TapDanceId> (0-15), played back via TAPDANCE(<TapDanceId>)\n"
        "      in a keymap.json. Limits: up to 3 taps are distinguished; SingleTap/\n"
        "      DoubleTap/TripleTap may be a plain key, a modified key (LCTL(KC_C)), or\n"
        "      MACRO(n); SingleHold may be a plain key, MT(mod,kc), LT(layer,kc), a modified\n"
        "      key, LM(layer,mod), or MACRO(n) (played once when the hold is confirmed)\n"
        "      (the tap-kc argument in MT/LT is ignored here). KC_NO (or \"\") in any of\n"
        "      the four means \"do nothing\" for that tap count / the hold.\n"
        "      Omit all 4 actions to clear it.\n"
        "  KeymapTool.exe --get-tapdance <UniqIdHex> <TapDanceId>\n"
        "      Show the current definition of a tap-dance (debugging).\n"
        "  KeymapTool.exe --set-nicola <UniqIdHex> <nicola.json>\n"
        "      Load a NICOLA (thumb-shift) layout, e.g. samples\\nicola.json. Thumb-shift\n"
        "      input is active only while the json's \"layer\" is on and the layer map is ON.\n"
        "  KeymapTool.exe --get-nicola <UniqIdHex> / --clear-nicola <UniqIdHex>\n"
        "      Show / remove the NICOLA layout loaded in the driver.\n"
        "  KeymapTool.exe --dump-nicola <nicola.json>\n"
        "      Show what --set-nicola would send, without touching the driver.\n"
        "  KeymapTool.exe --get-layer-state <UniqIdHex>\n"
        "      Show the current layer state (keyboard layers, app-managed layers, NICOLA on/off).\n"
        "  KeymapTool.exe --watch-layer <UniqIdHex>\n"
        "      Print a line every time the layer state changes (Ctrl+C to stop).\n"
        "  KeymapTool.exe --watch-helper\n"
        "      Print a line for every press/release of an HLP(id) key (helper event) on any\n"
        "      keyboard (Ctrl+C to stop). HLP keys output nothing themselves.\n"
        "  KeymapTool.exe --watch-keys\n"
        "      Print a line for every raw key press/release (before any remapping) on any keyboard\n"
        "      (Ctrl+C to stop). Needs Administrator, because it exposes everything typed.\n"
        "  KeymapTool.exe --set-layer <UniqIdHex> on|off|toggle <layer> [--no-nicola-keys]\n"
        "  KeymapTool.exe --set-layer <UniqIdHex> mask <hexMask> [--no-nicola-keys]\n"
        "      Turn app-managed layers on/off. They stay on until you press Enter (or this\n"
        "      command is killed). --no-nicola-keys: do not send NICOLA's onEnter/onExit keys.\n"
        "  KeymapTool.exe --get-scope <UniqIdHex>\n"
        "      Show whether this keyboard's persisted settings (layer map, macros, combos,\n"
        "      NICOLA, ...) are shared by model (same InstanceKey/ModelKey for every\n"
        "      keyboard of this model), bound to this one instance (port), or shared by\n"
        "      every keyboard (global).\n"
        "  KeymapTool.exe --set-scope <UniqIdHex> model|instance|global\n"
        "      Switch the persisted-settings scope. Switching from model to instance first\n"
        "      copies the current settings so nothing appears to disappear. Switching to\n"
        "      global uses the shared settings if some keyboard already has them, otherwise\n"
        "      copies the current ones. Other keyboards that are already connected pick the\n"
        "      change up when they reconnect; keymaps are per physical scan code, so mixing\n"
        "      keyboards with different layouts under global can give unintended mappings.\n"
        "  KeymapTool.exe --get-features <UniqIdHex>\n"
        "      Show which optional features are enabled on this keyboard.\n"
        "  KeymapTool.exe --set-key-override <UniqIdHex> <Id> [<TriggerMods> <TriggerKey> <Replacement> [repeat]]\n"
        "      Define key override <Id> (0-31): while all modifier kinds in <TriggerMods>\n"
        "      (e.g. MOD_LSFT, MOD_LCTL|MOD_LSFT; left/right are not distinguished) are held,\n"
        "      the key that outputs <TriggerKey> (KC_xxx, after layers) sends <Replacement>\n"
        "      (KC_xxx or a modified key such as LCTL(KC_Z)) instead, with the trigger\n"
        "      modifiers temporarily released. Example: MOD_LSFT KC_BSPC KC_DEL.\n"
        "      Omit the last three arguments to clear it.\n"
        "  KeymapTool.exe --get-key-override <UniqIdHex> <Id>\n"
        "      Show the current definition of a key override.\n"
        "  KeymapTool.exe --set-features <UniqIdHex> <macro:0|1> <combo:0|1> <tapdance:0|1> [keyoverride:0|1]\n"
        "      Enable/disable Macro, Combo, TapDance and Key Override on this keyboard (all enabled by\n"
        "      default; an omitted keyoverride flag keeps its current value).\n"
        "      Disabled features' keys do nothing (like KC_NO); combo member keys act as\n"
        "      their normal layer keys; key overrides stop applying. Key remapping, layers (MO/TG/DF/OSL),\n"
        "      MT/LT and NICOLA are always available. Settings stay stored while disabled.\n"
        "  KeymapTool.exe --get-emergency-release\n"
        "      Show the emergency-release key combination (driver-wide, not per-keyboard).\n"
        "  KeymapTool.exe --set-emergency-release KC_xxx KC_yyy [...]\n"
        "      Set the emergency-release combination (2-6 distinct keys). Holding all of\n"
        "      them down for 3 seconds on any connected keyboard turns off every\n"
        "      keyboard's layer map at once (same effect as --off on all of them). This\n"
        "      works no matter how broken the current layer map is, since it is checked\n"
        "      on the raw physical scancodes before any layer/macro/combo resolution.\n"
        "      Default if never set: KC_LCTL KC_RCTL KC_LSFT KC_RSFT.\n"
        "  KeymapTool.exe --get-i8042\n"
        "      Show the i8042prt override settings (driver-wide).\n"
        "  KeymapTool.exe --set-i8042-enabled 0|1\n"
        "      When 1, every time a keyboard is attached the driver rewrites\n"
        "      HKLM\\SYSTEM\\CurrentControlSet\\Services\\i8042prt\\Parameters: a keyboard with a\n"
        "      profile gets the profile's values; any other keyboard gets all five values\n"
        "      (OverrideKeyboardType/Subtype/Identifier, LayerDriver JPN/KOR) deleted.\n"
        "      When 0 (default) nothing is touched. Takes effect from the next attach.\n"
        "  KeymapTool.exe --set-i8042-profile <ModelKeyHex> [type=N] [subtype=N] [id=STR] [jpn=DLL] [kor=DLL]\n"
        "      Add/replace the profile of a keyboard model (ModelKey from --list). Values left\n"
        "      out are deleted from i8042prt when that keyboard attaches.\n"
        "      e.g. type=7 subtype=2 id=PCAT_106KEY jpn=kbd106.dll\n"
        "  KeymapTool.exe --del-i8042-profile <ModelKeyHex>\n"
        "      Remove a profile (that model is then treated as unspecified).\n"
        "\n"
        "  UniqIdHex is the value shown by --list (e.g. A1B20000).\n");
}

} // namespace

int main(int rawArgc, char** rawArgv) {
    // --suppress はどの位置に書いてもよいオプションとして先に取り除き、
    // 残りを従来通りの位置引数として解釈する。
    bool suppress = false;
    bool allowFakeShift = false;
    std::vector<char*> args;
    for (int i = 0; i < rawArgc; i++) {
        if (i > 0 && strcmp(rawArgv[i], "--suppress") == 0) {
            suppress = true;
        } else if (i > 0 && strcmp(rawArgv[i], "--allow-fake-shift") == 0) {
            allowFakeShift = true;
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

    // ドライバに接続せずに完結するコマンド。
    if (strcmp(cmd, "--dump-nicola") == 0) {
        if (argc < 3) { PrintUsage(); return 1; }
        return CmdDumpNicola(argv[2]);
    }

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
        // 位置引数（数値）は最大3つ。'=' を含む引数からは key=value（mode=/quicktap=/ttcount=）。
        int numeric = 0;
        while (3 + numeric < argc && numeric < 3 && strchr(argv[3 + numeric], '=') == nullptr) numeric++;
        if (numeric == 0) { PrintUsage(); return 1; }
        ULONG tapTermMs = (ULONG)strtoul(argv[3], nullptr, 10);
        ULONG comboTermMs = (numeric >= 2) ? (ULONG)strtoul(argv[4], nullptr, 10) : 0;
        ULONG tapDanceTermMs = (numeric >= 3) ? (ULONG)strtoul(argv[5], nullptr, 10) : 0;
        return CmdSetConfig(&client, uniqId, tapTermMs, comboTermMs, tapDanceTermMs,
                            argc - (3 + numeric), argv + 3 + numeric);
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

    if (strcmp(cmd, "--set-nicola") == 0) {
        ULONG uniqId;
        if (argc < 4 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        return CmdSetNicola(&client, uniqId, argv[3]);
    }

    if (strcmp(cmd, "--get-nicola") == 0) {
        ULONG uniqId;
        if (argc < 3 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        return CmdGetNicola(&client, uniqId);
    }

    if (strcmp(cmd, "--get-layer-state") == 0) {
        ULONG uniqId;
        if (argc < 3 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        return CmdGetLayerState(&client, uniqId);
    }

    if (strcmp(cmd, "--watch-layer") == 0) {
        ULONG uniqId;
        if (argc < 3 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        return CmdWatchLayer(&client, uniqId);
    }

    if (strcmp(cmd, "--watch-helper") == 0) {
        return CmdWatchHelper(&client);
    }

    if (strcmp(cmd, "--watch-keys") == 0) {
        return CmdWatchKeys(&client);
    }

    if (strcmp(cmd, "--set-layer") == 0) {
        ULONG uniqId;
        if (argc < 5 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        bool noNicolaKeys = (argc >= 6 && strcmp(argv[5], "--no-nicola-keys") == 0);
        return CmdSetLayer(&client, uniqId, argv[3], argv[4], noNicolaKeys);
    }

    if (strcmp(cmd, "--get-scope") == 0) {
        ULONG uniqId;
        if (argc < 3 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        return CmdGetScope(&client, uniqId);
    }

    if (strcmp(cmd, "--set-scope") == 0) {
        ULONG uniqId;
        if (argc < 4 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        return CmdSetScope(&client, uniqId, argv[3]);
    }

    if (strcmp(cmd, "--get-features") == 0) {
        ULONG uniqId;
        if (argc < 3 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        return CmdGetFeatures(&client, uniqId);
    }

    if (strcmp(cmd, "--set-features") == 0) {
        ULONG uniqId;
        if (argc < 6 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        return CmdSetFeatures(&client, uniqId, argv[3], argv[4], argv[5], (argc >= 7) ? argv[6] : nullptr);
    }

    if (strcmp(cmd, "--set-key-override") == 0) {
        ULONG uniqId;
        if (argc < 4 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        ULONG id = (ULONG)strtoul(argv[3], nullptr, 10);
        return CmdSetKeyOverride(&client, uniqId, id, argc - 4, argv + 4);
    }

    if (strcmp(cmd, "--get-key-override") == 0) {
        ULONG uniqId;
        if (argc < 4 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        ULONG id = (ULONG)strtoul(argv[3], nullptr, 10);
        return CmdGetKeyOverride(&client, uniqId, id);
    }

    if (strcmp(cmd, "--get-emergency-release") == 0) {
        return CmdGetEmergencyRelease(&client);
    }

    if (strcmp(cmd, "--set-emergency-release") == 0) {
        return CmdSetEmergencyRelease(&client, argc - 2, argv + 2);
    }

    if (strcmp(cmd, "--get-i8042") == 0) {
        return CmdGetI8042(&client);
    }

    if (strcmp(cmd, "--set-i8042-enabled") == 0) {
        if (argc < 3) { PrintUsage(); return 1; }
        return CmdSetI8042Enabled(&client, argv[2]);
    }

    if (strcmp(cmd, "--set-i8042-profile") == 0) {
        return CmdSetI8042Profile(&client, argc - 2, argv + 2);
    }

    if (strcmp(cmd, "--del-i8042-profile") == 0) {
        if (argc < 3) { PrintUsage(); return 1; }
        return CmdDelI8042Profile(&client, argv[2]);
    }

    if (strcmp(cmd, "--clear-nicola") == 0) {
        ULONG uniqId;
        if (argc < 3 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        return CmdClearNicola(&client, uniqId);
    }

    if (strcmp(cmd, "--capture") == 0) {
        ULONG uniqId;
        if (argc < 3 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        ULONG timeoutMs = (argc >= 4) ? (ULONG)strtoul(argv[3], nullptr, 10) : 0;
        return CmdCapture(&client, uniqId, timeoutMs, suppress, allowFakeShift);
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
