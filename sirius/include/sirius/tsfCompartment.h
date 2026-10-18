// 天狼（sirius）: MIT License, Copyright (C) 2008-2010, MATSUMOTO Reiji
#pragma once
#include <msctf.h>
#include <sirius/siriusLogger.h>

// clas tsfCompartment a.k.a tsfValue at sirius original

class tsfCompartment : public ITfCompartmentEventSink {
protected:
  bool m_bEffective;
  DWORD m_dwRefCount;
  DWORD m_dwCookie;
  DWORD* m_pAddr;
  wchar_t* m_name;
  ITfCompartment* m_iComp;
  virtual ~tsfCompartment() {
    if (m_iComp) {
      m_iComp->Release();
      m_iComp = NULL;
    }
  }
public:
  tsfCompartment(ITfThreadMgr* iTMgr, const GUID& guid, wchar_t* name, DWORD* pAddr) {
    m_dwRefCount = 1;
    m_bEffective = false;
    ITfCompartmentMgr* iCMgr = NULL;
    m_iComp = NULL;
    ITfSource* iSource = NULL;
    m_pAddr = pAddr;
    *m_pAddr = 0;
    m_name = name;

    VARIANT v;
    HRESULT hr = S_OK;
    do {
      if (!SUCCEEDED(hr = iTMgr->QueryInterface(IID_ITfCompartmentMgr, (void**)&iCMgr))) {
        break;
      }
      if (!SUCCEEDED(hr = iCMgr->GetCompartment(guid, &m_iComp))) {
        break;
      }
      if (!SUCCEEDED(hr = m_iComp->GetValue(&v))) {
        break;
      }
      if (v.vt == VT_EMPTY) {
        break;
      }
      *m_pAddr = v.ulVal;
      if (!SUCCEEDED(hr = m_iComp->QueryInterface(&iSource))) {
        break;
      }
      if (!SUCCEEDED(hr = iSource->AdviseSink(IID_ITfCompartmentEventSink, this, &m_dwCookie))){
        break;
      }
      m_bEffective = true;
    } while (0);
    if (iSource) iSource->Release();
    if (!m_bEffective && m_iComp) {
      m_iComp->Release();
      m_iComp = NULL;
    }
    if (iCMgr) iCMgr->Release();
  }

  bool effective() {
    return m_bEffective;
  }

  bool update() {
    bool result = false;
    HRESULT hr;
    VARIANT v;
    do {
      if (!m_bEffective || !m_iComp) {
        LOG("'%S': Not effective but call update(). m_bEffective=%d, m_iComp=%08x", m_name, m_bEffective, m_iComp);
        break;
      }
      if (!SUCCEEDED(hr = m_iComp->GetValue(&v))) {
        LOG("'%S': could not succeeded GetValue(). result = %08x", m_name, hr);
        break;
      }
      if (v.vt == VT_EMPTY) {
        LOG("'%S': is empty");
        break;
      }
      *m_pAddr = v.ulVal;
      result = true;
    } while (0);
    return result;
  }

  //IUnknown methods
  STDMETHODIMP QueryInterface(REFIID riid, LPVOID *ppReturn) {
    if (ppReturn == NULL)
      return E_INVALIDARG;
   *ppReturn = NULL;
    if(IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_ITfCompartmentEventSink)) {
        *ppReturn = (ITfCompartmentEventSink*)this;
        AddRef();
        return S_OK;
    }
    return E_NOINTERFACE;
  }
  STDMETHOD_ (DWORD, AddRef)() {
    return ++m_dwRefCount;
  }
  STDMETHOD_ (DWORD, Release)() {
    m_dwRefCount--;
    if (m_iComp) {
      if (m_dwRefCount == 1) {
        HRESULT hr = S_OK;
        ITfSource* iSource = NULL;
        if (SUCCEEDED(hr = m_iComp->QueryInterface(&iSource))) {
          if (SUCCEEDED(hr = iSource->UnadviseSink(m_dwCookie))) {
            ;
          } else {
            ;
          }
          iSource->Release();
        } else {
          ;
        }
        delete this;
        return 0;
      }
    } else {
      if(m_dwRefCount == 0) {
        delete this;
        return 0;
      }
    }
    return m_dwRefCount;
  }

  // ITfCompartmentEventSink methods
  STDMETHODIMP OnChange(REFGUID rguid) {
    if (m_pAddr && m_bEffective && m_iComp) {
      VARIANT v;
      HRESULT hr;
      if (SUCCEEDED(hr = m_iComp->GetValue(&v))) {
        if (v.vt != VT_EMPTY) {
          *m_pAddr = v.ulVal;
        }
      }
    }
    return S_OK;
  }
};