// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

/*
  NicolaCoreTest.c

  kbdlayer/NicolaCore.c（NICOLA同時打鍵判定）のユーザーモードテスト。
  NICOLA配列規格書の図1〜7・6.1節・参考7（キーリピート）の打鍵パターンを
  時刻付きのイベント列として流し、確定結果を文字列で照合する。

  出力の表記:
    C<sc>/<plane>  文字キー sc を plane（0=単独, 1=親指左, 2=親指右）で送出
    T+L / T-L      親指左の単独打鍵キーを押下 / 解放（R は親指右）
    P              生イベントを通常経路へ流す
*/

#include <windows.h>
#include <stdio.h>
#include <string.h>
#include "..\\kbdlayer\\NicolaCore.h"

#define SC_A 0x1E
#define SC_B 0x30
#define SC_X 0x2D // NICOLA対象外のキーの代わり

typedef struct _SIM {
    NICOLA_CORE_STATE State;
    NICOLA_CORE_PARAMS Params;
    char Log[512];
} SIM;

static int g_failed = 0;
static int g_passed = 0;

static void
AppendResult(SIM* sim, const NICOLA_CORE_RESULT* r)
{
    for (ULONG i = 0; i < r->Count; i++) {
        char buf[32];
        const NICOLA_CORE_OUTPUT* o = &r->Items[i];
        switch (o->Type) {
        case NICOLA_OUT_CHAR:
            sprintf_s(buf, sizeof(buf), "C%02X/%u ", o->ScanCode, o->Arg);
            break;
        case NICOLA_OUT_TAP_MAKE:
            sprintf_s(buf, sizeof(buf), "T+%c ", o->Arg == NICOLA_SIDE_LEFT ? 'L' : 'R');
            break;
        case NICOLA_OUT_TAP_BREAK:
            sprintf_s(buf, sizeof(buf), "T-%c ", o->Arg == NICOLA_SIDE_LEFT ? 'L' : 'R');
            break;
        case NICOLA_OUT_PASS:
            sprintf_s(buf, sizeof(buf), "P ");
            break;
        default:
            sprintf_s(buf, sizeof(buf), "? ");
            break;
        }
        strcat_s(sim->Log, sizeof(sim->Log), buf);
    }
}

// DPCの代わり: 時刻 t までに来る期限を順に発火させる。
static void
AdvanceTo(SIM* sim, ULONGLONG t)
{
    while (sim->State.Deadline != 0 && sim->State.Deadline <= t) {
        NICOLA_CORE_RESULT r;
        NicolaCoreOnTimeout(&sim->State, &sim->Params, sim->State.Deadline, &r);
        AppendResult(sim, &r);
    }
}

static void
Key(SIM* sim, ULONGLONG t, NICOLA_KEY_KIND kind, USHORT sc, BOOLEAN isBreak)
{
    NICOLA_CORE_RESULT r;
    AdvanceTo(sim, t);
    NicolaCoreOnKey(&sim->State, &sim->Params, kind, sc, isBreak, t, &r);
    AppendResult(sim, &r);
}

static void CharDown(SIM* s, ULONGLONG t, USHORT sc) { Key(s, t, NICOLA_KEY_CHAR, sc, FALSE); }
static void CharUp(SIM* s, ULONGLONG t, USHORT sc)   { Key(s, t, NICOLA_KEY_CHAR, sc, TRUE); }
static void LDown(SIM* s, ULONGLONG t) { Key(s, t, NICOLA_KEY_THUMB_LEFT, 0x7B, FALSE); }
static void LUp(SIM* s, ULONGLONG t)   { Key(s, t, NICOLA_KEY_THUMB_LEFT, 0x7B, TRUE); }
static void RDown(SIM* s, ULONGLONG t) { Key(s, t, NICOLA_KEY_THUMB_RIGHT, 0x79, FALSE); }
static void RUp(SIM* s, ULONGLONG t)   { Key(s, t, NICOLA_KEY_THUMB_RIGHT, 0x79, TRUE); }
static void Other(SIM* s, ULONGLONG t, BOOLEAN isBreak) { Key(s, t, NICOLA_KEY_OTHER, SC_X, isBreak); }

