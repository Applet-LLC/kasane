// 天狼（sirius）: MIT License, Copyright (C) 2008-2010, MATSUMOTO Reiji
#pragma once
#include <sirius/tsfCompartment.h>
#include <sirius/tsfText.h>
#include <sirius/commonValues.h>

class tsfValues {
protected:
  tsfCompartment* m_openClose;
  tsfCompartment* m_comversion;
  tsfCompartment* m_sentence;
  TsfText* m_TsfComposition;
  BOOL m_Composition;
  BOOL m_supportTsf;
  BOOL m_supportTsfOpenClose;
  BOOL m_supportTsfConversion;
  BOOL m_supportTsfSentence;
  BOOL m_supportTsfComposition;

  BOOL m_bEffective;
  UINT_PTR m_timer;
public:
  tsfValues(ITfThreadMgr* iMgr, CcommonValues* pCv);
  virtual ~tsfValues();
  bool update(ITfThreadMgr* iMgr, CcommonValues* pCv);
  void installWatcher(UINT msec, TIMERPROC proc);
  void uninstallWatcher();
};

void setupTsfValues();
void releaseTsfValues();
bool setupThreadTsfValues(CcommonValues* pCv);
void releaseThreadTsfValues();
bool installWatcher(UINT msec, TIMERPROC proc);
bool uninstallWatcher();
CcommonValues* SiriusCommonValue();