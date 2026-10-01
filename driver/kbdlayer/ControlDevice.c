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

// コントロールデバイスのセキュリティ記述子（SDDL）。
// - WD (Everyone): GR（読み取り=オープン可）のみ
//   -> 一覧・設定の読み出し・ON/OFF（FILE_READ_ACCESSのIOCTL）は管理者権限なしで呼べる。
// - SY (SYSTEM) / BA (Administrators): GA (フルコントロール)
//   -> 設定の書き換え・キャプチャ（FILE_WRITE_ACCESSのIOCTL）は昇格済みのプロセスだけ。
// 一般ユーザー権限でキーロガー（CAPTURE_NEXT_KEY）やUIPIを迂回したキー注入
// （SET_MACRO/SET_LAYERMAP）ができないようにするため（.plans/ioctl-access-control-review.md）。
// IOCTLごとのアクセスビットは common/kbdlayer_ioctl.h を参照。
static const UNICODE_STRING g_ControlDeviceSddl =
    RTL_CONSTANT_STRING(L"D:P(A;;GR;;;WD)(A;;GA;;;SY)(A;;GA;;;BA)");

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

    // 緊急時の解除手段（項番3）のワークアイテムは、紐づけたg_ControlDeviceObjectを
    // 削除する前に解放する。
    TeardownEmergencyReleaseGlobal();

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
        // そのハンドルで保留中のWAIT_LAYER_CHANGEを終わらせ、そのハンドルが立てた
        // アプリ管理レイヤーを下ろす（アプリが落ちてもレイヤーが立ちっぱなしにならない）。
        LayerNotifyCleanupOwner(irpSp->FileObject);
        ReleaseAppLayersForOwner(irpSp->FileObject);
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
    _In_ PIRP Irp,
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

    devExt = FindDeviceExtensionByUniqId(target->UniqId, Irp);
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
        goto Done;
    }
    if (!NT_SUCCESS(status)) {
        goto Done;
    }

    out->BlobLength = blobBytes;
    *BytesReturned = sizeof(KBDLAYER_GET_MAP_OUTPUT) + blobBytes;
    status = STATUS_SUCCESS;

