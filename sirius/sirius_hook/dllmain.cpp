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

#include "sirius_timing.h"
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

// 実験用（Explorer の遅延の切り分け。原因が分かったら外す）: C:\Users\Public\sirius-exp.txt の数値。128 = SimpleLoggerOpen をしない、256 = msctf.dll を読まない、512 = TLS と TSF の準備をしない
static int ExpBits()
{
  int v = 0;
  HANDLE f = CreateFileW(L"C:\\Users\\Public\\sirius-exp.txt", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
  if (f != INVALID_HANDLE_VALUE) {
    char b[16] = {0};
    DWORD n = 0;
    ReadFile(f, b, 15, &n, NULL);
    CloseHandle(f);
    v = atoi(b);
  }
  return v;
}

BOOL ProcessAttach(HINSTANCE hInstance)
{
  int exp = ExpBits();
  if (!(exp & 512) && !TLS::Initialize())
    return FALSE;

  if (!(exp & 128)) SimpleLoggerOpen();
  GetThreadMgr = getThreadMgrDummy;
  if (!(exp & 256)) {
    hMsctf = LoadLibrary(L"msctf.dll");
    if (hMsctf) {
      GetThreadMgr = (PTF_GETTHREADMGR)GetProcAddress(hMsctf, "TF_GetThreadMgr");
    }
  }
  if (!(exp & 512)) setupTsfValues();

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
      sirius_timing::Scope timing("DllMain.attach", 0, 0.0);
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
    {
      sirius_timing::Scope timing("DllMain.threadDetach", 0, 1.0);
      releaseThreadTsfValues();
    }
      break;
    case DLL_PROCESS_DETACH:
      ProcessDettach(siriusHookModule);
      break;
  }
  return bRet;
}

