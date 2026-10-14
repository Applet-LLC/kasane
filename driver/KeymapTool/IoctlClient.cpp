// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

#include "IoctlClient.h"
#include <cstdio>
#include <cstring>

bool IoctlClient::Open() {
    // 制御デバイスのSDDLはEveryoneにGRだけを与える（ControlDevice.c）。昇格していない
    // プロセスはGENERIC_WRITEで開けないので、読み取り専用で開き直す。読み取り専用でも
    // --list/--get/--on/--off 等（FILE_READ_ACCESSのIOCTL）は使える。
    handle_ = CreateFileW(
        KBDLAYER_CONTROL_DEVICE_PATH,
        GENERIC_READ | GENERIC_WRITE,
        0,
        NULL,
        OPEN_EXISTING,
        0,
        NULL);
    readOnly_ = false;
    if (handle_ == INVALID_HANDLE_VALUE && GetLastError() == ERROR_ACCESS_DENIED) {
        handle_ = CreateFileW(
            KBDLAYER_CONTROL_DEVICE_PATH,
            GENERIC_READ,
            0,
            NULL,
            OPEN_EXISTING,
            0,
            NULL);
        readOnly_ = true;
    }
    if (handle_ == INVALID_HANDLE_VALUE) {
        fwprintf(stderr, L"Failed to open %ls (kbdlayer.sys installed? control device created?). Error: %lu\n",
                 KBDLAYER_CONTROL_DEVICE_PATH, GetLastError());
        return false;
    }
    return true;
}

// 書き込み系IOCTL（FILE_WRITE_ACCESS）の前に呼ぶ。読み取り専用で開いている場合は、
// ドライバへ送っても必ずERROR_ACCESS_DENIEDになるので、理由を表示して失敗させる。
bool IoctlClient::RequireWriteAccess(const char* operation) {
    if (!readOnly_) {
        return true;
    }
    fprintf(stderr, "%s requires administrator rights. "
            "Run KeymapTool from an elevated (Run as administrator) prompt.\n", operation);
    SetLastError(ERROR_ACCESS_DENIED);
    return false;
}

void IoctlClient::Close() {
    if (handle_ != INVALID_HANDLE_VALUE) {
        CloseHandle(handle_);
        handle_ = INVALID_HANDLE_VALUE;
    }
}

bool IoctlClient::ListDevices(std::vector<DeviceListEntry>* out) {
    std::vector<unsigned char> buffer(sizeof(KBDLAYER_LIST_DEVICES_OUTPUT));
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_LIST_DEVICES,
                               NULL, 0,
                               buffer.data(), (DWORD)buffer.size(),
                               &bytesReturned, NULL);
    if (!ok && GetLastError() == ERROR_INSUFFICIENT_BUFFER) {
        buffer.resize(sizeof(KBDLAYER_LIST_DEVICES_OUTPUT) + 64 * sizeof(KBDLAYER_DEVICE_INFO));
        ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_LIST_DEVICES,
                              NULL, 0,
                              buffer.data(), (DWORD)buffer.size(),
                              &bytesReturned, NULL);
    }
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_LIST_DEVICES failed. Error: %lu\n", GetLastError());
        return false;
    }

    const KBDLAYER_LIST_DEVICES_OUTPUT* header = (const KBDLAYER_LIST_DEVICES_OUTPUT*)buffer.data();
    const KBDLAYER_DEVICE_INFO* infos =
        (const KBDLAYER_DEVICE_INFO*)(buffer.data() + sizeof(KBDLAYER_LIST_DEVICES_OUTPUT));

    out->clear();
    for (ULONG i = 0; i < header->DeviceCount; i++) {
        DeviceListEntry e;
        e.UniqId = infos[i].UniqId;
        e.HasLayerMap = infos[i].HasLayerMap != 0;
        e.Enabled = infos[i].Enabled != 0;
        e.ActiveLayerMask = infos[i].ActiveLayerMask;
        e.HardwareId = infos[i].HardwareId;
        out->push_back(e);
    }
    return true;
}

