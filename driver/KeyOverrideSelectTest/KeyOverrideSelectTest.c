// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: Apache-2.0

/*
  KeyOverrideSelectTest.c

  kbdlayer/KeyOverrideSelect.h（Key Overrideのレイヤー条件と優先順位）のユーザーモードテスト。
  MatchKeyOverrideLocked と同じ選び方（順位が高いものを選び、同順位なら先のものを残す）を、
  候補の表で再現して確かめる。
*/

#include <windows.h>
#include <stdio.h>
#include "..\kbdlayer\KeyOverrideSelect.h"

static int g_failures = 0;

#define CHECK(cond, msg) \
    do { if (!(cond)) { printf("FAIL: %s (line %d)\n", msg, __LINE__); g_failures++; } } while (0)

// MatchKeyOverrideLockedと同じ選び方。Layers[i]（i=Id）の中から、選ばれる件のIdを返す（無ければ-1）。
static int Select(const USHORT* Layers, int Count, ULONG EffectiveMask)
{
    LONG bestRank = KBDLAYER_KEY_OVERRIDE_RANK_NONE;
    int best = -1;

    for (int i = 0; i < Count; i++) {
        LONG rank = KeyOverrideCandidateRank(Layers[i], EffectiveMask);
        if (KeyOverrideRankBeats(rank, bestRank)) {
            bestRank = rank;
            best = i;
        }
    }
    return best;
}

#define L(n) ((USHORT)(1U << (n)))

int main(void)
{
    // --- 順位 ---
    CHECK(KeyOverrideCandidateRank(0, 0x1) == 0, "条件なしは、順位0");
    CHECK(KeyOverrideCandidateRank(0, 0) == 0, "条件なしは、実効マスクに依らず順位0");
    CHECK(KeyOverrideCandidateRank(L(3), 0x1) == KBDLAYER_KEY_OVERRIDE_RANK_NONE, "条件のレイヤーが立っていなければ候補にならない");
    CHECK(KeyOverrideCandidateRank(L(3), 0x1 | L(3)) == 4, "レイヤー3が立っていれば、順位4");
    CHECK(KeyOverrideCandidateRank(L(0), 0x1) == 1, "レイヤー0だけの条件でも、条件なしより上");
    CHECK(KeyOverrideCandidateRank((USHORT)(L(2) | L(5)), 0x1 | L(2) | L(5)) == 6, "複数の条件は、立っている最上位で順位が決まる");
    CHECK(KeyOverrideCandidateRank((USHORT)(L(2) | L(5)), 0x1 | L(2)) == 3, "立っているレイヤーだけを見る（5は立っていない）");
    CHECK(KeyOverrideCandidateRank(L(15), L(15)) == 16, "レイヤー15は、順位16");

    // --- 選び方 ---
    {
        USHORT none[] = { 0, 0, 0 };
        CHECK(Select(none, 3, 0x1) == 0, "条件なしだけなら、Idの小さいもの（従来と同じ）");
    }
    {
        USHORT layers[] = { 0, L(3) };
        CHECK(Select(layers, 2, 0x1 | L(3)) == 1, "レイヤー3が立っていれば、条件つきが、条件なしに勝つ");
        CHECK(Select(layers, 2, 0x1) == 0, "レイヤー3が立っていなければ、条件なしを使う");
    }
    {
        USHORT layers[] = { L(3) };
        CHECK(Select(layers, 1, 0x1) == -1, "条件のレイヤーが立っておらず、ほかに候補が無ければ、当たらない");
    }
    {
        USHORT layers[] = { L(3), L(5) };
        CHECK(Select(layers, 2, 0x1 | L(3) | L(5)) == 1, "条件つき同士は、上のレイヤーが勝つ（Idに依らない）");
        CHECK(Select(layers, 2, 0x1 | L(3)) == 0, "上のレイヤーが立っていなければ、立っているほう");
    }
    {
        USHORT layers[] = { L(3), L(3) };
        CHECK(Select(layers, 2, 0x1 | L(3)) == 0, "同じ順位なら、Idの小さいもの");
    }
    {
        USHORT layers[] = { 0, L(4), L(2), 0 };
        CHECK(Select(layers, 4, 0x1 | L(2) | L(4)) == 1, "条件なしと条件つきが混ざっても、最上位のレイヤーの条件つき");
        CHECK(Select(layers, 4, 0x1 | L(2)) == 2, "上のレイヤーが落ちたら、次");
        CHECK(Select(layers, 4, 0x1) == 0, "どの条件も立っていなければ、条件なしの先頭");
    }
    {
        // 上のレイヤーに「同じキー → 同じキー」を置いて、下の置き換えをやめる使い方。
        USHORT layers[] = { 0, L(3) }; // 0: Ctrl+F → 移動（全レイヤー）、1: Ctrl+F → Ctrl+F（レイヤー3）
        CHECK(Select(layers, 2, 0x1 | L(3)) == 1, "レイヤー3の間だけ、全体の置き換えをやめられる");
    }

    if (g_failures == 0) {
        printf("KeyOverrideSelectTest: all passed\n");
        return 0;
    }
    printf("KeyOverrideSelectTest: %d failure(s)\n", g_failures);
    return 1;
}
