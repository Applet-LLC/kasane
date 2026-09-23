// Copyright (c) 2026 Applet LLC
// SPDX-License-Identifier: MIT

/*
  kbdlayer/Driver.c

  kbdaddid/Driver.c からのフォーク。WDMキーボードフィルタドライバの骨格
  （DriverEntry/AddDevice/DispatchPass/DispatchPnp/DispatchRead/
  DispatchInternalIoctl/ServiceCallback/Unload と、それらのIRQL・ロック・
  IoRemoveLock・PnP remove順序の規律）はそのまま踏襲し、ServiceCallback内の
  変換呼び出しだけをApplyRemapからLayerEngineのResolveLayerActionへ差し替えた。
*/

#include <ntddk.h>
#include <wdm.h>
#include <kbdmou.h>
#include <ntstrsafe.h>
#include "..\\common\\devprop.h"
#include "..\\common\\regutil.h"
#include "..\\common\\fallback.h"
#include "..\\common\\hash.h"
#include "..\\common\\kbdlayer_ioctl.h"
#include "DeviceExtension.h"
#include "LayerEngine.h"
#include "ControlDevice.h"
#include "token.h"

#ifndef DBG
#define DbgPrint(...) ((void)0)
#endif

#ifndef DevicePropertyHardwareId
#define DevicePropertyHardwareId 1
#endif
#ifndef DevicePropertyBusNumber
#define DevicePropertyBusNumber 21
#endif
#ifndef DevicePropertyAddress
#define DevicePropertyAddress 28
#endif
#ifndef DevicePropertyDeviceInstanceId
#define DevicePropertyDeviceInstanceId 12
#endif
#ifndef DevicePropertyContainerId
#define DevicePropertyContainerId 29
#endif
#ifndef DevicePropertyLocationInformation
#define DevicePropertyLocationInformation 10
#endif
#ifndef DevicePropertyPhysicalDeviceLocation
#define DevicePropertyPhysicalDeviceLocation 33
#endif
#ifndef DevicePropertyCompatibleIds
#define DevicePropertyCompatibleIds 2
#endif

// DEVICE_EXTENSION / KBDLAYER_MAP_TABLE の定義は DeviceExtension.h に移動
// （ControlDevice.c / LayerEngine.c と共有するため）。

LIST_ENTRY g_deviceList;
KSPIN_LOCK g_listLock;
static LONG g_uniqNumCounter = 0;

DRIVER_INITIALIZE DriverEntry;
DRIVER_ADD_DEVICE AddDevice;
_Dispatch_type_(IRP_MJ_CREATE)
_Dispatch_type_(IRP_MJ_CLOSE)
_Dispatch_type_(IRP_MJ_CREATE_NAMED_PIPE)
DRIVER_DISPATCH DispatchPass;
_Dispatch_type_(IRP_MJ_READ) DRIVER_DISPATCH DispatchRead;
_Dispatch_type_(IRP_MJ_INTERNAL_DEVICE_CONTROL) DRIVER_DISPATCH DispatchInternalIoctl;
_Dispatch_type_(IRP_MJ_PNP) DRIVER_DISPATCH DispatchPnp;
_Dispatch_type_(IRP_MJ_DEVICE_CONTROL) DRIVER_DISPATCH DispatchDeviceControl;
DRIVER_UNLOAD Unload;
NTSTATUS ReadComplete(IN PDEVICE_OBJECT DeviceObject, IN PIRP Irp, IN PVOID Context);
VOID ServiceCallback(PVOID NormalContext, PVOID SystemArgument1,
                     PVOID SystemArgument2, PVOID SystemArgument3);

// g_listLockを保護しつつg_deviceListを走査し、DeviceIdが一致する
// DEVICE_EXTENSIONを返す（見つからなければNULL）。宣言はDeviceExtension.h。
// ControlDevice.c / LayerEngine.cのIOCTLハンドラからUniqId指定でデバイスを
// 引くために使う。
_IRQL_requires_max_(DISPATCH_LEVEL)
PDEVICE_EXTENSION
FindDeviceExtensionByUniqId(
    _In_ ULONG UniqId
    )
{
    KIRQL oldIrql;
    PLIST_ENTRY entry;
    PDEVICE_EXTENSION found = NULL;

    if (UniqId == 0) {
        // DeviceId=0は「未識別（Licenseが無効等）」を表す特別値であり、
        // 有効な操作対象にはなり得ない。
        return NULL;
    }

    KeAcquireSpinLock(&g_listLock, &oldIrql);
    for (entry = g_deviceList.Flink; entry != &g_deviceList; entry = entry->Flink) {
        PDEVICE_EXTENSION devExt = CONTAINING_RECORD(entry, DEVICE_EXTENSION, ListEntry);
        if (devExt->DeviceId == UniqId) {
            found = devExt;
            break;
        }
    }
    KeReleaseSpinLock(&g_listLock, oldIrql);

    return found;
}