bool IoctlClient::GetLayerMap(ULONG uniqId, std::vector<unsigned char>* outBlob) {
    KBDLAYER_TARGET target;
    target.UniqId = uniqId;

    std::vector<unsigned char> buffer(sizeof(KBDLAYER_GET_MAP_OUTPUT) + KBDLAYER_MAX_MAP_BLOB_BYTES);
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_GET_LAYERMAP,
                              &target, sizeof(target),
                              buffer.data(), (DWORD)buffer.size(),
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_GET_LAYERMAP failed. Error: %lu\n", GetLastError());
        return false;
    }

    const KBDLAYER_GET_MAP_OUTPUT* header = (const KBDLAYER_GET_MAP_OUTPUT*)buffer.data();
    outBlob->assign(
        buffer.begin() + sizeof(KBDLAYER_GET_MAP_OUTPUT),
        buffer.begin() + sizeof(KBDLAYER_GET_MAP_OUTPUT) + header->BlobLength);
    return true;
}

bool IoctlClient::SetLayerMap(ULONG uniqId, const std::vector<unsigned char>& blob) {
    if (!RequireWriteAccess("IOCTL_KBDLAYER_SET_LAYERMAP")) return false;
    std::vector<unsigned char> input(sizeof(KBDLAYER_SET_MAP_INPUT) + blob.size());
    KBDLAYER_SET_MAP_INPUT* header = (KBDLAYER_SET_MAP_INPUT*)input.data();
    header->Target.UniqId = uniqId;
    header->BlobLength = (ULONG)blob.size();
    if (!blob.empty()) {
        memcpy(input.data() + sizeof(KBDLAYER_SET_MAP_INPUT), blob.data(), blob.size());
    }

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_SET_LAYERMAP,
                              input.data(), (DWORD)input.size(),
                              NULL, 0,
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_SET_LAYERMAP failed. Error: %lu "
                "(ON中はSET不可。先に --off を送ってください)\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::ClearLayerMap(ULONG uniqId) {
    if (!RequireWriteAccess("IOCTL_KBDLAYER_CLEAR_LAYERMAP")) return false;
    KBDLAYER_TARGET target;
    target.UniqId = uniqId;
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_CLEAR_LAYERMAP,
                              &target, sizeof(target),
                              NULL, 0,
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_CLEAR_LAYERMAP failed. Error: %lu "
                "(ON中はCLEAR不可。先に --off を送ってください)\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::SetLayerMapEnabled(ULONG uniqId, bool enabled) {
    KBDLAYER_SET_ENABLED_INPUT input;
    input.Target.UniqId = uniqId;
    input.Enabled = enabled ? 1 : 0;
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_SET_LAYERMAP_ENABLED,
                              &input, sizeof(input),
                              NULL, 0,
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_SET_LAYERMAP_ENABLED failed. Error: %lu "
                "(ONにするにはマップが設定済みである必要があります)\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::CaptureNextKey(ULONG uniqId, ULONG timeoutMs, USHORT* outScanCode16, bool* outTimedOut,
                                bool allowFakeShift) {
    if (!RequireWriteAccess("IOCTL_KBDLAYER_CAPTURE_NEXT_KEY")) return false;
    KBDLAYER_CAPTURE_NEXT_KEY_INPUT input;
    input.Target.UniqId = uniqId;
    input.TimeoutMs = timeoutMs;
    input.Flags = allowFakeShift ? KBDLAYER_CAPTURE_FLAG_ALLOW_FAKE_SHIFT : 0;

    KBDLAYER_CAPTURE_NEXT_KEY_OUTPUT output;
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_CAPTURE_NEXT_KEY,
                              &input, sizeof(input),
                              &output, sizeof(output),
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_CAPTURE_NEXT_KEY failed. Error: %lu\n", GetLastError());
        return false;
    }

    *outScanCode16 = output.ScanCode;
    *outTimedOut = output.TimedOut != 0;
    return true;
}

