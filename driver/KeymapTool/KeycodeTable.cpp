// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

#include "KeycodeTable.h"
#include <unordered_map>
#include <cstdlib>

namespace {

// Enc16形式: 上位8bit = 0x00/0xE0/0xE1（拡張プレフィックス）、下位8bit = Set1 MakeCode。
// LayerEngine.c の EncodeScancode16/DecodeScancode16 と同じ規約。
constexpr USHORT SC(UCHAR prefix, UCHAR makeCode) {
    return (USHORT)((prefix << 8) | makeCode);
}
constexpr USHORT SC1(UCHAR makeCode) { return SC(0x00, makeCode); }
constexpr USHORT SCE0(UCHAR makeCode) { return SC(0xE0, makeCode); }

// PrintScreen/Pauseは実機のmake/breakシーケンスが複数バイトにまたがる特殊キーで、
// 単一のEnc16値では厳密には表現しきれない。Windows標準のScancode Map機能でも
// 同様の簡略化（E0,2A/E1,1D等の代表バイトのみを使う）が一般的に行われており、
// 本ツールもそれに倣う。実運用上ほぼ使われないキーであるため、正確なmake/break
// シーケンスの再現はスコープ外とする。
// Pauseは実機では{1D,E1}→{45,フラグ無し}の2イベントで届き、キャリブレーション
// (CAPTURE_NEXT_KEY)は先頭の{1D,E1}を記録するため、KC_PAUSEはE1 1Dとする
// （E1 45にするとキャプチャ値と食い違い、レイヤー0の素通しでも出力が化ける）。
// 後続の{45}はNumLockと同じ0x0045として別途解決される点に注意（キャプチャ側では
// ドライバのResolveCalibrationがE1直後の1件を記録対象から外すので誤記録されない）。
// PrintScreenの前置きの疑似Shift（E0 2A）も同様にキャプチャ対象外なので、
// キャリブレーション結果はKC_PSCREENと同じE0 37になる。
const std::unordered_map<std::string, USHORT>& BaseKeyTable() {
    static const std::unordered_map<std::string, USHORT> table = {
        // 英字
        {"KC_A", SC1(0x1E)}, {"KC_B", SC1(0x30)}, {"KC_C", SC1(0x2E)}, {"KC_D", SC1(0x20)},
        {"KC_E", SC1(0x12)}, {"KC_F", SC1(0x21)}, {"KC_G", SC1(0x22)}, {"KC_H", SC1(0x23)},
        {"KC_I", SC1(0x17)}, {"KC_J", SC1(0x24)}, {"KC_K", SC1(0x25)}, {"KC_L", SC1(0x26)},
        {"KC_M", SC1(0x32)}, {"KC_N", SC1(0x31)}, {"KC_O", SC1(0x18)}, {"KC_P", SC1(0x19)},
        {"KC_Q", SC1(0x10)}, {"KC_R", SC1(0x13)}, {"KC_S", SC1(0x1F)}, {"KC_T", SC1(0x14)},
        {"KC_U", SC1(0x16)}, {"KC_V", SC1(0x2F)}, {"KC_W", SC1(0x11)}, {"KC_X", SC1(0x2D)},
        {"KC_Y", SC1(0x15)}, {"KC_Z", SC1(0x2C)},
        // 数字（最上段）
        {"KC_1", SC1(0x02)}, {"KC_2", SC1(0x03)}, {"KC_3", SC1(0x04)}, {"KC_4", SC1(0x05)},
        {"KC_5", SC1(0x06)}, {"KC_6", SC1(0x07)}, {"KC_7", SC1(0x08)}, {"KC_8", SC1(0x09)},
        {"KC_9", SC1(0x0A)}, {"KC_0", SC1(0x0B)},
        // 記号・編集キー
        {"KC_ENTER", SC1(0x1C)}, {"KC_ENT", SC1(0x1C)},
        {"KC_ESCAPE", SC1(0x01)}, {"KC_ESC", SC1(0x01)},
        {"KC_BSPACE", SC1(0x0E)}, {"KC_BSPC", SC1(0x0E)},
        {"KC_TAB", SC1(0x0F)},
        {"KC_SPACE", SC1(0x39)}, {"KC_SPC", SC1(0x39)},
        {"KC_MINUS", SC1(0x0C)}, {"KC_MINS", SC1(0x0C)},
        {"KC_EQUAL", SC1(0x0D)}, {"KC_EQL", SC1(0x0D)},
        {"KC_LBRACKET", SC1(0x1A)}, {"KC_LBRC", SC1(0x1A)},
        {"KC_RBRACKET", SC1(0x1B)}, {"KC_RBRC", SC1(0x1B)},
        {"KC_BSLASH", SC1(0x2B)}, {"KC_BSLS", SC1(0x2B)},
        {"KC_NONUS_HASH", SC1(0x2B)}, {"KC_NUHS", SC1(0x2B)},
        {"KC_SCOLON", SC1(0x27)}, {"KC_SCLN", SC1(0x27)},
        {"KC_QUOTE", SC1(0x28)}, {"KC_QUOT", SC1(0x28)},
        {"KC_GRAVE", SC1(0x29)}, {"KC_GRV", SC1(0x29)}, {"KC_ZKHK", SC1(0x29)},
        {"KC_COMMA", SC1(0x33)}, {"KC_COMM", SC1(0x33)},
        {"KC_DOT", SC1(0x34)},
        {"KC_SLASH", SC1(0x35)}, {"KC_SLSH", SC1(0x35)},
        {"KC_NONUS_BSLASH", SC1(0x56)}, {"KC_NUBS", SC1(0x56)},
        {"KC_CAPSLOCK", SC1(0x3A)}, {"KC_CAPS", SC1(0x3A)},
        // JIS配列専用キー（109キーボードがUS 104キーに対して追加で持つ入力キー）。
        // KC_ZKHK（半角/全角）は上のKC_GRAVEと同じ物理位置(0x29)のエイリアスとして
        // 既に定義済みのためここには無い。それ以外の5キーはそれぞれ独立したスキャンコードを
        // 持つ、JIS配列固有の追加キー。
        {"KC_RO", SC1(0x73)}, {"KC_INT1", SC1(0x73)},       // \_ (ろ)。右Shift手前の追加キー
        {"KC_KANA", SC1(0x70)}, {"KC_INT2", SC1(0x70)},     // カタカナ ひらがな ローマ字
        {"KC_JYEN", SC1(0x7D)}, {"KC_INT3", SC1(0x7D)},     // ¥ (Backspace付近、US KC_BSLASHとは別物理位置の機種もある)
        {"KC_HENK", SC1(0x79)}, {"KC_INT4", SC1(0x79)},     // 変換（スペースキー右）
        {"KC_MHEN", SC1(0x7B)}, {"KC_INT5", SC1(0x7B)},     // 無変換（スペースキー左）
        // ファンクションキー
        {"KC_F1", SC1(0x3B)}, {"KC_F2", SC1(0x3C)}, {"KC_F3", SC1(0x3D)}, {"KC_F4", SC1(0x3E)},
        {"KC_F5", SC1(0x3F)}, {"KC_F6", SC1(0x40)}, {"KC_F7", SC1(0x41)}, {"KC_F8", SC1(0x42)},
        {"KC_F9", SC1(0x43)}, {"KC_F10", SC1(0x44)}, {"KC_F11", SC1(0x57)}, {"KC_F12", SC1(0x58)},
        // 修飾キー
        {"KC_LCTRL", SC1(0x1D)}, {"KC_LCTL", SC1(0x1D)},
        {"KC_LSHIFT", SC1(0x2A)}, {"KC_LSFT", SC1(0x2A)},
        {"KC_LALT", SC1(0x38)},
        {"KC_LGUI", SCE0(0x5B)}, {"KC_LCMD", SCE0(0x5B)}, {"KC_LWIN", SCE0(0x5B)},
        {"KC_RCTRL", SCE0(0x1D)}, {"KC_RCTL", SCE0(0x1D)},
        {"KC_RSHIFT", SC1(0x36)}, {"KC_RSFT", SC1(0x36)},
        {"KC_RALT", SCE0(0x38)}, {"KC_ALGR", SCE0(0x38)},
        {"KC_RGUI", SCE0(0x5C)}, {"KC_RCMD", SCE0(0x5C)}, {"KC_RWIN", SCE0(0x5C)},
        {"KC_APP", SCE0(0x5D)}, {"KC_APPLICATION", SCE0(0x5D)},
        // KC_NOをMT(mod,kc)/LT(layer,kc)のタップ時キーコード引数として使えるようにする
        // プレースホルダー（スキャンコード0x0000）。Resolve()自身は単独トークンとしての
        // "KC_NO"を最上部で特別扱い(ActionType=NONE)するためここには来ないが、MT/LTの
        // パースはResolveBaseKeyを直接呼ぶためこのテーブルに載せておく必要がある
        // （載っていないと"MT(MOD_LCTL,KC_NO)"のようなトークン全体が解決失敗する）。
        {"KC_NO", SC1(0x00)},
        // ナビゲーションクラスタ
        {"KC_PSCREEN", SCE0(0x37)}, {"KC_PSCR", SCE0(0x37)},
        {"KC_SCROLLLOCK", SC1(0x46)}, {"KC_SCRL", SC1(0x46)},
        {"KC_PAUSE", SC(0xE1, 0x1D)}, {"KC_PAUS", SC(0xE1, 0x1D)}, {"KC_BRK", SC(0xE1, 0x1D)},
        {"KC_INSERT", SCE0(0x52)}, {"KC_INS", SCE0(0x52)},
        {"KC_HOME", SCE0(0x47)},
        {"KC_PGUP", SCE0(0x49)},
        {"KC_DELETE", SCE0(0x53)}, {"KC_DEL", SCE0(0x53)},
        {"KC_END", SCE0(0x4F)},
        {"KC_PGDOWN", SCE0(0x51)}, {"KC_PGDN", SCE0(0x51)},
        {"KC_RIGHT", SCE0(0x4D)}, {"KC_RGHT", SCE0(0x4D)},
        {"KC_LEFT", SCE0(0x4B)},
        {"KC_DOWN", SCE0(0x50)},
        {"KC_UP", SCE0(0x48)},
        {"KC_NUMLOCK", SC1(0x45)}, {"KC_NUM", SC1(0x45)},
        // テンキー
        {"KC_KP_SLASH", SCE0(0x35)}, {"KC_PSLS", SCE0(0x35)},
        {"KC_KP_ASTERISK", SC1(0x37)}, {"KC_PAST", SC1(0x37)},
        {"KC_KP_MINUS", SC1(0x4A)}, {"KC_PMNS", SC1(0x4A)},
        {"KC_KP_PLUS", SC1(0x4E)}, {"KC_PPLS", SC1(0x4E)},
        {"KC_KP_ENTER", SCE0(0x1C)}, {"KC_PENT", SCE0(0x1C)},
        {"KC_KP_1", SC1(0x4F)}, {"KC_P1", SC1(0x4F)},
        {"KC_KP_2", SC1(0x50)}, {"KC_P2", SC1(0x50)},
        {"KC_KP_3", SC1(0x51)}, {"KC_P3", SC1(0x51)},
        {"KC_KP_4", SC1(0x4B)}, {"KC_P4", SC1(0x4B)},
        {"KC_KP_5", SC1(0x4C)}, {"KC_P5", SC1(0x4C)},
        {"KC_KP_6", SC1(0x4D)}, {"KC_P6", SC1(0x4D)},
        {"KC_KP_7", SC1(0x47)}, {"KC_P7", SC1(0x47)},
        {"KC_KP_8", SC1(0x48)}, {"KC_P8", SC1(0x48)},
        {"KC_KP_9", SC1(0x49)}, {"KC_P9", SC1(0x49)},
        {"KC_KP_0", SC1(0x52)}, {"KC_P0", SC1(0x52)},
        {"KC_KP_DOT", SC1(0x53)}, {"KC_PDOT", SC1(0x53)},
        // 主要メディア/システムキー
        {"KC_MUTE", SCE0(0x20)}, {"KC_KB_MUTE", SCE0(0x20)},
        {"KC_VOLD", SCE0(0x2E)}, {"KC_KB_VOLUME_DOWN", SCE0(0x2E)},
        {"KC_VOLU", SCE0(0x30)}, {"KC_KB_VOLUME_UP", SCE0(0x30)},
        {"KC_MPLY", SCE0(0x22)}, {"KC_MEDIA_PLAY_PAUSE", SCE0(0x22)},
        {"KC_MSTP", SCE0(0x24)}, {"KC_MEDIA_STOP", SCE0(0x24)},
        {"KC_MNXT", SCE0(0x19)}, {"KC_MEDIA_NEXT_TRACK", SCE0(0x19)},
        {"KC_MPRV", SCE0(0x10)}, {"KC_MEDIA_PREV_TRACK", SCE0(0x10)},
        {"KC_CALC", SCE0(0x21)}, {"KC_CALCULATOR", SCE0(0x21)},
    };
    return table;
}

const std::unordered_map<std::string, UCHAR>& ModifierBitTable() {
    // MT(mod,kc)のParam1に使うビット番号。driver/kbdlayer/LayerEngine.cの
    // s_ModifierScanCodesと同じ割り当て（0=LCTL,1=LSFT,2=LALT,3=LGUI,
    // 4=RCTL,5=RSFT,6=RALT,7=RGUI）。両側で変更する場合は必ず一致させること。
    static const std::unordered_map<std::string, UCHAR> table = {
        {"MOD_LCTL", 0}, {"MOD_LSFT", 1}, {"MOD_LALT", 2}, {"MOD_LGUI", 3},
        {"MOD_RCTL", 4}, {"MOD_RSFT", 5}, {"MOD_RALT", 6}, {"MOD_RGUI", 7},
    };
    return table;
}

// "MO(1)" のような "NAME(args)" 形式を分解する。argsはカンマ区切りのまま返す
// （呼び出し側がさらに分割する）。マッチしなければfalse。
bool SplitCall(const std::string& token, std::string* name, std::string* args) {
    size_t open = token.find('(');
    if (open == std::string::npos || token.back() != ')') return false;
    *name = token.substr(0, open);
    *args = token.substr(open + 1, token.size() - open - 2);
    return true;
}

std::vector<std::string> SplitArgs(const std::string& args) {
    std::vector<std::string> out;
    std::string cur;
    for (char c : args) {
        if (c == ',') {
            out.push_back(cur);
            cur.clear();
        } else if (c != ' ') {
            cur.push_back(c);
        }
    }
    out.push_back(cur);
    return out;
}

bool ParseLayerNumber(const std::string& s, UCHAR* out) {
    if (s.empty()) return false;
    char* end = nullptr;
    long v = strtol(s.c_str(), &end, 10);
    if (end != s.c_str() + s.size() || v < 0 || v >= KBDLAYER_MAX_LAYERS) return false;
    *out = (UCHAR)v;
    return true;
}

// MACRO(id)のid用。ParseLayerNumberと同じ形だが上限がKBDLAYER_MAX_MACRO_COUNT
// （レイヤー数とは無関係な範囲）である点だけが異なる。
bool ParseMacroId(const std::string& s, UCHAR* out) {
    if (s.empty()) return false;
    char* end = nullptr;
    long v = strtol(s.c_str(), &end, 10);
    if (end != s.c_str() + s.size() || v < 0 || v >= KBDLAYER_MAX_MACRO_COUNT) return false;
    *out = (UCHAR)v;
    return true;
}

// TAPDANCE(id)のid用。ParseMacroIdと同じ形だが上限がKBDLAYER_MAX_TAPDANCE_COUNT
// （マクロ数とは無関係な範囲）である点だけが異なる。
bool ParseTapDanceId(const std::string& s, UCHAR* out) {
    if (s.empty()) return false;
    char* end = nullptr;
    long v = strtol(s.c_str(), &end, 10);
    if (end != s.c_str() + s.size() || v < 0 || v >= KBDLAYER_MAX_TAPDANCE_COUNT) return false;
    *out = (UCHAR)v;
    return true;
}

} // namespace

