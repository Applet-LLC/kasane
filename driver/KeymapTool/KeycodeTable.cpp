// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

#include "KeycodeTable.h"
#include <unordered_map>
#include <cstdlib>
#include <cctype>
#include <algorithm>

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
        // ファンクションキー F13–F24（2026-10-08追加。物理キーが無くても出力できる。F24だけ0x76）
        {"KC_F13", SC1(0x64)}, {"KC_F14", SC1(0x65)}, {"KC_F15", SC1(0x66)}, {"KC_F16", SC1(0x67)},
        {"KC_F17", SC1(0x68)}, {"KC_F18", SC1(0x69)}, {"KC_F19", SC1(0x6A)}, {"KC_F20", SC1(0x6B)},
        {"KC_F21", SC1(0x6C)}, {"KC_F22", SC1(0x6D)}, {"KC_F23", SC1(0x6E)}, {"KC_F24", SC1(0x76)},
        // ブラウザー・アプリ起動キー（2026-10-08追加）
        {"KC_WSCH", SCE0(0x65)}, {"KC_WWW_SEARCH", SCE0(0x65)},
        {"KC_WHOM", SCE0(0x32)}, {"KC_WWW_HOME", SCE0(0x32)},
        {"KC_WBAK", SCE0(0x6A)}, {"KC_WWW_BACK", SCE0(0x6A)},
        {"KC_WFWD", SCE0(0x69)}, {"KC_WWW_FORWARD", SCE0(0x69)},
        {"KC_WSTP", SCE0(0x68)}, {"KC_WWW_STOP", SCE0(0x68)},
        {"KC_WREF", SCE0(0x67)}, {"KC_WWW_REFRESH", SCE0(0x67)},
        {"KC_WFAV", SCE0(0x66)}, {"KC_WWW_FAVORITES", SCE0(0x66)},
        {"KC_MAIL", SCE0(0x6C)},
        {"KC_MSEL", SCE0(0x6D)}, {"KC_MEDIA_SELECT", SCE0(0x6D)},
        {"KC_MYCM", SCE0(0x6B)}, {"KC_MY_COMPUTER", SCE0(0x6B)},
        // システム（電源）キー（2026-10-08追加。KC_PWRはOSの設定によってはシャットダウンになる）
        {"KC_PWR", SCE0(0x5E)}, {"KC_SYSTEM_POWER", SCE0(0x5E)},
        {"KC_SLEP", SCE0(0x5F)}, {"KC_SYSTEM_SLEEP", SCE0(0x5F)},
        {"KC_WAKE", SCE0(0x63)}, {"KC_SYSTEM_WAKE", SCE0(0x63)},
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