bool IoctlClient::SetConfig(ULONG uniqId, ULONG tapTermMs, ULONG comboTermMs, ULONG tapDanceTermMs,
                            const TapHoldOptions* tapHold) {
    if (!RequireWriteAccess("IOCTL_KBDLAYER_SET_CONFIG")) return false;
    KBDLAYER_SET_CONFIG_INPUT input = {};
    input.Target.UniqId = uniqId;
    input.TapTermMs = tapTermMs;
    input.ComboTermMs = comboTermMs;
    input.TapDanceTermMs = tapDanceTermMs;
    // tapHoldを渡さなければ旧サイズ（v1）で送り、ドライバ側の判定方式・Quick Tap・TTの回数は変えない。
    DWORD inputSize = KBDLAYER_SET_CONFIG_INPUT_V1_SIZE;
    if (tapHold != nullptr) {
        input.TapHoldMode = tapHold->TapHoldMode;
        input.QuickTapTermMs = tapHold->QuickTapTermMs;
        input.TappingToggle = tapHold->TappingToggle;
        inputSize = sizeof(input);
    }

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_SET_CONFIG,
                              &input, inputSize,
                              NULL, 0,
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_SET_CONFIG failed. Error: %lu\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::GetConfig(ULONG uniqId, KBDLAYER_GET_CONFIG_OUTPUT* out, bool* outHasTapHoldOptions) {
    KBDLAYER_TARGET input;
    input.UniqId = uniqId;
    // METHOD_BUFFEREDなので入力と出力は同じバッファを共有する。出力用のバッファは、入力より
    // 大きくても問題ない。
    KBDLAYER_GET_CONFIG_OUTPUT output = {};

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_GET_CONFIG,
                              &input, sizeof(input),
                              &output, sizeof(output),
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_GET_CONFIG failed. Error: %lu\n", GetLastError());
        return false;
    }
    // 古いドライバは先頭の3項目（v1）だけを返す。そのときは追加した項目を既定値にしておく。
    bool hasV2 = bytesReturned >= sizeof(output);
    if (!hasV2) {
        output.TapHoldMode = KBDLAYER_TAPHOLD_MODE_HOLD_ON_OTHER_KEY;
        output.QuickTapTermMs = 0;
        output.TappingToggle = KBDLAYER_DEFAULT_TAPPING_TOGGLE;
    }
    *out = output;
    if (outHasTapHoldOptions != nullptr) *outHasTapHoldOptions = hasV2;
    return true;
}