Done:
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return status;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetLayerMap(
    _In_ PIRP Irp,
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
    NTSTATUS status;

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

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    blob = (PUCHAR)SystemBuffer + sizeof(KBDLAYER_SET_MAP_INPUT);
    status = SetLayerMapForDevice(devExt, blob, blobLength);
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return status;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleClearLayerMap(
    _In_ PIRP Irp,
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_TARGET target = (PKBDLAYER_TARGET)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    NTSTATUS status;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_TARGET)) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(target->UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    status = ClearLayerMapForDevice(devExt);
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return status;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetLayerMapEnabled(
    _In_ PIRP Irp,
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_ENABLED_INPUT input = (PKBDLAYER_SET_ENABLED_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    NTSTATUS status;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_SET_ENABLED_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    status = SetLayerMapEnabledForDevice(devExt, input->Enabled != 0);
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return status;
}

// E-2: DeviceIdは一意でない前提で設計する（common/kbdlayer_ioctl.hのコメント参照）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleGetDeviceScope(
    _In_ PIRP Irp,
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
    if (outputLen < sizeof(KBDLAYER_DEVICE_SCOPE)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    devExt = FindDeviceExtensionByUniqId(target->UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // METHOD_BUFFERED: 読み終えたtargetの上に出力を書く。
    GetDeviceScopeForDevice(devExt, (PKBDLAYER_DEVICE_SCOPE)SystemBuffer);
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);

    *BytesReturned = sizeof(KBDLAYER_DEVICE_SCOPE);
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetDeviceScope(
    _In_ PIRP Irp,
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outputLen = IrpSp->Parameters.DeviceIoControl.OutputBufferLength;
    PKBDLAYER_SET_DEVICE_SCOPE_INPUT input = (PKBDLAYER_SET_DEVICE_SCOPE_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    NTSTATUS status;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_SET_DEVICE_SCOPE_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    status = SetDeviceScopeForDevice(devExt, input->Binding);
    if (NT_SUCCESS(status) && outputLen >= sizeof(KBDLAYER_DEVICE_SCOPE)) {
        // METHOD_BUFFERED: 読み終えたinputの上に出力を書く（任意）。
        GetDeviceScopeForDevice(devExt, (PKBDLAYER_DEVICE_SCOPE)SystemBuffer);
        *BytesReturned = sizeof(KBDLAYER_DEVICE_SCOPE);
    }
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return status;
}

// 機能別ON/OFF（項番4）。キーボードごとの設定。
_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleGetFeatures(
    _In_ PIRP Irp,
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outputLen = IrpSp->Parameters.DeviceIoControl.OutputBufferLength;
    PKBDLAYER_TARGET target = (PKBDLAYER_TARGET)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    ULONG features;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_TARGET)) {
        return STATUS_INVALID_PARAMETER;
    }
    if (outputLen < sizeof(KBDLAYER_GET_FEATURES_OUTPUT)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    devExt = FindDeviceExtensionByUniqId(target->UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    features = GetFeaturesForDevice(devExt);
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);

    // METHOD_BUFFERED: 読み終えたtargetの上に出力を書く。
    ((PKBDLAYER_GET_FEATURES_OUTPUT)SystemBuffer)->Features = features;
    *BytesReturned = sizeof(KBDLAYER_GET_FEATURES_OUTPUT);
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetFeatures(
    _In_ PIRP Irp,
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_FEATURES_INPUT input = (PKBDLAYER_SET_FEATURES_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    NTSTATUS status;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_SET_FEATURES_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    status = SetFeaturesForDevice(devExt, input->Features);
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return status;
}

// 緊急時の解除手段（項番3）。ドライバ全体の設定なので、特定のキーボード（Target）を
// 取らない（FindDeviceExtensionByUniqId・remove lockは不要）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleGetEmergencyRelease(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG outputLen = IrpSp->Parameters.DeviceIoControl.OutputBufferLength;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || outputLen < sizeof(KBDLAYER_EMERGENCY_RELEASE_CONFIG)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    GetEmergencyReleaseConfig((PKBDLAYER_EMERGENCY_RELEASE_CONFIG)SystemBuffer);
    *BytesReturned = sizeof(KBDLAYER_EMERGENCY_RELEASE_CONFIG);
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetEmergencyRelease(
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_EMERGENCY_RELEASE_CONFIG input = (PKBDLAYER_EMERGENCY_RELEASE_CONFIG)SystemBuffer;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_EMERGENCY_RELEASE_CONFIG)) {
        return STATUS_INVALID_PARAMETER;
    }

    return SetEmergencyReleaseConfig(input);
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetConfig(
    _In_ PIRP Irp,
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_CONFIG_INPUT input = (PKBDLAYER_SET_CONFIG_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    NTSTATUS status;

    *BytesReturned = 0;

    // 旧サイズ（v1: TapTerm/ComboTerm/TapDanceTermだけ）の入力も受け付ける（古いKeymapTool・GUI）。
    if (SystemBuffer == NULL || inputLen < KBDLAYER_SET_CONFIG_INPUT_V1_SIZE) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // v2の項目は範囲を先に検証する（不正なら、v1の項目も含めて何も変えない）。
    if (inputLen >= sizeof(KBDLAYER_SET_CONFIG_INPUT)) {
        status = SetTapHoldOptionsForDevice(devExt, input->TapHoldMode, input->QuickTapTermMs,
                                            input->TappingToggle);
        if (!NT_SUCCESS(status)) {
            goto Done;
        }
    }

    status = SetTapTermForDevice(devExt, input->TapTermMs);
    if (!NT_SUCCESS(status)) {
        goto Done;
    }
    status = SetComboTermForDevice(devExt, input->ComboTermMs);
    if (!NT_SUCCESS(status)) {
        goto Done;
    }
    status = SetTapDanceTermForDevice(devExt, input->TapDanceTermMs);

Done:
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return status;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetMacro(
    _In_ PIRP Irp,
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_MACRO_INPUT input = (PKBDLAYER_SET_MACRO_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    PKBDLAYER_MACRO_STEP steps;
    NTSTATUS status;

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

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    steps = (input->StepCount > 0)
        ? (PKBDLAYER_MACRO_STEP)((PUCHAR)SystemBuffer + sizeof(KBDLAYER_SET_MACRO_INPUT))
        : NULL;

    status = SetMacroForDevice(devExt, input->MacroId, steps, input->StepCount);
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return status;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleGetMacro(
    _In_ PIRP Irp,
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

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId, Irp);
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
        goto Done;
    }
    if (!NT_SUCCESS(status)) {
        goto Done;
    }

    out->StepCount = stepCount;
    *BytesReturned = sizeof(KBDLAYER_GET_MACRO_OUTPUT) + stepBytes;
    status = STATUS_SUCCESS;

Done:
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return status;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetCombo(
    _In_ PIRP Irp,
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_COMBO_INPUT input = (PKBDLAYER_SET_COMBO_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    NTSTATUS status;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_SET_COMBO_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // Entry.OutputAction.Type==NONEはクリアの合図（KeymapTool/IoctlClient.cpp参照）。
    if (input->Entry.OutputAction.Type == KBDLAYER_ACTION_NONE) {
        status = SetComboForDevice(devExt, input->ComboId, NULL);
    } else {
        status = SetComboForDevice(devExt, input->ComboId, &input->Entry);
    }
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return status;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleGetCombo(
    _In_ PIRP Irp,
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

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    status = GetComboForDevice(devExt, input->ComboId, &entry, &used);
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
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
    _In_ PIRP Irp,
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_TAPDANCE_INPUT input = (PKBDLAYER_SET_TAPDANCE_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    NTSTATUS status;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_SET_TAPDANCE_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // Entry.SingleTapAction.Type==NONEはクリアの合図（KeymapTool/IoctlClient.cpp参照）。
    if (input->Entry.SingleTapAction.Type == KBDLAYER_ACTION_NONE) {
        status = SetTapDanceForDevice(devExt, input->TapDanceId, NULL);
    } else {
        status = SetTapDanceForDevice(devExt, input->TapDanceId, &input->Entry);
    }
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return status;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleGetTapDance(
    _In_ PIRP Irp,
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

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    status = GetTapDanceForDevice(devExt, input->TapDanceId, &entry, &used);
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
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

// キャリブレーションウィザード専用。対象デバイスの次の物理キー押下1回分の
// スキャンコードを返す。IRPは保留（STATUS_PENDING）にし、キー到着・タイムアウト・
// キャンセル（プロセス終了等）・ハンドルのクローズ・デバイス削除のいずれかで
// LayerEngine.c側が完了させる（StartCaptureNextKey参照）。
// FindDeviceExtensionByUniqIdが取得したremove lockを、そのまま保留IRPに持たせる。
// 保留中はIRPがdevExt->RemoveLockを保持し続け、完了時に解放する。これで待機中に
// キーボードが取り外されてもdevExtは解放されない（DispatchPnpのREMOVE_DEVICEは、
// 先にAbortCaptureNextKeyで保留IRPを完了させてからIoReleaseRemoveLockAndWaitする）。
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
    PDEVICE_EXTENSION devExt;
    NTSTATUS status;
    ULONG timeoutMs;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_CAPTURE_NEXT_KEY_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }
    if (outputLen < sizeof(KBDLAYER_CAPTURE_NEXT_KEY_OUTPUT)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    timeoutMs = input->TimeoutMs != 0 ? input->TimeoutMs : KBDLAYER_CAPTURE_DEFAULT_TIMEOUT_MS;

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // STATUS_PENDINGなら、IRPの完了とremove lockの解放はLayerEngine.c側が行う。
    status = StartCaptureNextKey(devExt, Irp, timeoutMs);
    if (status != STATUS_PENDING) {
        IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    }
    return status;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetCaptureSuppress(
    _In_ PIRP Irp,
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_CAPTURE_SUPPRESS_INPUT input = (PKBDLAYER_SET_CAPTURE_SUPPRESS_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    NTSTATUS status;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_SET_CAPTURE_SUPPRESS_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // 抑止の持ち主はこのハンドル（FILE_OBJECT）。クローズ時のIRP_MJ_CLEANUPで
    // DispatchControlCreateCloseが同じFILE_OBJECTをキーに一括解除する。
    status = SetCaptureSuppressForDevice(devExt, input->Enable != 0, IrpSp->FileObject);
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return status;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleGetConfig(
    _In_ PIRP Irp,
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
    // 旧サイズ（v1）の出力バッファなら、先頭の3項目だけを返す（古いKeymapTool・GUI）。
    if (outputLen < KBDLAYER_GET_CONFIG_OUTPUT_V1_SIZE) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    devExt = FindDeviceExtensionByUniqId(target->UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // METHOD_BUFFERED: 入力(target)と出力は同じSystemBufferを共有する。
    // target->UniqIdは既に読み終えているので、そのまま上書きしてよい。
    {
        KBDLAYER_GET_CONFIG_OUTPUT config;
        ULONG copyLen = (outputLen >= sizeof(config)) ? sizeof(config) : KBDLAYER_GET_CONFIG_OUTPUT_V1_SIZE;

        GetConfigForDevice(devExt, &config);
        RtlCopyMemory(SystemBuffer, &config, copyLen);
        *BytesReturned = copyLen;
    }
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return STATUS_SUCCESS;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetNicola(
    _In_ PIRP Irp,
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    PKBDLAYER_SET_NICOLA_INPUT input = (PKBDLAYER_SET_NICOLA_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    ULONG blobLength;
    NTSTATUS status;

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

    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    status = SetNicolaForDevice(
        devExt,
        blobLength > 0 ? (PUCHAR)SystemBuffer + sizeof(KBDLAYER_SET_NICOLA_INPUT) : NULL,
        blobLength);
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return status;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleGetNicola(
    _In_ PIRP Irp,
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

    devExt = FindDeviceExtensionByUniqId(target->UniqId, Irp);
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
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);

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

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleGetLayerState(
    _In_ PIRP Irp,
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
    if (outputLen < sizeof(KBDLAYER_LAYER_STATE)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    devExt = FindDeviceExtensionByUniqId(target->UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // METHOD_BUFFERED: 読み終えたtargetの上に出力を書く。
    GetLayerStateForDevice(devExt, (PKBDLAYER_LAYER_STATE)SystemBuffer);
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);

    *BytesReturned = sizeof(KBDLAYER_LAYER_STATE);
    return STATUS_SUCCESS;
}

// レイヤー状態の変化を待つ。IRPは保留（STATUS_PENDING）にし、LayerNotify.cが完了させる
// （HandleCaptureNextKeyと同じく、FindDeviceExtensionByUniqIdが取得したremove lockを
// そのまま保留IRPに持たせる）。
_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleWaitLayerChange(
    _In_ PIRP Irp,
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outputLen = IrpSp->Parameters.DeviceIoControl.OutputBufferLength;
    PKBDLAYER_WAIT_LAYER_CHANGE_INPUT input = (PKBDLAYER_WAIT_LAYER_CHANGE_INPUT)SystemBuffer;
    PDEVICE_EXTENSION devExt;
    ULONG lastSeq;
    NTSTATUS status;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_WAIT_LAYER_CHANGE_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }
    if (outputLen < sizeof(KBDLAYER_LAYER_STATE)) {
        return STATUS_BUFFER_TOO_SMALL;
    }

    // METHOD_BUFFERED: 出力は同じバッファに後で書かれるので、入力は先に読んでおく。
    lastSeq = input->LastSeq;
    devExt = FindDeviceExtensionByUniqId(input->Target.UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // STATUS_PENDINGなら、IRPの完了とremove lockの解放はLayerNotify.c側が行う。
    status = StartWaitLayerChange(devExt, Irp, lastSeq);
    if (status != STATUS_PENDING) {
        IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    }
    return status;
}

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
HandleSetLayerState(
    _In_ PIRP Irp,
    _In_ PIO_STACK_LOCATION IrpSp,
    _In_opt_ PVOID SystemBuffer,
    _Out_ PULONG BytesReturned
    )
{
    ULONG inputLen = IrpSp->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outputLen = IrpSp->Parameters.DeviceIoControl.OutputBufferLength;
    PKBDLAYER_SET_LAYER_STATE_INPUT input = (PKBDLAYER_SET_LAYER_STATE_INPUT)SystemBuffer;
    KBDLAYER_SET_LAYER_STATE_INPUT params;
    KBDLAYER_LAYER_STATE state;
    PDEVICE_EXTENSION devExt;
    NTSTATUS status;

    *BytesReturned = 0;

    if (SystemBuffer == NULL || inputLen < sizeof(KBDLAYER_SET_LAYER_STATE_INPUT)) {
        return STATUS_INVALID_PARAMETER;
    }
    // METHOD_BUFFERED: 出力（任意）が同じバッファに書かれるので、入力を先に写しておく。
    params = *input;

    devExt = FindDeviceExtensionByUniqId(params.Target.UniqId, Irp);
    if (devExt == NULL) {
        return STATUS_NOT_FOUND;
    }

    // このハンドル（FILE_OBJECT）がアプリ管理レイヤーの所有者になる。クローズ時の
    // IRP_MJ_CLEANUPでDispatchControlCreateCloseが同じFILE_OBJECTをキーに下ろす。
    status = SetAppLayerStateForDevice(devExt, params.Op, params.Layer, params.Mask, params.Flags,
                                       IrpSp->FileObject, &state);
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);

    if (NT_SUCCESS(status) && outputLen >= sizeof(KBDLAYER_LAYER_STATE)) {
        RtlCopyMemory(SystemBuffer, &state, sizeof(state));
        *BytesReturned = sizeof(KBDLAYER_LAYER_STATE);
    }
    return status;
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
        status = HandleGetLayerMap(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_LAYERMAP:
        status = HandleSetLayerMap(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_CLEAR_LAYERMAP:
        status = HandleClearLayerMap(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_LAYERMAP_ENABLED:
        status = HandleSetLayerMapEnabled(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_CAPTURE_NEXT_KEY:
        status = HandleCaptureNextKey(Irp, irpSp, systemBuffer, &bytesReturned);
        if (status == STATUS_PENDING) {
            // IRPは保留中（LayerEngine.cのStartCaptureNextKeyがIoMarkIrpPending済み）。
            // ここで完了させてはならない。
            return STATUS_PENDING;
        }
        break;
    case IOCTL_KBDLAYER_SET_CONFIG:
        status = HandleSetConfig(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_MACRO:
        status = HandleSetMacro(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_GET_MACRO:
        status = HandleGetMacro(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_COMBO:
        status = HandleSetCombo(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_GET_COMBO:
        status = HandleGetCombo(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_TAPDANCE:
        status = HandleSetTapDance(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_GET_TAPDANCE:
        status = HandleGetTapDance(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_CAPTURE_SUPPRESS:
        status = HandleSetCaptureSuppress(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_GET_CONFIG:
        status = HandleGetConfig(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_NICOLA:
        status = HandleSetNicola(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_GET_NICOLA:
        status = HandleGetNicola(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_GET_LAYER_STATE:
        status = HandleGetLayerState(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_WAIT_LAYER_CHANGE:
        status = HandleWaitLayerChange(Irp, irpSp, systemBuffer, &bytesReturned);
        if (status == STATUS_PENDING) {
            // IRPは保留中（LayerNotify.cのStartWaitLayerChangeがキューへ入れ、
            // IoMarkIrpPending済み）。ここで完了させてはならない。
            return STATUS_PENDING;
        }
        break;
    case IOCTL_KBDLAYER_SET_LAYER_STATE:
        status = HandleSetLayerState(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_GET_DEVICE_SCOPE:
        status = HandleGetDeviceScope(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_DEVICE_SCOPE:
        status = HandleSetDeviceScope(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_GET_EMERGENCY_RELEASE:
        status = HandleGetEmergencyRelease(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_EMERGENCY_RELEASE:
        status = HandleSetEmergencyRelease(irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_GET_FEATURES:
        status = HandleGetFeatures(Irp, irpSp, systemBuffer, &bytesReturned);
        break;
    case IOCTL_KBDLAYER_SET_FEATURES:
        status = HandleSetFeatures(Irp, irpSp, systemBuffer, &bytesReturned);
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
