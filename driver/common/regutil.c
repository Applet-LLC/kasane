#include <ntddk.h>
#include <wdm.h>
#include <ntstrsafe.h>

#include "regutil.h"

/*
 RegReadDword - レジストリから REG_DWORD 値を読み取る共通ユーティリティ
 - 呼び出しは PASSIVE_LEVEL を前提とします（関数本体は PAGED セクション）。
 - プールタグ: 'rRgK'
*/

#if defined(ALLOC_PRAGMA)
#pragma alloc_text(PAGE, RegReadDword)
#pragma alloc_text(PAGE, RegReadServiceString)
#endif

_IRQL_requires_max_(PASSIVE_LEVEL)
_Success_(return == STATUS_SUCCESS)
NTSTATUS
RegReadDword(
    _In_ PCWSTR serviceName,
    _In_ PCWSTR valueName,
    _Out_ PULONG outValue,
    _In_ ULONG defaultValue
)
{
    PAGED_CODE();

    UNICODE_STRING keyName;
    UNICODE_STRING valueUni;
    WCHAR keyPath[256];
    OBJECT_ATTRIBUTES oa;
    HANDLE regKey = NULL;
    NTSTATUS status;
    ULONG resultLength = 0;
    PKEY_VALUE_PARTIAL_INFORMATION info = NULL;

    /* outValue が NULL の場合は何もしない（互換性のため STATUS_SUCCESS を返す） */
    if (outValue == NULL) {
        return STATUS_SUCCESS;
    }

    /* まずデフォルトを設定 */
    *outValue = defaultValue;

    if (!serviceName || !valueName) {
        return STATUS_SUCCESS;
    }

    status = RtlStringCchPrintfW(keyPath, RTL_NUMBER_OF(keyPath),
                                 L"\\Registry\\Machine\\SYSTEM\\CurrentControlSet\\Services\\%ws\\Parameters",
                                 serviceName);
    if (!NT_SUCCESS(status)) {
        DbgPrint("regutil: RtlStringCchPrintfW failed, status=0x%08X\n", status);
        return STATUS_SUCCESS;
    }

    RtlInitUnicodeString(&keyName, keyPath);
    RtlInitUnicodeString(&valueUni, valueName);
    InitializeObjectAttributes(&oa, &keyName, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);

    status = ZwOpenKey(&regKey, KEY_READ, &oa);
    if (!NT_SUCCESS(status)) {
        DbgPrint("regutil: ZwOpenKey(%ws) failed, status=0x%08X; using default %lu\n", keyPath, status, defaultValue);
        return STATUS_SUCCESS;
    }

    status = ZwQueryValueKey(regKey, &valueUni, KeyValuePartialInformation, NULL, 0, &resultLength);
    if (status != STATUS_BUFFER_TOO_SMALL && status != STATUS_SUCCESS) {
        DbgPrint("regutil: ZwQueryValueKey(size) failed, status=0x%08X; using default %lu\n", status, defaultValue);
        ZwClose(regKey);
        return STATUS_SUCCESS;
    }

    info = (PKEY_VALUE_PARTIAL_INFORMATION)ExAllocatePool2(POOL_FLAG_PAGED, resultLength, 'rRgK');
    if (!info) {
        DbgPrint("regutil: ExAllocatePool2 failed, using default %lu\n", defaultValue);
        ZwClose(regKey);
        return STATUS_SUCCESS;
    }

    status = ZwQueryValueKey(regKey, &valueUni, KeyValuePartialInformation, info, resultLength, &resultLength);
    if (NT_SUCCESS(status)) {
        if (info->Type == REG_DWORD && info->DataLength == sizeof(ULONG)) {
            ULONG val = *(ULONG*)info->Data;
            *outValue = val;
            DbgPrint("regutil: %ws\\%ws = %lu\n", serviceName, valueName, val);
        } else {
            DbgPrint("regutil: invalid type=%lu or length=%lu for %ws\\%ws; using default %lu\n",
                     info->Type, info->DataLength, serviceName, valueName, defaultValue);
        }
    } else {
        DbgPrint("regutil: ZwQueryValueKey(read) failed, status=0x%08X; using default %lu\n", status, defaultValue);
    }

    if (info) {
        ExFreePoolWithTag(info, 'rRgK');
    }
    ZwClose(regKey);

    /* 常に STATUS_SUCCESS を返す（既存コード互換） */
    return STATUS_SUCCESS;
}