bool IoctlClient::SetCaptureSuppress(ULONG uniqId, bool enable) {
    if (!RequireWriteAccess("IOCTL_KBDLAYER_SET_CAPTURE_SUPPRESS")) return false;
    KBDLAYER_SET_CAPTURE_SUPPRESS_INPUT input;
    input.Target.UniqId = uniqId;
    input.Enable = enable ? 1 : 0;

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_SET_CAPTURE_SUPPRESS,
                              &input, sizeof(input),
                              NULL, 0,
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_SET_CAPTURE_SUPPRESS failed. Error: %lu "
                "(another tool may already be suppressing this keyboard)\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::SetMacro(ULONG uniqId, ULONG macroId, const std::vector<KBDLAYER_MACRO_STEP>& steps) {
    if (!RequireWriteAccess("IOCTL_KBDLAYER_SET_MACRO")) return false;
    std::vector<unsigned char> input(sizeof(KBDLAYER_SET_MACRO_INPUT) + steps.size() * sizeof(KBDLAYER_MACRO_STEP));
    KBDLAYER_SET_MACRO_INPUT* header = (KBDLAYER_SET_MACRO_INPUT*)input.data();
    header->Target.UniqId = uniqId;
    header->MacroId = macroId;
    header->StepCount = (ULONG)steps.size();
    if (!steps.empty()) {
        memcpy(input.data() + sizeof(KBDLAYER_SET_MACRO_INPUT), steps.data(), steps.size() * sizeof(KBDLAYER_MACRO_STEP));
    }

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_SET_MACRO,
                              input.data(), (DWORD)input.size(),
                              NULL, 0,
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_SET_MACRO failed. Error: %lu\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::GetMacro(ULONG uniqId, ULONG macroId, std::vector<KBDLAYER_MACRO_STEP>* outSteps) {
    KBDLAYER_GET_MACRO_INPUT target;
    target.Target.UniqId = uniqId;
    target.MacroId = macroId;

    std::vector<unsigned char> buffer(sizeof(KBDLAYER_GET_MACRO_OUTPUT) + KBDLAYER_MAX_MACRO_STEPS * sizeof(KBDLAYER_MACRO_STEP));
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_GET_MACRO,
                              &target, sizeof(target),
                              buffer.data(), (DWORD)buffer.size(),
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_GET_MACRO failed. Error: %lu\n", GetLastError());
        return false;
    }

    const KBDLAYER_GET_MACRO_OUTPUT* header = (const KBDLAYER_GET_MACRO_OUTPUT*)buffer.data();
    const KBDLAYER_MACRO_STEP* steps = (const KBDLAYER_MACRO_STEP*)(buffer.data() + sizeof(KBDLAYER_GET_MACRO_OUTPUT));
    outSteps->assign(steps, steps + header->StepCount);
    return true;
}

bool IoctlClient::SetCombo(ULONG uniqId, ULONG comboId, const KBDLAYER_COMBO_ENTRY* entry) {
    if (!RequireWriteAccess("IOCTL_KBDLAYER_SET_COMBO")) return false;
    KBDLAYER_SET_COMBO_INPUT input;
    input.Target.UniqId = uniqId;
    input.ComboId = comboId;
    if (entry != nullptr) {
        input.Entry = *entry;
    } else {
        memset(&input.Entry, 0, sizeof(input.Entry));
        input.Entry.OutputAction.Type = KBDLAYER_ACTION_NONE; // クリアの合図（ControlDevice.c参照）
    }

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_SET_COMBO,
                              &input, sizeof(input),
                              NULL, 0,
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_SET_COMBO failed. Error: %lu\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::GetCombo(ULONG uniqId, ULONG comboId, KBDLAYER_COMBO_ENTRY* outEntry, bool* outUsed) {
    KBDLAYER_GET_COMBO_INPUT input;
    input.Target.UniqId = uniqId;
    input.ComboId = comboId;

    KBDLAYER_GET_COMBO_OUTPUT output;
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_GET_COMBO,
                              &input, sizeof(input),
                              &output, sizeof(output),
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_GET_COMBO failed. Error: %lu\n", GetLastError());
        return false;
    }

    *outEntry = output.Entry;
    *outUsed = output.Used != 0;
    return true;
}

bool IoctlClient::SetKeyOverride(ULONG uniqId, ULONG id, const KBDLAYER_KEY_OVERRIDE_ENTRY* entry) {
    if (!RequireWriteAccess("IOCTL_KBDLAYER_SET_KEY_OVERRIDE")) return false;
    KBDLAYER_SET_KEY_OVERRIDE_INPUT input = {};
    input.Target.UniqId = uniqId;
    input.Id = id;
    if (entry != nullptr) {
        input.Entry = *entry;
    } else {
        input.Entry.Replacement.Type = KBDLAYER_ACTION_NONE; // クリアの合図（ControlDevice.c参照）
    }

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_SET_KEY_OVERRIDE,
                              &input, sizeof(input),
                              NULL, 0,
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_SET_KEY_OVERRIDE failed. Error: %lu\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::GetKeyOverride(ULONG uniqId, ULONG id, KBDLAYER_KEY_OVERRIDE_ENTRY* outEntry, bool* outUsed) {
    KBDLAYER_GET_KEY_OVERRIDE_INPUT input = {};
    input.Target.UniqId = uniqId;
    input.Id = id;

    KBDLAYER_GET_KEY_OVERRIDE_OUTPUT output = {};
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_GET_KEY_OVERRIDE,
                              &input, sizeof(input),
                              &output, sizeof(output),
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_GET_KEY_OVERRIDE failed. Error: %lu\n", GetLastError());
        return false;
    }

    *outEntry = output.Entry;
    *outUsed = output.Used != 0;
    return true;
}