// 修飾付きキー（LCTL(kc)等）の関数名 → 修飾子マスク（KBDLAYER_MOD_*のビット。MT用の
// ModifierBitTable（ビット番号）とは表し方が違う）。C()/S()/A()/G()はQMKの短い別名。
// kasane/Keycodes/KeycodeTable.cs の ModFunctions と同じ内容・並びにすること
// （tools/Check-KeycodeTables.ps1 が照合する）。
const std::unordered_map<std::string, UCHAR>& ModFunctionTable() {
    static const std::unordered_map<std::string, UCHAR> table = {
        {"LCTL", 0x01}, {"LSFT", 0x02}, {"LALT", 0x04}, {"LGUI", 0x08},
        {"RCTL", 0x10}, {"RSFT", 0x20}, {"RALT", 0x40}, {"RGUI", 0x80},
        {"C", 0x01}, {"S", 0x02}, {"A", 0x04}, {"G", 0x08},
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

// HLP(id)のid用（ヘルパーイベント。CUSTOMアクションのParam2）。10進または0x付きの16進で0〜65535。
// （先頭が0の10進は8進にしない。kasane/Keycodes/KeycodeTable.cs の TryParseHelperId と同じ）
bool ParseHelperId(const std::string& s, USHORT* out) {
    bool hex = s.size() > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X');
    std::string digits = hex ? s.substr(2) : s;
    if (digits.empty()) return false;
    for (char c : digits) {
        if (hex ? !isxdigit((unsigned char)c) : !isdigit((unsigned char)c)) return false;
    }
    unsigned long v = strtoul(digits.c_str(), nullptr, hex ? 16 : 10);
    if (v > 0xFFFF) return false;
    *out = (USHORT)v;
    return true;
}

// 表に名前の無いキーを書くための、生のスキャンコード SC_XXXX（Enc16形式の16進4桁。上位2桁は
// 00/E0/E1、下位2桁は0以外）。kasane/Keycodes/KeycodeTable.cs の TryParseRawScanCode と同じ規則。
bool ParseRawScanCode(const std::string& token, USHORT* outScanCode16) {
    if (token.size() != 7 || token.compare(0, 3, "SC_") != 0) return false;
    for (size_t i = 3; i < token.size(); i++) {
        if (!isxdigit((unsigned char)token[i])) return false;
    }
    unsigned long v = strtoul(token.c_str() + 3, nullptr, 16);
    unsigned long prefix = v >> 8;
    if ((prefix != 0x00 && prefix != 0xE0 && prefix != 0xE1) || (v & 0xFF) == 0) return false;
    *outScanCode16 = (USHORT)v;
    return true;
}

// "LCTL(LSFT(KC_T))" のような修飾付きキーを、修飾子マスクと基本キーに分解する
// （入れ子を1段ずつ外す）。修飾関数が1つも無い（ただの基本キー）場合はfalse。
bool ParseModdedKey(const std::string& token, UCHAR* outMask, USHORT* outScanCode16) {
    UCHAR mask = 0;
    std::string cur = token;
    for (int depth = 0; depth < 8; depth++) {
        std::string name, inner;
        if (!SplitCall(cur, &name, &inner)) break;
        auto it = ModFunctionTable().find(name);
        if (it == ModFunctionTable().end()) return false;
        mask |= it->second;
        cur = inner;
        cur.erase(std::remove(cur.begin(), cur.end(), ' '), cur.end());
    }
    if (mask == 0) return false;
    USHORT sc = 0;
    auto base = BaseKeyTable().find(cur);
    if (base != BaseKeyTable().end()) {
        sc = base->second;
    } else if (!ParseRawScanCode(cur, &sc)) {
        return false;
    }
    if (sc == 0) return false;
    *outMask = mask;
    *outScanCode16 = sc;
    return true;
}

} // namespace

USHORT KeycodeTable::ModifierScanCode(int bit) {
    static const USHORT table[8] = {
        SC1(0x1D), SC1(0x2A), SC1(0x38), SCE0(0x5B),   // LCTL LSFT LALT LGUI
        SCE0(0x1D), SC1(0x36), SCE0(0x38), SCE0(0x5C),  // RCTL RSFT RALT RGUI
    };
    return (bit >= 0 && bit < 8) ? table[bit] : 0;
}

bool KeycodeTable::ResolveModMask(const std::string& token, UCHAR* outMask) {
    // "MOD_LCTL|MOD_LSFT" 形式（LM(layer, mod)の第2引数）。ModifierBitTableのビット番号を
    // マスクに直してORする。
    UCHAR mask = 0;
    size_t start = 0;
    while (start <= token.size()) {
        size_t bar = token.find('|', start);
        std::string part = token.substr(start, bar == std::string::npos ? std::string::npos : bar - start);
        UCHAR bit;
        if (!ResolveModifierBit(part, &bit)) return false;
        mask |= (UCHAR)(1u << bit);
        if (bar == std::string::npos) break;
        start = bar + 1;
    }
    if (mask == 0) return false;
    *outMask = mask;
    return true;
}

bool KeycodeTable::ResolveBaseKey(const std::string& token, USHORT* outScanCode16) {
    const auto& table = BaseKeyTable();
    auto it = table.find(token);
    if (it != table.end()) {
        *outScanCode16 = it->second;
        return true;
    }
    return ParseRawScanCode(token, outScanCode16);
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

    {
        // 修飾付きキー: LCTL(kc) / C(kc) / LCTL(LSFT(kc)) など。
        UCHAR mask;
        USHORT sc;
        if (ParseModdedKey(token, &mask, &sc)) {
            out->ActionType = KBDLAYER_ACTION_MODDED_KEY;
            out->Param1 = mask;
            out->Param2 = sc;
            return true;
        }
    }

    std::string name, args;
    if (SplitCall(token, &name, &args)) {
        std::vector<std::string> parts = SplitArgs(args);

        if (name == "LM" && parts.size() == 2) {
            // LM(layer, MOD_LCTL|MOD_LSFT)
            UCHAR layer;
            UCHAR mask;
            if (!ParseLayerNumber(parts[0], &layer)) return false;
            if (!ResolveModMask(parts[1], &mask)) return false;
            out->ActionType = KBDLAYER_ACTION_LAYER_MOD;
            out->Param1 = layer;
            out->Param2 = mask;
            return true;
        }

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
        if (name == "OSM" && parts.size() == 1) {
            UCHAR mask;
            if (!ResolveModMask(parts[0], &mask) || mask == 0) return false;
            out->ActionType = KBDLAYER_ACTION_ONESHOT_MOD;
            out->Param1 = mask;
            out->Param2 = 0;
            return true;
        }
        if (name == "TO" && parts.size() == 1) {
            UCHAR layer;
            if (!ParseLayerNumber(parts[0], &layer)) return false;
            out->ActionType = KBDLAYER_ACTION_LAYER_TO;
            out->Param1 = layer;
            out->Param2 = 0;
            return true;
        }
        if (name == "TT" && parts.size() == 1) {
            UCHAR layer;
            if (!ParseLayerNumber(parts[0], &layer)) return false;
            out->ActionType = KBDLAYER_ACTION_LAYER_TAP_TOGGLE;
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
        if (name == "HLP" && parts.size() == 1) {
            // ヘルパーイベント（CUSTOM）: キーは出さず、押下・解放をトレイ等のアプリへ知らせる。
            USHORT helperId;
            if (!ParseHelperId(parts[0], &helperId)) return false;
            out->ActionType = KBDLAYER_ACTION_CUSTOM;
            out->Param1 = 0;
            out->Param2 = helperId;
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
