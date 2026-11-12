// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: Apache-2.0

/*
  KeymapTool/Json.h

  QMK info.json / VIAエクスポートkeymap.json を読むための、最小限の自前JSONパーサ。
  外部依存（vcpkg等）を増やさずにKeymapToolを単独でビルドできるようにするため、
  ライブラリを使わずここで実装する。対応するのは object/array/string/number/
  bool/nullのみで、\uXXXXエスケープや科学的記数法の完全なRFC準拠は目指さない
  （info.json/keymap.jsonの実用上の範囲をカバーできれば十分というスコープ）。
*/

#ifndef _KEYMAPTOOL_JSON_H_
#define _KEYMAPTOOL_JSON_H_

#include <string>
#include <vector>
#include <utility>

class JsonValue {
public:
    enum class Type { Null, Bool, Number, String, Array, Object };

    Type type = Type::Null;
    bool boolValue = false;
    double numberValue = 0.0;
    std::string stringValue;
    std::vector<JsonValue> arrayValue;
    std::vector<std::pair<std::string, JsonValue>> objectValue; // 出現順を保持、小規模なので線形探索でよい

    bool IsNull() const { return type == Type::Null; }
    bool IsArray() const { return type == Type::Array; }
    bool IsObject() const { return type == Type::Object; }
    bool IsString() const { return type == Type::String; }
    bool IsNumber() const { return type == Type::Number; }

    // Objectの場合のみキー検索。見つからなければnullptr。
    const JsonValue* Find(const std::string& key) const;

    int AsInt(int def = 0) const { return type == Type::Number ? (int)numberValue : def; }
    double AsDouble(double def = 0.0) const { return type == Type::Number ? numberValue : def; }
    std::string AsString(const std::string& def = std::string()) const {
        return type == Type::String ? stringValue : def;
    }
};

class JsonParser {
public:
    // textをパースして*outへ格納する。失敗時はfalseを返し*errorへ理由を書く。
    static bool Parse(const std::string& text, JsonValue* out, std::string* error);
};

#endif // _KEYMAPTOOL_JSON_H_
