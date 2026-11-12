// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: Apache-2.0

#include "NicolaTables.h"
#include "Json.h"
#include "FileUtil.h"
#include "KeycodeTable.h"
#include <cstdio>
#include <cstring>
#include <cmath>

namespace {

constexpr USHORT kLeftShift = 0x002A;

// 1打分: スキャンコード（Enc16）+ Shiftの要否。
struct KeyStroke {
    USHORT ScanCode;
    bool Shift;
};

// --- ローマ字入力用 ---
// IMEのローマ字かな変換に渡すASCII列。英字と - . , / だけを使う（これらはUS/JIS配列で
// 同じ物理キーなので、OS側のキーボードレイアウトに依存しない）。
struct RomajiEntry {
    const char* Kana;
    const char* Romaji;
};

const RomajiEntry kRomajiTable[] = {
    {u8"あ", "a"},   {u8"い", "i"},   {u8"う", "u"},   {u8"え", "e"},   {u8"お", "o"},
    {u8"か", "ka"},  {u8"き", "ki"},  {u8"く", "ku"},  {u8"け", "ke"},  {u8"こ", "ko"},
    {u8"さ", "sa"},  {u8"し", "si"},  {u8"す", "su"},  {u8"せ", "se"},  {u8"そ", "so"},
    {u8"た", "ta"},  {u8"ち", "ti"},  {u8"つ", "tu"},  {u8"て", "te"},  {u8"と", "to"},
    {u8"な", "na"},  {u8"に", "ni"},  {u8"ぬ", "nu"},  {u8"ね", "ne"},  {u8"の", "no"},
    {u8"は", "ha"},  {u8"ひ", "hi"},  {u8"ふ", "hu"},  {u8"へ", "he"},  {u8"ほ", "ho"},
    {u8"ま", "ma"},  {u8"み", "mi"},  {u8"む", "mu"},  {u8"め", "me"},  {u8"も", "mo"},
    {u8"や", "ya"},  {u8"ゆ", "yu"},  {u8"よ", "yo"},
    {u8"ら", "ra"},  {u8"り", "ri"},  {u8"る", "ru"},  {u8"れ", "re"},  {u8"ろ", "ro"},
    {u8"わ", "wa"},  {u8"を", "wo"},  {u8"ん", "nn"},
    {u8"が", "ga"},  {u8"ぎ", "gi"},  {u8"ぐ", "gu"},  {u8"げ", "ge"},  {u8"ご", "go"},
    {u8"ざ", "za"},  {u8"じ", "zi"},  {u8"ず", "zu"},  {u8"ぜ", "ze"},  {u8"ぞ", "zo"},
    {u8"だ", "da"},  {u8"ぢ", "di"},  {u8"づ", "du"},  {u8"で", "de"},  {u8"ど", "do"},
    {u8"ば", "ba"},  {u8"び", "bi"},  {u8"ぶ", "bu"},  {u8"べ", "be"},  {u8"ぼ", "bo"},
    {u8"ぱ", "pa"},  {u8"ぴ", "pi"},  {u8"ぷ", "pu"},  {u8"ぺ", "pe"},  {u8"ぽ", "po"},
    {u8"ヴ", "vu"},  {u8"ゔ", "vu"},
    {u8"ぁ", "xa"},  {u8"ぃ", "xi"},  {u8"ぅ", "xu"},  {u8"ぇ", "xe"},  {u8"ぉ", "xo"},
    {u8"ゃ", "xya"}, {u8"ゅ", "xyu"}, {u8"ょ", "xyo"}, {u8"っ", "xtu"}, {u8"ゎ", "xwa"},
    {u8"ー", "-"},   {u8"。", "."},   {u8"、", ","},   {u8"・", "/"},
    // 全角の ，／． はローマ字入力では直接打てない。IMEの句読点設定（「，．」等）に
    // 従って変換される , と . を送る。
    {u8"，", ","},   {u8"．", "."},
};

bool AsciiToStroke(char c, KeyStroke* out) {
    static const char* const kLetters = "abcdefghijklmnopqrstuvwxyz";
    static const USHORT kLetterCodes[26] = {
        0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
        0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C,
    };
    const char* p = (c != '\0') ? strchr(kLetters, c) : nullptr;
    if (p != nullptr) {
        *out = { kLetterCodes[p - kLetters], false };
        return true;
    }
    switch (c) {
    case '-': *out = { 0x0C, false }; return true;
    case '.': *out = { 0x34, false }; return true;
    case ',': *out = { 0x33, false }; return true;
    case '/': *out = { 0x35, false }; return true;
    default: return false;
    }
}

bool RomajiStrokes(const std::string& kana, std::vector<KeyStroke>* out) {
    for (const RomajiEntry& e : kRomajiTable) {
        if (kana == e.Kana) {
            for (const char* r = e.Romaji; *r; r++) {
                KeyStroke s;
                if (!AsciiToStroke(*r, &s)) return false;
                out->push_back(s);
            }
            return true;
        }
    }
    return false;
}

// --- JISかな入力用 ---
// JISかな配列（Set1スキャンコード）。濁音・半濁音は清音 + ゛(0x1A) / ゜(0x1B) で表す。
struct KanaEntry {
    const char* Kana;
    USHORT ScanCode;
    bool Shift;
};

const KanaEntry kJisKanaTable[] = {
    {u8"ぬ", 0x02, false}, {u8"ふ", 0x03, false}, {u8"あ", 0x04, false}, {u8"う", 0x05, false},
    {u8"え", 0x06, false}, {u8"お", 0x07, false}, {u8"や", 0x08, false}, {u8"ゆ", 0x09, false},
    {u8"よ", 0x0A, false}, {u8"わ", 0x0B, false}, {u8"ほ", 0x0C, false}, {u8"へ", 0x0D, false},
    {u8"ー", 0x7D, false},
    {u8"ぁ", 0x04, true},  {u8"ぅ", 0x05, true},  {u8"ぇ", 0x06, true},  {u8"ぉ", 0x07, true},
    {u8"ゃ", 0x08, true},  {u8"ゅ", 0x09, true},  {u8"ょ", 0x0A, true},  {u8"を", 0x0B, true},
    {u8"た", 0x10, false}, {u8"て", 0x11, false}, {u8"い", 0x12, false}, {u8"す", 0x13, false},
    {u8"か", 0x14, false}, {u8"ん", 0x15, false}, {u8"な", 0x16, false}, {u8"に", 0x17, false},
    {u8"ら", 0x18, false}, {u8"せ", 0x19, false}, {u8"゛", 0x1A, false}, {u8"゜", 0x1B, false},
    {u8"ぃ", 0x12, true},
    {u8"ち", 0x1E, false}, {u8"と", 0x1F, false}, {u8"し", 0x20, false}, {u8"は", 0x21, false},
    {u8"き", 0x22, false}, {u8"く", 0x23, false}, {u8"ま", 0x24, false}, {u8"の", 0x25, false},
    {u8"り", 0x26, false}, {u8"れ", 0x27, false}, {u8"け", 0x28, false}, {u8"む", 0x2B, false},
    {u8"つ", 0x2C, false}, {u8"さ", 0x2D, false}, {u8"そ", 0x2E, false}, {u8"ひ", 0x2F, false},
    {u8"こ", 0x30, false}, {u8"み", 0x31, false}, {u8"も", 0x32, false}, {u8"ね", 0x33, false},
    {u8"る", 0x34, false}, {u8"め", 0x35, false}, {u8"ろ", 0x73, false},
    {u8"っ", 0x2C, true},  {u8"、", 0x33, true},  {u8"。", 0x34, true},  {u8"・", 0x35, true},
    // 全角の ，／． はかな入力では直接打てないため、句読点で代用する。
    {u8"，", 0x33, true},  {u8"．", 0x34, true},
};

struct DakutenEntry {
    const char* Kana;
    const char* Base;
    bool Handakuten; // false = ゛, true = ゜
};

const DakutenEntry kDakutenTable[] = {
    {u8"が", u8"か", false}, {u8"ぎ", u8"き", false}, {u8"ぐ", u8"く", false}, {u8"げ", u8"け", false}, {u8"ご", u8"こ", false},
    {u8"ざ", u8"さ", false}, {u8"じ", u8"し", false}, {u8"ず", u8"す", false}, {u8"ぜ", u8"せ", false}, {u8"ぞ", u8"そ", false},
    {u8"だ", u8"た", false}, {u8"ぢ", u8"ち", false}, {u8"づ", u8"つ", false}, {u8"で", u8"て", false}, {u8"ど", u8"と", false},
    {u8"ば", u8"は", false}, {u8"び", u8"ひ", false}, {u8"ぶ", u8"ふ", false}, {u8"べ", u8"へ", false}, {u8"ぼ", u8"ほ", false},
    {u8"ぱ", u8"は", true},  {u8"ぴ", u8"ひ", true},  {u8"ぷ", u8"ふ", true},  {u8"ぺ", u8"へ", true},  {u8"ぽ", u8"ほ", true},
    {u8"ヴ", u8"う", false}, {u8"ゔ", u8"う", false},
};

bool JisKanaStroke(const std::string& kana, std::vector<KeyStroke>* out) {
    for (const KanaEntry& e : kJisKanaTable) {
        if (kana == e.Kana) {
            out->push_back({ e.ScanCode, e.Shift });
            return true;
        }
    }
    return false;
}

bool JisKanaStrokes(const std::string& kana, std::vector<KeyStroke>* out) {
    if (JisKanaStroke(kana, out)) return true;
    for (const DakutenEntry& e : kDakutenTable) {
        if (kana == e.Kana) {
            return JisKanaStroke(e.Base, out) &&
                   JisKanaStroke(e.Handakuten ? u8"゜" : u8"゛", out);
        }
    }
    return false;
}

// UTF-8文字列を1文字ずつに分ける。
std::vector<std::string> SplitUtf8(const std::string& s) {
    std::vector<std::string> chars;
    for (size_t i = 0; i < s.size();) {
        unsigned char c = (unsigned char)s[i];
        size_t len = (c < 0x80) ? 1 : (c >> 5) == 0x6 ? 2 : (c >> 4) == 0xE ? 3 : (c >> 3) == 0x1E ? 4 : 1;
        chars.push_back(s.substr(i, len));
        i += len;
    }
    return chars;
}

enum class OutputMode { Romaji, JisKana };

// 文字列（通常は1文字）を送出ステップ列に変換する。変換できない文字があれば
// falseを返し、*badCharにその文字を入れる。
bool TextToSteps(const std::string& text, OutputMode mode,
                 std::vector<KBDLAYER_MACRO_STEP>* steps, std::string* badChar) {
    for (const std::string& ch : SplitUtf8(text)) {
        std::vector<KeyStroke> strokes;
        bool ok = (mode == OutputMode::Romaji) ? RomajiStrokes(ch, &strokes) : JisKanaStrokes(ch, &strokes);
        if (!ok) {
            *badChar = ch;
            return false;
        }
        for (const KeyStroke& s : strokes) {
            if (s.Shift) steps->push_back({ kLeftShift, 0, 0 });
            steps->push_back({ s.ScanCode, 0, 0 });
            steps->push_back({ s.ScanCode, 1, 0 });
            if (s.Shift) steps->push_back({ kLeftShift, 1, 0 });
        }
    }
    return true;
}

// 単純キーのトークン（"KC_HENK"など）を物理スキャンコードへ。空文字列・"KC_NO"は0。
bool ResolveKeyToken(const std::string& token, bool allowEmpty, USHORT* out, std::string* error) {
    if (token.empty() || token == "KC_NO") {
        if (!allowEmpty) {
            *error = "a key is required here (got \"" + token + "\")";
            return false;
        }
        *out = 0;
        return true;
    }
    KeycodeMapping mapping;
    if (!KeycodeTable::Resolve(token, &mapping) || mapping.ActionType != KBDLAYER_ACTION_KEY) {
        *error = "\"" + token + "\" is not a plain key (KC_*)";
        return false;
    }
    *out = mapping.Param2;
    return true;
}

std::string StringField(const JsonValue& root, const char* name, const std::string& def) {
    const JsonValue* v = root.Find(name);
    return (v != nullptr && v->IsString()) ? v->stringValue : def;
}

double NumberField(const JsonValue& root, const char* name, double def) {
    const JsonValue* v = root.Find(name);
    return (v != nullptr && v->IsNumber()) ? v->numberValue : def;
}

bool BoolField(const JsonValue& root, const char* name, bool def) {
    const JsonValue* v = root.Find(name);
    return (v != nullptr && v->type == JsonValue::Type::Bool) ? v->boolValue : def;
}

const char* PlaneName(ULONG plane) {
    switch (plane) {
    case KBDLAYER_NICOLA_PLANE_NONE: return "single";
    case KBDLAYER_NICOLA_PLANE_THUMB_LEFT: return "thumbL";
    default: return "thumbR";
    }
}

} // namespace