/*
 BuildParametersKeyPath - "Services\<serviceName>\Parameters[\<subKeyPath>]" の
 完全パスを組み立てる内部ヘルパー。RegReadBinary/RegWriteBinary/RegReadDwordEx/
 RegWriteDword から共通で使う。
 alloc_text で PAGE セクションに配置するには、pragma より前に宣言が見えている
 必要があるため、定義に先立って前方宣言する。
*/
_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
BuildParametersKeyPath(
    _In_ PCWSTR serviceName,
    _In_opt_ PCWSTR subKeyPath,
    _Out_writes_z_(PathLengthInChars) PWCHAR PathBuffer,
    _In_ ULONG PathLengthInChars
);

#if defined(ALLOC_PRAGMA)
#pragma alloc_text(PAGE, BuildParametersKeyPath)
#endif

_IRQL_requires_max_(PASSIVE_LEVEL)
static NTSTATUS
BuildParametersKeyPath(
    _In_ PCWSTR serviceName,
    _In_opt_ PCWSTR subKeyPath,
    _Out_writes_z_(PathLengthInChars) PWCHAR PathBuffer,
    _In_ ULONG PathLengthInChars
)
{
    PAGED_CODE();

    if (subKeyPath != NULL && subKeyPath[0] != L'\0') {
        return RtlStringCchPrintfW(PathBuffer, PathLengthInChars,
            L"\\Registry\\Machine\\SYSTEM\\CurrentControlSet\\Services\\%ws\\Parameters\\%ws",
            serviceName, subKeyPath);
    }

    return RtlStringCchPrintfW(PathBuffer, PathLengthInChars,
        L"\\Registry\\Machine\\SYSTEM\\CurrentControlSet\\Services\\%ws\\Parameters",
        serviceName);
}

