// 天狼（sirius）: MIT License, Copyright (C) 2008-2010, MATSUMOTO Reiji
#include "stdafx.h"
#include <stdio.h>
#include <imm.h>
#include <msctf.h>
#include <map>
#include <sirius/commonValues.h>
#include <sirius/tsfCompartment.h>
#include <sirius/tsfText.h>
#include "tsfValues.h"
#include "sirius_hook.h"
#include <sirius/siriusLogger.h>
#include "sirius_timing.h"

tsfValues::tsfValues(ITfThreadMgr* iTMgr, CcommonValues* pCv) {
  m_openClose = NULL;
  m_comversion = NULL;
  m_sentence = NULL;
  m_TsfComposition = NULL;
  m_timer = 0;
  if (iTMgr) {
    m_openClose = new tsfCompartment(iTMgr, GUID_COMPARTMENT_KEYBOARD_OPENCLOSE, L"GUID_COMPARTMENT_KEYBOARD_OPENCLOSE", &pCv->m_bImeStatus);
    m_comversion = new tsfCompartment(iTMgr, GUID_COMPARTMENT_KEYBOARD_INPUTMODE_CONVERSION, L"GUID_COMPARTMENT_KEYBOARD_INPUTMODE_CONVERSION", &pCv->m_conversion);
    m_sentence = new tsfCompartment(iTMgr, GUID_COMPARTMENT_KEYBOARD_INPUTMODE_SENTENCE, L"GUID_COMPARTMENT_KEYBOARD_INPUTMODE_SENTENCE", &pCv->m_sentence);
    m_TsfComposition = new TsfText();
    m_supportTsf = TRUE;
    m_supportTsfOpenClose = m_openClose->effective() ? TRUE : FALSE;
    m_supportTsfConversion = m_comversion->effective() ? TRUE : FALSE;
    m_supportTsfSentence = m_sentence->effective() ? TRUE : FALSE;
    m_supportTsfComposition = m_TsfComposition->m_support_composition_now() ? TRUE : FALSE;
    m_Composition = m_TsfComposition->m_composition_now() ? TRUE : FALSE;
    m_bEffective = m_TsfComposition->effective() ? TRUE : FALSE;
  } else {
    m_supportTsf = FALSE;
    m_supportTsfOpenClose = FALSE;
    m_supportTsfConversion = FALSE;
    m_supportTsfSentence = FALSE;
    m_supportTsfComposition = FALSE;
    m_Composition = FALSE;
    m_bEffective = FALSE;
  }
  pCv->m_supportTsf = m_supportTsf;
  pCv->m_supportTsfOpenClose = m_supportTsfOpenClose;
  pCv->m_supportTsfConversion = m_supportTsfConversion;
  pCv->m_supportTsfSentence = m_supportTsfSentence;
  pCv->m_supportTsfComposition = m_supportTsfComposition;
  pCv->m_Composition = m_Composition;
  pCv->m_bEffective = m_bEffective;
}

tsfValues::~tsfValues() {
  uninstallWatcher();
  if (m_openClose) m_openClose->Release();
  if (m_comversion) m_comversion->Release();
  if (m_sentence) m_sentence->Release();
  if (m_TsfComposition) m_TsfComposition->Release();
}

