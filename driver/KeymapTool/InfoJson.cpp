// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

#include "InfoJson.h"
#include "Json.h"
#include "FileUtil.h"

bool InfoJson::Load(
    const std::string& filePath,
    const std::string& layoutName,
    std::vector<LayoutKeyPosition>* outPositions,
    int* outRows,
    int* outCols,
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
        *error = "info.json root is not an object";
        return false;
    }

    const JsonValue* matrix = root.Find("matrix");
    if (matrix != nullptr && matrix->IsObject()) {
        const JsonValue* rows = matrix->Find("rows");
        const JsonValue* cols = matrix->Find("cols");
        *outRows = rows ? rows->AsInt(0) : 0;
        *outCols = cols ? cols->AsInt(0) : 0;
    } else {
        *outRows = 0;
        *outCols = 0;
    }

    const JsonValue* layouts = root.Find("layouts");
    if (layouts == nullptr || !layouts->IsObject() || layouts->objectValue.empty()) {
        *error = "info.json has no \"layouts\" object";
        return false;
    }

    const JsonValue* chosenLayout = nullptr;
    if (!layoutName.empty()) {
        chosenLayout = layouts->Find(layoutName);
        if (chosenLayout == nullptr) {
            *error = "layout \"" + layoutName + "\" not found in info.json";
            return false;
        }
    } else {
        chosenLayout = &layouts->objectValue[0].second;
    }

    const JsonValue* layoutArray = chosenLayout->Find("layout");
    if (layoutArray == nullptr || !layoutArray->IsArray()) {
        *error = "chosen layout has no \"layout\" array";
        return false;
    }

    outPositions->clear();
    outPositions->reserve(layoutArray->arrayValue.size());
    for (const JsonValue& keyDef : layoutArray->arrayValue) {
        const JsonValue* matrixPos = keyDef.Find("matrix");
        if (matrixPos == nullptr || !matrixPos->IsArray() || matrixPos->arrayValue.size() != 2) {
            *error = "layout entry missing 2-element \"matrix\" array";
            return false;
        }
        LayoutKeyPosition pos;
        pos.Row = matrixPos->arrayValue[0].AsInt(-1);
        pos.Col = matrixPos->arrayValue[1].AsInt(-1);
        if (pos.Row < 0 || pos.Col < 0) {
            *error = "layout entry has invalid matrix row/col";
            return false;
        }
        outPositions->push_back(pos);
    }

    if (outPositions->empty()) {
        *error = "layout has zero keys";
        return false;
    }

    return true;
}
