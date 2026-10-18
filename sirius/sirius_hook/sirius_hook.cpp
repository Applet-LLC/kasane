// 天狼（sirius）: MIT License, Copyright (C) 2008-2010, MATSUMOTO Reiji
// commonValues.cpp: CcommonValues クラスのインプリメンテーション
//
//////////////////////////////////////////////////////////////////////

#include "stdafx.h"
#include <stdio.h>
#include <stdarg.h>
#include <imm.h>
#include <msctf.h>
#include <sirius/commonValues.h>
#include <sirius/tsfEdit.h>
#include <sirius/tsfText.h>
#include <sirius/siriusLogger.h>
#include "sirius_hook.h"
#include "tsfValues.h"
#include "tlsDeclaration.h"

#ifdef _DEBUG
#undef THIS_FILE
static char THIS_FILE[]=__FILE__;
// #define new DEBUG_NEW
#endif

#pragma data_seg( ".HookData" )
HHOOK hGetMessageHook = NULL;
HHOOK hSendMessageHook = NULL;
HHOOK hSysGetMsgHook = NULL;
bool m_isHooked = false;
#pragma data_seg()
static HANDLE hMapping = NULL;
static CcommonValues* pCv = NULL;
static int bReentrantCheck= 0;
static ITfThreadMgr* threadMgr = NULL;

void insertNatureBuffer(LPCWSTR str)
{
    int n = (int)wcslen(str);
    if (NATURE_SIZE - 1 < n + pCv->m_nNaturePos) n = NATURE_SIZE - 1 - pCv->m_nNaturePos;
    if (n) {
        wcsncpy_s(pCv->m_strNatureBuffer + pCv->m_nNaturePos, NATURE_SIZE - pCv->m_nNaturePos, str, n);
        pCv->m_nNaturePos += n;
        pCv->m_strNatureBuffer[pCv->m_nNaturePos] = '\0';
    }
}

void deleteNatureBuffer(int count)
{
    if (count < pCv->m_nNaturePos) pCv->m_nNaturePos -= count;
    else pCv->m_nNaturePos = 0;
    pCv->m_strNatureBuffer[pCv->m_nNaturePos] = '\0';
}

void clearNatureBuffer()
{
    pCv->m_nNaturePos = 0;
    pCv->m_strNatureBuffer[0] = '\0';
}

static void cmdGetImeString(LPARAM)
{
  TsfEdit* tsfc = new TsfEdit(GetThreadMgr);
  if (SUCCEEDED(tsfc->go(pCv->m_strImeBuffer, MAX_REPLACE_SIZE, &pCv->m_imeStringSync))) {
    tsfc->Release();
    return;
  }
  tsfc->Release();
  pCv->m_imeStringSync = 0;
  pCv->m_strImeBuffer[0] = 0;

#ifdef NO_IMM32
  HWND hWnd = GetFocus();
  if (!IsWindow(hWnd)) return;
  HIMC hImc = ImmGetContext(hWnd);
  if (ImmGetOpenStatus(hImc)) {
      for (int i = 0; i < MAX_REPLACE_SIZE; i++)
          pCv->m_strImeBuffer[i] = 0;
      ImmGetCompositionString(hImc, GCS_COMPSTR , pCv->m_strImeBuffer, (MAX_REPLACE_SIZE - 1) * sizeof(WCHAR));
  }
  ImmReleaseContext(hWnd, hImc);
#endif // NO_IMM32
}

static void cmdImeClear(LPARAM)
{
#ifdef NO_IMM32
    HWND hWnd = GetFocus();
    if (!IsWindow(hWnd)) return;
    HIMC hImc = ImmGetContext(hWnd);
    if (ImmGetOpenStatus(hImc)) {
        ImmSetCompositionString(hImc, SCS_SETSTR, NULL, 0, NULL, 0);
    }
    ImmReleaseContext(hWnd, hImc);
#endif // NO_IMM32
}

static void cmdPeekMessage(LPARAM)
{
    MSG msg;
//  pCv->m_peekMessageResult = PeekMessage(&msg, NULL, WM_KEYFIRST , WM_KEYLAST , PM_NOREMOVE) ? TRUE : FALSE;
    pCv->m_peekMessageResult = PeekMessage(&msg, NULL, 0, 0, PM_NOREMOVE) ? TRUE : FALSE;
}