bool tsfValues::update(ITfThreadMgr* iTMgr, CcommonValues* pCv)
{
  if (m_openClose) m_openClose->Release();
  if (m_comversion) m_comversion->Release();
  if (m_sentence) m_sentence->Release();
  if (m_TsfComposition) m_TsfComposition->Release();
    m_openClose = NULL;
    m_comversion = NULL;
    m_sentence = NULL;
    m_TsfComposition = NULL;
  if (iTMgr) {
    m_openClose = new tsfCompartment(iTMgr, GUID_COMPARTMENT_KEYBOARD_OPENCLOSE, L"GUID_COMPARTMENT_KEYBOARD_OPENCLOSE", &pCv->m_bImeStatus);
    m_comversion = new tsfCompartment(iTMgr, GUID_COMPARTMENT_KEYBOARD_INPUTMODE_CONVERSION, L"GUID_COMPARTMENT_KEYBOARD_INPUTMODE_CONVERSION", &pCv->m_conversion);
    m_sentence = new tsfCompartment(iTMgr, GUID_COMPARTMENT_KEYBOARD_INPUTMODE_SENTENCE, L"GUID_COMPARTMENT_KEYBOARD_INPUTMODE_SENTENCE", &pCv->m_sentence);
    m_TsfComposition = new TsfText();
    m_supportTsf = TRUE;
    m_supportTsfOpenClose = m_openClose->effective() ? TRUE : FALSE;
    m_supportTsfConversion = m_comversion->effective() ? TRUE : FALSE;
    m_supportTsfSentence = m_sentence->effective() ? TRUE : FALSE;
    m_supportTsfComposition = m_TsfComposition->m_support_composition_now() ? TRUE : FALSE;
    m_Composition = m_TsfComposition->m_composition_now() ? TRUE : FALSE;
    m_bEffective = m_TsfComposition->effective() ? TRUE : FALSE;
  } else {
    m_supportTsf = FALSE;
    m_supportTsfOpenClose = FALSE;
    m_supportTsfConversion = FALSE;
    m_supportTsfSentence = FALSE;
    m_supportTsfComposition = FALSE;
    m_Composition = FALSE;
    m_bEffective = FALSE;
  }
  pCv->m_supportTsf = m_supportTsf;
  pCv->m_supportTsfOpenClose = m_supportTsfOpenClose;
  pCv->m_supportTsfConversion = m_supportTsfConversion;
  pCv->m_supportTsfSentence = m_supportTsfSentence;
  pCv->m_supportTsfComposition = m_supportTsfComposition;
  pCv->m_Composition = m_Composition;
  pCv->m_bEffective = m_bEffective;
  return true;
}

void tsfValues::installWatcher(UINT msec, TIMERPROC proc) {
  m_timer = SetTimer(NULL, 0, msec, proc);
}

void tsfValues::uninstallWatcher() {
  if (m_timer) KillTimer(NULL, m_timer);
  m_timer = 0;
}

typedef std::map<tsfValues*, bool> tsfValuesTable;

static DWORD dwTlsIndex = 0;
static tsfValuesTable* pValuesTable = NULL;
static CRITICAL_SECTION cs;

void setupTsfValues()
{
  dwTlsIndex = TlsAlloc();
  InitializeCriticalSection(&cs);
  pValuesTable = new tsfValuesTable;
}

void releaseTsfValues()
{
  EnterCriticalSection(&cs);
  for (tsfValuesTable::iterator i = pValuesTable->begin(); i != pValuesTable->end(); i++) {
    delete i->first;
  }
  delete pValuesTable;
  LeaveCriticalSection(&cs);
  DeleteCriticalSection(&cs);
  TlsFree(dwTlsIndex);
}

bool installWatcher(UINT msec, TIMERPROC proc)
{
  if (TlsGetValue(dwTlsIndex)) {
    ((tsfValues*)TlsGetValue(dwTlsIndex))->installWatcher(msec, proc);
    return true;
  }
  return false;
}

bool uninstallWatcher()
{
  if (TlsGetValue(dwTlsIndex)) {
    ((tsfValues*)TlsGetValue(dwTlsIndex))->uninstallWatcher();
    return true;
  }
  return false;
}

bool setupThreadTsfValues(CcommonValues* pCv)
{
  sirius_timing::Scope timingAll("setupThreadTsfValues", 0, 1.0);
  ITfThreadMgr* iTMgr = NULL;
  if (!SUCCEEDED(GetThreadMgr(&iTMgr))) {
    iTMgr = NULL;
  }
  if (TlsGetValue(dwTlsIndex)) {
    ((tsfValues*)TlsGetValue(dwTlsIndex))->update(iTMgr, pCv);
    return pCv->m_supportTsf ? true : false;
  }
  tsfValues* pTv = new tsfValues(iTMgr, pCv);
  TlsSetValue(dwTlsIndex, pTv);
  EnterCriticalSection(&cs);
  (*pValuesTable)[pTv] = true;
  LeaveCriticalSection(&cs);
  return pCv->m_supportTsf ? true : false;
}

void releaseThreadTsfValues()
{
  sirius_timing::Scope timing("releaseThreadTsfValues", 0, 1.0);
  EnterCriticalSection(&cs);
  if (pValuesTable->find((tsfValues*)TlsGetValue(dwTlsIndex)) != pValuesTable->end()) {
    pValuesTable->erase((tsfValues*)TlsGetValue(dwTlsIndex));
    delete (tsfValues*)TlsGetValue(dwTlsIndex);
    TlsSetValue(dwTlsIndex, NULL);
  }
  LeaveCriticalSection(&cs);
}

