// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: Apache-2.0

/*
  kbdlayer/NicolaCore.c

  NICOLA同時打鍵判定の状態機械（NicolaCore.h参照）。カーネルAPIは使わない。

  保留スロットは「文字キー1つ」と「親指キー1つ」の2つだけ。両方が保留中のときは
  CharFirstで押下順を区別する。規格の各節との対応:

    状態       次のイベント            処理
    ---------  ----------------------  ------------------------------------------------
    M(文字a)   判定時間経過            a 単独（5.3 図1）
               a OFF                   a 単独（5.3 図2）
               文字b ON                a 単独、b を保留（5.3 図3）
               親指s ON                MS へ（5.4.1 図5）
    S(親指s)   判定時間経過            s 単独打鍵（連続シフト時はタイムアウトしない）
               s OFF                   s 単独打鍵
               文字a ON                SM へ（5.4.1 図4）
               親指x ON                s 単独打鍵、x を保留
    MS(a→s)    文字b ON                t1=s-a, t2=b-s。t1≦t2 なら a+s、b を保留。
                                       t1＞t2 なら a 単独、s+b（5.4.2 図6）
               a OFF                   t2=OFF-s。t1＞t2×n または t2＜MinOverlap なら
                                       a 単独・s を保留し直す（逐次打鍵）。
                                       それ以外は a+s（5.5.2 図7）
               s OFF / 期限 / 親指x ON a+s
    SM(s→a)    親指x ON                t1=a-s, t2=x-a。t1≦t2 なら a+s、x を保留。
                                       t1＞t2 なら s 単独打鍵、MS(a→x)へ（5.4.2 後段）
               s OFF                   5.5.2 と対称に判定。逐次なら s 単独打鍵・a を保留
               a OFF / 期限 / 文字b ON a+s（b は保留）

  MS/SMの期限は「3キー目・OFFがもう判定を変えられなくなる時刻」:
  重なり開始から max(t1, t1/n, MinOverlap)（ただし判定時間で頭打ち）。
*/

#include "NicolaCore.h"

static ULONG
OwnedIndex(
    _In_ USHORT ScanCode
    )
{
    ULONG index = ScanCode & 0xFF;
    UCHAR prefix = (UCHAR)(ScanCode >> 8);
    if (prefix == 0xE1) {
        index |= 0x200;
    } else if (prefix == 0xE0) {
        index |= 0x100;
    }
    return index;
}

static BOOLEAN
IsOwned(
    _In_ const NICOLA_CORE_STATE* State,
    _In_ USHORT ScanCode
    )
{
    ULONG index = OwnedIndex(ScanCode);
    return (State->OwnedDown[index >> 3] & (1u << (index & 7))) != 0;
}

static VOID
SetOwned(
    _Inout_ PNICOLA_CORE_STATE State,
    _In_ USHORT ScanCode,
    _In_ BOOLEAN Owned
    )
{
    ULONG index = OwnedIndex(ScanCode);
    UCHAR mask = (UCHAR)(1u << (index & 7));
    if (Owned) {
        State->OwnedDown[index >> 3] |= mask;
    } else {
        State->OwnedDown[index >> 3] &= (UCHAR)~mask;
    }
}

static VOID
Push(
    _Inout_ PNICOLA_CORE_RESULT Result,
    _In_ UCHAR Type,
    _In_ UCHAR Arg,
    _In_ USHORT ScanCode
    )
{
    if (Result->Count < NICOLA_CORE_MAX_OUTPUTS) {
        Result->Items[Result->Count].Type = Type;
        Result->Items[Result->Count].Arg = Arg;
        Result->Items[Result->Count].ScanCode = ScanCode;
        Result->Count++;
    }
}

static UCHAR
PlaneForSide(
    _In_ UCHAR Side
    )
{
    return (Side == NICOLA_SIDE_LEFT) ? KBDLAYER_NICOLA_PLANE_THUMB_LEFT : KBDLAYER_NICOLA_PLANE_THUMB_RIGHT;
}