NTSTATUS DriverEntry(IN PDRIVER_OBJECT DriverObject, IN PUNICODE_STRING RegistryPath) {
    NTSTATUS controlStatus;

    UNREFERENCED_PARAMETER(RegistryPath);
    KdPrint(("kbdlayer: DriverEntry called\n"));

    InitializeListHead(&g_deviceList);
    KeInitializeSpinLock(&g_listLock);

    DriverObject->DriverExtension->AddDevice = AddDevice;
    DriverObject->DriverUnload = Unload;

    for (ULONG i = 0; i <= IRP_MJ_MAXIMUM_FUNCTION; i++) {
        DriverObject->MajorFunction[i] = DispatchPass;
    }
    DriverObject->MajorFunction[IRP_MJ_READ] = DispatchRead;
    DriverObject->MajorFunction[IRP_MJ_INTERNAL_DEVICE_CONTROL] = DispatchInternalIoctl;
    DriverObject->MajorFunction[IRP_MJ_PNP] = DispatchPnp;
    DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL] = DispatchDeviceControl;

    // コントロールデバイス（\\.\KbdLayerControl）の作成。失敗しても致命的エラーとは
    // せず、既存のUniqNumスタンプ機能自体はそのまま使えるようにする
    // （レイヤーマップ機能のみ利用不可になる）。
    controlStatus = CreateControlDevice(DriverObject);
    if (!NT_SUCCESS(controlStatus)) {
        KdPrint(("kbdlayer: DriverEntry: CreateControlDevice failed, status=0x%08X (continuing)\n", controlStatus));
    }

    return STATUS_SUCCESS;
}

static NTSTATUS ReadDeviceIdMode(ULONG* mode) {
    return RegReadDword(L"kbdlayer", L"DeviceIdMode", mode, 0);
}