static void
Begin(SIM* sim, ULONG flags, ULONG minOverlapMs)
{
    NicolaCoreInit(&sim->State);
    sim->Params.SimulTermMs = 100;
    sim->Params.OverlapRatioPercent = 100;
    sim->Params.MinOverlapMs = minOverlapMs;
    sim->Params.Flags = flags;
    sim->Log[0] = '\0';
}

static void
Expect(SIM* sim, const char* name, const char* expected)
{
    // 末尾の空白を落として比較する
    size_t len = strlen(sim->Log);
    while (len > 0 && sim->Log[len - 1] == ' ') {
        sim->Log[--len] = '\0';
    }
    if (strcmp(sim->Log, expected) == 0) {
        printf("PASS  %s\n", name);
        g_passed++;
    } else {
        printf("FAIL  %s\n      expected: \"%s\"\n      actual:   \"%s\"\n", name, expected, sim->Log);
        g_failed++;
    }
}

int
main(void)
{
    SIM s;

    Begin(&s, 0, 0);
    CharDown(&s, 0, SC_A); AdvanceTo(&s, 1000); CharUp(&s, 1200, SC_A);
    Expect(&s, "5.3 fig1: char ON -> timeout = single", "C1E/0");

    Begin(&s, 0, 0);
    CharDown(&s, 0, SC_A); CharUp(&s, 30, SC_A); AdvanceTo(&s, 1000);
    Expect(&s, "5.3 fig2: char ON -> char OFF within term = single", "C1E/0");

    Begin(&s, 0, 0);
    CharDown(&s, 0, SC_A); CharDown(&s, 30, SC_B); CharUp(&s, 40, SC_A); AdvanceTo(&s, 1000);
    Expect(&s, "5.3 fig3: char a ON -> char b ON = a single, b pending", "C1E/0 C30/0");

    Begin(&s, 0, 0);
    LDown(&s, 0); CharDown(&s, 30, SC_A); AdvanceTo(&s, 1000); CharUp(&s, 1100, SC_A); LUp(&s, 1200);
    Expect(&s, "5.4.1 fig4: thumb ON -> char ON (prefix)", "C1E/1");

    Begin(&s, 0, 0);
    CharDown(&s, 0, SC_A); RDown(&s, 40); AdvanceTo(&s, 1000); CharUp(&s, 1100, SC_A); RUp(&s, 1200);
    Expect(&s, "5.4.1 fig5: char ON -> thumb ON (postfix)", "C1E/2");

    Begin(&s, 0, 0);
    CharDown(&s, 0, SC_A); RDown(&s, 20); CharDown(&s, 60, SC_B); AdvanceTo(&s, 1000);
    Expect(&s, "5.4.2 fig6: t1 <= t2 -> a+s, b single", "C1E/2 C30/0");

    Begin(&s, 0, 0);
    CharDown(&s, 0, SC_A); RDown(&s, 40); CharDown(&s, 60, SC_B); AdvanceTo(&s, 1000);
    Expect(&s, "5.4.2 fig6: t1 > t2 -> a single, s+b", "C1E/0 C30/2");

    Begin(&s, 0, 0);
    LDown(&s, 0); CharDown(&s, 40, SC_A); RDown(&s, 50); AdvanceTo(&s, 1000); LUp(&s, 1100);
    Expect(&s, "5.4.2: thumb a -> char -> thumb b, closer to b", "T+L C1E/2 T-L");

    Begin(&s, 0, 0);
    LDown(&s, 0); CharDown(&s, 20, SC_A); RDown(&s, 60); AdvanceTo(&s, 1000);
    Expect(&s, "5.4.2: thumb a -> char -> thumb b, closer to a", "C1E/1 T+R");

    Begin(&s, 0, 0);
    CharDown(&s, 0, SC_A); RDown(&s, 40); CharUp(&s, 50, SC_A); AdvanceTo(&s, 1000); RUp(&s, 1100);
    Expect(&s, "5.5.2 fig7: t1 > t2*n -> sequential", "C1E/0 T+R T-R");

    Begin(&s, 0, 0);
    CharDown(&s, 0, SC_A); RDown(&s, 10); CharUp(&s, 100, SC_A); RUp(&s, 120);
    Expect(&s, "5.5.2 fig7: t1 <= t2*n -> simultaneous", "C1E/2");

    Begin(&s, 0, 30);
    CharDown(&s, 0, SC_A); RDown(&s, 5); CharUp(&s, 20, SC_A); RUp(&s, 25);
    Expect(&s, "5.5.2: overlap < MinOverlapMs -> sequential", "C1E/0 T+R T-R");

    Begin(&s, 0, 0);
    LDown(&s, 0); CharDown(&s, 60, SC_A); LUp(&s, 70); AdvanceTo(&s, 1000);
    Expect(&s, "5.5.2 mirrored: thumb -> char -> thumb OFF quickly = sequential", "T+L T-L C1E/0");

    Begin(&s, 0, 0);
    LDown(&s, 0); LUp(&s, 50);
    Expect(&s, "5.5: thumb single tap", "T+L T-L");

    Begin(&s, 0, 0);
    LDown(&s, 0); AdvanceTo(&s, 300); CharDown(&s, 300, SC_A); AdvanceTo(&s, 1000); LUp(&s, 1100);
    Expect(&s, "thumb held past term (no continuous shift): tap, char single", "T+L C1E/0 T-L");

    Begin(&s, 0, 0);
    LDown(&s, 0); AdvanceTo(&s, 200); LDown(&s, 250); LDown(&s, 280); LUp(&s, 300);
    Expect(&s, "thumb tap key repeat while held", "T+L T+L T+L T-L");

    Begin(&s, KBDLAYER_NICOLA_FLAG_CONTINUOUS_SHIFT, 0);
    LDown(&s, 0); CharDown(&s, 300, SC_A); CharUp(&s, 350, SC_A); CharDown(&s, 400, SC_B); CharUp(&s, 450, SC_B); LUp(&s, 500);
    LDown(&s, 600); LUp(&s, 650);
    Expect(&s, "6.1 continuous shift", "C1E/1 C30/1 T+L T-L");

    Begin(&s, 0, 0);
    CharDown(&s, 0, SC_A); Other(&s, 10, FALSE); Other(&s, 20, TRUE); CharUp(&s, 30, SC_A);
    Expect(&s, "other key flushes pending first", "C1E/0 P P");

    Begin(&s, 0, 0);
    CharUp(&s, 0, SC_A);
    Expect(&s, "break of a key not owned passes through", "P");

    Begin(&s, 0, 0);
    CharDown(&s, 0, SC_A); AdvanceTo(&s, 500); CharDown(&s, 500, SC_A); CharDown(&s, 530, SC_A); CharUp(&s, 560, SC_A);
    Expect(&s, "ref 7: char key repeat repeats confirmed output", "C1E/0 C1E/0 C1E/0");

    Begin(&s, 0, 0);
    RDown(&s, 0); CharDown(&s, 20, SC_A);
    {
        NICOLA_CORE_RESULT r;
        NicolaCoreReset(&s.State, &r);
        AppendResult(&s, &r);
    }
    Expect(&s, "reset flushes pending pair", "C1E/2");

    Begin(&s, 0, 0);
    LDown(&s, 0); AdvanceTo(&s, 200);
    {
        NICOLA_CORE_RESULT r;
        NicolaCoreReset(&s.State, &r);
        AppendResult(&s, &r);
    }
    LUp(&s, 300);
    Expect(&s, "reset releases held tap key; later thumb break passes", "T+L T-L P");

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    return g_failed == 0 ? 0 : 1;
}