static void cmdPeekKeyMessage(LPARAM)
{
    MSG msg;
    pCv->m_peekMessageResult = PeekMessage(&msg, NULL, WM_KEYFIRST , WM_KEYLAST , PM_NOREMOVE) ? TRUE : FALSE;
}

static void cmdGetFocus(LPARAM lParam)
{
  pCv->m_hWndFocus = GetFocus();
#ifndef _WIN64
  pCv->dmy1 = NULL;
#endif
}

static void cmdImeOpen(LPARAM param)
{
#ifdef NO_IMM32
    cmdGetFocus(0);
    if (!IsWindow(pCv->m_hWndFocus)) return;
    BOOL openStatus = param != 0 ? TRUE : FALSE;
    HIMC hImc = ImmGetContext(pCv->m_hWndFocus);
    ImmReleaseContext(pCv->m_hWndFocus, hImc);
#endif // NO_IMM32
}

static bool getTsfIntValue(const GUID& guid, DWORD* result)
{
  ITfThreadMgr* iTMgr = NULL;
  ITfCompartmentMgr* iCMgr = NULL;
  ITfCompartment* iComp = NULL;
  VARIANT v;
  bool bTfsGet = false;
  do {
    if (!SUCCEEDED(GetThreadMgr(&iTMgr)) || !iTMgr) {
      break;
    }
    if (!SUCCEEDED(iTMgr->QueryInterface(IID_ITfCompartmentMgr, (void**)&iCMgr))) {
      break;
    }
    if (!SUCCEEDED(iCMgr->GetCompartment(guid, &iComp))) {
      break;
    }
    if (!SUCCEEDED(iComp->GetValue(&v))) {
      break;
    }
    if (v.vt == VT_EMPTY) {
      break;
    }
    bTfsGet = true;
  } while (0);
  if (iComp) iComp->Release();
  if (iCMgr) iCMgr->Release();
  if (iTMgr) iTMgr->Release();

  if (!bTfsGet) return false;
  *result = v.ulVal;
  return true;
}

static void cmdGetImeStatus(LPARAM)
{
  //return;			// what this?
  DWORD v;
  if (getTsfIntValue(GUID_COMPARTMENT_KEYBOARD_OPENCLOSE, &v)) {
    pCv->m_bImeStatus = v != 0 ? TRUE : FALSE;
    return;
  }
#ifdef NO_IMM32
  cmdGetFocus(0);
  if (!IsWindow(pCv->m_hWndFocus)) return;
  HIMC hImc = ImmGetContext(pCv->m_hWndFocus);
  pCv->m_bImeStatus = ImmGetOpenStatus(hImc) ? TRUE : FALSE;
  ImmReleaseContext(pCv->m_hWndFocus, hImc);
#endif // NO_IMM32
}

static void cmdGetImeStatusEx(LPARAM)
{
  cmdGetImeStatus(0);
  DWORD v;
  if (getTsfIntValue(GUID_COMPARTMENT_KEYBOARD_INPUTMODE_CONVERSION, &v)) {
    pCv->m_conversion = v;
    if (getTsfIntValue(GUID_COMPARTMENT_KEYBOARD_INPUTMODE_SENTENCE, &v)) {
      pCv->m_sentence = v;
    }
    return;
  }
#ifdef NO_IMM32
  cmdGetFocus(0);
  if (!IsWindow(pCv->m_hWndFocus)) return;
  HIMC hImc = ImmGetContext(pCv->m_hWndFocus);
  DWORD conv = 0, sent = 0;
  if (ImmGetConversionStatus(hImc, &conv, &sent)) {
    pCv->m_conversion = conv;
    pCv->m_sentence = sent;
  }
  COMPOSITIONFORM comp = {(DWORD)-1, {-1, -1}, {-1, -1, -1, -1}};
  if (ImmGetCompositionWindow(hImc, &comp)) {
     pCv->m_composition = comp;
  }
  CANDIDATEFORM candi = {(DWORD)-1, (DWORD)-1, {-1, -1}, {-1, -1, -1, -1}};
  if (ImmGetCandidateWindow(hImc, 0, &candi)) {
     pCv->m_candidate = candi;
  }
  LOGFONT f = {0};
  if (ImmGetCompositionFont(hImc, &f)) {
    pCv->m_font = f;
  }
  ImmReleaseContext(pCv->m_hWndFocus, hImc);
#endif // NO_IMM32
}