NTSTATUS AddDevice(IN PDRIVER_OBJECT DriverObject, IN PDEVICE_OBJECT PhysicalDeviceObject) {
    NTSTATUS status;
    PDEVICE_OBJECT deviceObject = NULL;
    PDEVICE_EXTENSION devExt = NULL;
    BOOLEAN validPdoFound = FALSE;
    ULONG deviceIdMode = 0;

    KdPrint(("kbdlayer: AddDevice called for PDO=0x%p\n", PhysicalDeviceObject));
    DbgPrint("kbdlayer: DeviceType=0x%08X, Characteristics=0x%08X\n",
             PhysicalDeviceObject->DeviceType,
             PhysicalDeviceObject->Characteristics);

    // DeviceIdModeを取得
    ReadDeviceIdMode(&deviceIdMode);

    LONG uniqNum = InterlockedIncrement(&g_uniqNumCounter);

    status = IoCreateDevice(
        DriverObject,
        sizeof(DEVICE_EXTENSION),
        NULL,
        FILE_DEVICE_KEYBOARD,
        0,
        FALSE,
        &deviceObject
    );

    KdPrint(("kbdlayer: IoCreateDevice status: 0x%08X\n", status));
    if (!NT_SUCCESS(status)) {
        return status;
    }

    devExt = (PDEVICE_EXTENSION)deviceObject->DeviceExtension;
    RtlZeroMemory(devExt, sizeof(DEVICE_EXTENSION));
    devExt->LowerDevice = IoAttachDeviceToDeviceStack(deviceObject, PhysicalDeviceObject);
    KdPrint(("kbdlayer: IoAttachDeviceToDeviceStack result: 0x%p\n", devExt->LowerDevice));
    if (devExt->LowerDevice == NULL) {
        KdPrint(("kbdlayer: IoAttachDeviceToDeviceStack failed, deleting device and returning STATUS_NO_SUCH_DEVICE\n"));
        IoDeleteDevice(deviceObject);
        return STATUS_NO_SUCH_DEVICE;
    }

    IoInitializeRemoveLock(&devExt->RemoveLock, KBDLAYER_POOL_TAG, 0, 0);

    // レイヤーエンジン用の状態を初期化する。RtlZeroMemory済みなので
    // LayerMap=NULL・LayerMapEnabled=FALSE・ActiveLayerMask=0・DefaultLayer=0は
    // 既定で成立しているが、KSPIN_LOCK/KEVENTは明示的に初期化するのがWDKの作法。
    KeInitializeSpinLock(&devExt->EngineLock);
    devExt->LayerMap = NULL;
    devExt->LayerMapEnabled = FALSE;
    InitializeTapHoldState(devExt); // HoldTimer/HoldDpcの初期化含む（Phase 2）
    KeInitializeEvent(&devExt->CaptureEvent, NotificationEvent, FALSE);

    // LicenseTokenをECDSA検証してライセンス状態を決定（PASSIVE_LEVEL）
    {
        WCHAR tokenBuf[512] = {0};
        RegReadServiceString(L"kbdlayer", L"LicenseToken", tokenBuf, RTL_NUMBER_OF(tokenBuf));
        devExt->License = (tokenBuf[0] != 0 && ValidateLicenseToken(tokenBuf)) ? 1UL : 0UL;
        KBDLAYER_TRACE("AddDevice: token validation result License=%lu\n", devExt->License);
    }

    // License無効ならDeviceIdを0に設定して残りの処理をスキップ
    if (devExt->License == 0) {
        devExt->DeviceId = 0;
        KBDLAYER_TRACE("AddDevice: License=0 (disabled); DeviceId=0 for PDO=0x%p\n", PhysicalDeviceObject);
        KdPrint(("kbdlayer: License disabled; set DeviceId=0 and skip identification\n"));

        KIRQL oldIrql;
        KeAcquireSpinLock(&g_listLock, &oldIrql);
        InsertTailList(&g_deviceList, &devExt->ListEntry);
        KeReleaseSpinLock(&g_listLock, oldIrql);

        deviceObject->Flags |= devExt->LowerDevice->Flags & (DO_BUFFERED_IO | DO_DIRECT_IO | DO_POWER_PAGABLE);
        deviceObject->DeviceType = devExt->LowerDevice->DeviceType;
        deviceObject->Characteristics = devExt->LowerDevice->Characteristics;
        deviceObject->Flags &= ~DO_DEVICE_INITIALIZING;

        return STATUS_SUCCESS;
    }

    DbgPrint("kbdlayer: PhysicalDeviceObject=0x%p, Type=0x%08X, Characteristics=0x%08X, DeviceExtension=0x%p\n",
             PhysicalDeviceObject,
             PhysicalDeviceObject->DeviceType,
             PhysicalDeviceObject->Characteristics,
             devExt);

    // HardwareId取得
    WCHAR hwidBuf[256] = {0};
    ULONG hwidLen = sizeof(hwidBuf);
    NTSTATUS hwidStatus = IoGetDeviceProperty(
        PhysicalDeviceObject,
        DevicePropertyHardwareId,
        hwidLen,
        hwidBuf,
        &hwidLen
    );

    if (NT_SUCCESS(hwidStatus)) {
        WCHAR* ptr = hwidBuf;
        while (*ptr) {
            DbgPrint("kbdlayer: HardwareId=%ws\n", ptr);
            if (wcsncmp(ptr, L"HID\\", 4) == 0 || wcsncmp(ptr, L"ACPI\\", 5) == 0) {
                DbgPrint("kbdlayer: %ws device detected\n", wcsncmp(ptr, L"HID\\", 4) == 0 ? L"HID" : L"PS/2");
            }
            ptr += wcslen(ptr) + 1;
        }
        // キャッシュをデバイス拡張へコピー
        RtlCopyMemory(devExt->HardwareId, hwidBuf, hwidLen);
    } else {
        DbgPrint("kbdlayer: HardwareId not available, status=0x%08X\n", hwidStatus);
    }

    // デバイスインスタンスパスモード (DeviceIdMode=0)
    if (deviceIdMode == 0) {
        WCHAR instanceIdBuf[512] = {0};
        ULONG instanceIdLen = sizeof(instanceIdBuf);
        DEVPROPTYPE propertyType = 0;
        NTSTATUS instanceIdStatus = IoGetDevicePropertyData(
            PhysicalDeviceObject,
            &DEVPKEY_Device_InstanceId,
            0,
            0,
            instanceIdLen,
            instanceIdBuf,
            &instanceIdLen,
            &propertyType
        );

        if (NT_SUCCESS(instanceIdStatus) && instanceIdLen >= sizeof(WCHAR) && propertyType == DEVPROP_TYPE_STRING) {
            if (instanceIdBuf[instanceIdLen / sizeof(WCHAR) - 1] == L'\0') {
                BOOLEAN validString = TRUE;
                if (validString && instanceIdBuf[0] != L'\0') {
                    DbgPrint("kbdlayer: DeviceInstanceId=%ws (from IoGetDevicePropertyData, type=0x%08X)\n", instanceIdBuf, propertyType);
                    devExt->DeviceId = Fnv1aHash16Upper(instanceIdBuf);
                    KBDLAYER_TRACE("AddDevice: PDO=0x%p DeviceId=0x%08X InstanceId=%ws\n",
                                   PhysicalDeviceObject, devExt->DeviceId, instanceIdBuf);
                    DbgPrint("kbdlayer: DeviceId generated by mode=DeviceInstanceId: %08X\n", devExt->DeviceId);
                    validPdoFound = TRUE;
                } else {
                    DbgPrint("kbdlayer: DeviceInstanceId invalid, length=%lu bytes\n", instanceIdLen);
                }
            }
        } else {
            DbgPrint("kbdlayer: IoGetDevicePropertyData failed, status=0x%08X, required length=%lu, type=0x%08X\n",
                     instanceIdStatus, instanceIdLen, propertyType);
        }
    }

    // ハードウェアIDモード (DeviceIdMode=1)
    if (deviceIdMode == 1 && !validPdoFound) {
        if (NT_SUCCESS(hwidStatus) && hwidBuf[0]) {
            devExt->DeviceId = Fnv1aHash16Upper(hwidBuf);
            DbgPrint("kbdlayer: DeviceId generated by mode=HardwareId: %08X\n", devExt->DeviceId);
            validPdoFound = TRUE;
        } else {
            DbgPrint("kbdlayer: HardwareId not available for mode=HardwareId, status=0x%08X\n", hwidStatus);
        }
    }

    // フォールバック処理：共通のTry関数群へ移譲（TryQueryIdAsyncは使用しない）
    if (!validPdoFound) {
        DbgPrint("kbdlayer: Fallback: Normal fixed sequence (delegated to common/fallback)\n");

        PFALLBACK_TRY sequence[] = {
            TryContainerId,
            TryIoGetDeviceProperty,
            TryCompatibleIds,
            TryHardwareIdWithUniq,
            UseFixedValue,
            NULL
        };
        for (int i = 0; sequence[i] != NULL; ++i) {
            ULONG candidateId = 0;
            if (sequence[i](PhysicalDeviceObject, &candidateId, (ULONG)uniqNum)) {
                devExt->DeviceId = candidateId;
                validPdoFound = TRUE;
                break;
            }
        }
    }

    // DeviceIdが確定したので、永続化されているレイヤーマップがあればここで復元する
    // （PnP再認識・再起動時の自動復元）。まだg_deviceListに公開されていない
    // （他スレッドからの競合が無い）ため、EngineLockを取らずに直接devExtへ
    // 書き込んでよい。
    if (devExt->License != 0 && devExt->DeviceId != 0) {
        LoadPersistedLayerMapForDevice(devExt);
    }

    KIRQL oldIrql;
    KeAcquireSpinLock(&g_listLock, &oldIrql);
#if DBG
    if (wcsncmp(hwidBuf, L"HID\\", 4) == 0 || wcsncmp(hwidBuf, L"ACPI\\", 5) == 0) {
        DbgPrint("kbdlayer: %ws device (HardwareId=%ws) detected, using DeviceId: %08X\n",
                 wcsncmp(hwidBuf, L"HID\\", 4) == 0 ? L"HID" : L"PS/2", hwidBuf, devExt->DeviceId);
    } else {
        DbgPrint("kbdlayer: Non-HID device (HardwareId=%ws, DeviceType=0x%08X), using DeviceId: %08X\n",
                 hwidBuf, PhysicalDeviceObject->DeviceType, devExt->DeviceId);
    }
#endif
    InsertTailList(&g_deviceList, &devExt->ListEntry);
    KeReleaseSpinLock(&g_listLock, oldIrql);

    deviceObject->Flags |= devExt->LowerDevice->Flags & (DO_BUFFERED_IO | DO_DIRECT_IO | DO_POWER_PAGABLE);
    deviceObject->DeviceType = devExt->LowerDevice->DeviceType;
    deviceObject->Characteristics = devExt->LowerDevice->Characteristics;
    deviceObject->Flags &= ~DO_DEVICE_INITIALIZING;

    return STATUS_SUCCESS;
}