bool NicolaTables::BuildBlob(const std::string& jsonPath,
                             std::vector<unsigned char>* outBlob,
                             std::vector<std::string>* warnings,
                             std::string* error)
{
    std::string text;
    if (!ReadFileToString(jsonPath, &text)) {
        *error = "failed to open " + jsonPath;
        return false;
    }
    if (text.size() >= 3 && (unsigned char)text[0] == 0xEF && (unsigned char)text[1] == 0xBB &&
        (unsigned char)text[2] == 0xBF) {
        text.erase(0, 3); // UTF-8 BOM
    }

    JsonValue root;
    if (!JsonParser::Parse(text, &root, error)) {
        return false;
    }
    if (!root.IsObject()) {
        *error = "NICOLA json root is not an object";
        return false;
    }

    OutputMode mode;
    std::string output = StringField(root, "output", "romaji");
    if (output == "romaji") {
        mode = OutputMode::Romaji;
    } else if (output == "jis-kana") {
        mode = OutputMode::JisKana;
    } else {
        *error = "\"output\" must be \"romaji\" or \"jis-kana\" (got \"" + output + "\")";
        return false;
    }

    KBDLAYER_NICOLA_HEADER header;
    memset(&header, 0, sizeof(header));
    header.Version = KBDLAYER_NICOLA_VERSION;
    header.Enabled = BoolField(root, "enabled", true) ? 1 : 0;
    header.Layer = (ULONG)NumberField(root, "layer", 0);
    header.SimulTermMs = (ULONG)NumberField(root, "simulTermMs", KBDLAYER_NICOLA_DEFAULT_SIMUL_TERM_MS);
    header.OverlapRatioPercent = (ULONG)std::lround(NumberField(root, "overlapRatio", 1.0) * 100.0);
    header.MinOverlapMs = (ULONG)NumberField(root, "minOverlapMs", 0);
    header.Flags = BoolField(root, "continuousShift", false) ? KBDLAYER_NICOLA_FLAG_CONTINUOUS_SHIFT : 0;

    if (header.Layer >= KBDLAYER_MAX_LAYERS) {
        *error = "\"layer\" must be 0.." + std::to_string(KBDLAYER_MAX_LAYERS - 1);
        return false;
    }
    if (header.SimulTermMs == 0 || header.SimulTermMs > 1000 ||
        header.OverlapRatioPercent == 0 || header.OverlapRatioPercent > 1000 || header.MinOverlapMs > 1000) {
        *error = "simulTermMs must be 1..1000, overlapRatio 0.01..10, minOverlapMs 0..1000";
        return false;
    }

    struct { const char* Name; USHORT* Out; bool AllowEmpty; } keyFields[] = {
        { "thumbLeft", &header.ThumbLeftScanCode, false },
        { "thumbRight", &header.ThumbRightScanCode, false },
        { "thumbLeftTap", &header.ThumbLeftTapKey, true },
        { "thumbRightTap", &header.ThumbRightTapKey, true },
        { "onEnter", &header.OnEnterKey, true },
        { "onExit", &header.OnExitKey, true },
    };
    for (const auto& f : keyFields) {
        std::string keyError;
        if (!ResolveKeyToken(StringField(root, f.Name, ""), f.AllowEmpty, f.Out, &keyError)) {
            *error = std::string("\"") + f.Name + "\": " + keyError;
            return false;
        }
    }
    if (header.ThumbLeftScanCode == header.ThumbRightScanCode) {
        *error = "\"thumbLeft\" and \"thumbRight\" must be different keys";
        return false;
    }

    // "left"/"right" の各キー: [単独, 同側の親指, 反対側の親指]。
    // 左領域の同側 = 親指左、右領域の同側 = 親指右（規格の図2）。
    std::vector<KBDLAYER_NICOLA_KEY_ENTRY> entries;
    struct { const char* Name; ULONG SamePlane; ULONG CrossPlane; } regions[] = {
        { "left", KBDLAYER_NICOLA_PLANE_THUMB_LEFT, KBDLAYER_NICOLA_PLANE_THUMB_RIGHT },
        { "right", KBDLAYER_NICOLA_PLANE_THUMB_RIGHT, KBDLAYER_NICOLA_PLANE_THUMB_LEFT },
    };
    for (const auto& region : regions) {
        const JsonValue* keys = root.Find(region.Name);
        if (keys == nullptr) continue;
        if (!keys->IsObject()) {
            *error = std::string("\"") + region.Name + "\" must be an object of \"KC_x\": [single, same, cross]";
            return false;
        }
        for (const auto& kv : keys->objectValue) {
            const std::string& token = kv.first;
            const JsonValue& planes = kv.second;
            if (!planes.IsArray() || planes.arrayValue.empty() || planes.arrayValue.size() > 3) {
                *error = "\"" + token + "\": expected an array of 1-3 strings [single, same, cross]";
                return false;
            }

            KBDLAYER_NICOLA_KEY_ENTRY entry;
            memset(&entry, 0, sizeof(entry));
            std::string keyError;
            if (!ResolveKeyToken(token, false, &entry.SourceScanCode, &keyError)) {
                *error = std::string("\"") + region.Name + "\": " + keyError;
                return false;
            }
            if (entry.SourceScanCode == header.ThumbLeftScanCode || entry.SourceScanCode == header.ThumbRightScanCode) {
                *error = "\"" + token + "\" is also a thumb key";
                return false;
            }
            for (const auto& existing : entries) {
                if (existing.SourceScanCode == entry.SourceScanCode) {
                    *error = "\"" + token + "\" is defined more than once";
                    return false;
                }
            }

            const ULONG planeIndex[3] = { KBDLAYER_NICOLA_PLANE_NONE, region.SamePlane, region.CrossPlane };
            for (size_t i = 0; i < planes.arrayValue.size(); i++) {
                const JsonValue& v = planes.arrayValue[i];
                if (!v.IsString()) {
                    *error = "\"" + token + "\": plane values must be strings";
                    return false;
                }
                if (v.stringValue.empty()) continue;

                std::vector<KBDLAYER_MACRO_STEP> steps;
                std::string badChar;
                if (!TextToSteps(v.stringValue, mode, &steps, &badChar)) {
                    warnings->push_back(token + " " + PlaneName(planeIndex[i]) + ": \"" + badChar +
                                        "\" cannot be typed in " + output + " mode; left empty");
                    continue;
                }
                if (steps.size() > KBDLAYER_NICOLA_MAX_STEPS) {
                    *error = "\"" + token + "\": \"" + v.stringValue + "\" needs " + std::to_string(steps.size()) +
                             " steps (max " + std::to_string(KBDLAYER_NICOLA_MAX_STEPS) + ")";
                    return false;
                }
                KBDLAYER_NICOLA_OUTPUT& out = entry.Planes[planeIndex[i]];
                out.StepCount = (UCHAR)steps.size();
                memcpy(out.Steps, steps.data(), steps.size() * sizeof(KBDLAYER_MACRO_STEP));
            }
            entries.push_back(entry);
        }
    }

    if (entries.empty()) {
        *error = "no character keys: define them under \"left\" and \"right\"";
        return false;
    }
    if (entries.size() > KBDLAYER_NICOLA_MAX_KEYS) {
        *error = "too many character keys (max " + std::to_string(KBDLAYER_NICOLA_MAX_KEYS) + ")";
        return false;
    }
    header.KeyCount = (ULONG)entries.size();

    outBlob->resize(sizeof(header) + entries.size() * sizeof(KBDLAYER_NICOLA_KEY_ENTRY));
    memcpy(outBlob->data(), &header, sizeof(header));
    memcpy(outBlob->data() + sizeof(header), entries.data(), entries.size() * sizeof(KBDLAYER_NICOLA_KEY_ENTRY));
    return true;
}