static void doCommand(cmd command, LPARAM lParam)
{
/*
#ifdef _WIN64
  L("x1 command(64)=%d, test=%d", command, pCv->test++);
#else
  L("y1 command(32)=%d, test=%d", command, pCv->test++);
#endif
  */
  switch (command) {
    case e_imeClear:
        cmdImeClear(lParam);
        break;
    case e_getImeString:
        cmdGetImeString(lParam);
        break;
    case e_peekMessage:
        cmdPeekMessage(lParam);
        break;
    case e_peekKeyMessage:
        cmdPeekKeyMessage(lParam);
        break;
/*
    case e_getFocus:
        cmdGetFocus(lParam);
        break;
        */
    case e_getImeStatus:
        cmdGetImeStatus(lParam);
        break;
    case e_getImeStatusEx:
        cmdGetImeStatusEx(lParam);
        break;
    case e_imeOpen:
        cmdImeOpen(lParam);
        break;
    }
}

static bool allocateCommonValue(DWORD msg = 0)
{
  if (pCv) return true;

  SECURITY_DESCRIPTOR sd;
  SECURITY_ATTRIBUTES sa;
  bReentrantCheck++;
  if (!InitializeSecurityDescriptor(&sd, SECURITY_DESCRIPTOR_REVISION)) {
    LOG("InitializeSecurityDescriptor() failed. Error=%d", GetLastError());
  }
  if (!SetSecurityDescriptorDacl(&sd, TRUE, NULL, NULL)) {
    LOG("SetSecurityDescriptorDacl() failed. Error=%d", GetLastError());
  }
  sa.nLength = sizeof(SECURITY_ATTRIBUTES);
  sa.lpSecurityDescriptor = &sd;
  sa.bInheritHandle = TRUE;

  hMapping = ::CreateFileMapping((HANDLE)-1, &sa, PAGE_READWRITE, 0, sizeof(CcommonValues), L"KASANE_SIRIUS_VALUES1");
  if (!hMapping) {
    LOG("CreateFileMapping() failed. Error=%d", GetLastError());
    return false;
  }
  pCv = (CcommonValues*)MapViewOfFile(hMapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(CcommonValues));
  if (!pCv) {
    LOG("MapViewOfFile() failed. Error=%d", GetLastError());
    return false;
  }
  pCv->test++;
  pCv->m_multipleMessage = FALSE;

  bReentrantCheck--;
  return true;
}

static void updateImeStatus(HWND hWnd) 
{
#ifdef NO_IMM32
  if (!IsWindow(pCv->m_hWndFocus)) {
    LOG("IsWindow() failed.");
    return;
  }
  HIMC hImc = ImmGetContext(pCv->m_hWndFocus);
  if (hImc) {
    DWORD conv = 0, sent = 0;
    pCv->m_bImeStatus = ImmGetOpenStatus(hImc) ? TRUE : FALSE;
    if (ImmGetConversionStatus(hImc, &conv, &sent)) {
      pCv->m_conversion = conv;
      pCv->m_sentence = sent;
    } else {
      LOG("ImmGetConversionStatus() failed. Error=%08x", GetLastError());
    }
    ImmReleaseContext(pCv->m_hWndFocus, hImc);
  } else {
    DWORD error = GetLastError();
    if (error) {
      LOG("ImmGetContext() failed. Error=%08x", GetLastError());
    } else {
      pCv->m_bImeStatus = FALSE;
    }
  }
#endif // NO_IMM32
}

static bool tsfInitComplete()
{
    return pCv->m_supportTsfOpenClose && pCv->m_supportTsfConversion && pCv->m_supportTsfSentence; //&& pCv->m_supportTsfComposition;
}

static VOID CALLBACK TimerProc(HWND hwnd,UINT message, UINT iTimerID, DWORD dwTime)
{
  if (GetFocus() == pCv->m_hWndFocus) {
    if (setupThreadTsfValues(pCv) && tsfInitComplete()) {
      uninstallWatcher();
    }
  } else {
    uninstallWatcher();
  }
}

void IncrementDllRefCount()
{
  static BOOL fDone = FALSE;

  if (!fDone)
  {
    TCHAR szPath[MAX_PATH];
    if (GetModuleFileName(siriusHookModule, szPath, MAX_PATH))
    {
      LoadLibrary(szPath);
      fDone = TRUE;
    }
  }
}

