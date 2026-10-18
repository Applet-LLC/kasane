// 天狼（sirius）: MIT License, Copyright (C) 2008-2010, MATSUMOTO Reiji
// dllmain.cpp : DLL アプリケーションのエントリ ポイントを定義します。
#include "stdafx.h"
#include "sirius_hook.h"
#include "tsfValues.h"
#include <sirius/siriusLogger.h>
#include <sirius/simpleLoggerImpl.h>
#include "tlsDeclaration.h"

typedef BOOL (__stdcall *FUNCTYPE)(UINT, DWORD);
typedef BOOL (__stdcall *FUNCTYPE7)(HWND, UINT, DWORD, int);

#ifndef MSGFLT_ADD
#define MSGFLT_ADD		1
#define MSGFLT_REMOVE	2
#endif

#ifndef MSGFLT_ALLOW
#define MSGFLT_RESET	0
#define MSGFLT_ALLOW	1
#endif

HMODULE siriusHookModule = NULL;
PTF_GETTHREADMGR GetThreadMgr = NULL;
static HMODULE hMsctf = NULL;
TLS g_TLS;
DWORD TLS::dwTlsIndex = TLS_OUT_OF_INDEXES;
DWORD g_dwThreadDllMain = 0;

extern int bReentrantCheck= 0;


static void L(wchar_t* fmt, ...) {
  wchar_t buf[1000];
  va_list argp;
  va_start(argp, fmt);
  _vsnwprintf_s(buf, 1000, 1000, fmt, argp);
  va_end(argp);  
  wcscat_s(buf, 1000, L"\n");
  bReentrantCheck++;
  OutputDebugString(buf);
  bReentrantCheck--;
}

static HRESULT WINAPI getThreadMgrDummy(ITfThreadMgr **p)
{
  *p = NULL;
  return S_OK;
}

BOOL ProcessAttach(HINSTANCE hInstance)
{
  if (!TLS::Initialize())
    return FALSE;

  SimpleLoggerOpen();
  GetThreadMgr = getThreadMgrDummy;
  hMsctf = LoadLibrary(L"msctf.dll");
  if (hMsctf) {
    GetThreadMgr = (PTF_GETTHREADMGR)GetProcAddress(hMsctf, "TF_GetThreadMgr");
  }
  setupTsfValues();

  // kasane: のどかへの通知（FindWindow("nodokaTasktray")・ChangeWindowMessageFilter）は削除。ホストは共有メモリを読む。

  return TRUE;
}

void ProcessDettach(HINSTANCE hInstance)
{
  TLS::DestroyTLS();
  TLS::Uninitialize();
  releaseTsfValues();
  SimpleLoggerClose();
}

BOOL APIENTRY DllMain( HMODULE hModule,
                       DWORD  ul_reason_for_call,
                       LPVOID lpReserved
					 )
{
  BOOL bRet = TRUE;
  g_dwThreadDllMain = GetCurrentThreadId();
  siriusHookModule = hModule;        // Instannce/Moduleの保存

  switch(ul_reason_for_call)
  {
    case DLL_PROCESS_ATTACH:
    {
      if (!ProcessAttach(siriusHookModule))
      {
        ProcessDettach(siriusHookModule);
        bRet = FALSE;
      }
      break;
    }
    case DLL_THREAD_ATTACH:
      break;
    case DLL_THREAD_DETACH:
      releaseThreadTsfValues();
      break;
    case DLL_PROCESS_DETACH:
      ProcessDettach(siriusHookModule);
      break;
  }
  return bRet;
}

