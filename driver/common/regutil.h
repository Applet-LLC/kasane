#ifndef _ADDID_COMMON_REGUTIL_H_
#define _ADDID_COMMON_REGUTIL_H_

/*
 RegReadDword - レジストリから REG_DWORD 値を読み取る共通ユーティリティ
 - 呼び出しは PASSIVE_LEVEL を前提とします（関数本体は PAGED セクション）。
 - カーネルモード専用のヘッダのみを使用しています。
*/

#include <ntddk.h>
#include <wdm.h>
#include <ntstrsafe.h>

_IRQL_requires_max_(PASSIVE_LEVEL)
_Success_(return == STATUS_SUCCESS)
NTSTATUS
RegReadDword(
    _In_ PCWSTR serviceName,
    _In_ PCWSTR valueName,
    _Out_ PULONG outValue,
    _In_ ULONG defaultValue
);

/*
 RegReadServiceString - サービスの Parameters キーから REG_SZ 値を読み取る
 - HKLM\SYSTEM\CurrentControlSet\Services\<serviceName>\Parameters\<valueName>
 - 呼び出しは PASSIVE_LEVEL を前提とします。
 - outBuf に読み取った WCHAR 文字列を格納する（null 終端保証）。
 - 値が存在しない場合は outBuf[0] = 0 にして STATUS_SUCCESS を返す。
*/
_IRQL_requires_max_(PASSIVE_LEVEL)
_Success_(return == STATUS_SUCCESS)
NTSTATUS
RegReadServiceString(
    _In_ PCWSTR serviceName,
    _In_ PCWSTR valueName,
    _Out_writes_z_(maxChars) PWCHAR outBuf,
    _In_ ULONG maxChars
);

/*
 RegReadBinary / RegWriteBinary / RegReadDwordEx / RegWriteDword
 - RegReadDword と同じく PASSIVE_LEVEL 専用・PAGED セクションのユーティリティ。
 - RegReadDword と異なり subKeyPath（Parameters 直下のサブキーパス。例:
   L"ScancodeMaps\\0000A1B2"）を指定できる。subKeyPath が NULL または空文字列の
   場合は Parameters 直下を対象にする。
 - RegReadBinary/RegWriteBinary は「値が無い」ことと「読み取り失敗」を呼び出し側が
   区別できるよう、失敗時のステータスをそのまま返す（RegReadDword のように
   defaultValue で握りつぶさない）。
 - RegWriteBinary は Buffer=NULL または BufferLength=0 の場合、値そのものを削除する
   （既存の値が無い場合も STATUS_SUCCESS を返す）。
 - RegReadDwordEx は RegReadDword と同じ「値が無ければ defaultValue を設定して
   常に STATUS_SUCCESS を返す」方式。
 - 書き込み側（RegWriteBinary/RegWriteDword）は、対象のサブキーが存在しなければ
   ZwCreateKey（REG_OPTION_NON_VOLATILE）で作成する。途中の階層（例: "LayerMaps"）が
   無い場合も、親から順に作成する（ZwCreateKey 単体は最後の1階層しか作らないため）。
*/

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
RegReadBinary(
    _In_ PCWSTR serviceName,
    _In_opt_ PCWSTR subKeyPath,
    _In_ PCWSTR valueName,
    _Out_writes_bytes_to_opt_(BufferLength, *ActualLength) PVOID Buffer,
    _In_ ULONG BufferLength,
    _Out_ PULONG ActualLength
);

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
RegWriteBinary(
    _In_ PCWSTR serviceName,
    _In_opt_ PCWSTR subKeyPath,
    _In_ PCWSTR valueName,
    _In_reads_bytes_opt_(BufferLength) PVOID Buffer,
    _In_ ULONG BufferLength
);

_IRQL_requires_max_(PASSIVE_LEVEL)
_Success_(return == STATUS_SUCCESS)
NTSTATUS
RegReadDwordEx(
    _In_ PCWSTR serviceName,
    _In_opt_ PCWSTR subKeyPath,
    _In_ PCWSTR valueName,
    _Out_ PULONG outValue,
    _In_ ULONG defaultValue
);

_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS
RegWriteDword(
    _In_ PCWSTR serviceName,
    _In_opt_ PCWSTR subKeyPath,
    _In_ PCWSTR valueName,
    _In_ ULONG value
);

#endif // _ADDID_COMMON_REGUTIL_H_