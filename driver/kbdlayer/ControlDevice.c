// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

#include <ntddk.h>
#include <wdm.h>
#include <wdmsec.h>
#include <ntstrsafe.h>
#include "DeviceExtension.h"
#include "LayerEngine.h"
#include "ControlDevice.h"
#include "..\\common\\kbdlayer_ioctl.h"

#ifndef DBG
#define DbgPrint(...) ((void)0)
#endif

// IOCTL_KBDLAYER_CAPTURE_NEXT_KEY の入力でTimeoutMs=0が指定された場合に使う既定値。
#define KBDLAYER_CAPTURE_DEFAULT_TIMEOUT_MS 10000

PDEVICE_OBJECT g_ControlDeviceObject = NULL;

// コントロールデバイスの既定セキュリティ記述子（SDDL）。kbdaddidと同じ方針:
// - WD (Everyone): GRGWGX (読み取り・書き込み・実行=オープン) を許可
//   -> KeymapTool（キャリブレーションウィザード含む）を管理者権限なしで実行できるようにする。
// - SY (SYSTEM) / BA (Administrators): GA (フルコントロール)
static const UNICODE_STRING g_ControlDeviceSddl =
    RTL_CONSTANT_STRING(L"D:P(A;;GRGWGX;;;WD)(A;;GA;;;SY)(A;;GA;;;BA)");

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
CreateControlDevice(
    _In_ PDRIVER_OBJECT DriverObject
    )
{
    UNICODE_STRING deviceName;
    UNICODE_STRING symLinkName;
    NTSTATUS status;

    PAGED_CODE();

    RtlInitUnicodeString(&deviceName, KBDLAYER_CONTROL_DEVICE_NT_NAME);

    status = IoCreateDeviceSecure(
        DriverObject,
        0,
        &deviceName,
        FILE_DEVICE_KBDLAYER,
        FILE_DEVICE_SECURE_OPEN,
        FALSE,
        &g_ControlDeviceSddl,
        NULL,
        &g_ControlDeviceObject);

    if (!NT_SUCCESS(status)) {
        KdPrint(("kbdlayer: CreateControlDevice: IoCreateDeviceSecure failed, status=0x%08X\n", status));
        g_ControlDeviceObject = NULL;
        return status;
    }

    RtlInitUnicodeString(&symLinkName, KBDLAYER_CONTROL_SYMLINK_NT_NAME);
    status = IoCreateSymbolicLink(&symLinkName, &deviceName);
    if (!NT_SUCCESS(status)) {
        KdPrint(("kbdlayer: CreateControlDevice: IoCreateSymbolicLink failed, status=0x%08X\n", status));
        IoDeleteDevice(g_ControlDeviceObject);
        g_ControlDeviceObject = NULL;
        return status;
    }

    // DriverEntryから直接作られる制御デバイスはPnPのAddDeviceを経由しないため、
    // 自分でDO_DEVICE_INITIALIZINGを落とす必要がある。
    g_ControlDeviceObject->Flags &= ~DO_DEVICE_INITIALIZING;

    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
VOID
TeardownControlDevice(
    VOID
    )
{
    UNICODE_STRING symLinkName;

    PAGED_CODE();

    if (g_ControlDeviceObject == NULL) {
        return;
    }

    RtlInitUnicodeString(&symLinkName, KBDLAYER_CONTROL_SYMLINK_NT_NAME);
    IoDeleteSymbolicLink(&symLinkName);

    IoDeleteDevice(g_ControlDeviceObject);
    g_ControlDeviceObject = NULL;
}

// IRP_MJ_CREATE/CLOSE/CLEANUP等（Driver.cのDispatchPass経由で届く）。
// CLEANUPはハンドルの最後のクローズ時（プロセス終了・クラッシュ時を含む）に
// 必ず届くため、そのハンドルが有効化していた入力抑止をここで解除する。
NTSTATUS
DispatchControlCreateClose(
    IN PDEVICE_OBJECT DeviceObject,
    IN PIRP Irp
    )
{
    PIO_STACK_LOCATION irpSp = IoGetCurrentIrpStackLocation(Irp);

    UNREFERENCED_PARAMETER(DeviceObject);

    if (irpSp->MajorFunction == IRP_MJ_CLEANUP && irpSp->FileObject != NULL) {
        ReleaseCaptureSuppressForOwner(irpSp->FileObject);
    }

    Irp->IoStatus.Status = STATUS_SUCCESS;
    Irp->IoStatus.Information = 0;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleListDevices(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG outputLen = IrpSp->Parameters.DeviceIoControl.OutputBufferLength;
    ULONG count = 0;
    ULONG required;
    KIRQL oldIrql;
    PLIST_ENTRY entry;
    PDEVICE_EXTENSION devExt;
    PKBDLAYER_LIST_DEVICES_OUTPUT out = (PKBDLAYER_LIST_DEVICES_OUTPUT)SystemBuffer;

    *BytesReturned = 0;

    KeAcquireSpinLock(&g_listLock, &oldIrql);

    for (entry = g_deviceList.Flink; entry != &g_deviceList; entry = entry->Flink) {
        devExt = CONTAINING_RECORD(entry, DEVICE_EXTENSION, ListEntry);
        if (devExt->DeviceId != 0) {
            count++;
        }
    }

    required = sizeof(KBDLAYER_LIST_DEVICES_OUTPUT) + count * sizeof(KBDLAYER_DEVICE_INFO);

    if (SystemBuffer == NULL || outputLen < required) {
        KeReleaseSpinLock(&g_listLock, oldIrql);
        *BytesReturned = required;
        return STATUS_BUFFER_TOO_SMALL;
    }

    out->DeviceCount = count;
    {
        PKBDLAYER_DEVICE_INFO infoArray =
            (PKBDLAYER_DEVICE_INFO)((PUCHAR)out + sizeof(KBDLAYER_LIST_DEVICES_OUTPUT));
        ULONG i = 0;

        for (entry = g_deviceList.Flink; entry != &g_deviceList; entry = entry->Flink) {
            devExt = CONTAINING_RECORD(entry, DEVICE_EXTENSION, ListEntry);
            if (devExt->DeviceId == 0) {
                continue;
            }
            infoArray[i].UniqId = devExt->DeviceId;
            infoArray[i].HasLayerMap = (devExt->LayerMap != NULL) ? 1 : 0;
            infoArray[i].Enabled = devExt->LayerMapEnabled ? 1 : 0;
            infoArray[i].ActiveLayerMask = devExt->ActiveLayerMask | (1UL << devExt->DefaultLayer);
            RtlCopyMemory(infoArray[i].HardwareId, devExt->HardwareId, sizeof(infoArray[i].HardwareId));
            i++;
        }
    }

    KeReleaseSpinLock(&g_listLock, oldIrql);

    *BytesReturned = required;
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleGetLayerMap(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outputLen = IrpSp->Parameters.DeviceIoControl.OutputBufferLength;
    PKBDLAYER_TARGET target = (PKBDLAYER_TARGET)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    PKBDLAYER_GET_MAP_OUTPUT out;
    ULONG blobCapacity;
    ULONG blobBytes = 0;
    NTSTATUS status;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_TARGET)) {
        return STATUS_INVALID_PARAMETER;
    }
    if (outputLen < sizeof(KBDLAYER_GET_MAP_OUTPUT)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    devExt = FindDeviceExtensionByUniqId(target->UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // METHOD_BUFFERED: 入力(target)と出力(out)は同じSystemBufferを共有する。
    // target->UniqIdは既に読み終えているので、そのまま上書きしてよい。
    out = (PKBDLAYER_GET_MAP_OUTPUT)SystemBuffer;
    blobCapacity = outputLen - sizeof(KBDLAYER_GET_MAP_OUTPUT);

    status = GetLayerMapForDevice(
        devExt,
        (PUCHAR)out + sizeof(KBDLAYER_GET_MAP_OUTPUT),
        blobCapacity,
        &blobBytes,
        NULL);

    if (status == STATUS_BUFFER_TOO_SMALL) {
        *BytesReturned = sizeof(KBDLAYER_GET_MAP_OUTPUT) + blobBytes;
        return STATUS_BUFFER_TOO_SMALL;
    }
    if (!NT_SUCCESS(status)) {
        return status;
    }

    out->BlobLength = blobBytes;
    *BytesReturned = sizeof(KBDLAYER_GET_MAP_OUTPUT) + blobBytes;
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetLayerMap(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_MAP_INPUT input = (PKBDLAYER_SET_MAP_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    PVOID blob;
    ULONG blobLength;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_SET_MAP_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }

    blobLength = input->BlobLength;
    if (blobLength < sizeof(KBDLAYER_MAP_HEADER) || blobLength > KBDLAYER_MAX_MAP_BLOB_BYTES) {
        return STATUS_INVALID_PARAMETER;
    }
    if (inputLen < sizeof(KBDLAYER_SET_MAP_INPUT) + blobLength) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    blob = (PUCHAR)SystemBuffer + sizeof(KBDLAYER_SET_MAP_INPUT);
    return SetLayerMapForDevice(devExt, blob, blobLength);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleClearLayerMap(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_TARGET target = (PKBDLAYER_TARGET)SystemBuffer;
    PDEVICE_EXTENSION devExt;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_TARGET)) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(target->UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    return ClearLayerMapForDevice(devExt);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetLayerMapEnabled(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_ENABLED_INPUT input = (PKBDLAYER_SET_ENABLED_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_SET_ENABLED_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    return SetLayerMapEnabledForDevice(devExt, input->Enabled != 0);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetConfig(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_CONFIG_INPUT input = (PKBDLAYER_SET_CONFIG_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_SET_CONFIG_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    {
        NTSTATUS status = SetTapTermForDevice(devExt, input->TapTermMs);
        if (!NT_SUCCESS(status)) {
            return status;
        }
        status = SetComboTermForDevice(devExt, input->ComboTermMs);
        if (!NT_SUCCESS(status)) {
            return status;
        }
        return SetTapDanceTermForDevice(devExt, input->TapDanceTermMs);
    }
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetMacro(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_MACRO_INPUT input = (PKBDLAYER_SET_MACRO_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    PKBDLAYER_MACRO_STEP steps;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_SET_MACRO_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }

    if (input->StepCount > KBDLAYER_MAX_MACRO_STEPS) {
        return STATUS_INVALID_PARAMETER;
    }
    if (inputLen < sizeof(KBDLAYER_SET_MACRO_INPUT) + input->StepCount * sizeof(KBDLAYER_MACRO_STEP)) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    steps = (input->StepCount > 0)
        ? (PKBDLAYER_MACRO_STEP)((PUCHAR)SystemBuffer + sizeof(KBDLAYER_SET_MACRO_INPUT))
        : NULL;

    return SetMacroForDevice(devExt, input->MacroId, steps, input->StepCount);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleGetMacro(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outputLen = IrpSp->Parameters.DeviceIoControl.OutputBufferLength;
    PKBDLAYER_GET_MACRO_INPUT input = (PKBDLAYER_GET_MACRO_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    PKBDLAYER_GET_MACRO_OUTPUT out;
    ULONG stepsCapacity;
    ULONG stepBytes = 0;
    ULONG stepCount = 0;
    NTSTATUS status;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_GET_MACRO_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }
    if (outputLen < sizeof(KBDLAYER_GET_MACRO_OUTPUT)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // METHOD_BUFFERED: 入力(input)と出力(out)は同じSystemBufferを共有する。
    // input->MacroIdは既に読み終えているので、そのまま上書きしてよい
    // （HandleGetLayerMapと同じパターン）。
    out = (PKBDLAYER_GET_MACRO_OUTPUT)SystemBuffer;
    stepsCapacity = outputLen - sizeof(KBDLAYER_GET_MACRO_OUTPUT);

    status = GetMacroForDevice(
        devExt,
        input->MacroId,
        (PUCHAR)out + sizeof(KBDLAYER_GET_MACRO_OUTPUT),
        stepsCapacity,
        &stepBytes,
        &stepCount);

    if (status == STATUS_BUFFER_TOO_SMALL) {
        *BytesReturned = sizeof(KBDLAYER_GET_MACRO_OUTPUT) + stepBytes;
        return STATUS_BUFFER_TOO_SMALL;
    }
    if (!NT_SUCCESS(status)) {
        return status;
    }

    out->StepCount = stepCount;
    *BytesReturned = sizeof(KBDLAYER_GET_MACRO_OUTPUT) + stepBytes;
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetCombo(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_COMBO_INPUT input = (PKBDLAYER_SET_COMBO_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_SET_COMBO_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // Entry.OutputAction.Type==NONEはクリアの合図（KeymapTool/IoctlClient.cpp参照）。
    if (input->Entry.OutputAction.Type == KBDLAYER_ACTION_NONE) {
        return SetComboForDevice(devExt, input->ComboId, NULL);
    }
    return SetComboForDevice(devExt, input->ComboId, &input->Entry);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleGetCombo(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outputLen = IrpSp->Parameters.DeviceIoControl.OutputBufferLength;
    PKBDLAYER_GET_COMBO_INPUT input = (PKBDLAYER_GET_COMBO_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    PKBDLAYER_GET_COMBO_OUTPUT out;
    KBDLAYER_COMBO_ENTRY entry;
    BOOLEAN used = FALSE;
    NTSTATUS status;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_GET_COMBO_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }
    if (outputLen < sizeof(KBDLAYER_GET_COMBO_OUTPUT)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    status = GetComboForDevice(devExt, input->ComboId, &entry, &used);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    // METHOD_BUFFERED: 入力(input)と出力(out)は同じSystemBufferを共有する
    // （HandleGetMacroと同じパターン）。
    out = (PKBDLAYER_GET_COMBO_OUTPUT)SystemBuffer;
    out->Used = used ? 1 : 0;
    out->Entry = entry;

    *BytesReturned = sizeof(KBDLAYER_GET_COMBO_OUTPUT);
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetTapDance(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_TAPDANCE_INPUT input = (PKBDLAYER_SET_TAPDANCE_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_SET_TAPDANCE_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // Entry.SingleTapAction.Type==NONEはクリアの合図（KeymapTool/IoctlClient.cpp参照）。
    if (input->Entry.SingleTapAction.Type == KBDLAYER_ACTION_NONE) {
        return SetTapDanceForDevice(devExt, input->TapDanceId, NULL);
    }
    return SetTapDanceForDevice(devExt, input->TapDanceId, &input->Entry);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleGetTapDance(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outputLen = IrpSp->Parameters.DeviceIoControl.OutputBufferLength;
    PKBDLAYER_GET_TAPDANCE_INPUT input = (PKBDLAYER_GET_TAPDANCE_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    PKBDLAYER_GET_TAPDANCE_OUTPUT out;
    KBDLAYER_TAPDANCE_ENTRY entry;
    BOOLEAN used = FALSE;
    NTSTATUS status;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_GET_TAPDANCE_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }
    if (outputLen < sizeof(KBDLAYER_GET_TAPDANCE_OUTPUT)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    status = GetTapDanceForDevice(devExt, input->TapDanceId, &entry, &used);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    // METHOD_BUFFERED: 入力(input)と出力(out)は同じSystemBufferを共有する
    // （HandleGetComboと同じパターン）。
    out = (PKBDLAYER_GET_TAPDANCE_OUTPUT)SystemBuffer;
    out->Used = used ? 1 : 0;
    out->Entry = entry;

    *BytesReturned = sizeof(KBDLAYER_GET_TAPDANCE_OUTPUT);
    return STATUS_SUCCESS;
}

// キャリブレーションウィザード専用。呼び出し元スレッドをPASSIVE_LEVELでブロックし、
// 対象デバイスの次の物理キー押下1回分のスキャンコードを返す。
// devExt->RemoveLockを保持したまま待機することで、待機中にキーボードが
// サプライズリムーブされてもdevExtが解放されないようにする
// （Driver.c DispatchPnpのIRP_MN_REMOVE_DEVICE処理はIoReleaseRemoveLockAndWaitで
// 既存のロック保持者がいなくなるのを待ってからLayerMap解放・devExt解放を行うため、
// このロックを保持している間はdevExtの寿命が保証される）。
// トレードオフ: このロックを保持している間（最大TimeoutMs、既定10秒）は
// DispatchPnpのREMOVE_DEVICE処理もそれだけブロックされる。IRPキャンセル
// ルーチンによる早期離脱はPhase 1のスコープでは実装しない
// （キャリブレーション中に対象キーボードを引き抜く操作は通常発生しないため、
// 実用上の影響は小さいと判断）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleCaptureNextKey(
    _In_ PIRP Irp,
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outputLen = IrpSp->Parameters.DeviceIoControl.OutputBufferLength;
    PKBDLAYER_CAPTURE_NEXT_KEY_INPUT input = (PKBDLAYER_CAPTURE_NEXT_KEY_INPUT)SystemBuffer;
    PKBDLAYER_CAPTURE_NEXT_KEY_OUTPUT out;
    PDEVICE_EXTENSION devExt;
    NTSTATUS status;
    NTSTATUS waitStatus;
    ULONG timeoutMs;
    LARGE_INTEGER timeout;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_CAPTURE_NEXT_KEY_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }
    if (outputLen < sizeof(KBDLAYER_CAPTURE_NEXT_KEY_OUTPUT)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    timeoutMs = input->TimeoutMs != 0 ? input->TimeoutMs : KBDLAYER_CAPTURE_DEFAULT_TIMEOUT_MS;

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    status = IoAcquireRemoveLock(&devExt->RemoveLock, Irp);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    status = BeginCaptureNextKey(devExt);
    if (!NT_SUCCESS(status)) {
        IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
        return status;
    }

    // 負の相対時間（100ns単位）。
    timeout.QuadPart = -((LONGLONG)timeoutMs * 10000LL);
    waitStatus = KeWaitForSingleObject(&devExt->CaptureEvent, Executive, KernelMode, FALSE, &timeout);

    out = (PKBDLAYER_CAPTURE_NEXT_KEY_OUTPUT)SystemBuffer;
    if (waitStatus == STATUS_TIMEOUT) {
        CancelCaptureNextKey(devExt);
        out->ScanCode = 0;
        out->TimedOut = 1;
    } else {
        out->ScanCode = ConsumeCapturedScanCode(devExt);
        out->TimedOut = 0;
    }

    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);

    *BytesReturned = sizeof(KBDLAYER_CAPTURE_NEXT_KEY_OUTPUT);
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetCaptureSuppress(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_CAPTURE_SUPPRESS_INPUT input = (PKBDLAYER_SET_CAPTURE_SUPPRESS_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_SET_CAPTURE_SUPPRESS_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // 抑止の持ち主はこのハンドル（FILE_OBJECT）。クローズ時のIRP_MJ_CLEANUPで
    // DispatchControlCreateCloseが同じFILE_OBJECTをキーに一括解除する。
    return SetCaptureSuppressForDevice(devExt, input->Enable != 0, IrpSp->FileObject);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleGetConfig(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outputLen = IrpSp->Parameters.DeviceIoControl.OutputBufferLength;
    PKBDLAYER_TARGET target = (PKBDLAYER_TARGET)SystemBuffer;
    PDEVICE_EXTENSION devExt;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_TARGET)) {
        return STATUS_INVALID_PARAMETER;
    }
    if (outputLen < sizeof(KBDLAYER_GET_CONFIG_OUTPUT)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    devExt = FindDeviceExtensionByUniqId(target->UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // METHOD_BUFFERED: 入力(target)と出力は同じSystemBufferを共有する。
    // target->UniqIdは既に読み終えているので、そのまま上書きしてよい。
    GetConfigForDevice(devExt, (PKBDLAYER_GET_CONFIG_OUTPUT)SystemBuffer);
    *BytesReturned = sizeof(KBDLAYER_GET_CONFIG_OUTPUT);
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetNicola(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_NICOLA_INPUT input = (PKBDLAYER_SET_NICOLA_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    ULONG blobLength;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_SET_NICOLA_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }

    // BlobLength=0 はクリア。それ以外の形式検証はSetNicolaForDevice側で行う。
    blobLength = input->BlobLength;
    if (blobLength > KBDLAYER_MAX_NICOLA_BLOB_BYTES) {
        return STATUS_INVALID_PARAMETER;
    }
    if (inputLen < sizeof(KBDLAYER_SET_NICOLA_INPUT) + blobLength) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    return SetNicolaForDevice(
        devExt,
        blobLength > 0 ? (PUCHAR)SystemBuffer + sizeof(KBDLAYER_SET_NICOLA_INPUT) : NULL,
        blobLength);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleGetNicola(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outputLen = IrpSp->Parameters.DeviceIoControl.OutputBufferLength;
    PKBDLAYER_TARGET target = (PKBDLAYER_TARGET)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    PKBDLAYER_GET_NICOLA_OUTPUT out;
    ULONG blobBytes = 0;
    NTSTATUS status;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_TARGET)) {
        return STATUS_INVALID_PARAMETER;
    }
    if (outputLen < sizeof(KBDLAYER_GET_NICOLA_OUTPUT)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    devExt = FindDeviceExtensionByUniqId(target->UniqId);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // METHOD_BUFFERED: HandleGetLayerMapと同じく、読み終えたtargetの上に出力を書く。
    out = (PKBDLAYER_GET_NICOLA_OUTPUT)SystemBuffer;
    status = GetNicolaForDevice(
        devExt,
        (PUCHAR)out + sizeof(KBDLAYER_GET_NICOLA_OUTPUT),
        outputLen - sizeof(KBDLAYER_GET_NICOLA_OUTPUT),
        &blobBytes);

    if (status == STATUS_BUFFER_TOO_SMALL) {
        *BytesReturned = sizeof(KBDLAYER_GET_NICOLA_OUTPUT) + blobBytes;
        return STATUS_BUFFER_TOO_SMALL;
    }
    if (!NT_SUCCESS(status)) {
        return status;
    }

    out->BlobLength = blobBytes;
    *BytesReturned = sizeof(KBDLAYER_GET_NICOLA_OUTPUT) + blobBytes;
    return STATUS_SUCCESS;
}

NTSTATUS
DispatchControlIoctl(
    IN PDEVICE_OBJECT DeviceObject,
    IN PIRP Irp
    )
{
    PIO_STACK_LOCATION irpSp = IoGetCurrentIrpStackLocation(Irp);
    ULONG code = irpSp->Parameters.DeviceIoControl.IoControlCode;
    PVOID systemBuffer = Irp->AssociatedIrp.SystemBuffer;
    NTSTATUS status;
    ULONG bytesReturned = 0;

    UNREFERENCED_PARAMETER(DeviceObject);

    switch (code) {
    case IOCTL_KBDLAYER_LIST_DEVICES:
        status = HandleListDevices(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_GET_LAYERMAP:
        status = HandleGetLayerMap(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_LAYERMAP:
        status = HandleSetLayerMap(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_CLEAR_LAYERMAP:
        status = HandleClearLayerMap(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_LAYERMAP_ENABLED:
        status = HandleSetLayerMapEnabled(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_CAPTURE_NEXT_KEY:
        status = HandleCaptureNextKey(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_CONFIG:
        status = HandleSetConfig(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_MACRO:
        status = HandleSetMacro(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_GET_MACRO:
        status = HandleGetMacro(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_COMBO:
        status = HandleSetCombo(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_GET_COMBO:
        status = HandleGetCombo(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_TAPDANCE:
        status = HandleSetTapDance(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_GET_TAPDANCE:
        status = HandleGetTapDance(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_CAPTURE_SUPPRESS:
        status = HandleSetCaptureSuppress(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_GET_CONFIG:
        status = HandleGetConfig(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_NICOLA:
        status = HandleSetNicola(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_GET_NICOLA:
        status = HandleGetNicola(irpSp, systemBuffer, &bytesReturned);
        break;
    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }

    Irp->IoStatus.Status = status;
    Irp->IoStatus.Information = bytesReturned;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return status;
}