bool IoctlClient::SetTapDance(ULONG uniqId, ULONG tapDanceId, const KBDLAYER_TAPDANCE_ENTRY* entry) {
    if (!RequireWriteAccess("IOCTL_KBDLAYER_SET_TAPDANCE")) return false;
    KBDLAYER_SET_TAPDANCE_INPUT input;
    input.Target.UniqId = uniqId;
    input.TapDanceId = tapDanceId;
    if (entry != nullptr) {
        input.Entry = *entry;
    } else {
        memset(&input.Entry, 0, sizeof(input.Entry));
        input.Entry.SingleTapAction.Type = KBDLAYER_ACTION_NONE; // クリアの合図（ControlDevice.c参照）
    }

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_SET_TAPDANCE,
                              &input, sizeof(input),
                              NULL, 0,
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_SET_TAPDANCE failed. Error: %lu\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::GetTapDance(ULONG uniqId, ULONG tapDanceId, KBDLAYER_TAPDANCE_ENTRY* outEntry, bool* outUsed) {
    KBDLAYER_GET_TAPDANCE_INPUT input;
    input.Target.UniqId = uniqId;
    input.TapDanceId = tapDanceId;

    KBDLAYER_GET_TAPDANCE_OUTPUT output;
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_GET_TAPDANCE,
                              &input, sizeof(input),
                              &output, sizeof(output),
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_GET_TAPDANCE failed. Error: %lu\n", GetLastError());
        return false;
    }

    *outEntry = output.Entry;
    *outUsed = output.Used != 0;
    return true;
}

bool IoctlClient::SetNicola(ULONG uniqId, const std::vector<unsigned char>& blob) {
    if (!RequireWriteAccess("IOCTL_KBDLAYER_SET_NICOLA")) return false;
    std::vector<unsigned char> input(sizeof(KBDLAYER_SET_NICOLA_INPUT) + blob.size());
    KBDLAYER_SET_NICOLA_INPUT* header = (KBDLAYER_SET_NICOLA_INPUT*)input.data();
    header->Target.UniqId = uniqId;
    header->BlobLength = (ULONG)blob.size();
    if (!blob.empty()) {
        memcpy(input.data() + sizeof(KBDLAYER_SET_NICOLA_INPUT), blob.data(), blob.size());
    }

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_SET_NICOLA,
                              input.data(), (DWORD)input.size(),
                              NULL, 0,
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_SET_NICOLA failed. Error: %lu\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::GetNicola(ULONG uniqId, std::vector<unsigned char>* outBlob) {
    KBDLAYER_TARGET target;
    target.UniqId = uniqId;

    std::vector<unsigned char> buffer(sizeof(KBDLAYER_GET_NICOLA_OUTPUT) + KBDLAYER_MAX_NICOLA_BLOB_BYTES);
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_GET_NICOLA,
                              &target, sizeof(target),
                              buffer.data(), (DWORD)buffer.size(),
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_GET_NICOLA failed. Error: %lu\n", GetLastError());
        return false;
    }

    const KBDLAYER_GET_NICOLA_OUTPUT* header = (const KBDLAYER_GET_NICOLA_OUTPUT*)buffer.data();
    outBlob->assign(
        buffer.begin() + sizeof(KBDLAYER_GET_NICOLA_OUTPUT),
        buffer.begin() + sizeof(KBDLAYER_GET_NICOLA_OUTPUT) + header->BlobLength);
    return true;
}

