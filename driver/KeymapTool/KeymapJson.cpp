// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: Apache-2.0

#include "KeymapJson.h"
#include "Json.h"
#include "FileUtil.h"

bool KeymapJson::Load(
    const std::string& filePath,
    std::vector<std::vector<std::string>>* outLayers,
    std::string* error)
{
    std::string text;
    if (!ReadFileToString(filePath, &text)) {
        *error = "failed to open " + filePath;
        return false;
    }

    JsonValue root;
    if (!JsonParser::Parse(text, &root, error)) {
        return false;
    }
    if (!root.IsObject()) {
        *error = "keymap.json root is not an object";
        return false;
    }

    const JsonValue* layersVal = root.Find("layout");
    if (layersVal == nullptr) {
        layersVal = root.Find("layers");
    }
    if (layersVal == nullptr || !layersVal->IsArray()) {
        *error = "keymap.json has no \"layout\"/\"layers\" array";
        return false;
    }

    outLayers->clear();
    outLayers->reserve(layersVal->arrayValue.size());
    for (const JsonValue& layer : layersVal->arrayValue) {
        if (!layer.IsArray()) {
            *error = "keymap.json layer is not an array";
            return false;
        }
        std::vector<std::string> keys;
        keys.reserve(layer.arrayValue.size());
        for (const JsonValue& k : layer.arrayValue) {
            if (!k.IsString()) {
                *error = "keymap.json layer entry is not a string";
                return false;
            }
            keys.push_back(k.stringValue);
        }
        outLayers->push_back(std::move(keys));
    }

    if (outLayers->empty()) {
        *error = "keymap.json has zero layers";
        return false;
    }

    return true;
}
