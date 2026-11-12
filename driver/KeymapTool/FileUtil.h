// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: Apache-2.0

#ifndef _KEYMAPTOOL_FILEUTIL_H_
#define _KEYMAPTOOL_FILEUTIL_H_

#include <string>
#include <fstream>
#include <sstream>

inline bool ReadFileToString(const std::string& path, std::string* out) {
    std::ifstream f(path, std::ios::binary);
    if (!f) return false;
    std::ostringstream ss;
    ss << f.rdbuf();
    *out = ss.str();
    return true;
}

inline bool WriteStringToFile(const std::string& path, const std::string& content) {
    std::ofstream f(path, std::ios::binary | std::ios::trunc);
    if (!f) return false;
    f << content;
    return (bool)f;
}

#endif // _KEYMAPTOOL_FILEUTIL_H_