bool IoctlClient::GetLayerState(ULONG uniqId, KBDLAYER_LAYER_STATE* out) {
    KBDLAYER_TARGET target;
    target.UniqId = uniqId;

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_GET_LAYER_STATE,
                              &target, sizeof(target),
                              out, sizeof(*out),
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_GET_LAYER_STATE failed. Error: %lu\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::WaitLayerChange(ULONG uniqId, ULONG lastSeq, KBDLAYER_LAYER_STATE* out, bool* outCancelled) {
    KBDLAYER_WAIT_LAYER_CHANGE_INPUT input;
    input.Target.UniqId = uniqId;
    input.LastSeq = lastSeq;

    *outCancelled = false;
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_WAIT_LAYER_CHANGE,
                              &input, sizeof(input),
                              out, sizeof(*out),
                              &bytesReturned, NULL);
    if (!ok) {
        DWORD error = GetLastError();
        // STATUS_CANCELLED(995) / STATUS_DEVICE_REMOVED(1617: キーボードが取り外された)
        if (error == ERROR_OPERATION_ABORTED || error == ERROR_DEVICE_REMOVED) {
            *outCancelled = true;
            fprintf(stderr, "%s\n", error == ERROR_DEVICE_REMOVED
                        ? "The keyboard was removed."
                        : "The wait was cancelled.");
            return false;
        }
        fprintf(stderr, "IOCTL_KBDLAYER_WAIT_LAYER_CHANGE failed. Error: %lu\n", error);
        return false;
    }
    return true;
}

bool IoctlClient::WaitHelperEvent(ULONG lastSeq, KBDLAYER_WAIT_HELPER_EVENT_OUTPUT* outHeader,
                                  std::vector<KBDLAYER_HELPER_EVENT>* outEvents, bool* outCancelled,
                                  bool* outUnsupported) {
    KBDLAYER_WAIT_HELPER_EVENT_INPUT input;
    input.LastSeq = lastSeq;
    input.Flags = 0;

    std::vector<unsigned char> buffer(sizeof(KBDLAYER_WAIT_HELPER_EVENT_OUTPUT) +
                                      KBDLAYER_HELPER_EVENT_QUEUE_SIZE * sizeof(KBDLAYER_HELPER_EVENT));
    *outCancelled = false;
    *outUnsupported = false;
    outEvents->clear();

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_WAIT_HELPER_EVENT,
                              &input, sizeof(input),
                              buffer.data(), (DWORD)buffer.size(),
                              &bytesReturned, NULL);
    if (!ok) {
        DWORD error = GetLastError();
        if (error == ERROR_OPERATION_ABORTED) {
            *outCancelled = true;
            fprintf(stderr, "The wait was cancelled.\n");
            return false;
        }
        if (error == ERROR_INVALID_FUNCTION) {
            // STATUS_INVALID_DEVICE_REQUEST: ヘルパーイベントより前のドライバ。
            *outUnsupported = true;
            fprintf(stderr, "This driver does not support helper events (HLP). Update kbdlayer.sys.\n");
            return false;
        }
        fprintf(stderr, "IOCTL_KBDLAYER_WAIT_HELPER_EVENT failed. Error: %lu\n", error);
        return false;
    }
    if (bytesReturned < sizeof(KBDLAYER_WAIT_HELPER_EVENT_OUTPUT)) {
        fprintf(stderr, "IOCTL_KBDLAYER_WAIT_HELPER_EVENT returned too few bytes (%lu).\n", bytesReturned);
        return false;
    }

    const KBDLAYER_WAIT_HELPER_EVENT_OUTPUT* header = (const KBDLAYER_WAIT_HELPER_EVENT_OUTPUT*)buffer.data();
    ULONG count = header->Count;
    ULONG fits = (bytesReturned - (ULONG)sizeof(KBDLAYER_WAIT_HELPER_EVENT_OUTPUT)) / (ULONG)sizeof(KBDLAYER_HELPER_EVENT);
    if (count > fits) count = fits;
    *outHeader = *header;
    const KBDLAYER_HELPER_EVENT* events =
        (const KBDLAYER_HELPER_EVENT*)(buffer.data() + sizeof(KBDLAYER_WAIT_HELPER_EVENT_OUTPUT));
    outEvents->assign(events, events + count);
    return true;
}

