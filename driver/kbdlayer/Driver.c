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
#include "I8042Override.h"
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
_Dispatch_type_(IRP_MJ_CLEANUP)
_Dispatch_type_(IRP_MJ_CREATE_NAMED_PIPE)
DRIVER_DISPATCH DispatchPass;
_Dispatch_type_(IRP_MJ_READ) DRIVER_DISPATCH DispatchRead;
_Dispatch_type_(IRP_MJ_INTERNAL_DEVICE_CONTROL) DRIVER_DISPATCH DispatchInternalIoctl;
_Dispatch_type_(IRP_MJ_PNP) DRIVER_DISPATCH DispatchPnp;
_Dispatch_type_(IRP_MJ_DEVICE_CONTROL) DRIVER_DISPATCH DispatchDeviceControl;
DRIVER_UNLOAD Unload;
IO_COMPLETION_ROUTINE ReadComplete;
VOID ServiceCallback(PVOID NormalContext, PVOID SystemArgument1,
                     PVOID SystemArgument2, PVOID SystemArgument3);
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN ResolveAndForward(_In_ PDEVICE_EXTENSION devExt, _In_ PKEYBOARD_INPUT_DATA InputDataStart,
                                 _In_ PKEYBOARD_INPUT_DATA InputDataEnd, _Inout_opt_ PVOID SystemArgument3);

