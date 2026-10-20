#pragma once
#include <windows.h>
#include <stdio.h>
#include <string.h>

// kasane: 計測用の診断（Explorer の起動が遅くなる不具合の原因を調べるため）。
// C:\Users\Public\sirius-timing.on があるプロセスだけが、遅い処理（しきい値以上）を C:\Users\Public\sirius-timing.log に1行ずつ追記する。
// ファイルが無ければ、プロセスごとに最初の1回の GetFileAttributes だけで、あとは何もしない。
namespace sirius_timing {

inline bool Enabled()
{
  static volatile LONG state = 0; // 0 = 未確認、1 = 有効、2 = 無効
  LONG s = state;
  if (s == 0) {
    s = (GetFileAttributesW(L"C:\\Users\\Public\\sirius-timing.on") != INVALID_FILE_ATTRIBUTES) ? 1 : 2;
    state = s;
  }
  return s == 1;
}

inline LONGLONG Now()
{
  LARGE_INTEGER t;
  QueryPerformanceCounter(&t);
  return t.QuadPart;
}

inline double ToMs(LONGLONG ticks)
{
  static LONGLONG freq = 0;
  if (!freq) {
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    freq = f.QuadPart;
  }
  return ticks * 1000.0 / freq;
}

inline const char* ProcessName()
{
  static char name[64] = "";
  static bool done = false;
  if (!done) {
    wchar_t path[MAX_PATH];
    if (GetModuleFileNameW(NULL, path, MAX_PATH)) {
      const wchar_t* base = wcsrchr(path, L'\\');
      WideCharToMultiByte(CP_ACP, 0, base ? base + 1 : path, -1, name, sizeof(name) - 1, NULL, NULL);
    }
    done = true;
  }
  return name;
}

inline void Write(const char* what, double ms, unsigned extra, const char* note)
{
  char line[300];
  SYSTEMTIME st;
  GetLocalTime(&st);
  _snprintf_s(line, sizeof(line), _TRUNCATE, "%02d:%02d:%02d.%03d pid=%lu tid=%lu %s %s %.2fms 0x%X %s\r\n",
    st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, GetCurrentProcessId(), GetCurrentThreadId(),
    ProcessName(), what, ms, extra, note ? note : "");
  HANDLE h = CreateFileW(L"C:\\Users\\Public\\sirius-timing.log", FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                         OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
  if (h == INVALID_HANDLE_VALUE) return;
  DWORD written;
  WriteFile(h, line, (DWORD)strlen(line), &written, NULL);
  CloseHandle(h);
}

// 5秒ごとの集計（呼び出し回数・合計・最大）。1回ずつは遅くなくても、全メッセージで呼ばれる処理の合計の重さを見るため。
struct Agg {
  LONGLONG calls = 0, total = 0, max = 0, windowStart = 0;
};

inline void Record(Agg& a, const char* what, LONGLONG ticks)
{
  LONGLONG now = Now();
  if (!a.windowStart) a.windowStart = now;
  a.calls++;
  a.total += ticks;
  if (ticks > a.max) a.max = ticks;
  if (ToMs(now - a.windowStart) >= 5000.0) {
    char note[100];
    _snprintf_s(note, sizeof(note), _TRUNCATE, "calls=%lld max=%.2fms", a.calls, ToMs(a.max));
    Write(what, ToMs(a.total), 0, note);
    a.calls = a.total = a.max = 0;
    a.windowStart = now;
  }
}

// スコープの処理時間を測る。threshold 以上なら1行書く。agg があれば、5秒ごとの集計にも足す。
struct Scope {
  const char* what;
  unsigned extra;
  double threshold;
  Agg* agg;
  bool on;
  LONGLONG t0;

  Scope(const char* what_, unsigned extra_ = 0, double threshold_ = 3.0, Agg* agg_ = NULL)
    : what(what_), extra(extra_), threshold(threshold_), agg(agg_), on(Enabled()), t0(0)
  {
    if (on) t0 = Now();
  }
  ~Scope()
  {
    if (!on) return;
    LONGLONG ticks = Now() - t0;
    if (ToMs(ticks) >= threshold) Write(what, ToMs(ticks), extra, "");
    if (agg) Record(*agg, what, ticks);
  }
};

} // namespace sirius_timing