static void messageHookCore(HWND hWnd, UINT message, WPARAM wParam, LPARAM lParam)
{

  if (message == WM_SETFOCUS && hWnd == pCv->m_hWndFocus) {
    if (GetModuleFileName(NULL, pCv->m_moduleName, MAX_PATH - 1)) {
      pCv->m_moduleName[MAX_PATH - 1] = '\0';
    } else {
      wcscpy_s(pCv->m_moduleName, MAX_PATH, L"[ERROR]");
    }
#ifdef _WIN64
    pCv->m_x64 = 1;
#else
    pCv->m_x64 = 0;
#endif
    pCv->m_processId = GetCurrentProcessId();
    pCv->m_threadId = GetCurrentThreadId();
    if (!setupThreadTsfValues(pCv)) {
      updateImeStatus(hWnd);
    } else if (!tsfInitComplete()) {
      installWatcher(1000, (TIMERPROC)TimerProc);
    }
  }
  if (bReentrantCheck) return;
  if (pCv->m_multipleMessage) return;
  pCv->m_multipleMessage = TRUE;

  switch (message) {
  case WM_ENTERMENULOOP:
    pCv->m_menuLoop = TRUE;
    break;
  case WM_EXITMENULOOP:
    pCv->m_menuLoop = FALSE;
    break;
  case WM_IME_NOTIFY:
    if (GetWindowThreadProcessId(pCv->m_hWndFocus, NULL) == GetWindowThreadProcessId(hWnd, NULL)) {
      switch (wParam) {
      case IMN_SETOPENSTATUS:
      case IMN_SETSENTENCEMODE:
      case IMN_SETCONVERSIONMODE:
        updateImeStatus(hWnd);
      }
    }
    break;
  case WM_IME_SETCONTEXT:
    if (GetWindowThreadProcessId(pCv->m_hWndFocus, NULL) == GetWindowThreadProcessId(hWnd, NULL)) {
      updateImeStatus(hWnd);
    }
    break;
  case WM_IME_COMPOSITION:
      if (lParam & GCS_RESULTSTR) {
          clearNatureBuffer();
      }
      break;
  default:
    if (message == pCv->m_wm_sirius_controll) {
          if ((cmd)wParam != e_null) doCommand((cmd)wParam, lParam);
          pCv->m_commandComplete = 1;
      }
    break;
  }
  pCv->m_multipleMessage = FALSE;
}

static LRESULT CALLBACK CallWndRetProc(int nCode, WPARAM wParam, LPARAM lParam) {
  if(nCode < 0 || nCode != HC_ACTION || !m_isHooked)
    return ::CallNextHookEx(hSendMessageHook, nCode, wParam, lParam);
  CWPRETSTRUCT* pmsg = (CWPRETSTRUCT*)lParam;
  if(allocateCommonValue(pmsg->message)){
	messageHookCore(pmsg->hwnd, pmsg->message, pmsg->wParam, pmsg->lParam);
  }
  return ::CallNextHookEx(hSendMessageHook, nCode, wParam, lParam);
}


static LRESULT CALLBACK CallWndProc(int nCode, WPARAM wParam, LPARAM lParam) {
  if(nCode < 0 || nCode != HC_ACTION || !m_isHooked)
    return ::CallNextHookEx(hGetMessageHook, nCode, wParam, lParam);
  CWPSTRUCT* pmsg = (CWPSTRUCT*)lParam;
  switch (pmsg->message) {
  case WM_SETFOCUS:
		if(allocateCommonValue()){
			if (GetForegroundWindow() == pmsg->hwnd || IsChild(GetForegroundWindow(), pmsg->hwnd)) {
				pCv->m_hWndFocus = pmsg->hwnd;
			}
		}
		break;
  case WM_KILLFOCUS:
		allocateCommonValue();
		releaseThreadTsfValues();
		break;
  }
  return ::CallNextHookEx(hGetMessageHook, nCode, wParam, lParam);
}

static LRESULT CALLBACK SysGetMsgProc(int nCode, WPARAM wParam, LPARAM lParam) {
  if (nCode == HC_ACTION && (wParam & PM_REMOVE) && m_isHooked)
  {
    MSG &msg = (*(MSG *)lParam);

    switch (msg.message)
    {
		case WM_KEYUP:
		case WM_KEYDOWN:
		case WM_SYSKEYDOWN:
		case WM_SYSKEYUP:
		    if(allocateCommonValue()){
                TsfText::Init();
                IncrementDllRefCount();
            }
          break;
    }
  }
  return CallNextHookEx(hSysGetMsgHook, nCode, wParam, lParam);
}

