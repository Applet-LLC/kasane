// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: Apache-2.0

#include "Json.h"
#include <cctype>
#include <cstdlib>

const JsonValue* JsonValue::Find(const std::string& key) const {
    if (type != Type::Object) return nullptr;
    for (auto& kv : objectValue) {
        if (kv.first == key) return &kv.second;
    }
    return nullptr;
}

namespace {

class Lexer {
public:
    explicit Lexer(const std::string& text) : s_(text), pos_(0) {}

    void SkipWhitespace() {
        while (pos_ < s_.size()) {
            char c = s_[pos_];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') {
                pos_++;
            } else if (c == '/' && pos_ + 1 < s_.size() && s_[pos_ + 1] == '/') {
                // VIAエクスポート等が末尾に "//" コメントを含むことがあるため、寛容に読み飛ばす。
                while (pos_ < s_.size() && s_[pos_] != '\n') pos_++;
            } else {
                break;
            }
        }
    }

    bool Eof() const { return pos_ >= s_.size(); }
    char Peek() const { return pos_ < s_.size() ? s_[pos_] : '\0'; }
    char Next() { return pos_ < s_.size() ? s_[pos_++] : '\0'; }

    bool Consume(char expected) {
        SkipWhitespace();
        if (Peek() != expected) return false;
        pos_++;
        return true;
    }

    size_t Pos() const { return pos_; }

private:
    const std::string& s_;
    size_t pos_;
};

bool ParseValue(Lexer& lex, JsonValue* out, std::string* error);

// ParseNumberはLexerが文字位置しか保持しないため、数値の部分文字列を切り出すのに
// 元のテキストを参照する。JsonParser::Parseが呼び出し中だけ設定する。
const std::string* g_currentText = nullptr;

bool ParseString(Lexer& lex, std::string* out, std::string* error) {
    if (!lex.Consume('"')) {
        *error = "expected '\"'";
        return false;
    }
    out->clear();
    for (;;) {
        if (lex.Eof()) {
            *error = "unterminated string";
            return false;
        }
        char c = lex.Next();
        if (c == '"') break;
        if (c == '\\') {
            char esc = lex.Next();
            switch (esc) {
            case '"': out->push_back('"'); break;
            case '\\': out->push_back('\\'); break;
            case '/': out->push_back('/'); break;
            case 'n': out->push_back('\n'); break;
            case 't': out->push_back('\t'); break;
            case 'r': out->push_back('\r'); break;
            case 'b': out->push_back('\b'); break;
            case 'f': out->push_back('\f'); break;
            case 'u': {
                // \uXXXX: BMP範囲のみサポート（サロゲートペアは非対応）。
                // keymap.json/info.jsonのキーコード文字列は基本ASCIIのため実用上問題ない。
                unsigned int code = 0;
                for (int i = 0; i < 4; i++) {
                    char h = lex.Next();
                    code <<= 4;
                    if (h >= '0' && h <= '9') code |= (h - '0');
                    else if (h >= 'a' && h <= 'f') code |= (h - 'a' + 10);
                    else if (h >= 'A' && h <= 'F') code |= (h - 'A' + 10);
                    else { *error = "invalid \\u escape"; return false; }
                }
                if (code < 0x80) {
                    out->push_back((char)code);
                } else if (code < 0x800) {
                    out->push_back((char)(0xC0 | (code >> 6)));
                    out->push_back((char)(0x80 | (code & 0x3F)));
                } else {
                    out->push_back((char)(0xE0 | (code >> 12)));
                    out->push_back((char)(0x80 | ((code >> 6) & 0x3F)));
                    out->push_back((char)(0x80 | (code & 0x3F)));
                }
                break;
            }
            default:
                *error = "invalid escape";
                return false;
            }
        } else {
            out->push_back(c);
        }
    }
    return true;
}