bool KeycodeTable::ResolveBaseKey(const std::string& token, USHORT* outScanCode16) {
    const auto& table = BaseKeyTable();
    auto it = table.find(token);
    if (it == table.end()) return false;
    *outScanCode16 = it->second;
    return true;
}

bool KeycodeTable::ResolveModifierBit(const std::string& token, UCHAR* outBit) {
    const auto& table = ModifierBitTable();
    auto it = table.find(token);
    if (it == table.end()) return false;
    *outBit = it->second;
    return true;
}

bool KeycodeTable::Resolve(const std::string& token, KeycodeMapping* out) {
    if (token == "KC_TRNS" || token == "_______" || token == "KC_TRANSPARENT") {
        out->ActionType = KBDLAYER_ACTION_TRANSPARENT;
        out->Param1 = 0;
        out->Param2 = 0;
        return true;
    }
    if (token == "KC_NO" || token == "XXXXXXX") {
        out->ActionType = KBDLAYER_ACTION_NONE;
        out->Param1 = 0;
        out->Param2 = 0;
        return true;
    }

    USHORT scanCode;
    if (ResolveBaseKey(token, &scanCode)) {
        out->ActionType = KBDLAYER_ACTION_KEY;
        out->Param1 = 0;
        out->Param2 = scanCode;
        return true;
    }

    std::string name, args;
    if (SplitCall(token, &name, &args)) {
        std::vector<std::string> parts = SplitArgs(args);

        if (name == "MO" && parts.size() == 1) {
            UCHAR layer;
            if (!ParseLayerNumber(parts[0], &layer)) return false;
            out->ActionType = KBDLAYER_ACTION_MOMENTARY;
            out->Param1 = layer;
            out->Param2 = 0;
            return true;
        }
        if (name == "TG" && parts.size() == 1) {
            UCHAR layer;
            if (!ParseLayerNumber(parts[0], &layer)) return false;
            out->ActionType = KBDLAYER_ACTION_TOGGLE;
            out->Param1 = layer;
            out->Param2 = 0;
            return true;
        }
        if (name == "DF" && parts.size() == 1) {
            UCHAR layer;
            if (!ParseLayerNumber(parts[0], &layer)) return false;
            out->ActionType = KBDLAYER_ACTION_DEFAULT;
            out->Param1 = layer;
            out->Param2 = 0;
            return true;
        }
        if (name == "OSL" && parts.size() == 1) {
            UCHAR layer;
            if (!ParseLayerNumber(parts[0], &layer)) return false;
            out->ActionType = KBDLAYER_ACTION_ONESHOT_LAYER;
            out->Param1 = layer;
            out->Param2 = 0;
            return true;
        }
        if (name == "MT" && parts.size() == 2) {
            UCHAR bit;
            USHORT tapSc;
            if (!ResolveModifierBit(parts[0], &bit)) return false;
            if (!ResolveBaseKey(parts[1], &tapSc)) return false;
            out->ActionType = KBDLAYER_ACTION_MODTAP;
            out->Param1 = bit;
            out->Param2 = tapSc;
            return true;
        }
        if (name == "LT" && parts.size() == 2) {
            UCHAR layer;
            USHORT tapSc;
            if (!ParseLayerNumber(parts[0], &layer)) return false;
            if (!ResolveBaseKey(parts[1], &tapSc)) return false;
            out->ActionType = KBDLAYER_ACTION_LAYERTAP;
            out->Param1 = layer;
            out->Param2 = tapSc;
            return true;
        }
        if (name == "MACRO" && parts.size() == 1) {
            UCHAR macroId;
            if (!ParseMacroId(parts[0], &macroId)) return false;
            out->ActionType = KBDLAYER_ACTION_MACRO;
            out->Param1 = macroId;
            out->Param2 = 0;
            return true;
        }
        if (name == "TAPDANCE" && parts.size() == 1) {
            UCHAR tapDanceId;
            if (!ParseTapDanceId(parts[0], &tapDanceId)) return false;
            out->ActionType = KBDLAYER_ACTION_TAPDANCE;
            out->Param1 = tapDanceId;
            out->Param2 = 0;
            return true;
        }
    }

    return false;
}
