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

int CmdCapture(IoctlClient* client, ULONG uniqId, ULONG timeoutMs) {
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

// info.jsonを使うキャリブレーション: --calibrate <UniqIdHex> <info.json> <out-profile.json> [layoutName]
int CmdCalibrate(IoctlClient* client, ULONG uniqId, const std::string& infoJsonPath,
                  const std::string& outProfilePath, const std::string& layoutName) {
    std::vector<LayoutKeyPosition> positions;
    int rows = 0, cols = 0;
    std::string error;
    if (!InfoJson::Load(infoJsonPath, layoutName, &positions, &rows, &cols, &error)) {
        fprintf(stderr, "failed to load info.json: %s\n", error.c_str());
        return 1;
    }

    CalibrationProfile profile;
    if (!RunCalibrationWizard(client, uniqId, positions, &profile)) {
        return 1;
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
int CmdCalibrateSimple(IoctlClient* client, ULONG uniqId, int keyCount, const std::string& outProfilePath) {
    std::vector<LayoutKeyPosition> positions(keyCount);
    for (int i = 0; i < keyCount; i++) {
        positions[i].Row = 0;
        positions[i].Col = i;
    }

    CalibrationProfile profile;
    if (!RunCalibrationWizard(client, uniqId, positions, &profile)) {
        return 1;
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
        "  KeymapTool.exe --capture <UniqIdHex> [timeoutMs]\n"
        "      One-shot test of IOCTL_KBDLAYER_CAPTURE_NEXT_KEY.\n"
        "  KeymapTool.exe --calibrate <UniqIdHex> <info.json> <out-profile.json> [layoutName]\n"
        "      Walk every key in info.json's layout, prompting you to press it,\n"
        "      and save the physical scancode mapping to out-profile.json.\n"
        "  KeymapTool.exe --calibrate-simple <UniqIdHex> <keyCount> <out-profile.json>\n"
        "      Same, but without an info.json: just walks keyCount keys in enumeration order\n"
        "      (for boards with no published info.json).\n"
        "  KeymapTool.exe --apply <UniqIdHex> <info.json> <keymap.json> <profile.json> [layoutName]\n"
        "      Build a layer map from a VIA-exported keymap.json + info.json + a calibration\n"
        "      profile, and SET it on the device (still requires --on to activate).\n"
        "  KeymapTool.exe --apply-simple <UniqIdHex> <keymap.json> <profile.json>\n"
        "      Same, but matches keymap.json's flat per-layer arrays to the profile by\n"
        "      enumeration order (paired with --calibrate-simple).\n"
        "\n"
        "  UniqIdHex is the value shown by --list (e.g. A1B20000).\n");
}

} // namespace

int main(int argc, char** argv) {
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

    if (strcmp(cmd, "--capture") == 0) {
        ULONG uniqId;
        if (argc < 3 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        ULONG timeoutMs = (argc >= 4) ? (ULONG)strtoul(argv[3], nullptr, 10) : 0;
        return CmdCapture(&client, uniqId, timeoutMs);
    }

    if (strcmp(cmd, "--calibrate") == 0) {
        ULONG uniqId;
        if (argc < 5 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        std::string layoutName = (argc >= 6) ? argv[5] : std::string();
        return CmdCalibrate(&client, uniqId, argv[3], argv[4], layoutName);
    }

    if (strcmp(cmd, "--calibrate-simple") == 0) {
        ULONG uniqId;
        if (argc < 5 || !ParseHexU32(argv[2], &uniqId)) { PrintUsage(); return 1; }
        int keyCount = atoi(argv[3]);
        if (keyCount <= 0) { PrintUsage(); return 1; }
        return CmdCalibrateSimple(&client, uniqId, keyCount, argv[4]);
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