#define SAFE_FAIL_EXIT(s) do { status = (s); goto fail; } while(0)

NTSTATUS DispatchPass(IN PDEVICE_OBJECT DeviceObject, IN PIRP Irp) {
    PDEVICE_EXTENSION devExt;

    // コントロールデバイス（\\.\KbdLayerControl）宛のIRP_MJ_CREATE/CLOSE/
    // CREATE_NAMED_PIPEはここで捕捉する。コントロールデバイスにはLowerDeviceも
    // DEVICE_EXTENSIONも存在しないため、以降のフィルタFDO用処理に進んではならない。
    if (DeviceObject == g_ControlDeviceObject) {
        return DispatchControlCreateClose(DeviceObject, Irp);
    }

    devExt = (PDEVICE_EXTENSION)DeviceObject->DeviceExtension;
    IoSkipCurrentIrpStackLocation(Irp);
    return IoCallDriver(devExt->LowerDevice, Irp);
}

// IRP_MJ_DEVICE_CONTROL専用ディスパッチ。コントロールデバイス宛ならレイヤーマップの
// IOCTL群（ControlDevice.c）へ、それ以外（通常は発生しない）はフィルタFDOとして
// 素通しする。
NTSTATUS DispatchDeviceControl(IN PDEVICE_OBJECT DeviceObject, IN PIRP Irp) {
    PDEVICE_EXTENSION devExt;

    if (DeviceObject == g_ControlDeviceObject) {
        return DispatchControlIoctl(DeviceObject, Irp);
    }

    devExt = (PDEVICE_EXTENSION)DeviceObject->DeviceExtension;
    IoSkipCurrentIrpStackLocation(Irp);
    return IoCallDriver(devExt->LowerDevice, Irp);
}

