// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: Apache-2.0
//
// i8042prt\Parameters の書き換え／初期化（キーボードの接続時）。
// 仕様は common/kbdlayer_ioctl.h の KBDLAYER_I8042_CONFIG のコメント参照。

#ifndef _KBDLAYER_I8042_OVERRIDE_H_
#define _KBDLAYER_I8042_OVERRIDE_H_

#include <ntddk.h>
#include <wdm.h>
#include "..\\common\\kbdlayer_ioctl.h"

// DriverEntryから1回だけ呼ぶ: AddDeviceどうしの書き換えを直列化するミューテックスを初期化する。
VOID
InitializeI8042Override(
    VOID
    );

// AddDeviceから、ModelKey確定後に呼ぶ（PASSIVE_LEVEL。スピンロック保持中は不可）。
// Enabled=0、設定が無い・壊れている、ModelKey=0 のときは何もしない。失敗はAddDeviceの成否に
// 影響させない（DbgPrintExで記録するだけ）。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
ApplyI8042OverrideForDevice(
    _In_ ULONG ModelKey
    );

// GET_I8042_CONFIG の実体。設定が無ければ Enabled=0・ProfileCount=0 を返す。
_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
GetI8042OverrideConfig(
    _Out_ PKBDLAYER_I8042_CONFIG OutConfig
    );

// SET_I8042_CONFIG の実体。検証に失敗したら何も保存せず STATUS_INVALID_PARAMETER。
// 保存に失敗したらそのNTSTATUSを返す。
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
SetI8042OverrideConfig(
    _In_ const KBDLAYER_I8042_CONFIG* Config
    );

#endif // _KBDLAYER_I8042_OVERRIDE_H_
