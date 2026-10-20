// 天狼（sirius）: MIT License, Copyright (C) 2008-2010, MATSUMOTO Reiji
#pragma once
#include <msctf.h>

// class TsfEdit a.k.a TsfComposition at sirius original

class TsfEdit : public ITfEditSession
{
public:
  typedef HRESULT (WINAPI *PTF_GETTHREADMGR)(ITfThreadMgr**);
protected:
  DWORD m_refCount;
  ITfContext* m_iContext;
  IID m_cls;
  wchar_t* m_outBuf;
  ULONG  m_outBufLen;
  PTF_GETTHREADMGR GetThreadMgr;
  ULONG* m_statusBuf;

  static void log(char* fmt, ...) {
    char buf[1000];
    va_list argp;
    va_start(argp, fmt);
    vsnprintf_s(buf, 1000, 1000, fmt, argp);
    va_end(argp);  
    strcat_s(buf, 1000, "\n");
    OutputDebugStringA(buf);
  }
  static void log(wchar_t* fmt, ...) {
    wchar_t buf[1000];
    va_list argp;
    va_start(argp, fmt);
    _vsnwprintf_s(buf, 1000, 1000, fmt, argp);
    va_end(argp); 
    wcscat_s(buf, 1000, L"\n");
    OutputDebugString(buf);
  }


public:
  TsfEdit(PTF_GETTHREADMGR func) {
    static const GUID _cls = { 0xc992be2d, 0x28c1, 0x4578, { 0x8b, 0x58, 0xd7, 0x65, 0x4c, 0xcc, 0x24, 0xd7 } };
    m_cls = _cls;
    m_refCount = 1;
    GetThreadMgr = func;
  }

  //IUnknown methods
  STDMETHODIMP QueryInterface(REFIID riid, LPVOID *ppReturn) {
    if (ppReturn == NULL)
      return E_INVALIDARG;
    *ppReturn = NULL;
    if(IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_ITfEditSession)) {
        *ppReturn = (ITfEditSession*)this;
        AddRef();
        return S_OK;
    }
    return E_NOINTERFACE;
  }
  STDMETHOD_ (DWORD, AddRef)() {
    return ++m_refCount;
  }
  STDMETHOD_ (DWORD, Release)() {
    if(--m_refCount == 0) {
        delete this;
        return 0;
    }
    return m_refCount;
  }

  // ITfEditSession methods
  STDMETHODIMP DoEditSession(/* [in] */ TfEditCookie ec) {
    HRESULT hr;
    const GUID* rGuidProperties[1];
    ITfReadOnlyProperty* pTrackProperty = NULL;
    ITfRange* start = NULL;
    ITfRange* end = NULL;
    IEnumTfRanges* enumRanges = NULL;

    do {
      rGuidProperties[0] = &GUID_PROP_COMPOSING;
      if (!SUCCEEDED(hr = m_iContext->TrackProperties(rGuidProperties, 1, NULL, 0, &pTrackProperty))) {
        log("e6\n");
        break;
      }
      if (!SUCCEEDED(hr = m_iContext->GetStart(ec, &start))) {
        log("e7\n");
        break;
      }
      if (!SUCCEEDED(hr = m_iContext->GetEnd(ec, &end))) {
        log("e8\n");
        break;
      }
      if (!SUCCEEDED(hr = start->ShiftEndToRange(ec, end, TF_ANCHOR_END))) {
        log("e9a\n");
        break;
      }
      if (!SUCCEEDED(hr = pTrackProperty->EnumRanges(ec, &enumRanges, start))) {
        log("e10\n");
        break;
      }

      ITfRange* propRange = NULL;
      ULONG uFetched = 0;
      while((hr = enumRanges->Next(1, &propRange, &uFetched)) == S_OK && uFetched) {
        VARIANT var;
        VariantInit(&var);
        if (SUCCEEDED(hr = pTrackProperty->GetValue(ec, propRange, &var))) {
          wchar_t buf1[101];
          ULONG len = 0;
          propRange->GetText(ec, 0, buf1, 100, &len);
          buf1[len] = 0;
          IEnumTfPropertyValue* enumPropertyVal;
          if (SUCCEEDED(hr = var.punkVal->QueryInterface(IID_IEnumTfPropertyValue, (LPVOID*)&enumPropertyVal))) {
             TF_PROPERTYVAL tfPropVal;
             while((hr = enumPropertyVal->Next(1, &tfPropVal, &uFetched)) == S_OK && uFetched) {
                if(tfPropVal.varValue.vt == VT_I4 && tfPropVal.varValue.lVal) {
                  ULONG len = 0;
                  propRange->GetText(ec, 0, m_outBuf, m_outBufLen - 1, &len);
                  m_outBuf[len] = 0;
                  hr = S_OK;
                  enumPropertyVal->Release();
                  propRange->Release();
                  goto ESC;
                }
             }
             enumPropertyVal->Release();
          }
          propRange->Release();
        }
      }
    }while (0);
    hr = S_FALSE;
ESC:
    *m_statusBuf = 0;
    if (enumRanges) enumRanges->Release();
    if (end) end->Release();
    if (start) start->Release();
    if (pTrackProperty) pTrackProperty->Release();
    return hr;
  }

  HRESULT go(wchar_t* outBuf, ULONG outBufLen, ULONG* statusBuf) {
    m_statusBuf = statusBuf;
    *m_statusBuf = 0;
    HRESULT result = E_FAIL;
    m_outBuf = outBuf;
    m_outBufLen = outBufLen;
    m_outBuf[0] = 0;
    ITfThreadMgr* iTMgr = NULL;
    ITfDocumentMgr* iDMgr = NULL;
    m_iContext = NULL;
    ITfClientId* iCid = NULL;
    TfClientId cid;
    do {
      if (!SUCCEEDED(GetThreadMgr(&iTMgr)) || !iTMgr) {
        log("e1\n");
        break;
      }
      if (!SUCCEEDED(iTMgr->GetFocus(&iDMgr)) || !iDMgr) {
        log("e2\n");
        break;
      }
      if (!SUCCEEDED(iDMgr->GetBase(&m_iContext))) {
        log("e3\n");
        break;
      }
      if (!SUCCEEDED(iTMgr->QueryInterface(&iCid))) {
        log("e4\n");
        break;
      }
      if (!SUCCEEDED(iCid->GetClientId(m_cls, &cid))) {
        log("e5\n");
        break;
      }
      *m_statusBuf = 1;
      m_iContext->RequestEditSession(cid, this, TF_ES_READ, &result);
    } while (0);
    if (iCid) iCid->Release();
    if (m_iContext) m_iContext->Release();
    if (iDMgr) iDMgr->Release();
    if (iTMgr) iTMgr->Release();
    return result;
  }

};
