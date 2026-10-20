// 天狼（sirius）: MIT License, Copyright (C) 2008-2010, MATSUMOTO Reiji
#pragma once

#define NO_IMM32		// IMM32による取得コードを抑止します。

#include <Msctf.h>

typedef HRESULT (WINAPI *PTF_GETTHREADMGR)(ITfThreadMgr **pptim);
extern PTF_GETTHREADMGR GetThreadMgr;
extern HMODULE siriusHookModule;
