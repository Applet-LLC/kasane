// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: Apache-2.0

/*
  kbdlayer/KeyOverrideSelect.h

  Key Overrideのレイヤー条件の判定と、候補が複数あるときの優先順位（2026-11-25）。
  ロック・デバイス拡張に依らない純粋な関数だけを置く。LayerEngine.cのMatchKeyOverrideLockedが使い、
  同じヘッダーを driver/KeyOverrideSelectTest（ユーザーモードのテストEXE）でも試す。

  規則（kbdlayer_ioctl.hの「Key Overrideのレイヤー条件」と同じ）:
    - Layers == 0: 全レイヤーで候補（順位0）。
    - Layers != 0: 実効レイヤーマスクと重なるときだけ候補。順位 = 1 + 「Layers & 実効マスク」の最上位のレイヤー。
    - 順位が高いものを選ぶ。同順位なら、先に調べたもの（Idの小さいもの）を残す。
*/

#ifndef _KBDLAYER_KEY_OVERRIDE_SELECT_H_
#define _KBDLAYER_KEY_OVERRIDE_SELECT_H_

#include "..\\common\\kbdlayer_ioctl.h"

// 候補にならない（条件のレイヤーが1枚も立っていない）ことを表す順位。
#define KBDLAYER_KEY_OVERRIDE_RANK_NONE (-1L)

// 1件の候補の順位。候補にならなければ KBDLAYER_KEY_OVERRIDE_RANK_NONE。
// Layers: 件のレイヤー条件（0 = 条件なし）。EffectiveMask: 実効レイヤーマスク（ビットnがレイヤーn）。
static __inline LONG
KeyOverrideCandidateRank(
    _In_ USHORT Layers,
    _In_ ULONG EffectiveMask
    )
{
    ULONG overlap;
    LONG top = -1;

    if (Layers == 0) {
        return 0;
    }
    overlap = (ULONG)Layers & EffectiveMask;
    if (overlap == 0) {
        return KBDLAYER_KEY_OVERRIDE_RANK_NONE;
    }
    for (LONG layer = KBDLAYER_MAX_LAYERS - 1; layer >= 0; layer--) {
        if (overlap & (1UL << layer)) {
            top = layer;
            break;
        }
    }
    return 1 + top;
}

// 今の最良の順位 BestRank より、この候補（順位 Rank）が良いか。候補にならないものと、同順位は良くない
// （先に調べたもの = Idの小さいものを残す）。最初の候補のときは BestRank に RANK_NONE を渡す。
static __inline BOOLEAN
KeyOverrideRankBeats(
    _In_ LONG Rank,
    _In_ LONG BestRank
    )
{
    return (Rank != KBDLAYER_KEY_OVERRIDE_RANK_NONE && Rank > BestRank) ? TRUE : FALSE;
}

#endif // _KBDLAYER_KEY_OVERRIDE_SELECT_H_