// 文字キーを確定送出する。StillDown = その文字キーがまだ押されているか（キーリピート用）。
static VOID
EmitChar(
    _Inout_ PNICOLA_CORE_STATE State,
    _Inout_ PNICOLA_CORE_RESULT Result,
    _In_ USHORT ScanCode,
    _In_ UCHAR Plane,
    _In_ BOOLEAN StillDown
    )
{
    Push(Result, NICOLA_OUT_CHAR, Plane, ScanCode);
    State->LastCharScan = ScanCode;
    State->LastCharPlane = Plane;
    State->LastCharDown = StillDown;
}

// 保留中の文字キーを単独打鍵として確定する。
static VOID
EmitPendingCharAlone(
    _Inout_ PNICOLA_CORE_STATE State,
    _Inout_ PNICOLA_CORE_RESULT Result,
    _In_ BOOLEAN StillDown
    )
{
    EmitChar(State, Result, State->CharScan, KBDLAYER_NICOLA_PLANE_NONE, StillDown);
    State->CharPending = FALSE;
}

// 保留中の文字キーと親指キーを同時打鍵として確定する。
static VOID
EmitPendingPair(
    _Inout_ PNICOLA_CORE_STATE State,
    _Inout_ PNICOLA_CORE_RESULT Result,
    _In_ BOOLEAN CharStillDown
    )
{
    EmitChar(State, Result, State->CharScan, PlaneForSide(State->ThumbSide), CharStillDown);
    State->ThumbUsed[State->ThumbSide] = TRUE;
    State->CharPending = FALSE;
    State->ThumbPending = FALSE;
}

// 保留中の親指キーを単独打鍵として確定する。まだ押されていれば押下だけ送り、
// 解放は物理キーを離したときに送る（単独打鍵キーのキーリピートを生かすため）。
static VOID
EmitPendingThumbTap(
    _Inout_ PNICOLA_CORE_STATE State,
    _Inout_ PNICOLA_CORE_RESULT Result
    )
{
    UCHAR side = State->ThumbSide;

    Push(Result, NICOLA_OUT_TAP_MAKE, side, 0);
    if (State->ThumbDown[side]) {
        State->ThumbTapHeld[side] = TRUE;
    } else {
        Push(Result, NICOLA_OUT_TAP_BREAK, side, 0);
    }
    State->ThumbUsed[side] = TRUE;
    State->ThumbPending = FALSE;
}

// 逐次打鍵か（規格5.5.2）。T1 = 押下時刻のずれ、T2 = 重なっていた時間。
static BOOLEAN
IsSequential(
    _In_ const NICOLA_CORE_PARAMS* Params,
    _In_ ULONGLONG T1,
    _In_ ULONGLONG T2
    )
{
    if (T2 < Params->MinOverlapMs) {
        return TRUE;
    }
    return (T1 * 100) > (T2 * Params->OverlapRatioPercent);
}

// 両方保留中のとき、重なり開始からどれだけ待てば判定が変わらなくなるか。
static ULONGLONG
PairWaitMs(
    _In_ const NICOLA_CORE_PARAMS* Params,
    _In_ ULONGLONG T1
    )
{
    ULONGLONG wait = T1;
    ULONGLONG byRatio = (T1 * 100) / Params->OverlapRatioPercent;

    if (byRatio > wait) {
        wait = byRatio;
    }
    if (Params->MinOverlapMs > wait) {
        wait = Params->MinOverlapMs;
    }
    if (wait > Params->SimulTermMs) {
        wait = Params->SimulTermMs;
    }
    return wait;
}

static BOOLEAN
IsContinuous(
    _In_ const NICOLA_CORE_PARAMS* Params
    )
{
    return (Params->Flags & KBDLAYER_NICOLA_FLAG_CONTINUOUS_SHIFT) != 0;
}