void NicolaTables::PrintBlob(const unsigned char* blob, size_t blobLength) {
    if (blobLength < sizeof(KBDLAYER_NICOLA_HEADER)) {
        printf("  (NICOLA not set)\n");
        return;
    }
    const KBDLAYER_NICOLA_HEADER* h = (const KBDLAYER_NICOLA_HEADER*)blob;
    const KBDLAYER_NICOLA_KEY_ENTRY* entries = (const KBDLAYER_NICOLA_KEY_ENTRY*)(blob + sizeof(*h));
    size_t available = (blobLength - sizeof(*h)) / sizeof(KBDLAYER_NICOLA_KEY_ENTRY);
    ULONG keyCount = (ULONG)min((size_t)h->KeyCount, available);

    printf("  Version=%lu Enabled=%lu Layer=%lu KeyCount=%lu\n", h->Version, h->Enabled, h->Layer, h->KeyCount);
    printf("  ThumbLeft=0x%04X (tap 0x%04X) ThumbRight=0x%04X (tap 0x%04X) OnEnter=0x%04X OnExit=0x%04X\n",
           h->ThumbLeftScanCode, h->ThumbLeftTapKey, h->ThumbRightScanCode, h->ThumbRightTapKey,
           h->OnEnterKey, h->OnExitKey);
    printf("  SimulTermMs=%lu OverlapRatioPercent=%lu MinOverlapMs=%lu Flags=0x%lX%s\n",
           h->SimulTermMs, h->OverlapRatioPercent, h->MinOverlapMs, h->Flags,
           (h->Flags & KBDLAYER_NICOLA_FLAG_CONTINUOUS_SHIFT) ? " (continuous shift)" : "");
    for (ULONG i = 0; i < keyCount; i++) {
        printf("    [%2lu] 0x%04X", i, entries[i].SourceScanCode);
        for (ULONG p = 0; p < KBDLAYER_NICOLA_PLANE_COUNT; p++) {
            const KBDLAYER_NICOLA_OUTPUT& out = entries[i].Planes[p];
            printf("  %s:", PlaneName(p));
            if (out.StepCount == 0) printf(" -");
            for (ULONG s = 0; s < out.StepCount && s < KBDLAYER_NICOLA_MAX_STEPS; s++) {
                printf(" %X%c", out.Steps[s].ScanCode, out.Steps[s].IsBreak ? '^' : 'v');
            }
        }
        printf("\n");
    }
}