bool IoctlClient::WaitKeyEvent(ULONG lastSeq, KBDLAYER_WAIT_KEY_EVENT_OUTPUT* outHeader,
                               std::vector<KBDLAYER_KEY_EVENT>* outEvents, bool* outCancelled,
                               bool* outUnsupported, bool* outAccessDenied) {
    KBDLAYER_WAIT_KEY_EVENT_INPUT input;
    input.LastSeq = lastSeq;
    input.Flags = 0;

    std::vector<unsigned char> buffer(sizeof(KBDLAYER_WAIT_KEY_EVENT_OUTPUT) +
                                      KBDLAYER_KEY_EVENT_QUEUE_SIZE * sizeof(KBDLAYER_KEY_EVENT));
    *outCancelled = false;
    *outUnsupported = false;
    *outAccessDenied = false;
    outEvents->clear();

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_WAIT_KEY_EVENT,
                              &input, sizeof(input),
                              buffer.data(), (DWORD)buffer.size(),
                              &bytesReturned, NULL);
    if (!ok) {
        DWORD error = GetLastError();
        if (error == ERROR_OPERATION_ABORTED) {
            *outCancelled = true;
            fprintf(stderr, "The wait was cancelled.\n");
            return false;
        }
        if (error == ERROR_ACCESS_DENIED) {
            *outAccessDenied = true;
            fprintf(stderr, "Access denied. Watching raw key events needs Administrator rights.\n");
            return false;
        }
        if (error == ERROR_INVALID_FUNCTION) {
            // STATUS_INVALID_DEVICE_REQUEST: キーイベントの通知より前のドライバ。
            *outUnsupported = true;
            fprintf(stderr, "This driver does not support key events (WAIT_KEY_EVENT). Update kbdlayer.sys.\n");
            return false;
        }
        fprintf(stderr, "IOCTL_KBDLAYER_WAIT_KEY_EVENT failed. Error: %lu\n", error);
        return false;
    }
    if (bytesReturned < sizeof(KBDLAYER_WAIT_KEY_EVENT_OUTPUT)) {
        fprintf(stderr, "IOCTL_KBDLAYER_WAIT_KEY_EVENT returned too few bytes (%lu).\n", bytesReturned);
        return false;
    }

    const KBDLAYER_WAIT_KEY_EVENT_OUTPUT* header = (const KBDLAYER_WAIT_KEY_EVENT_OUTPUT*)buffer.data();
    ULONG count = header->Count;
    ULONG fits = (bytesReturned - (ULONG)sizeof(KBDLAYER_WAIT_KEY_EVENT_OUTPUT)) / (ULONG)sizeof(KBDLAYER_KEY_EVENT);
    if (count > fits) count = fits;
    *outHeader = *header;
    const KBDLAYER_KEY_EVENT* events =
        (const KBDLAYER_KEY_EVENT*)(buffer.data() + sizeof(KBDLAYER_WAIT_KEY_EVENT_OUTPUT));
    outEvents->assign(events, events + count);
    return true;
}