bool ParseNumber(Lexer& lex, double* out, std::string* error) {
    lex.SkipWhitespace();
    size_t start = lex.Pos();
    if (lex.Peek() == '-') lex.Next();
    while (isdigit((unsigned char)lex.Peek())) lex.Next();
    if (lex.Peek() == '.') {
        lex.Next();
        while (isdigit((unsigned char)lex.Peek())) lex.Next();
    }
    if (lex.Peek() == 'e' || lex.Peek() == 'E') {
        lex.Next();
        if (lex.Peek() == '+' || lex.Peek() == '-') lex.Next();
        while (isdigit((unsigned char)lex.Peek())) lex.Next();
    }
    size_t end = lex.Pos();
    if (end == start) {
        *error = "invalid number";
        return false;
    }
    // Lexerは参照経由でしか文字列を持たないため、部分文字列を作って変換する。
    std::string sub = g_currentText->substr(start, end - start);
    *out = strtod(sub.c_str(), nullptr);
    return true;
}

bool ParseArray(Lexer& lex, JsonValue* out, std::string* error) {
    if (!lex.Consume('[')) { *error = "expected '['"; return false; }
    out->type = JsonValue::Type::Array;
    lex.SkipWhitespace();
    if (lex.Peek() == ']') { lex.Next(); return true; }
    for (;;) {
        JsonValue elem;
        if (!ParseValue(lex, &elem, error)) return false;
        out->arrayValue.push_back(std::move(elem));
        lex.SkipWhitespace();
        if (lex.Peek() == ',') { lex.Next(); continue; }
        if (lex.Peek() == ']') { lex.Next(); break; }
        *error = "expected ',' or ']' in array";
        return false;
    }
    return true;
}

bool ParseObject(Lexer& lex, JsonValue* out, std::string* error) {
    if (!lex.Consume('{')) { *error = "expected '{'"; return false; }
    out->type = JsonValue::Type::Object;
    lex.SkipWhitespace();
    if (lex.Peek() == '}') { lex.Next(); return true; }
    for (;;) {
        lex.SkipWhitespace();
        std::string key;
        if (!ParseString(lex, &key, error)) return false;
        if (!lex.Consume(':')) { *error = "expected ':' in object"; return false; }
        JsonValue val;
        if (!ParseValue(lex, &val, error)) return false;
        out->objectValue.emplace_back(std::move(key), std::move(val));
        lex.SkipWhitespace();
        if (lex.Peek() == ',') { lex.Next(); continue; }
        if (lex.Peek() == '}') { lex.Next(); break; }
        *error = "expected ',' or '}' in object";
        return false;
    }
    return true;
}

bool ParseValue(Lexer& lex, JsonValue* out, std::string* error) {
    lex.SkipWhitespace();
    char c = lex.Peek();
    if (c == '"') {
        out->type = JsonValue::Type::String;
        return ParseString(lex, &out->stringValue, error);
    }
    if (c == '{') {
        return ParseObject(lex, out, error);
    }
    if (c == '[') {
        return ParseArray(lex, out, error);
    }
    if (c == 't') {
        // true
        for (char expect : {'t', 'r', 'u', 'e'}) {
            if (lex.Next() != expect) { *error = "invalid literal"; return false; }
        }
        out->type = JsonValue::Type::Bool;
        out->boolValue = true;
        return true;
    }
    if (c == 'f') {
        for (char expect : {'f', 'a', 'l', 's', 'e'}) {
            if (lex.Next() != expect) { *error = "invalid literal"; return false; }
        }
        out->type = JsonValue::Type::Bool;
        out->boolValue = false;
        return true;
    }
    if (c == 'n') {
        for (char expect : {'n', 'u', 'l', 'l'}) {
            if (lex.Next() != expect) { *error = "invalid literal"; return false; }
        }
        out->type = JsonValue::Type::Null;
        return true;
    }
    if (c == '-' || isdigit((unsigned char)c)) {
        out->type = JsonValue::Type::Number;
        return ParseNumber(lex, &out->numberValue, error);
    }
    *error = "unexpected character";
    return false;
}

} // namespace

bool JsonParser::Parse(const std::string& text, JsonValue* out, std::string* error) {
    g_currentText = &text;
    Lexer lex(text);
    bool ok = ParseValue(lex, out, error);
    g_currentText = nullptr;
    if (!ok) return false;
    lex.SkipWhitespace();
    if (!lex.Eof()) {
        *error = "trailing data after JSON value";
        return false;
    }
    return true;
}
