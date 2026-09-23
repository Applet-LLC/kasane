// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

#include "IoctlClient.h"
#include <cstdio>

bool IoctlClient::Open() {
    handle_ = CreateFileW(
        KBDLAYER_CONTROL_DEVICE_PATH,
        GENERIC_READ | GENERIC_WRITE,
        0,
        NULL,
        OPEN_EXISTING,
        0,
        NULL);
    if (handle_ == INVALID_HANDLE_VALUE) {
        fwprintf(stderr, L"Failed to open %ls (kbdlayer.sys installed? control device created?). Error: %lu\n",
                 KBDLAYER_CONTROL_DEVICE_PATH, GetLastError());
        return false;
    }
    return true;
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

bool IoctlClient::CaptureNextKey(ULONG uniqId, ULONG timeoutMs, USHORT* outScanCode16, bool* outTimedOut) {
    KBDLAYER_CAPTURE_NEXT_KEY_INPUT input;
    input.Target.UniqId = uniqId;
    input.TimeoutMs = timeoutMs;

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

bool IoctlClient::SetConfig(ULONG uniqId, ULONG tapTermMs) {
    KBDLAYER_SET_CONFIG_INPUT input;
    input.Target.UniqId = uniqId;
    input.TapTermMs = tapTermMs;

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(handle_, IOCTL_KBDLAYER_SET_CONFIG,
                              &input, sizeof(input),
                              NULL, 0,
                              &bytesReturned, NULL);
    if (!ok) {
        fprintf(stderr, "IOCTL_KBDLAYER_SET_CONFIG failed. Error: %lu\n", GetLastError());
        return false;
    }
    return true;
}