// IRP_MJ_PNP専用ディスパッチ。IRP_MN_REMOVE_DEVICEを捕捉し、kbdclass.cの
// KeyboardPnPと同じ順序（remove lockをドレイン→下位へフォワード→自分の
// リンク解除/detach/delete）でフィルタのデバイスオブジェクトを解放する。
NTSTATUS DispatchPnp(IN PDEVICE_OBJECT DeviceObject, IN PIRP Irp) {
    PDEVICE_EXTENSION devExt = (PDEVICE_EXTENSION)DeviceObject->DeviceExtension;
    PIO_STACK_LOCATION irpStack = IoGetCurrentIrpStackLocation(Irp);
    NTSTATUS status;

    status = IoAcquireRemoveLock(&devExt->RemoveLock, Irp);
    if (!NT_SUCCESS(status)) {
        Irp->IoStatus.Status = status;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
        return status;
    }

    if (irpStack->MinorFunction == IRP_MN_REMOVE_DEVICE) {
        KIRQL oldIrql;

        // 以降のIoAcquireRemoveLock呼び出し元(DispatchRead/DispatchInternalIoctl/
        // ControlDevice.cのCAPTURE_NEXT_KEYハンドラ等)が新規に処理を開始しないように
        // した上で、既存の処理が抜けるのを待つ。
        IoReleaseRemoveLockAndWait(&devExt->RemoveLock, Irp);

        // 先に下位(port driver側)へREMOVE_DEVICEをフォワードする。PnPのremoveは
        // 上位から下位へ同期的に伝播するため、ここで下位のremoval処理を先に
        // 終わらせてから自分のFDOを消すことで、port driverに渡した
        // ServiceCallback関数ポインタ経由のuse-after-freeを避ける。
        IoSkipCurrentIrpStackLocation(Irp);
        status = IoCallDriver(devExt->LowerDevice, Irp);

        KeAcquireSpinLock(&g_listLock, &oldIrql);
        RemoveEntryList(&devExt->ListEntry);
        KeReleaseSpinLock(&g_listLock, oldIrql);

        // Phase 2: HoldTimer/HoldDpcはServiceCallbackの呼び出し経路とは独立に
        // （タイマー満了によって）DPCがキューされるため、上のIoCallDriver完了だけでは
        // 安全な解放を保証できない。KeCancelTimerでこれからの発火を止め、
        // KeFlushQueuedDpcsで既にキューされていたDPCの実行完了を待ってから
        // devExtの解放（このIRPの先のIoDeleteDeviceでのデバイス拡張領域解放）に進む。
        // HoldTapDpcRoutine自体はPendingActiveがFALSEなら何もしないので、この時点で
        // 発火しても安全だが、それでも「devExt解放後にDPCが動く」余地を残さないために
        // 明示的に同期する。
        KeCancelTimer(&devExt->HoldTimer);
        KeFlushQueuedDpcs();

        // この時点ではport driver側のREMOVE_DEVICE処理（上のIoCallDriver）が
        // 既に完了しておりServiceCallbackは再度呼ばれ得ないため、LayerMapを
        // ロック無しで安全に解放できる（Unloadの対応箇所と同じ理屈）。
        if (devExt->LayerMap != NULL) {
            ExFreePoolWithTag(devExt->LayerMap, KBDLAYER_MAP_POOL_TAG);
            devExt->LayerMap = NULL;
        }

        IoDetachDevice(devExt->LowerDevice);
        IoDeleteDevice(DeviceObject);
        return status;
    }

    IoSkipCurrentIrpStackLocation(Irp);
    status = IoCallDriver(devExt->LowerDevice, Irp);
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return status;
}