static VOID
RecomputeDeadline(
    _Inout_ PNICOLA_CORE_STATE State,
    _In_ const NICOLA_CORE_PARAMS* Params
    )
{
    if (State->CharPending && State->ThumbPending) {
        if (State->CharFirst) {
            State->Deadline = State->ThumbTime + PairWaitMs(Params, State->ThumbTime - State->CharTime);
        } else {
            State->Deadline = State->CharTime + PairWaitMs(Params, State->CharTime - State->ThumbTime);
        }
    } else if (State->CharPending) {
        State->Deadline = State->CharTime + Params->SimulTermMs;
    } else if (State->ThumbPending && !IsContinuous(Params)) {
        State->Deadline = State->ThumbTime + Params->SimulTermMs;
    } else {
        State->Deadline = 0;
    }
}

// 期限到来時の確定処理（RecomputeDeadlineは呼び出し元が行う）。
static VOID
HandleExpired(
    _Inout_ PNICOLA_CORE_STATE State,
    _Inout_ PNICOLA_CORE_RESULT Result
    )
{
    if (State->CharPending && State->ThumbPending) {
        EmitPendingPair(State, Result, TRUE);
    } else if (State->CharPending) {
        EmitPendingCharAlone(State, Result, TRUE);
    } else if (State->ThumbPending) {
        EmitPendingThumbTap(State, Result);
    }
}

// 期限を過ぎている保留があれば先に確定させる（DPCより先にキーイベントが届いた場合や、
// 期限が「今」以前に計算された場合のため）。
static VOID
ProcessExpired(
    _Inout_ PNICOLA_CORE_STATE State,
    _In_ const NICOLA_CORE_PARAMS* Params,
    _In_ ULONGLONG NowMs,
    _Inout_ PNICOLA_CORE_RESULT Result
    )
{
    // 1回の確定で保留は必ず減るので、ループは高々2回で終わる。
    while (State->Deadline != 0 && NowMs >= State->Deadline) {
        HandleExpired(State, Result);
        RecomputeDeadline(State, Params);
    }
}

// 保留をすべて確定させる（NICOLA対象外のキーが割り込んだとき）。
static VOID
FlushPending(
    _Inout_ PNICOLA_CORE_STATE State,
    _Inout_ PNICOLA_CORE_RESULT Result
    )
{
    HandleExpired(State, Result);
    // 文字が単独確定しても親指が保留に残るケースは無い（HandleExpiredは両方保留なら
    // ペアで確定する）が、念のため親指も確定させる。
    if (State->ThumbPending) {
        EmitPendingThumbTap(State, Result);
    }
}

// 連続シフト中の親指キー（押されたまま・保留でない・単独打鍵を送っていない）を探す。
// 見つからなければ-1。両方なら後から押した方。
static LONG
FindHeldShiftThumb(
    _In_ const NICOLA_CORE_STATE* State,
    _In_ const NICOLA_CORE_PARAMS* Params
    )
{
    LONG found = -1;

    if (!IsContinuous(Params)) {
        return -1;
    }
    for (UCHAR side = 0; side < 2; side++) {
        if (!State->ThumbDown[side] || State->ThumbTapHeld[side]) {
            continue;
        }
        if (State->ThumbPending && State->ThumbSide == side) {
            continue;
        }
        if (found < 0 || State->ThumbDownTime[side] > State->ThumbDownTime[found]) {
            found = side;
        }
    }
    return found;
}

// 何も保留していない（か、保留を処理し終えた）状態での文字キー押下。
static VOID
StartChar(
    _Inout_ PNICOLA_CORE_STATE State,
    _In_ const NICOLA_CORE_PARAMS* Params,
    _In_ USHORT ScanCode,
    _In_ ULONGLONG NowMs,
    _Inout_ PNICOLA_CORE_RESULT Result
    )
{
    LONG held = FindHeldShiftThumb(State, Params);

    if (held >= 0) {
        // 規格6.1: 親指キーを押している間の文字キーは、時間によらず同時打鍵。
        EmitChar(State, Result, ScanCode, PlaneForSide((UCHAR)held), TRUE);
        State->ThumbUsed[held] = TRUE;
        return;
    }
    State->CharPending = TRUE;
    State->CharScan = ScanCode;
    State->CharTime = NowMs;
}