bool IoctlClient::SetLayerState(ULONG uniqId, ULONG op, ULONG layer, ULONG mask, ULONG flags,
                                KBDLAYER_LAYER_STATE* out) {
    KBDLAYER_SET_LAYER_STATE_INPUT input;
    input.Target.UniqId = uniqId;
    input.Op = op;
    input.Layer = layer;
    input.Mask = mask;
    input.Flags = flags;

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_SET_LAYER_STATE,
                              &input, sizeof(input),
                              out, sizeof(*out),
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_SET_LAYER_STATE failed. Error: %lu "
                "(layer must exist in the layer map)\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::GetDeviceScope(ULONG uniqId, KBDLAYER_DEVICE_SCOPE* out) {
    KBDLAYER_TARGET target;
    target.UniqId = uniqId;

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_GET_DEVICE_SCOPE,
                              &target, sizeof(target),
                              out, sizeof(*out),
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_GET_DEVICE_SCOPE failed. Error: %lu\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::GetEmergencyRelease(KBDLAYER_EMERGENCY_RELEASE_CONFIG* out) {
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_GET_EMERGENCY_RELEASE,
                              NULL, 0,
                              out, sizeof(*out),
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_GET_EMERGENCY_RELEASE failed. Error: %lu\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::SetEmergencyRelease(const KBDLAYER_EMERGENCY_RELEASE_CONFIG& config) {
    if (!RequireWriteAccess("IOCTL_KBDLAYER_SET_EMERGENCY_RELEASE")) return false;
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_SET_EMERGENCY_RELEASE,
                              (LPVOID)&config, sizeof(config),
                              NULL, 0,
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_SET_EMERGENCY_RELEASE failed. Error: %lu "
                "(need 2-6 distinct keys)\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::GetI8042Config(KBDLAYER_I8042_CONFIG* out) {
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_GET_I8042_CONFIG,
                              NULL, 0,
                              out, sizeof(*out),
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_GET_I8042_CONFIG failed. Error: %lu\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::SetI8042Config(const KBDLAYER_I8042_CONFIG& config) {
    if (!RequireWriteAccess("IOCTL_KBDLAYER_SET_I8042_CONFIG")) return false;
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_SET_I8042_CONFIG,
                              (LPVOID)&config, sizeof(config),
                              NULL, 0,
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_SET_I8042_CONFIG failed. Error: %lu "
                "(87 = invalid: names must be 1-%d chars of plain ASCII without \\ / : and ..; "
                "at most %d profiles; Type/Subtype 0-65535)\n",
                GetLastError(), KBDLAYER_I8042_STR_CHARS - 1, KBDLAYER_I8042_MAX_PROFILES);
        return false;
    }
    return true;
}

bool IoctlClient::GetFeatures(ULONG uniqId, ULONG* outFeatures) {
    KBDLAYER_TARGET target;
    target.UniqId = uniqId;
    KBDLAYER_GET_FEATURES_OUTPUT output = {};
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_GET_FEATURES,
                              &target, sizeof(target),
                              &output, sizeof(output),
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_GET_FEATURES failed. Error: %lu\n", GetLastError());
        return false;
    }
    *outFeatures = output.Features;
    return true;
}

bool IoctlClient::SetFeatures(ULONG uniqId, ULONG features) {
    if (!RequireWriteAccess("IOCTL_KBDLAYER_SET_FEATURES")) return false;
    KBDLAYER_SET_FEATURES_INPUT input;
    input.Target.UniqId = uniqId;
    input.Features = features;
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_SET_FEATURES,
                              &input, sizeof(input),
                              NULL, 0,
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_SET_FEATURES failed. Error: %lu\n", GetLastError());
        return false;
    }
    return true;
}

bool IoctlClient::SetDeviceScope(ULONG uniqId, ULONG binding, KBDLAYER_DEVICE_SCOPE* out) {
    if (!RequireWriteAccess("IOCTL_KBDLAYER_SET_DEVICE_SCOPE")) return false;
    KBDLAYER_SET_DEVICE_SCOPE_INPUT input;
    input.Target.UniqId = uniqId;
    input.Binding = binding;

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_SET_DEVICE_SCOPE,
                              &input, sizeof(input),
                              out, sizeof(*out),
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_SET_DEVICE_SCOPE failed. Error: %lu "
                "(instanceへの切替は差し込み口を特定できるデバイスのみ)\n", GetLastError());
        return false;
    }
    return true;
}
