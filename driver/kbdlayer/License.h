// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: Apache-2.0

/*
  kbdlayer/License.h

  ライセンスの状態はドライバ全体で1つ（トークンがマシン単位のため）。キーボードごとの
  DEVICE_EXTENSIONには持たない。

  起動直後のAddDeviceでは、ECDSA検証（BCrypt）が失敗する環境がある（同じトークンでも、起動の
  23秒後には成功する）。内蔵のACPI PS/2キーボードは起動時にしかAddDeviceが走らず、あとから
  やり直せないので、検証をAddDeviceから切り離した。AddDeviceは常にDeviceIdの算出と永続設定の
  復元を行い、ライセンスは「使うとき」にLicenseIsEffective()でゲートする。

  状態:
    VALID        検証に成功した。
    PROVISIONAL  前回はVALIDだった（Parameters\LicenseLastState=1）が、今回はまだ検証できていない。
                 期限（既定5分）まで有効。期限は判定側（LicenseIsEffective）が見るので、
                 再検証のタイマーが動かなくても、期限が過ぎれば自動で無効になる（フェイルクローズ）。
    INVALID      無効。

  再検証（ドライバ自身で完結。サービスに依存しない）:
    遅延タイマー(5秒) → DPC → 作業項目(PASSIVE_LEVEL)で、期限まで検証を繰り返す。
    成功でVALID。期限まで失敗が続けばINVALID。
  再検証IOCTL（サービスから。KBDLAYER_REVALIDATE_*）は、同期で検証する。

  INVALIDへの遷移では、全キーボードの生きた状態（押下中のmakeのbreak、保留中の判定、
  アクティブなレイヤー）を中止する。永続設定（LayerMaps\...\Enabled等）は書き換えない。

  買い切り版（KBDLAYER_PRO）は、常にVALID。タイマーも永続化も無い。
*/

#ifndef _KBDLAYER_LICENSE_H_
#define _KBDLAYER_LICENSE_H_

#include <ntddk.h>
#include <wdm.h>
#include "..\\common\\kbdlayer_ioctl.h"

#ifndef KBDLAYER_PRO
// LicenseIsEffective()のインライン展開のために公開している。License.c以外から書かないこと。
extern volatile LONG g_LicenseState;
extern volatile LONGLONG g_LicenseProvisionalDeadline; // KeQueryInterruptTime()基準（100ns単位）
#endif

// ライセンスが今、実効（機能を使ってよい）かどうか。DISPATCH_LEVELまでで呼べる（非ページ）。
// VALIDのときはvolatileの読み出し1回で済む（ServiceCallbackのホットパスから呼ばれる）。
_IRQL_requires_max_(DISPATCH_LEVEL)
FORCEINLINE
BOOLEAN
LicenseIsEffective(
    VOID
    )
{
#ifdef KBDLAYER_PRO
    return TRUE;
#else
    LONG state = g_LicenseState;

    if (state == (LONG)KBDLAYER_LICENSE_STATE_VALID) {
        return TRUE;
    }
    if (state == (LONG)KBDLAYER_LICENSE_STATE_PROVISIONAL) {
        return (LONGLONG)KeQueryInterruptTime() < g_LicenseProvisionalDeadline;
    }
    return FALSE;
#endif
}

// DriverEntryから呼ぶ（制御デバイスの作成の後）。ControlDeviceObjectは作業項目の確保に使う。
// NULLなら（制御デバイスの作成に失敗した場合）再検証はできず、暫定有効の期限で自動的に無効になる。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
InitializeLicense(
    _In_opt_ PDEVICE_OBJECT ControlDeviceObject
    );

// Unloadの最初に呼ぶ。タイマー取消 → DPCの完了待ち → 作業項目の完了待ち → 解放。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
TeardownLicense(
    VOID
    );

// 再検証IOCTLの実処理。ModeはKBDLAYER_REVALIDATE_*。戻りのStateは、再検証の後のKBDLAYER_LICENSE_STATE_*。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
LicenseRevalidate(
    _In_ ULONG Mode,
    _Out_ PULONG State
    );

// 現在の状態（診断用）。KBDLAYER_LICENSE_STATE_*。期限切れの暫定有効はINVALIDとして返す。
_IRQL_requires_max_(DISPATCH_LEVEL)
ULONG
LicenseGetState(
    VOID
    );

#endif // _KBDLAYER_LICENSE_H_