static VOID
OnCharMake(
    _Inout_ PNICOLA_CORE_STATE State,
    _In_ const NICOLA_CORE_PARAMS* Params,
    _In_ USHORT ScanCode,
    _In_ ULONGLONG NowMs,
    _Inout_ PNICOLA_CORE_RESULT Result
    )
{
    if (State->CharPending && State->ThumbPending) {
        if (State->CharFirst) {
            // 規格5.4.2 図6: 文字a → 親指s → 文字b
            ULONGLONG t1 = State->ThumbTime - State->CharTime;
            ULONGLONG t2 = NowMs - State->ThumbTime;
            if (t1 <= t2) {
                EmitPendingPair(State, Result, TRUE);
                StartChar(State, Params, ScanCode, NowMs, Result);
            } else {
                UCHAR side = State->ThumbSide;
                EmitPendingCharAlone(State, Result, TRUE);
                State->ThumbPending = FALSE;
                State->ThumbUsed[side] = TRUE;
                EmitChar(State, Result, ScanCode, PlaneForSide(side), TRUE);
            }
        } else {
            // 親指s → 文字a → 文字b: s+a は確定、b は新たに保留
            EmitPendingPair(State, Result, TRUE);
            StartChar(State, Params, ScanCode, NowMs, Result);
        }
        return;
    }

    if (State->CharPending) {
        // 規格5.3 図3
        EmitPendingCharAlone(State, Result, TRUE);
        StartChar(State, Params, ScanCode, NowMs, Result);
        return;
    }

    if (State->ThumbPending) {
        if (NowMs - State->ThumbTime > Params->SimulTermMs) {
            // 連続シフト時のみ到達する（通常は期限切れで単独打鍵に確定済み）:
            // 押しっぱなしの親指キーと即座に組む。
            UCHAR side = State->ThumbSide;
            State->ThumbPending = FALSE;
            State->ThumbUsed[side] = TRUE;
            EmitChar(State, Result, ScanCode, PlaneForSide(side), TRUE);
            return;
        }
        // 規格5.4.1 図4（前置型）: 3キー目を待つため保留する
        State->CharPending = TRUE;
        State->CharScan = ScanCode;
        State->CharTime = NowMs;
        State->CharFirst = FALSE;
        return;
    }

    StartChar(State, Params, ScanCode, NowMs, Result);
}

static VOID
OnThumbMake(
    _Inout_ PNICOLA_CORE_STATE State,
    _In_ UCHAR Side,
    _In_ ULONGLONG NowMs,
    _Inout_ PNICOLA_CORE_RESULT Result
    )
{
    State->ThumbDown[Side] = TRUE;
    State->ThumbDownTime[Side] = NowMs;
    State->ThumbUsed[Side] = FALSE;
    State->ThumbTapHeld[Side] = FALSE;

    if (State->CharPending && State->ThumbPending) {
        if (State->CharFirst) {
            // 文字a → 親指s → 親指x: a+s は確定、x を保留
            EmitPendingPair(State, Result, TRUE);
        } else {
            // 規格5.4.2 後段: 親指s → 文字a → 親指x
            ULONGLONG t1 = State->CharTime - State->ThumbTime;
            ULONGLONG t2 = NowMs - State->CharTime;
            if (t1 <= t2) {
                EmitPendingPair(State, Result, TRUE);
            } else {
                // s は単独打鍵、a は x と組む候補（MS）として保留を続ける
                EmitPendingThumbTap(State, Result);
                State->ThumbPending = TRUE;
                State->ThumbSide = Side;
                State->ThumbTime = NowMs;
                State->CharFirst = TRUE;
                return;
            }
        }
    } else if (State->ThumbPending) {
        EmitPendingThumbTap(State, Result);
    } else if (State->CharPending) {
        // 規格5.4.1 図5（後置型）
        State->ThumbPending = TRUE;
        State->ThumbSide = Side;
        State->ThumbTime = NowMs;
        State->CharFirst = TRUE;
        return;
    }

    State->ThumbPending = TRUE;
    State->ThumbSide = Side;
    State->ThumbTime = NowMs;
}

