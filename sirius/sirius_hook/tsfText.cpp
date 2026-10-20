// 天狼（sirius）: MIT License, Copyright (C) 2008-2010, MATSUMOTO Reiji
#include <msctf.h>
#include "sirius_hook.h"
#include "tlsDeclaration.h"
#include "tsfValues.h"
#include <sirius/tsfText.h>

// class TsfText

TsfText::TsfText() {
    m_dwRefCount = 1;

    _dwThreadMgrEventSinkCookie = TF_INVALID_COOKIE;
    _dwTextEditSinkCookie = TF_INVALID_COOKIE;
    _pTextEditSinkContext = NULL;
    _pszCompositionText = NULL;
    _uCompositionText = 0;
    m_bEffective = false;
    m_supportComposition = false;
    m_composition = false;
  }

TsfText::~TsfText() {
}

  bool TsfText::effective() {
    return m_bEffective;
  }

  BOOL TsfText::m_support_composition_now() {
      return m_supportComposition;
  }

  BOOL TsfText::m_composition_now() {
      return m_composition;
  }

  //IUnknown methods
  STDMETHODIMP TsfText::QueryInterface(REFIID riid, LPVOID *ppReturn) {
    if (ppReturn == NULL)
      return E_INVALIDARG;
    *ppReturn = NULL;
    if(IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_ITfTextEditSink)) {
      *ppReturn = (ITfTextEditSink *)this;
    } else if(IsEqualIID(riid, IID_ITfThreadMgrEventSink)) {
      *ppReturn = (ITfThreadMgrEventSink *)this;
    }

    if(*ppReturn) {
      AddRef();
      return S_OK;
    }

    return E_NOINTERFACE;
  }

  STDMETHODIMP_(ULONG) TsfText::AddRef() {
    return ++m_dwRefCount;
  }

  STDMETHODIMP_(ULONG) TsfText::Release() {
    if(--m_dwRefCount == 0) {
        delete this;
        return 0;
    }
    return m_dwRefCount;
  }

  BOOL TsfText::Init() {
    TLS *pTLS = TLS::GetTLS();

    if (!pTLS)
      return FALSE;

    if (pTLS->_pTsfText)
      return TRUE;

    pTLS->_pTsfText = new TsfText;
    if (!pTLS->_pTsfText)
      return FALSE;

    ITfThreadMgr* iTMgr;
    HRESULT hr;

    hr = CoCreateInstance(CLSID_TF_ThreadMgr, 
                          NULL, 
                          CLSCTX_INPROC_SERVER, 
                          IID_ITfThreadMgr, 
                          (void**)&iTMgr);

    if (hr != S_OK)
    {
      pTLS->_pTsfText->Release();
      pTLS->_pTsfText = NULL;
      return FALSE;
    }

    pTLS->_pTsfText->iTMgr = iTMgr;
    pTLS->_pTsfText->_InitThreadMgrSink();

    ITfDocumentMgr *pDocMgr;
    if (SUCCEEDED(iTMgr->GetFocus(&pDocMgr)))
    {
      if (pDocMgr)
      {
        pTLS->_pTsfText->_InitTextEditSink(pDocMgr);
        pDocMgr->Release();
      }
    }
    return TRUE;
}

  // ITfThreadMgrEventSink
  STDMETHODIMP TsfText::OnInitDocumentMgr(ITfDocumentMgr *pDocMgr) {
    return S_OK;
  }

  STDMETHODIMP TsfText::OnUninitDocumentMgr(ITfDocumentMgr *pDocMgr) {
    return S_OK;
  }

  STDMETHODIMP TsfText::OnSetFocus(ITfDocumentMgr *pDocMgrFocus, ITfDocumentMgr *pDocMgrPrevFocus) {
    CcommonValues* pCv;
    pCv = SiriusCommonValue();
    FlushViewOfFile(pCv, 0);

    if (pCv->m_isHooked)
      _InitTextEditSink(pDocMgrFocus);
    return S_OK;
  }

  STDMETHODIMP TsfText::OnPushContext(ITfContext *pContext) {
    return S_OK;
  }

  STDMETHODIMP TsfText::OnPopContext(ITfContext *pContext)  {
    return S_OK;
  }

  // ITfTextEditSink
  STDMETHODIMP TsfText::OnEndEdit(ITfContext *pContext, TfEditCookie ecReadOnly, ITfEditRecord *pEditRecord) {

    CcommonValues* pCv;
    pCv = SiriusCommonValue();
    FlushViewOfFile(pCv, 0);

    if (pCv->m_isHooked){
      _CheckComposition(pContext, ecReadOnly);
    }
    return S_OK;
}

  BOOL TsfText:: _InitThreadMgrSink() {
    ITfSource *pSource;
    BOOL fRet;

    if (iTMgr == NULL)
        return FALSE;

    if (iTMgr->QueryInterface(IID_ITfSource, (void **)&pSource) != S_OK)
        return FALSE;

    fRet = FALSE;

    if (pSource->AdviseSink(IID_ITfThreadMgrEventSink, (ITfThreadMgrEventSink *)this, &_dwThreadMgrEventSinkCookie) != S_OK)
    {
        _dwThreadMgrEventSinkCookie = TF_INVALID_COOKIE;
        goto Exit;
    }

    fRet = TRUE;

Exit:
    pSource->Release();
    return fRet;
  }

  void TsfText::_UninitThreadMgrSink() {
    ITfSource *pSource;

    if (_dwThreadMgrEventSinkCookie == TF_INVALID_COOKIE)
        return;

    if (iTMgr == NULL)
        return;

    if (iTMgr->QueryInterface(IID_ITfSource, (void **)&pSource) == S_OK)
    {
        pSource->UnadviseSink(_dwThreadMgrEventSinkCookie);
        pSource->Release();
    }

    _dwThreadMgrEventSinkCookie = TF_INVALID_COOKIE;
  }

  BOOL TsfText::_InitTextEditSink(ITfDocumentMgr *pDocMgr) {
    ITfSource *pSource;
    BOOL fRet;

    if (_dwTextEditSinkCookie != TF_INVALID_COOKIE)
    {
        if (_pTextEditSinkContext->QueryInterface(IID_ITfSource, (void **)&pSource) == S_OK)
        {
            pSource->UnadviseSink(_dwTextEditSinkCookie);
            pSource->Release();
        }

        _pTextEditSinkContext->Release();
        _pTextEditSinkContext = NULL;
        _dwTextEditSinkCookie = TF_INVALID_COOKIE;
    }

    if (pDocMgr == NULL)
    {
        return TRUE;
    }
    if (pDocMgr->GetTop(&_pTextEditSinkContext) != S_OK)
    {
        return FALSE;
    }
    if (_pTextEditSinkContext == NULL)
    {
        return TRUE;
    }

    fRet = FALSE;

    if (_pTextEditSinkContext->QueryInterface(IID_ITfSource, (void **)&pSource) == S_OK)
    {
        if (pSource->AdviseSink(IID_ITfTextEditSink, (ITfTextEditSink *)this, &_dwTextEditSinkCookie) == S_OK)
        {
            fRet = TRUE;
        }
        else
        {
            _dwTextEditSinkCookie = TF_INVALID_COOKIE;
        }
        pSource->Release();
    }

    if (fRet == FALSE)
    {
        _pTextEditSinkContext->Release();
        _pTextEditSinkContext = NULL;
    }

    return fRet;
  }


  void TsfText::_AppendCompositionText(ITfRange *pRange, TfEditCookie ecReadOnly) {
    BOOL fEmpty;
    while (pRange->IsEmpty(ecReadOnly, &fEmpty) == S_OK && !fEmpty)
    {
        WCHAR *pwstr;
        WCHAR wstr[256 + 1];
        ULONG ulcch = ARRAYSIZE(wstr) - 1;
        ULONG ulcchTotal;
        pRange->GetText(ecReadOnly, TF_TF_MOVESTART, wstr, ulcch, &ulcch);

        ulcchTotal = _uCompositionText + ulcch;
        if (_pszCompositionText)
            pwstr = (WCHAR *)LocalReAlloc(_pszCompositionText, LPTR, (ulcchTotal + 1) * sizeof(WCHAR));
        else
            pwstr = (WCHAR *)LocalAlloc(LPTR, (ulcchTotal + 1)* sizeof(WCHAR));

        if (pwstr)
        {
            memcpy(pwstr+_uCompositionText, wstr,  ulcch * sizeof(WCHAR));
            _pszCompositionText = pwstr;
            _uCompositionText = ulcchTotal;
        }
    }
  }

  void TsfText::_ClearCompositionText() {
    if (_pszCompositionText)
    {
        LocalFree(_pszCompositionText);
        _pszCompositionText = NULL;
    }
    _uCompositionText = 0;
  }

  BOOL TsfText::_CheckComposition(ITfContext *pContext, TfEditCookie ecReadOnly) {
    HRESULT hr;
    ITfContextComposition *pContextComposition;
    WCHAR *pszSavedCompositionText = NULL;
    UINT  uSavedCompositionText = 0;

    m_supportComposition = false;

    if (_pszCompositionText)
    {
        pszSavedCompositionText = (WCHAR *)LocalAlloc(LPTR, (_uCompositionText + 1) * sizeof(WCHAR));
        if (!pszSavedCompositionText)
            return FALSE;

        memcpy(pszSavedCompositionText, 
               _pszCompositionText,
               _uCompositionText);

       uSavedCompositionText = _uCompositionText;
    }

    _ClearCompositionText();

    hr = pContext->QueryInterface(IID_ITfContextComposition,
                                  (void **)&pContextComposition);
    if (hr == S_OK)
    {
        IEnumITfCompositionView *pEnumCompositionView;
        m_supportComposition = true;

        hr = pContextComposition->EnumCompositions(&pEnumCompositionView);
        if (hr == S_OK)
        {
            ITfCompositionView *pCompositionView;

            while (pEnumCompositionView->Next(1, &pCompositionView, NULL) == S_OK)
            {
                ITfRange *pRange;
                hr = pCompositionView->GetRange(&pRange);
                if (hr == S_OK)
                {
                    _AppendCompositionText(pRange, ecReadOnly);
                    pRange->Release();
                }
                pCompositionView->Release();
            }
            pEnumCompositionView->Release();
        }
        pContextComposition->Release();
    }

    if (!_uCompositionText && _pszCompositionText)
    {
        _pszCompositionText[0] = L'\0';
    }

    if ((uSavedCompositionText != _uCompositionText) ||
        memcmp(pszSavedCompositionText, _pszCompositionText, _uCompositionText * sizeof(WCHAR)))
    {
        if(lstrlen(_pszCompositionText) > 0)
        {
            m_composition = true;
        } else {
            m_composition = false;
        }
    }

    if (pszSavedCompositionText)
        LocalFree(pszSavedCompositionText);

    return TRUE;
  }

