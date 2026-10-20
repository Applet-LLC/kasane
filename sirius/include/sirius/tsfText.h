// 天狼（sirius）: MIT License, Copyright (C) 2008-2010, MATSUMOTO Reiji
#pragma once

class TsfText : public ITfThreadMgrEventSink, public ITfTextEditSink
{
public:
  TsfText();
  ~TsfText();

  bool effective();
  BOOL m_support_composition_now();
  BOOL m_composition_now();

  //IUnknown methods
  STDMETHODIMP QueryInterface(REFIID riid, LPVOID *ppReturn);
  STDMETHODIMP_(ULONG) AddRef();
  STDMETHODIMP_(ULONG) Release();

  // ITfThreadMgrEventSink
  STDMETHODIMP OnInitDocumentMgr(ITfDocumentMgr *pDocMgr);
  STDMETHODIMP OnUninitDocumentMgr(ITfDocumentMgr *pDocMgr);
  STDMETHODIMP OnSetFocus(ITfDocumentMgr *pDocMgrFocus, ITfDocumentMgr *pDocMgrPrevFocus);
  STDMETHODIMP OnPushContext(ITfContext *pContext);
  STDMETHODIMP OnPopContext(ITfContext *pContext);

  // ITfTextEditSink
  STDMETHODIMP OnEndEdit(ITfContext *pContext, TfEditCookie ecReadOnly, ITfEditRecord *pEditRecord);
 
  static BOOL Init();
  BOOL _InitThreadMgrSink();
  void _UninitThreadMgrSink();
  BOOL _InitTextEditSink(ITfDocumentMgr *pDocMgr);
  void _AppendCompositionText(ITfRange *pRange, TfEditCookie ecReadOnly);
  void _ClearCompositionText();
  BOOL _CheckComposition(ITfContext *pContext, TfEditCookie ecReadOnly);

  typedef HRESULT (WINAPI *PTF_GETTHREADMGR)(ITfThreadMgr**);
  BOOL m_composition;
  BOOL m_supportComposition;
  bool m_bEffective;
   
protected:
  DWORD m_dwRefCount;
  DWORD _dwThreadMgrEventSinkCookie;
  DWORD _dwTextEditSinkCookie;
  ITfContext *_pTextEditSinkContext;
  ITfThreadMgr* iTMgr;

  WCHAR *_pszCompositionText;
  ULONG _uCompositionText;

  wchar_t* m_outBuf;
  ULONG  m_outBufLen;
  ULONG* m_statusBuf;
};
