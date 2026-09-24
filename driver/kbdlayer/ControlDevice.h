// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

/*
  kbdlayer/ControlDevice.h

  ユーザーモードから \\.\KbdLayerControl 経由でレイヤーマップを設定/取得し、
  キャリブレーションウィザード用のキー捕捉を行うための、名前付きコントロール
  デバイス。既存のper-キーボードフィルタFDO（無名）とは別に、DriverEntryで
  1つだけ作成される。kbdaddid/ControlDevice.h からのフォーク。
*/

#ifndef _KBDLAYER_CONTROLDEVICE_H_
#define _KBDLAYER_CONTROLDEVICE_H_

#include <ntddk.h>

extern PDEVICE_OBJECT g_ControlDeviceObject;

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
CreateControlDevice(
    _In_ PDRIVER_OBJECT DriverObject
    );

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
TeardownControlDevice(
    VOID
    );

_Dispatch_type_(IRP_MJ_CREATE)
_Dispatch_type_(IRP_MJ_CLOSE)
_Dispatch_type_(IRP_MJ_CLEANUP)
DRIVER_DISPATCH DispatchControlCreateClose;

_Dispatch_type_(IRP_MJ_DEVICE_CONTROL)
DRIVER_DISPATCH DispatchControlIoctl;

#endif // _KBDLAYER_CONTROLDEVICE_H_
