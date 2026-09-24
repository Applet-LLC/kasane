// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

#include "CalibrationProfile.h"
#include "Json.h"
#include "FileUtil.h"
#include <cstdio>
#include <sstream>

bool CalibrationProfile::Save(const std::string& filePath, std::string* error) const {
    std::ostringstream out;
    out << "{\n  \"positions\": [\n";
    for (size_t i = 0; i < Keys.size(); i++) {
        const CalibratedKey& k = Keys[i];
        out << "    {\"row\": " << k.Row << ", \"col\": " << k.Col
            << ", \"scanCode\": " << k.ScanCode16 << "}";
        if (i + 1 < Keys.size()) out << ",";
        out << "\n";
    }
    out << "  ]\n}\n";

    if (!WriteStringToFile(filePath, out.str())) {
        *error = "failed to write " + filePath;
        return false;
    }
    return true;
}

bool CalibrationProfile::Load(const std::string& filePath, std::string* error) {
    std::string text;
    if (!ReadFileToString(filePath, &text)) {
        *error = "failed to open " + filePath;
        return false;
    }

    JsonValue root;
    if (!JsonParser::Parse(text, &root, error)) {
        return false;
    }
    const JsonValue* positions = root.Find("positions");
    if (positions == nullptr || !positions->IsArray()) {
        *error = "calibration profile has no \"positions\" array";
        return false;
    }

    Keys.clear();
    for (const JsonValue& entry : positions->arrayValue) {
        const JsonValue* row = entry.Find("row");
        const JsonValue* col = entry.Find("col");
        const JsonValue* sc = entry.Find("scanCode");
        if (row == nullptr || col == nullptr || sc == nullptr) {
            *error = "calibration profile entry missing row/col/scanCode";
            return false;
        }
        CalibratedKey k;
        k.Row = row->AsInt(0);
        k.Col = col->AsInt(0);
        k.ScanCode16 = (USHORT)sc->AsInt(0);
        Keys.push_back(k);
    }
    return true;
}

const CalibratedKey* CalibrationProfile::Find(int row, int col) const {
    for (const auto& k : Keys) {
        if (k.Row == row && k.Col == col) return &k;
    }
    return nullptr;
}

bool RunCalibrationWizard(
    IoctlClient* client,
    ULONG uniqId,
    const std::vector<LayoutKeyPosition>& positions,
    CalibrationProfile* outProfile,
    int maxConsecutiveTimeouts)
{
    int consecutiveTimeouts = 0;

    outProfile->Keys.clear();
    outProfile->Keys.reserve(positions.size());

    printf("Calibration wizard: %zu keys. Press each highlighted key on the physical keyboard "
           "when prompted (Ctrl+C to abort).\n", positions.size());

    for (size_t i = 0; i < positions.size(); i++) {
        const LayoutKeyPosition& pos = positions[i];
        printf("[%zu/%zu] Press the key at matrix (row=%d, col=%d)... ",
               i + 1, positions.size(), pos.Row, pos.Col);
        fflush(stdout);

        for (;;) {
            USHORT scanCode = 0;
            bool timedOut = false;
            if (!client->CaptureNextKey(uniqId, 0, &scanCode, &timedOut)) {
                return false;
            }
            if (timedOut) {
                consecutiveTimeouts++;
                if (maxConsecutiveTimeouts > 0 && consecutiveTimeouts >= maxConsecutiveTimeouts) {
                    printf("\nNo key pressed for too long; calibration aborted (nothing was saved).\n");
                    return false;
                }
                printf("\n  (no key detected within timeout, retrying) ");
                fflush(stdout);
                continue;
            }
            consecutiveTimeouts = 0;
            printf("captured scancode 0x%04X\n", scanCode);
            CalibratedKey k;
            k.Row = pos.Row;
            k.Col = pos.Col;
            k.ScanCode16 = scanCode;
            outProfile->Keys.push_back(k);
            break;
        }
    }

    printf("Calibration complete: %zu keys captured.\n", outProfile->Keys.size());
    return true;
}