static VOID
OnCharBreak(
    _Inout_ PNICOLA_CORE_STATE State,
    _In_ const NICOLA_CORE_PARAMS* Params,
    _In_ USHORT ScanCode,
    _In_ ULONGLONG NowMs,
    _Inout_ PNICOLA_CORE_RESULT Result
    )
{
    if (!State->CharPending || State->CharScan != ScanCode) {
        // 確定済みの文字キーの解放。送出はmake/breakの組で済んでいるので何もしない。
        if (State->LastCharScan == ScanCode) {
            State->LastCharDown = FALSE;
        }
        return;
    }

    if (!State->ThumbPending) {
        // 規格5.3 図2
        EmitPendingCharAlone(State, Result, FALSE);
        return;
    }

    if (State->CharFirst) {
        // 規格5.5.2 図7: 文字a → 親指s → 文字a OFF
        ULONGLONG t1 = State->ThumbTime - State->CharTime;
        ULONGLONG t2 = NowMs - State->ThumbTime;
        if (IsSequential(Params, t1, t2)) {
            // 逐次打鍵: a は単独、s はまだ保留（この後の文字キーと組みうる）
            EmitPendingCharAlone(State, Result, FALSE);
            return;
        }
    }
    EmitPendingPair(State, Result, FALSE);
}

static VOID
OnThumbBreak(
    _Inout_ PNICOLA_CORE_STATE State,
    _In_ const NICOLA_CORE_PARAMS* Params,
    _In_ UCHAR Side,
    _In_ ULONGLONG NowMs,
    _Inout_ PNICOLA_CORE_RESULT Result
    )
{
    State->ThumbDown[Side] = FALSE;

    if (State->ThumbPending && State->ThumbSide == Side) {
        if (!State->CharPending) {
            EmitPendingThumbTap(State, Result);
        } else if (State->CharFirst) {
            EmitPendingPair(State, Result, TRUE);
        } else {
            // 規格5.5.2と対称: 親指s → 文字a → 親指s OFF
            ULONGLONG t1 = State->CharTime - State->ThumbTime;
            ULONGLONG t2 = NowMs - State->CharTime;
            if (IsSequential(Params, t1, t2)) {
                // 逐次打鍵: s は単独打鍵、a は単独か同時かをまだ保留する
                EmitPendingThumbTap(State, Result);
            } else {
                EmitPendingPair(State, Result, TRUE);
            }
        }
    } else if (State->ThumbTapHeld[Side]) {
        Push(Result, NICOLA_OUT_TAP_BREAK, Side, 0);
    }

    State->ThumbTapHeld[Side] = FALSE;
    State->ThumbUsed[Side] = FALSE;
}

// 押しっぱなしによるオートリピート（既に押下を飲み込んだキーの再make）。
static VOID
OnRepeat(
    _Inout_ PNICOLA_CORE_STATE State,
    _In_ NICOLA_KEY_KIND Kind,
    _In_ USHORT ScanCode,
    _Inout_ PNICOLA_CORE_RESULT Result
    )
{
    if (Kind == NICOLA_KEY_CHAR) {
        if (State->CharPending && State->CharScan == ScanCode) {
            return; // まだ判定中
        }
        if (State->LastCharDown && State->LastCharScan == ScanCode) {
            // 参考7: リピート時は確定済みの出力を繰り返す
            Push(Result, NICOLA_OUT_CHAR, State->LastCharPlane, ScanCode);
        }
        return;
    }

    {
        UCHAR side = (Kind == NICOLA_KEY_THUMB_LEFT) ? NICOLA_SIDE_LEFT : NICOLA_SIDE_RIGHT;
        if (State->ThumbTapHeld[side]) {
            Push(Result, NICOLA_OUT_TAP_MAKE, side, 0);
        }
    }
}