// g_listLockを保護しつつg_deviceListを走査し、DeviceIdが一致する
// DEVICE_EXTENSIONを返す（見つからなければNULL）。宣言はDeviceExtension.h。
// ControlDevice.cのIOCTLハンドラからUniqId指定でデバイスを引くために使う。
//
// 見つけたdevExtには、g_listLockを保持したままIoAcquireRemoveLock(Irp)を取ってから返す。
// 呼び出し元はdevExtを使い終えたらIoReleaseRemoveLock(&devExt->RemoveLock, Irp)を
// 呼ぶこと。これでIOCTL処理中にキーボードが取り外されてもdevExtは解放されない。
// この順序が安全なのは、DispatchPnpのREMOVE_DEVICEがIoReleaseRemoveLockAndWaitの
// 後でRemoveEntryListするため（リストに載っている間はdevExtのメモリが有効で、
// 削除開始後はIoAcquireRemoveLockが失敗する）。削除中のデバイスもNULLを返す。
_IRQL_requires_max_(DISPATCH_LEVEL)
PDEVICE_EXTENSION
FindDeviceExtensionByUniqId(
    _In_ ULONG UniqId,
    _In_ PIRP Irp
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
            if (NT_SUCCESS(IoAcquireRemoveLock(&devExt->RemoveLock, Irp))) {
                found = devExt;
            }
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
    InitializeLayerNotify(); // WAIT_LAYER_CHANGEの保留IRPのキュー（LayerNotify.c）
    InitializeHelperNotify(); // WAIT_HELPER_EVENTのイベントと保留IRPのキュー（HelperNotify.c）
    InitializeKeyStream();    // WAIT_KEY_EVENTのイベントと保留IRPのキュー・所有者の表（KeyStream.c）
    InitializeI8042Override(); // i8042prt書き換えの直列化用ミューテックス（I8042Override.c）

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
    } else {
        // 緊急時の解除手段（項番3）のグローバル設定を読み込み、ワークアイテムを
        // g_ControlDeviceObjectへ紐づけて確保する。制御デバイスが無ければレイヤーマップ
        // 自体を設定できないので、その場合は不要（IOCTLハンドラも呼ばれない）。
        InitializeEmergencyReleaseGlobal(g_ControlDeviceObject);
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
    KeInitializeSpinLock(&devExt->OutputLock);
    KeInitializeSpinLock(&devExt->EngineLock);
    devExt->LayerMap = NULL;
    devExt->LayerMapEnabled = FALSE;
    InitializeTapHoldState(devExt);  // HoldTimer/HoldDpcの初期化含む（Phase 2）
    InitializeComboState(devExt);    // ComboTimer/ComboDpcの初期化含む（Phase 4）
    InitializeTapDanceState(devExt); // TapDanceTimer/TapDanceDpcの初期化含む（Phase 5）
    InitializeNicolaState(devExt);   // NicolaTimer/NicolaDpcの初期化含む（Phase 6）
    InitializeCaptureState(devExt);  // CaptureTimer/CaptureDpcの初期化（CAPTURE_NEXT_KEYの保留IRP用）
    InitializeEmergencyReleaseState(devExt); // EmergencyTimer/EmergencyDpcの初期化（緊急時の解除手段）
    // 機能別ON/OFF（項番4）の既定は全機能有効。永続化された値があれば
    // LoadPersistedLayerMapForDeviceが上書きする。
    devExt->EnabledFeatures = KBDLAYER_FEATURE_ALL;

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

    // 永続化スコープ用のInstanceKey/ModelKeyは、アドレッシング用DeviceIdの計算方式
    // （DeviceIdMode）に関わらず常に両方計算する（E-2: DeviceIdは一意でない前提で設計する。
    // common/kbdlayer_ioctl.hのコメント参照）。DeviceInstanceIdの取得自体は元々
    // DeviceIdMode==0のときだけ行っていたが、ここでは無条件にする。
    WCHAR instanceIdBuf[512] = {0};
    {
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

        if (NT_SUCCESS(instanceIdStatus) && instanceIdLen >= sizeof(WCHAR) && propertyType == DEVPROP_TYPE_STRING &&
            instanceIdBuf[instanceIdLen / sizeof(WCHAR) - 1] == L'\0' && instanceIdBuf[0] != L'\0') {
            DbgPrint("kbdlayer: DeviceInstanceId=%ws (from IoGetDevicePropertyData, type=0x%08X)\n", instanceIdBuf, propertyType);
            devExt->InstanceKey = Fnv1aHash16Upper(instanceIdBuf);
        } else {
            DbgPrint("kbdlayer: IoGetDevicePropertyData failed, status=0x%08X, required length=%lu, type=0x%08X\n",
                     instanceIdStatus, instanceIdLen, propertyType);
        }
    }
    if (NT_SUCCESS(hwidStatus) && hwidBuf[0]) {
        devExt->ModelKey = Fnv1aHash16Upper(hwidBuf);
    }

    // i8042prtのParametersを、このキーボードの型番に合わせて書き換える／初期化する
    // （オプションが無効なら何もしない）。port driverがStartされる前の、PASSIVE_LEVELのここで行う。
    // 失敗してもAddDeviceは続ける。
    ApplyI8042OverrideForDevice(devExt->ModelKey);

    // デバイスインスタンスパスモード (DeviceIdMode=0)
    if (deviceIdMode == 0 && devExt->InstanceKey != 0) {
        devExt->DeviceId = devExt->InstanceKey;
        KBDLAYER_TRACE("AddDevice: PDO=0x%p DeviceId=0x%08X InstanceId=%ws\n",
                       PhysicalDeviceObject, devExt->DeviceId, instanceIdBuf);
        DbgPrint("kbdlayer: DeviceId generated by mode=DeviceInstanceId: %08X\n", devExt->DeviceId);
        validPdoFound = TRUE;
    }

    // ハードウェアIDモード (DeviceIdMode=1)
    if (deviceIdMode == 1 && !validPdoFound && devExt->ModelKey != 0) {
        devExt->DeviceId = devExt->ModelKey;
        DbgPrint("kbdlayer: DeviceId generated by mode=HardwareId: %08X\n", devExt->DeviceId);
        validPdoFound = TRUE;
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
        // 永続化スコープ（Model共有 or この1台だけ）はInstanceKeyに紐づく設定として
        // 別に持つ（DeviceBindingサブキー、値名=InstanceKeyHex）。無ければ既定のModel。
        ULONG bindingValue = KBDLAYER_BINDING_MODEL;
        if (devExt->InstanceKey != 0) {
            WCHAR instanceKeyHex[16];
            RtlStringCchPrintfW(instanceKeyHex, RTL_NUMBER_OF(instanceKeyHex), L"%08X", devExt->InstanceKey);
            RegReadDwordEx(L"kbdlayer", L"DeviceBinding", instanceKeyHex, &bindingValue, KBDLAYER_BINDING_MODEL);
        }
        // 知らない値（将来の拡張・壊れた値）は既定のModelとして扱う。
        if (bindingValue != KBDLAYER_BINDING_INSTANCE && bindingValue != KBDLAYER_BINDING_GLOBAL) {
            bindingValue = KBDLAYER_BINDING_MODEL;
        }
        devExt->PersistBinding = bindingValue;

        MigrateLegacyPersistedSettingsIfNeeded(devExt);
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

    // コントロールデバイス（\\.\KbdLayerControl）宛のIRP_MJ_CREATE/CLOSE/CLEANUP/
    // CREATE_NAMED_PIPEはここで捕捉する（CLEANUPでは入力抑止の自動解除を行う）。コントロールデバイスにはLowerDeviceも
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

        // 保留中のCAPTURE_NEXT_KEYはremove lockを握ったままなので、先に完了させる
        // （そうしないと下のIoReleaseRemoveLockAndWaitがタイムアウトまで待たされる）。
        // 以後の保留を受け付けないようにしてから（RemovePendingの説明参照）、保留中のIRPを完了させる。
        KeAcquireSpinLock(&devExt->EngineLock, &oldIrql);
        devExt->RemovePending = TRUE;
        KeReleaseSpinLock(&devExt->EngineLock, oldIrql);
        AbortCaptureNextKey(devExt, STATUS_DEVICE_REMOVED);
        // WAIT_LAYER_CHANGEの保留IRPも同じ理由で先に完了させる。
        LayerNotifyAbortDevice(devExt, STATUS_DEVICE_REMOVED);

        // 以降のIoAcquireRemoveLock呼び出し元(DispatchRead/DispatchInternalIoctl/
        // ControlDevice.cの全IOCTLハンドラ=FindDeviceExtensionByUniqId経由)が新規に
        // 処理を開始しないようにした上で、既存の処理が抜けるのを待つ。
        // このあとでリストから外すので、FindDeviceExtensionByUniqIdがリスト上で見つけた
        // devExtは、IoAcquireRemoveLockが失敗するだけでメモリは有効なまま。
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
        // Phase 4: ComboTimer/ComboDpcも同じ理由で明示的に無害化する。
        KeCancelTimer(&devExt->ComboTimer);
        // Phase 5: TapDanceTimer/TapDanceDpcも同様。
        KeCancelTimer(&devExt->TapDanceTimer);
        // Phase 6: NicolaTimer/NicolaDpcも同様。
        KeCancelTimer(&devExt->NicolaTimer);
        // キャリブレーションのCaptureTimer/CaptureDpcも同様。
        KeCancelTimer(&devExt->CaptureTimer);
        // 緊急時の解除手段のEmergencyTimer/EmergencyDpcも同様。
        KeCancelTimer(&devExt->EmergencyTimer);
        KeFlushQueuedDpcs();

        // この時点ではport driver側のREMOVE_DEVICE処理（上のIoCallDriver）が
        // 既に完了しておりServiceCallbackは再度呼ばれ得ないため、LayerMapを
        // ロック無しで安全に解放できる（Unloadの対応箇所と同じ理屈）。
        if (devExt->LayerMap != NULL) {
            ExFreePoolWithTag(devExt->LayerMap, KBDLAYER_MAP_POOL_TAG);
            devExt->LayerMap = NULL;
        }
        FreeNicolaForDevice(devExt);

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
    KIRQL outputIrql;

    // 状態の決定から送出までを、タイマーDPC・IOCTL経路の送出と直列化する
    // （DeviceExtension.hのOutputLockの説明参照）。出口（関数末尾）まで保持する。
    KeAcquireSpinLock(&devExt->OutputLock, &outputIrql);

    // DeviceIdはユーザーモードから非同期に設定される。0の間は素通し
    // （ただしキャリブレーションウィザードのCAPTURE_NEXT_KEYはDeviceId確定後
    // にしか呼べないため、この経路には影響しない）。
    if (devExt->DeviceId != 0) {
        // 緊急時の解除手段（release-roadmap-priority.md 項番3）。レイヤーマップ・
        // マクロ・コンボの内容に一切依存せず必ず動くよう、生の物理スキャンコードの
        // 段階で、どの処理よりも先に見る（キャリブレーションの抑止中でも見逃さない）。
        // イベント自体は書き換えない（観測のみ）。
        CheckEmergencyRelease(devExt, InputDataStart, InputDataEnd);

        // 生の物理キーの押下・解放を、WAIT_KEY_EVENTで待っている設定画面へ渡す（観測のみ。
        // 所有者がいなければ、すぐ戻る）。ResolveCalibrationの入力抑止より前に見るので、抑止中のキーも見える。
        KeyStreamObserve(devExt->DeviceId, InputDataStart, InputDataEnd);

        // キャリブレーション（キャプチャ記録・入力抑止）。キャプチャを取りこぼさない
        // よう、どのResolve*よりも先に処理する。抑止中に捨てたイベントはここで配列から
        // 取り除かれる。詳細はLayerEngine.cのResolveCalibration参照。
        {
            PKEYBOARD_INPUT_DATA calEnd = ResolveCalibration(devExt, InputDataStart, InputDataEnd);
            if (calEnd != InputDataEnd) {
                compacted = TRUE;
                InputDataEnd = calEnd;
                if (InputDataEnd == InputDataStart) {
                    // 全件を捨てた。kbdclassは呼ばず、元の件数を消費済みとして報告する
                    // （ResolveMacro等がイベントを飲み込むときと同じ作法）。
                    goto consumed;
                }
            }
        }

        // Phase 6: NICOLA判定が有効な間は、複数件バッチを1件ずつに分けて流す。
        // USBキーボードでは親指キーと文字キーの同時押しが1つのレポート（=1バッチ）で
        // 届きうるが、各Resolve*は1件バッチしか扱わないため。
        // 修飾付きキー・LMを含むマップが有効な間と、それらとして押下中のキーがある間も同じ
        // （押下が1件で届いて修飾キーを足し、解放が複数件のバッチで届いて戻さない、という
        // 押しっぱなしを防ぐ。NeedsSingleEventDispatch参照）。
        if ((InputDataEnd - InputDataStart) > 1 &&
            (NicolaIsActive(devExt) || NeedsSingleEventDispatch(devExt))) {
            for (PKEYBOARD_INPUT_DATA p = InputDataStart; p < InputDataEnd; p++) {
                KEYBOARD_INPUT_DATA single = *p;
                ULONG dummyConsumed = 0;
                ResolveAndForward(devExt, &single, &single + 1, &dummyConsumed);
            }
            // port driverには元の件数をすべて消費したと報告する（下のconsumedの説明参照）。
            compacted = TRUE;
            goto consumed;
        }

        if (ResolveAndForward(devExt, InputDataStart, InputDataEnd, SystemArgument3)) {
            compacted = TRUE;
        }
        goto consumed;
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
    // Resolve*がバッチを処理済みとして戻った場合も、先にResolveCalibrationが配列を
    // 詰めていれば、Resolve*が報告した（詰めた後の）件数を元の件数で上書きする。
consumed:
    if (compacted && SystemArgument3 != NULL) {
        *(PULONG)SystemArgument3 = originalCount;
    }

    // Phase 6: このバッチでレイヤー状態が変わり、NICOLA判定の有効/無効が切り替わったなら
    // OnEnterKey/OnExitKey（IMEオン/オフ等）を送る。
    if (devExt->DeviceId != 0) {
        NicolaSyncActivation(devExt);
    }

    KeReleaseSpinLock(&devExt->OutputLock, outputIrql);

    // このバッチでレイヤー状態（MO/TG/OSL/LT/DF・NICOLAの有効）が変わっていれば、
    // WAIT_LAYER_CHANGEで待っているアプリへ知らせる（IRPの完了はOutputLockの外で行う）。
    if (devExt->DeviceId != 0) {
        LayerNotifyIfChanged(devExt);
    }
}

// ServiceCallbackのうち、キャリブレーション後の「Resolve*の連鎖 → kbdclassへ転送」部分。
// 戻り値: ResolveLayerActionが配列を詰めた（port driverへは元の件数を報告すべき）ならTRUE。
// Resolve*のいずれかがバッチを処理済みにした場合は転送せずFALSEを返す
// （そのResolve*がSystemArgument3へ消費件数を書き込み済み）。
_IRQL_requires_max_(DISPATCH_LEVEL)
static BOOLEAN
ResolveAndForward(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _Inout_opt_ PVOID SystemArgument3)
{
    // 機能別ON/OFF（項番4）。無効な機能のResolve*は呼ばない。SetFeaturesForDeviceは
    // OutputLockを保持して書き換えるので、OutputLock保持中のここでは一貫した値が読める。
    // MACRO(id)/TAPDANCE(id)のアクションは、ここを飛ばすとResolveLayerActionの
    // default節（LayerEngineShouldDropEntry）で「何もしない」として扱われる。
    // Comboのメンバーキーは、コンボ判定を経ずに通常のキーとして解決される。
    ULONG features = devExt->EnabledFeatures;

    // TT(layer): TTキーを押している間・タップの合間に別のキーが押されたことを、どのResolve*が
    // 処理するキーかに関係なく記録する（LayerEngine.cのHandleTapToggleLocked参照）。
    LayerEngineObserveKeyEvents(devExt, InputDataStart, InputDataEnd);

    // 先に始まった判定に、割り込んだキーを先に渡す: タップダンスが判定中で、そのキー以外が押されたら、
    // NICOLA・コンボ・MT/LTが新しい判定を始める前に、タップダンスを割り込みとして確定させる
    // （タップの出力、または長押しの確定）。以前はResolveTapDanceが連鎖の後ろにあったため、
    // 割り込んだキーがMT/LTやコンボのメンバーだと、そちらが先に保留を始めてタップダンスに届かず、
    // 出力の順序が逆になっていた（2026-09-30の外部レビューで指摘、2026-10-04に修正）。
    // 割り込んだキー自体が別のタップダンスキーで、新しい判定を始めた場合だけ、ここで完結する。
    // それ以外は確定させた後、このキーを以降の連鎖で通常どおり解決する（後段のResolveTapDanceは、
    // もう判定中ではないので何もしない）。
    if ((features & KBDLAYER_FEATURE_TAPDANCE) &&
        TapDanceInterruptedBy(devExt, InputDataStart, InputDataEnd) &&
        ResolveTapDance(devExt, InputDataStart, InputDataEnd, SystemArgument3)) {
        return FALSE;
    }

    // Phase 6: NICOLA（親指シフト）。判定が有効な間、文字キー・親指キーは物理キー単位で
    // ここが先に受け取る（コンボ等より優先）。詳細はLayerEngine.cのResolveNicola参照。
    if (ResolveNicola(devExt, InputDataStart, InputDataEnd, SystemArgument3)) {
        return FALSE;
    }

    // Phase 4: コンボ（2キー限定v1）。物理キー単位の判定なので、レイヤーや
    // タップ/ホールドの解釈より先に判定する。対象キーならここで完結する。
    // 詳細はLayerEngine.cのResolveCombo参照。
    if ((features & KBDLAYER_FEATURE_COMBO) &&
        ResolveCombo(devExt, InputDataStart, InputDataEnd, SystemArgument3)) {
        return FALSE;
    }

    // Phase 2: Mod-Tap/Layer-Tapのタップ/ホールド判定（判定方式はSET_CONFIGのTapHoldMode）。
    // MT/LTキーの保留・確定・タイムアウトに関わるイベントと、判定中に溜めた別のキーの
    // イベントはここで完結する（溜めたイベントは、判定が決まってからResolveAndForwardTailへ
    // 流し直される）。詳細はLayerEngine.cのResolveTapHold参照。
    if (ResolveTapHold(devExt, InputDataStart, InputDataEnd, SystemArgument3)) {
        return FALSE;
    }

    return ResolveAndForwardTail(devExt, InputDataStart, InputDataEnd, SystemArgument3);
}

// ResolveAndForwardのうち、タップ/ホールド判定より後ろ（TapDance → Macro → 修飾付きキー →
// ResolveLayerAction → kbdclassへ転送）。ResolveTapHoldが、判定中に溜めたイベントを判定の
// 確定後に流し直すときにも呼ぶ（ServiceCallback・HoldTapDpcRoutine・OFFの後始末から。いずれも
// OutputLock保持中・EngineLock非保持）。戻り値はResolveAndForwardと同じ。
_IRQL_requires_max_(DISPATCH_LEVEL)
BOOLEAN
ResolveAndForwardTail(
    _In_ PDEVICE_EXTENSION devExt,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _Inout_opt_ PVOID SystemArgument3)
{
    BOOLEAN compacted = FALSE;
    ULONG features = devExt->EnabledFeatures; // ResolveAndForwardと同じ（OutputLock保持中に読む）

    // Phase 5: タップダンス（v1: タップ回数3回まで）。ResolveTapHoldと同じグループ
    // （1つの物理キーの振る舞い）として、ResolveMacroの前に判定する。詳細は
    // LayerEngine.cのResolveTapDance参照。
    if ((features & KBDLAYER_FEATURE_TAPDANCE) &&
        ResolveTapDance(devExt, InputDataStart, InputDataEnd, SystemArgument3)) {
        return FALSE;
    }

    // Phase 3: マクロ(MACRO(id))。ResolveTapHoldと同じく、対象キーならここで
    // 完結し通常のResolveLayerAction以降の処理は行わない。詳細はLayerEngine.cの
    // ResolveMacro参照。
    if ((features & KBDLAYER_FEATURE_MACRO) &&
        ResolveMacro(devExt, InputDataStart, InputDataEnd, SystemArgument3)) {
        return FALSE;
    }

    // 修飾付きキー（LCTL(kc)等）とLM(layer,mod)。修飾キーを足してからキーを送り、解放で戻す。
    // 押下中の記録があるキーは、マップのOFF・差し替え後も記録どおりに処理する。
    // 詳細はLayerEngine.cのResolveModdedKey参照。
    if (ResolveModdedKey(devExt, InputDataStart, InputDataEnd, SystemArgument3)) {
        return FALSE;
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

    // 送出は必ずForwardToClassを通す（修飾キーの押下状態の記録と、修飾付きキー・LMが
    // 足した修飾キーを普通の出力のbreakで離さないための処理。LayerEngine.c参照）。
    if (ForwardToClass(devExt, InputDataStart, InputDataEnd, SystemArgument3)) {
        compacted = TRUE;
    }
    return compacted;
}

_Use_decl_annotations_
NTSTATUS ReadComplete(PDEVICE_OBJECT DeviceObject, PIRP Irp, PVOID Context) {
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
            // Phase 4: ComboTimer/ComboDpcも同様。
            KeCancelTimer(&devExt->ComboTimer);
            // Phase 5: TapDanceTimer/TapDanceDpcも同様。
            KeCancelTimer(&devExt->TapDanceTimer);
            // Phase 6: NicolaTimer/NicolaDpcも同様。
            KeCancelTimer(&devExt->NicolaTimer);
            // キャリブレーションのCaptureTimer/CaptureDpcも同様。
            KeCancelTimer(&devExt->CaptureTimer);
            // 緊急時の解除手段のEmergencyTimer/EmergencyDpcも同様。
            KeCancelTimer(&devExt->EmergencyTimer);
            KeFlushQueuedDpcs();

            if (devExt->LayerMap != NULL) {
                ExFreePoolWithTag(devExt->LayerMap, KBDLAYER_MAP_POOL_TAG);
                devExt->LayerMap = NULL;
            }
            FreeNicolaForDevice(devExt);

            IoDetachDevice(devExt->LowerDevice);
            IoDeleteDevice(deviceObject);
        }

        deviceObject = nextDevice;
    }

    TeardownControlDevice();
}