NTSTATUS DispatchRead(IN PDEVICE_OBJECT DeviceObject, IN PIRP Irp) {
    PDEVICE_EXTENSION devExt = (PDEVICE_EXTENSION)DeviceObject->DeviceExtension;
    NTSTATUS status;

    KdPrint(("kbdlayer: DispatchRead called for DeviceObject=0x%p\n", DeviceObject));

    status = IoAcquireRemoveLock(&devExt->RemoveLock, Irp);
    if (!NT_SUCCESS(status)) {
        Irp->IoStatus.Status = status;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
        return status;
    }

    IoCopyCurrentIrpStackLocationToNext(Irp);
    IoSetCompletionRoutine(Irp, ReadComplete, DeviceObject, TRUE, TRUE, TRUE);
    return IoCallDriver(devExt->LowerDevice, Irp);
}

// IOCTL_INTERNAL_KEYBOARD_CONNECTをフックしてClassServiceをラップする。
// それ以外のIRP_MJ_INTERNAL_DEVICE_CONTROLは素通しする。
NTSTATUS DispatchInternalIoctl(IN PDEVICE_OBJECT DeviceObject, IN PIRP Irp) {
    PIO_STACK_LOCATION irpStack = IoGetCurrentIrpStackLocation(Irp);
    PDEVICE_EXTENSION devExt = (PDEVICE_EXTENSION)DeviceObject->DeviceExtension;
    NTSTATUS lockStatus;

    lockStatus = IoAcquireRemoveLock(&devExt->RemoveLock, Irp);
    if (!NT_SUCCESS(lockStatus)) {
        Irp->IoStatus.Status = lockStatus;
        IoCompleteRequest(Irp, IO_NO_INCREMENT);
        return lockStatus;
    }

    if (irpStack->Parameters.DeviceIoControl.IoControlCode == IOCTL_INTERNAL_KEYBOARD_CONNECT) {
        PCONNECT_DATA connectData =
            (PCONNECT_DATA)irpStack->Parameters.DeviceIoControl.Type3InputBuffer;

        // DeviceId/LicenseはPnP初期化後にユーザーモードから設定されるため、
        // ここでは値の有無を問わず必ずhookする。ExtraInfo書き込み判定はServiceCallback内で行う。
        if (connectData != NULL) {
            devExt->OriginalClassDeviceObject = connectData->ClassDeviceObject;
#pragma warning(suppress: 4152)
            devExt->OriginalClassService      = connectData->ClassService;
            connectData->ClassDeviceObject    = DeviceObject;
#pragma warning(suppress: 4152)
            connectData->ClassService         = ServiceCallback;
            KBDLAYER_TRACE("DispatchInternalIoctl: ClassService hooked DeviceObject=0x%p DeviceId=0x%08X\n",
                           DeviceObject, devExt->DeviceId);
            KdPrint(("kbdlayer: ClassService hooked for DeviceObject=0x%p\n", DeviceObject));
        }
    }

    IoSkipCurrentIrpStackLocation(Irp);
    lockStatus = IoCallDriver(devExt->LowerDevice, Irp);
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return lockStatus;
}