static void ping(WPARAM wParam, LPARAM lParam)
{
    ::SendMessage(pCv->m_hWndFront, WM_SIRIUS_PING, wParam, lParam);
}

void SiriusReleaseHook()
{
  if (!pCv) return;
  m_isHooked = false;	// フックが外れる前にフックルーチンに飛び込む可能性があるので、このフラグを先に落とす。
  pCv->m_isHooked = m_isHooked;

  if (hSysGetMsgHook) {
    UnhookWindowsHookEx(hSysGetMsgHook);
    hSysGetMsgHook = NULL;
  }
  if (hGetMessageHook) {
    UnhookWindowsHookEx(hGetMessageHook);
    hGetMessageHook = NULL;
  }
  if (hSendMessageHook) {
    UnhookWindowsHookEx(hSendMessageHook);
    hSendMessageHook = NULL;
  }

  UnmapViewOfFile(pCv);
  pCv = NULL;
  CloseHandle(hMapping);
  hMapping = NULL;
  DWORD dwResult;
  SendMessageTimeout(HWND_BROADCAST, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, 5000, (PDWORD_PTR)&dwResult);
}


CcommonValues* SiriusSetupHook(DWORD dwMessageId)
{
  if(allocateCommonValue() != true){
	  return NULL;
  }

  if (dwMessageId) {
    ::memset(pCv, 0, sizeof(CcommonValues));
    pCv->m_wm_sirius_controll = dwMessageId;
    pCv->test = 123456;
  }
  hSendMessageHook = SetWindowsHookEx(WH_CALLWNDPROCRET, (HOOKPROC)CallWndRetProc, siriusHookModule, 0);
  if (!hSendMessageHook) {
    LOG("SetWindowsHookEx() failed. Error=%d", GetLastError());
    SiriusReleaseHook();
    return NULL;
  }
  hGetMessageHook = SetWindowsHookEx(WH_CALLWNDPROC, (HOOKPROC)CallWndProc, siriusHookModule, 0);
  if (!hGetMessageHook) {
    LOG("SetWindowsHookEx() failed. Error=%d", GetLastError());
    SiriusReleaseHook();
    return NULL;
  }
  hSysGetMsgHook = SetWindowsHookEx(WH_GETMESSAGE, (HOOKPROC)SysGetMsgProc, siriusHookModule, 0); 
  if (!hSysGetMsgHook) {
    LOG("SetWindowsHookEx(WH_GETMESSAGE) failed. Error=%d", GetLastError());
    SiriusReleaseHook();
    return NULL;
  } 

  m_isHooked = true;	// hook中であることを示す。
  pCv->m_isHooked = m_isHooked;

  return pCv;
}

CcommonValues* SiriusCommonValue()
{
  allocateCommonValue();
  return pCv;
}

// kasane: ホスト側が構造体のフィールドの位置を DLL から得る（C# 側で構造体を再定義しない）。
// 値は DLL 自身の offsetof なので、構造体を変えても、ホストは再ビルドなしで合う。
void SiriusGetLayout(SiriusLayout* out)
{
  if (!out) return;
  out->cbSize = sizeof(CcommonValues);
  out->version = SIRIUS_LAYOUT_VERSION;
  out->hWndFocus = (DWORD)offsetof(CcommonValues, m_hWndFocus);
  out->hWndFront = (DWORD)offsetof(CcommonValues, m_hWndFront);
  out->conversion = (DWORD)offsetof(CcommonValues, m_conversion);
  out->imeStatus = (DWORD)offsetof(CcommonValues, m_bImeStatus);
  out->supportTsf = (DWORD)offsetof(CcommonValues, m_supportTsf);
  out->supportTsfOpenClose = (DWORD)offsetof(CcommonValues, m_supportTsfOpenClose);
  out->supportTsfConversion = (DWORD)offsetof(CcommonValues, m_supportTsfConversion);
  out->processId = (DWORD)offsetof(CcommonValues, m_processId);
  out->isHooked = (DWORD)offsetof(CcommonValues, m_isHooked);
}
