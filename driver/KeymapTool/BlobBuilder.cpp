// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

#include "BlobBuilder.h"
#include "KeycodeTable.h"
#include "..\\common\\kbdlayer_ioctl.h"
#include <sstream>

bool BuildLayerMapBlob(
    const std::vector<std::vector<std::string>>& layers,
    const std::vector<USHORT>& scanCodesByIndex,
    std::vector<unsigned char>* outBlob,
    std::string* error)
{
    size_t layerCount = layers.size();
    size_t keyCount = scanCodesByIndex.size();

    if (layerCount < 1 || layerCount > KBDLAYER_MAX_LAYERS) {
        std::ostringstream ss;
        ss << "layer count " << layerCount << " out of range (1.." << KBDLAYER_MAX_LAYERS << ")";
        *error = ss.str();
        return false;
    }
    if (keyCount < 1 || keyCount > KBDLAYER_MAX_KEYS) {
        std::ostringstream ss;
        ss << "key count " << keyCount << " out of range (1.." << KBDLAYER_MAX_KEYS << ")";
        *error = ss.str();
        return false;
    }
    for (size_t L = 0; L < layerCount; L++) {
        if (layers[L].size() != keyCount) {
            std::ostringstream ss;
            ss << "layer " << L << " has " << layers[L].size()
               << " keys, expected " << keyCount << " (matching physical key count)";
            *error = ss.str();
            return false;
        }
    }

    std::vector<KBDLAYER_KEY_ENTRY> entries(keyCount);
    for (size_t i = 0; i < keyCount; i++) {
        KBDLAYER_KEY_ENTRY& entry = entries[i];
        ZeroMemory(&entry, sizeof(entry));
        entry.SourceScanCode = scanCodesByIndex[i];

        // レイヤー数は常にKBDLAYER_MAX_LAYERS（ドライバ側でLayerCountを廃止した）。
        // keymap.jsonに書かれていないレイヤーは、ゼロ（KBDLAYER_ACTION_NONE＝何も送らない）
        // ではなくTRANSPARENT（下のレイヤーへフォールスルー）で埋める。ヘルパーアプリが
        // 未定義の上位レイヤーをSET_LAYER_STATEで動的にONにしても、キーが効かなくならない
        // ようにするため。
        for (size_t L = layerCount; L < KBDLAYER_MAX_LAYERS; L++) {
            entry.Actions[L].Type = KBDLAYER_ACTION_TRANSPARENT;
        }

        for (size_t L = 0; L < layerCount; L++) {
            KeycodeMapping mapping;
            if (!KeycodeTable::Resolve(layers[L][i], &mapping)) {
                std::ostringstream ss;
                ss << "unresolved keycode \"" << layers[L][i] << "\" at layer " << L
                   << ", key index " << i;
                *error = ss.str();
                return false;
            }
            entry.Actions[L].Type = mapping.ActionType;
            entry.Actions[L].Param1 = mapping.Param1;
            entry.Actions[L].Param2 = mapping.Param2;
        }
    }

    KBDLAYER_MAP_HEADER header;
    header.Header = 0;
    header.KeyCount = (ULONG)keyCount;

    outBlob->resize(sizeof(header) + entries.size() * sizeof(KBDLAYER_KEY_ENTRY));
    unsigned char* p = outBlob->data();
    memcpy(p, &header, sizeof(header));
    p += sizeof(header);
    if (!entries.empty()) {
        memcpy(p, entries.data(), entries.size() * sizeof(KBDLAYER_KEY_ENTRY));
    }

    return true;
}