// kbdclassがClassServiceを呼ぶ前に、レイヤーマップの解決とExtraInformationへの
// DeviceId書き込みを行う。DISPATCH_LEVEL以下で呼ばれる。非ページメモリのみ使用、
// ブロッキング処理は一切行わない。
VOID ServiceCallback(
    IN PVOID NormalContext,
    IN PVOID SystemArgument1,
    IN PVOID SystemArgument2,
    IN OUT PVOID SystemArgument3)
{
    PDEVICE_OBJECT DeviceObject          = (PDEVICE_OBJECT)NormalContext;
    PKEYBOARD_INPUT_DATA InputDataStart  = (PKEYBOARD_INPUT_DATA)SystemArgument1;
    PKEYBOARD_INPUT_DATA InputDataEnd    = (PKEYBOARD_INPUT_DATA)SystemArgument2;
    PDEVICE_EXTENSION devExt = (PDEVICE_EXTENSION)DeviceObject->DeviceExtension;
    ULONG originalCount = (ULONG)(InputDataEnd - InputDataStart);
    BOOLEAN compacted = FALSE;

    // DeviceIdはユーザーモードから非同期に設定される。0の間は素通し
    // （ただしキャリブレーションウィザードのCAPTURE_NEXT_KEYはDeviceId確定後
    // にしか呼べないため、この経路には影響しない）。
    if (devExt->DeviceId != 0) {
        // Phase 2: Mod-Tap/Layer-Tapのタップ/ホールド判定（Hold On Other Key Press方式）。
        // MT/LTキーの保留・確定・タイムアウトに関わるイベントはここで完結し、通常の
        // ResolveLayerAction以降の処理は行わない。詳細はLayerEngine.cのResolveTapHold参照。
        if (ResolveTapHold(devExt, InputDataStart, InputDataEnd, SystemArgument3)) {
            return;
        }

        // Phase 3: マクロ(MACRO(id))。ResolveTapHoldと同じく、対象キーならここで
        // 完結し通常のResolveLayerAction以降の処理は行わない。詳細はLayerEngine.cの
        // ResolveMacro参照。
        if (ResolveMacro(devExt, InputDataStart, InputDataEnd, SystemArgument3)) {
            return;
        }

        PKEYBOARD_INPUT_DATA newEnd = ResolveLayerAction(devExt, InputDataStart, InputDataEnd);
        if (newEnd != InputDataEnd) {
            compacted = TRUE;
            InputDataEnd = newEnd;
        }

        for (PKEYBOARD_INPUT_DATA p = InputDataStart; p < InputDataEnd; p++) {
            p->ExtraInformation =
                (p->ExtraInformation & 0x0000FFFFUL) | (devExt->DeviceId & 0xFFFF0000UL);
            KBDLAYER_TRACE("ServiceCallback DeviceId=0x%08X -> ExtraInfo=0x%08X sc=0x%X flags=0x%X\n",
                           devExt->DeviceId, p->ExtraInformation, p->MakeCode, p->Flags);
        }
    } else {
        KBDLAYER_TRACE("ServiceCallback DeviceId=0 (skip) sc=0x%X flags=0x%X\n",
                       InputDataStart->MakeCode, InputDataStart->Flags);
    }

    devExt->OriginalClassService(
        devExt->OriginalClassDeviceObject,
        InputDataStart,
        InputDataEnd,
        SystemArgument3);

    // レイヤーマップでキー入力を無効化（配列を縮小）した場合、kbdclassが
    // SystemArgument3(InputDataConsumed)に書き込む値は縮小後の件数に基づいてしまい、
    // port driver側のリングバッファ管理と食い違う（アンダーラン）。port driverには
    // 「元の件数をすべて消費した」と報告することでこれを防ぐ
    // （kbdaddid/RemapTable.cで踏み抜いた落とし穴と同種の対策）。
    // 何も詰めていない場合はkbdclass自身の報告（バックプレッシャー等）をそのまま
    // 活かすため、ここで上書きしない。
    if (compacted && SystemArgument3 != NULL) {
        *(PULONG)SystemArgument3 = originalCount;
    }
}

