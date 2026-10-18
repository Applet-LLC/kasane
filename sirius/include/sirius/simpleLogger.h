// 天狼（sirius）: MIT License, Copyright (C) 2008-2010, MATSUMOTO Reiji
#pragma once

#ifndef SIMPLE_LOG_MAX_LOGS
#define SIMPLE_LOG_MAX_LOGS 5
#endif

#ifndef SIMPLE_LOG_APP_NAME
#define SIMPLE_LOG_APP_NAME L"simplelog"
#endif

#ifndef SIMPLE_LOG_PREFIX
#define SIMPLE_LOG_PREFIX L"system-"
#endif

bool SimpleLoggerFileName(wchar_t* szPath, size_t nMaxLogs, SYSTEMTIME* pSt);
void SimpleLogger(char* fn, int line, char* fmt, ...);

#define LOG(...) SimpleLogger(__FILE__, __LINE__, ## __VA_ARGS__) 
