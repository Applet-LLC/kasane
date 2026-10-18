// 天狼（sirius）: MIT License, Copyright (C) 2008-2010, MATSUMOTO Reiji
#pragma once
#include <stdio.h>
#include <stdarg.h>
#include <string>
#include <vector>
#include <algorithm>
#include <initguid.h>
#include <Shlobj.h>
#include <stdlib.h>

static HANDLE hSimpleLog = INVALID_HANDLE_VALUE;
static SYSTEMTIME stSimpleLogStartDay = {0,};
static bool opend = false;

bool SimpleLoggerFileName(wchar_t* szPath, size_t nMaxLogs, SYSTEMTIME* pSt)
{
    IMalloc *pMalloc;
    LPITEMIDLIST pIdl;
    szPath[0] = '\0';

    if(!SHGetMalloc(&pMalloc))
		return false;

    if (!SUCCEEDED(SHGetSpecialFolderLocation(::GetDesktopWindow(), CSIDL_APPDATA, &pIdl))) { 
        pMalloc->Release();
        return false;
    }
    if (!SHGetPathFromIDList(pIdl, szPath)) {
        pMalloc->Release();
        return false;
    }
    wcscat_s(szPath, MAX_PATH, L"\\" SIMPLE_LOG_APP_NAME L"\\logs");
    size_t nDirLen = wcslen(szPath);

    DWORD dwResult = GetFileAttributes(szPath);
    if (dwResult == 0xffffffff || (dwResult & FILE_ATTRIBUTE_DIRECTORY) == 0) {
      return false;
      /*
      if (!CreateDirectory(szPath, NULL)) {
          pMalloc->Free(pIdl);
          return false;
      }
      */
    }
    WIN32_FIND_DATA fd;
    HANDLE hFind;
    std::vector<std::wstring> files;

    wcscat_s(szPath, MAX_PATH, L"\\" SIMPLE_LOG_PREFIX L"*.log");
    hFind = FindFirstFile(szPath, &fd);
    if (hFind != INVALID_HANDLE_VALUE) {
      do {
        files.push_back(fd.cFileName);
      } while (FindNextFile(hFind, &fd));
    }
    FindClose(hFind);

    wchar_t wFname[MAX_PATH];
    swprintf_s(wFname, L"\\" SIMPLE_LOG_PREFIX L"%04d%02d%02d.log", pSt->wYear, pSt->wMonth, pSt->wDay);

    if (files.size() > nMaxLogs) {
      std::sort(files.begin(), files.end());
      for (size_t i = 0; i < files.size() - nMaxLogs; i++) {
        if (files[i] != wFname) {
          szPath[nDirLen] = '\0';
          wcscat_s(szPath, MAX_PATH, files[i].c_str());
          DeleteFile(szPath);
        }
      }
    }
    szPath[nDirLen] = '\0';
    wcscat_s(szPath, MAX_PATH, wFname);

    pMalloc->Free(pIdl);
    return true;
}

bool SimpleLoggerOpen() 
{
  SYSTEMTIME	st;
  GetLocalTime(&st);
  opend = false;
  wchar_t wFname[MAX_PATH];
  HANDLE hMutex = CreateMutex(NULL, FALSE, SIMPLE_LOG_APP_NAME L"-70DFF7D6-1DC6-4b48-9903-CDA18892CEF7");
  if (SimpleLoggerFileName(wFname, SIMPLE_LOG_MAX_LOGS, &st)) {
    hSimpleLog = CreateFile(wFname, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    opend = true;
  }
  ReleaseMutex(hMutex);
  CloseHandle(hMutex);
  return opend;
}


void SimpleLogger(char* fn, int line, char* fmt, ...) {
  if (!opend) return;

  char buf[1000];
  va_list argp;
  va_start(argp, fmt);
  vsnprintf_s(buf, 1000, 1000, fmt, argp);
  va_end(argp);  
  strcat_s(buf, 1000, "\n");

  SYSTEMTIME	st;
  GetLocalTime(&st);

  HANDLE hMutex = CreateMutex(NULL, FALSE, SIMPLE_LOG_APP_NAME L"-70DFF7D6-1DC6-4b48-9903-CDA18892CEF7");
  WaitForSingleObject(hMutex , INFINITE);

  char buf2[1000];
  char modname[MAX_PATH];
  GetModuleFileNameA(NULL, modname, MAX_PATH);

  if (st.wYear != stSimpleLogStartDay.wYear
    || st.wMonth != stSimpleLogStartDay.wMonth || st.wDay != stSimpleLogStartDay.wDay) {
      CloseHandle(hSimpleLog);
      wchar_t wFname[MAX_PATH];
      if (!SimpleLoggerFileName(wFname, SIMPLE_LOG_MAX_LOGS, &st)) return;
      hSimpleLog = CreateFile(wFname, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
      if (hSimpleLog == INVALID_HANDLE_VALUE) return;
      stSimpleLogStartDay = st;
  }
  sprintf_s(buf2, 1000, "%02d:%02d:%02d.%03d\t%08x:%08x\t%d:%d\t%d\t%s\t%s(%d): %s",st.wHour,st.wMinute,st.wSecond,st.wMilliseconds,
    ::GetCurrentProcessId(), ::GetCurrentThreadId(), ::GetCurrentProcessId(), ::GetCurrentThreadId(), (int)(INT_PTR)hSimpleLog, modname, fn, line, buf);

  DWORD wsize;
  OVERLAPPED ol = {NULL};
  ol.hEvent = NULL;
  ol.Offset = 0;
  ol.OffsetHigh = 0;
  ::SetFilePointer(hSimpleLog, 0, 0, FILE_END);
  ::WriteFile(hSimpleLog, buf2, (DWORD)strlen(buf2), &wsize, NULL);
  ReleaseMutex(hMutex);
  CloseHandle(hMutex);
}

void SimpleLoggerClose() {
	if(opend == true){
		CloseHandle(hSimpleLog);
		hSimpleLog = INVALID_HANDLE_VALUE;
		opend = false;
	}
}