_IRQL_requires_max_(DISPATCH_LEVEL)
NTSTATUS ReadComplete(IN PDEVICE_OBJECT DeviceObject, IN PIRP Irp, IN PVOID Context) {
    UNREFERENCED_PARAMETER(Context);
    KdPrint(("kbdlayer: ReadComplete called for DeviceObject=0x%p, Status=0x%08X, IRQL=%lu\n",
             DeviceObject, Irp->IoStatus.Status, KeGetCurrentIrql()));

    PDEVICE_EXTENSION devExt = (PDEVICE_EXTENSION)DeviceObject->DeviceExtension;

    // Licenseが無効なら何もしない
    if (devExt->License == 0) {
        DbgPrint("kbdlayer: License disabled; not modifying ExtraInformation\n");
        if (Irp->PendingReturned) {
            IoMarkIrpPending(Irp);
        }
        IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
        return Irp->IoStatus.Status;
    }

    // IRPのバッファを安全にチェック
    if (Irp->IoStatus.Status == STATUS_SUCCESS &&
        Irp->AssociatedIrp.SystemBuffer != NULL &&
        Irp->IoStatus.Information >= sizeof(KEYBOARD_INPUT_DATA)) {
        PKEYBOARD_INPUT_DATA inputData = (PKEYBOARD_INPUT_DATA)Irp->AssociatedIrp.SystemBuffer;
        ULONG numKeys = (ULONG)(Irp->IoStatus.Information / sizeof(KEYBOARD_INPUT_DATA));
        if (numKeys > 0 && Irp->IoStatus.Information % sizeof(KEYBOARD_INPUT_DATA) == 0) {
            for (ULONG i = 0; i < numKeys; i++) {
                /* 下位16bitは他ドライバ／アプリ用に維持し、DeviceId（上位16bitハッシュ）をOR */
                inputData[i].ExtraInformation =
                    (inputData[i].ExtraInformation & 0x0000FFFFUL) | (devExt->DeviceId & 0xFFFF0000UL);
                KdPrint(("kbdlayer: Set ExtraInformation=0x%08X (DeviceId=0x%08X) key %lu MakeCode=0x%X Flags=0x%X\n",
                         inputData[i].ExtraInformation, devExt->DeviceId,
                         i, inputData[i].MakeCode, inputData[i].Flags));
            }
        } else {
            DbgPrint("kbdlayer: Invalid numKeys=%lu or Information=%zu in ReadComplete\n",
                     numKeys, Irp->IoStatus.Information);
        }
    } else {
        DbgPrint("kbdlayer: Skipping inputData processing: Status=0x%08X, SystemBuffer=%p, Information=%zu\n",
                 Irp->IoStatus.Status, Irp->AssociatedIrp.SystemBuffer, Irp->IoStatus.Information);
    }

    if (Irp->PendingReturned) {
        IoMarkIrpPending(Irp);
    }
    IoReleaseRemoveLock(&devExt->RemoveLock, Irp);
    return Irp->IoStatus.Status;
}

VOID Unload(IN PDRIVER_OBJECT DriverObject) {
    PDEVICE_OBJECT deviceObject = DriverObject->DeviceObject;
    while (deviceObject != NULL) {
        PDEVICE_OBJECT nextDevice = deviceObject->NextDevice;

        // コントロールデバイスはDEVICE_EXTENSIONを持たない別種のデバイスオブジェクト
        // なので、以下のfilter FDO向け処理（RemoveEntryList等）にかけてはならない。
        // TeardownControlDevice側でまとめて破棄する。
        if (deviceObject != g_ControlDeviceObject) {
            PDEVICE_EXTENSION devExt = (PDEVICE_EXTENSION)deviceObject->DeviceExtension;

            KIRQL oldIrql;
            KeAcquireSpinLock(&g_listLock, &oldIrql);
            RemoveEntryList(&devExt->ListEntry);
            KeReleaseSpinLock(&g_listLock, oldIrql);

            // Phase 2: DispatchPnpのIRP_MN_REMOVE_DEVICE処理と同じ理由で、
            // devExt解放前にHoldTimer/HoldDpcを確実に無害化する。
            KeCancelTimer(&devExt->HoldTimer);
            KeFlushQueuedDpcs();

            if (devExt->LayerMap != NULL) {
                ExFreePoolWithTag(devExt->LayerMap, KBDLAYER_MAP_POOL_TAG);
                devExt->LayerMap = NULL;
            }

            IoDetachDevice(devExt->LowerDevice);
            IoDeleteDevice(deviceObject);
        }

        deviceObject = nextDevice;
    }

    TeardownControlDevice();
}