VOID
NicolaCoreInit(
    _Out_ PNICOLA_CORE_STATE State
    )
{
    RtlZeroMemory(State, sizeof(*State));
}

VOID
NicolaCoreOnKey(
    _Inout_ PNICOLA_CORE_STATE State,
    _In_ const NICOLA_CORE_PARAMS* Params,
    _In_ NICOLA_KEY_KIND Kind,
    _In_ USHORT ScanCode,
    _In_ BOOLEAN IsBreak,
    _In_ ULONGLONG NowMs,
    _Out_ PNICOLA_CORE_RESULT Result
    )
{
    Result->Count = 0;

    ProcessExpired(State, Params, NowMs, Result);

    if (Kind == NICOLA_KEY_OTHER) {
        // 対象外のキーが割り込んだら、保留を先に確定させて順序を守る。
        FlushPending(State, Result);
        Push(Result, NICOLA_OUT_PASS, 0, 0);
        RecomputeDeadline(State, Params);
        Result->Deadline = State->Deadline;
        return;
    }

    if (IsBreak) {
        if (!IsOwned(State, ScanCode)) {
            // このエンジンが押下を飲み込んでいないキー（判定が有効になる前や、修飾キー
            // 押下中に押されたキー）の解放。OS側で押しっぱなしにならないよう流す。
            Push(Result, NICOLA_OUT_PASS, 0, 0);
        } else {
            SetOwned(State, ScanCode, FALSE);
            if (Kind == NICOLA_KEY_CHAR) {
                OnCharBreak(State, Params, ScanCode, NowMs, Result);
            } else {
                OnThumbBreak(State, Params,
                             (Kind == NICOLA_KEY_THUMB_LEFT) ? NICOLA_SIDE_LEFT : NICOLA_SIDE_RIGHT,
                             NowMs, Result);
            }
        }
    } else if (IsOwned(State, ScanCode)) {
        OnRepeat(State, Kind, ScanCode, Result);
    } else {
        SetOwned(State, ScanCode, TRUE);
        if (Kind == NICOLA_KEY_CHAR) {
            OnCharMake(State, Params, ScanCode, NowMs, Result);
        } else {
            OnThumbMake(State,
                        (Kind == NICOLA_KEY_THUMB_LEFT) ? NICOLA_SIDE_LEFT : NICOLA_SIDE_RIGHT,
                        NowMs, Result);
        }
    }

    RecomputeDeadline(State, Params);
    ProcessExpired(State, Params, NowMs, Result);
    Result->Deadline = State->Deadline;
}

VOID
NicolaCoreOnTimeout(
    _Inout_ PNICOLA_CORE_STATE State,
    _In_ const NICOLA_CORE_PARAMS* Params,
    _In_ ULONGLONG NowMs,
    _Out_ PNICOLA_CORE_RESULT Result
    )
{
    Result->Count = 0;
    ProcessExpired(State, Params, NowMs, Result);
    Result->Deadline = State->Deadline;
}

VOID
NicolaCoreReset(
    _Inout_ PNICOLA_CORE_STATE State,
    _Out_ PNICOLA_CORE_RESULT Result
    )
{
    Result->Count = 0;
    FlushPending(State, Result);
    for (UCHAR side = 0; side < 2; side++) {
        if (State->ThumbTapHeld[side]) {
            Push(Result, NICOLA_OUT_TAP_BREAK, side, 0);
        }
    }
    RtlZeroMemory(State, sizeof(*State));
    Result->Deadline = 0;
}
