// 天狼（sirius）: MIT License, Copyright (C) 2008-2010, MATSUMOTO Reiji
#pragma once
#include "sirius_hook.h"
#include <sirius/tsfText.h>

class TsfText;

class TLS
{
private:
  static DWORD dwTlsIndex;

public:
  class TsfText *_pTsfText;

public:
  static inline BOOL Initialize()
  {
    dwTlsIndex = TlsAlloc();
    if (dwTlsIndex == TLS_OUT_OF_INDEXES)
      return FALSE;
    return TRUE;
  }

  static inline void Uninitialize()
  {
    if (dwTlsIndex != TLS_OUT_OF_INDEXES)
    {
      TlsFree(dwTlsIndex);
      dwTlsIndex = TLS_OUT_OF_INDEXES;
    }
  }

  static inline TLS* GetTLS()
  {
    return InternalAllocateTLS();
  }

  static inline TLS* ReferenceTLS()
  {
    return (TLS*)TlsGetValue(dwTlsIndex);
  }

  static inline BOOL DestroyTLS()
  {
    return InternalDestroyTLS();
  }

private:
  static inline TLS* InternalAllocateTLS()
  {
    TLS* pTLS = (TLS*)TlsGetValue(dwTlsIndex);
    if (pTLS == NULL)
    {
      if ((pTLS = (TLS*)LocalAlloc(LPTR, sizeof(TLS))) == NULL)
        return NULL;
      if (!TlsSetValue(dwTlsIndex, pTLS))
      {
        LocalFree(pTLS);
        return NULL;
      }

      // Init value
      pTLS->_pTsfText = NULL;
    }
    return pTLS;
  }

  static BOOL InternalDestroyTLS()
  {
    if (dwTlsIndex == TLS_OUT_OF_INDEXES)
      return FALSE;

    TLS* pTLS = (TLS*)TlsGetValue(dwTlsIndex);
    if (pTLS != NULL)
    {
      if (pTLS->_pTsfText)
      {
        pTLS->_pTsfText->Release();
        pTLS->_pTsfText = NULL;
      }

      LocalFree(pTLS);
      TlsSetValue(dwTlsIndex, NULL);
      return TRUE;
    }
    return FALSE;
  }

};