/*
 RegReadBinary - REG_BINARY 値をキャラクタバッファへ読み取る。
 - "値が無い" と "読み取り失敗" を呼び出し側が区別できるよう、失敗ステータスを
   そのまま返す（RegReadDword のように default で握りつぶさない）。
 - プールタグ: 'bRgK'
*/
#if defined(ALLOC_PRAGMA)
#pragma alloc_text(PAGE, RegReadBinary)
#endif

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
RegReadBinary(
    _In_ PCWSTR serviceName,
    _In_opt_ PCWSTR subKeyPath,
    _In_ PCWSTR valueName,
    _Out_writes_bytes_to_opt_(BufferLength, *ActualLength) PVOID Buffer,
    _In_ ULONG BufferLength,
    _Out_ PULONG ActualLength
)
{
    PAGED_CODE();

    UNICODE_STRING keyName;
    UNICODE_STRING valueUni;
    WCHAR keyPath[256];
    OBJECT_ATTRIBUTES oa;
    HANDLE regKey = NULL;
    NTSTATUS status;
    ULONG resultLength = 0;
    PKEY_VALUE_PARTIAL_INFORMATION info = NULL;

    if (ActualLength == NULL) {
        return STATUS_INVALID_PARAMETER;
    }
    *ActualLength = 0;

    if (!serviceName || !valueName) {
        return STATUS_INVALID_PARAMETER;
    }

    status = BuildParametersKeyPath(serviceName, subKeyPath, keyPath, RTL_NUMBER_OF(keyPath));
    if (!NT_SUCCESS(status)) {
        return status;
    }

    RtlInitUnicodeString(&keyName, keyPath);
    InitializeObjectAttributes(&oa, &keyName, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);

    status = ZwOpenKey(&regKey, KEY_READ, &oa);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    RtlInitUnicodeString(&valueUni, valueName);

    status = ZwQueryValueKey(regKey, &valueUni, KeyValuePartialInformation, NULL, 0, &resultLength);
    if (status != STATUS_BUFFER_TOO_SMALL && status != STATUS_SUCCESS) {
        ZwClose(regKey);
        return status;
    }

    info = (PKEY_VALUE_PARTIAL_INFORMATION)ExAllocatePool2(POOL_FLAG_PAGED, resultLength, 'bRgK');
    if (!info) {
        ZwClose(regKey);
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    status = ZwQueryValueKey(regKey, &valueUni, KeyValuePartialInformation, info, resultLength, &resultLength);
    if (NT_SUCCESS(status)) {
        if (info->Type == REG_BINARY) {
            *ActualLength = info->DataLength;
            if (Buffer != NULL) {
                if (info->DataLength <= BufferLength) {
                    RtlCopyMemory(Buffer, info->Data, info->DataLength);
                } else {
                    status = STATUS_BUFFER_TOO_SMALL;
                }
            }
        } else {
            status = STATUS_OBJECT_TYPE_MISMATCH;
        }
    }

    ExFreePoolWithTag(info, 'bRgK');
    ZwClose(regKey);
    return status;
}

/*
 RegWriteBinary - REG_BINARY 値を書き込む。サブキーが無ければ作成する。
 Buffer=NULL または BufferLength=0 の場合は値を削除する
 （既存の値が無い場合も STATUS_SUCCESS を返す）。
*/
#if defined(ALLOC_PRAGMA)
#pragma alloc_text(PAGE, RegWriteBinary)
#endif

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
RegWriteBinary(
    _In_ PCWSTR serviceName,
    _In_opt_ PCWSTR subKeyPath,
    _In_ PCWSTR valueName,
    _In_reads_bytes_opt_(BufferLength) PVOID Buffer,
    _In_ ULONG BufferLength
)
{
    PAGED_CODE();

    UNICODE_STRING keyName;
    UNICODE_STRING valueUni;
    WCHAR keyPath[256];
    OBJECT_ATTRIBUTES oa;
    HANDLE regKey = NULL;
    NTSTATUS status;
    ULONG disposition = 0;

    if (!serviceName || !valueName) {
        return STATUS_INVALID_PARAMETER;
    }

    status = BuildParametersKeyPath(serviceName, subKeyPath, keyPath, RTL_NUMBER_OF(keyPath));
    if (!NT_SUCCESS(status)) {
        return status;
    }

    RtlInitUnicodeString(&keyName, keyPath);
    InitializeObjectAttributes(&oa, &keyName, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);

    if (Buffer == NULL || BufferLength == 0) {
        /* 削除操作: サブキーが無い場合は ZwOpenKey が失敗するので "既に無い" として成功扱い */
        status = ZwOpenKey(&regKey, KEY_SET_VALUE, &oa);
        if (!NT_SUCCESS(status)) {
            return STATUS_SUCCESS;
        }
        RtlInitUnicodeString(&valueUni, valueName);
        status = ZwDeleteValueKey(regKey, &valueUni);
        ZwClose(regKey);
        if (status == STATUS_OBJECT_NAME_NOT_FOUND) {
            status = STATUS_SUCCESS;
        }
        return status;
    }

    status = ZwCreateKey(&regKey, KEY_SET_VALUE, &oa, 0, NULL, REG_OPTION_NON_VOLATILE, &disposition);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    RtlInitUnicodeString(&valueUni, valueName);
    status = ZwSetValueKey(regKey, &valueUni, 0, REG_BINARY, Buffer, BufferLength);
    ZwClose(regKey);
    return status;
}

/*
 RegReadDwordEx - RegReadDword のサブキー対応版。RegReadDword と同じく、値が無ければ
 defaultValue を設定して常に STATUS_SUCCESS を返す。
*/
#if defined(ALLOC_PRAGMA)
#pragma alloc_text(PAGE, RegReadDwordEx)
#endif

_IRQL_requires_max_(PASSIVE_LEVEL)
_Success_(return == STATUS_SUCCESS)
NTSTATUS
RegReadDwordEx(
    _In_ PCWSTR serviceName,
    _In_opt_ PCWSTR subKeyPath,
    _In_ PCWSTR valueName,
    _Out_ PULONG outValue,
    _In_ ULONG defaultValue
)
{
    PAGED_CODE();

    UNICODE_STRING keyName;
    UNICODE_STRING valueUni;
    WCHAR keyPath[256];
    OBJECT_ATTRIBUTES oa;
    HANDLE regKey = NULL;
    NTSTATUS status;
    ULONG resultLength = 0;
    PKEY_VALUE_PARTIAL_INFORMATION info = NULL;

    if (outValue == NULL) {
        return STATUS_SUCCESS;
    }
    *outValue = defaultValue;

    if (!serviceName || !valueName) {
        return STATUS_SUCCESS;
    }

    status = BuildParametersKeyPath(serviceName, subKeyPath, keyPath, RTL_NUMBER_OF(keyPath));
    if (!NT_SUCCESS(status)) {
        return STATUS_SUCCESS;
    }

    RtlInitUnicodeString(&keyName, keyPath);
    InitializeObjectAttributes(&oa, &keyName, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);

    status = ZwOpenKey(&regKey, KEY_READ, &oa);
    if (!NT_SUCCESS(status)) {
        return STATUS_SUCCESS;
    }

    RtlInitUnicodeString(&valueUni, valueName);

    status = ZwQueryValueKey(regKey, &valueUni, KeyValuePartialInformation, NULL, 0, &resultLength);
    if (status != STATUS_BUFFER_TOO_SMALL && status != STATUS_SUCCESS) {
        ZwClose(regKey);
        return STATUS_SUCCESS;
    }

    info = (PKEY_VALUE_PARTIAL_INFORMATION)ExAllocatePool2(POOL_FLAG_PAGED, resultLength, 'rRgK');
    if (!info) {
        ZwClose(regKey);
        return STATUS_SUCCESS;
    }

    status = ZwQueryValueKey(regKey, &valueUni, KeyValuePartialInformation, info, resultLength, &resultLength);
    if (NT_SUCCESS(status) && info->Type == REG_DWORD && info->DataLength == sizeof(ULONG)) {
        *outValue = *(ULONG*)info->Data;
    }

    ExFreePoolWithTag(info, 'rRgK');
    ZwClose(regKey);
    return STATUS_SUCCESS;
}

/*
 RegWriteDword - REG_DWORD 値を書き込む。サブキーが無ければ作成する。
*/
#if defined(ALLOC_PRAGMA)
#pragma alloc_text(PAGE, RegWriteDword)
#endif

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
RegWriteDword(
    _In_ PCWSTR serviceName,
    _In_opt_ PCWSTR subKeyPath,
    _In_ PCWSTR valueName,
    _In_ ULONG value
)
{
    PAGED_CODE();

    UNICODE_STRING keyName;
    UNICODE_STRING valueUni;
    WCHAR keyPath[256];
    OBJECT_ATTRIBUTES oa;
    HANDLE regKey = NULL;
    NTSTATUS status;
    ULONG disposition = 0;

    if (!serviceName || !valueName) {
        return STATUS_INVALID_PARAMETER;
    }

    status = BuildParametersKeyPath(serviceName, subKeyPath, keyPath, RTL_NUMBER_OF(keyPath));
    if (!NT_SUCCESS(status)) {
        return status;
    }

    RtlInitUnicodeString(&keyName, keyPath);
    InitializeObjectAttributes(&oa, &keyName, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);

    status = ZwCreateKey(&regKey, KEY_SET_VALUE, &oa, 0, NULL, REG_OPTION_NON_VOLATILE, &disposition);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    RtlInitUnicodeString(&valueUni, valueName);
    status = ZwSetValueKey(regKey, &valueUni, 0, REG_DWORD, &value, sizeof(value));
    ZwClose(regKey);
    return status;
}
_IRQL_requires_max_(PASSIVE_LEVEL)
_Success_(return == STATUS_SUCCESS)
NTSTATUS
RegReadServiceString(
    _In_ PCWSTR serviceName,
    _In_ PCWSTR valueName,
    _Out_writes_z_(maxChars) PWCHAR outBuf,
    _In_ ULONG maxChars
)
{
    PAGED_CODE();

    if (!outBuf || maxChars == 0) return STATUS_INVALID_PARAMETER;
    outBuf[0] = 0;
    if (!serviceName || !valueName) return STATUS_SUCCESS;

    UNICODE_STRING keyName;
    UNICODE_STRING valueUni;
    WCHAR keyPath[256];
    OBJECT_ATTRIBUTES oa;
    HANDLE regKey = NULL;
    NTSTATUS status;
    ULONG resultLength = 0;
    PKEY_VALUE_PARTIAL_INFORMATION info = NULL;

    status = RtlStringCchPrintfW(keyPath, RTL_NUMBER_OF(keyPath),
                                 L"\\Registry\\Machine\\SYSTEM\\CurrentControlSet\\Services\\%ws\\Parameters",
                                 serviceName);
    if (!NT_SUCCESS(status)) return STATUS_SUCCESS;

    RtlInitUnicodeString(&keyName, keyPath);
    RtlInitUnicodeString(&valueUni, valueName);
    InitializeObjectAttributes(&oa, &keyName, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);

    status = ZwOpenKey(&regKey, KEY_READ, &oa);
    if (!NT_SUCCESS(status)) return STATUS_SUCCESS;

    status = ZwQueryValueKey(regKey, &valueUni, KeyValuePartialInformation, NULL, 0, &resultLength);
    if (status != STATUS_BUFFER_TOO_SMALL && !NT_SUCCESS(status)) {
        ZwClose(regKey);
        return STATUS_SUCCESS;
    }

    info = (PKEY_VALUE_PARTIAL_INFORMATION)ExAllocatePool2(POOL_FLAG_PAGED, resultLength, 'sRgK');
    if (!info) {
        ZwClose(regKey);
        return STATUS_SUCCESS;
    }

    status = ZwQueryValueKey(regKey, &valueUni, KeyValuePartialInformation, info, resultLength, &resultLength);
    if (NT_SUCCESS(status) && info->Type == REG_SZ && info->DataLength >= sizeof(WCHAR)) {
        ULONG wchars = info->DataLength / sizeof(WCHAR);
        ULONG copyChars = (wchars < maxChars) ? wchars : maxChars - 1;
        RtlCopyMemory(outBuf, info->Data, copyChars * sizeof(WCHAR));
        outBuf[copyChars] = 0;
    }

    ExFreePoolWithTag(info, 'sRgK');
    ZwClose(regKey);
    return STATUS_SUCCESS;
}