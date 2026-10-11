// Copyright 2008-2026 applet <applet@bp.iij4u.or.jp>
// License: EPL-2.0 - https://www.eclipse.org/legal/epl-2.0/
//
// Forked from kbdaddid_subscribe's addid/DriverManager/DriverManager.cpp for this project
// (formerly LayerDriver, now kasane). Renames: kbdaddid -> kbdlayer. mouaddid (mouse) support was removed entirely --
// kasane has no mouse component, and this tool no longer offers a "mouse" driver_type or
// carries any mouaddid-specific registry/branch logic. nodokad/nodokad2 interop (a separate,
// unrelated product by the same author) is left exactly as in the source.

#include <Windows.h>
#include <SetupAPI.h>
#include <newdev.h>
#include <algorithm>
#include <cwctype>
#include <cstdio>
#include <cwchar>
#include <filesystem>
#include <iostream>
#include <shlwapi.h>
#include <string>
#include <vector>

#pragma comment(lib, "setupapi.lib")
#pragma comment(lib, "newdev.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "version.lib")

bool InstallDriver(const std::wstring& driverName, const std::wstring& classGuidString, const std::wstring& deviceIdMode);
void WriteDeviceIdMode(const std::wstring& driverName, const std::wstring& deviceIdMode);
void WriteDriverVersion(const std::wstring& driverName, const std::wstring& sysFilePath);
bool UninstallDriver(const std::wstring& driverName, const std::wstring& classGuidString, const std::wstring& optionalInfPath);
bool ModifyUpperFilters(const std::wstring& classGuidString, const std::wstring& driverName, bool add, bool insertBeforeClassDriver = false);
std::wstring GetExecutablePath();
bool stopAndWaitService(const std::wstring& serviceName, DWORD timeoutMs);
std::vector<std::wstring> EnumerateDriverStoreInfs(const std::wstring& driverName);
bool RemoveDriverStorePackages(const std::wstring& driverName);
void RemoveLeftoverSystemFiles(const std::wstring& driverName);
bool ServiceExists(const std::wstring& serviceName);
int CountUpperFiltersOccurrences(const std::wstring& classGuidString, const std::wstring& driverName);
static int InfClassFilterRegistrations(const std::wstring& classGuidString, const std::wstring& driverName, bool remove);
bool UpperFiltersContains(const std::wstring& classGuidString, const std::wstring& driverName);
bool VerifyUninstalled(const std::wstring& classGuidString, const std::wstring& driverName);
bool VerifyInstalled(const std::wstring& classGuidString, const std::wstring& driverName);
std::wstring GetServiceImagePath(const std::wstring& serviceName);
bool ContainsCaseInsensitive(const std::wstring& haystack, const std::wstring& needle);
void RollbackNewPackageOnly(const std::wstring& driverStoreInfPath);
void WarnIfOtherManagedDriversInconsistent(const std::wstring& excludeDriverName);
std::wstring GetInfDriverVer(const std::wstring& infPath);
bool IsDriverVerNewerOrEqual(const std::wstring& verA, const std::wstring& verB);
std::wstring SelectNewestDriverStorePackage(const std::vector<std::wstring>& infPaths);
bool IsSupportedWindowsEnvironment();
DWORD GetNativeProcessorArchitecture();
bool IsNativeArm64();
std::wstring ArchSubdirName();
#if !defined(_M_ARM64)
int RelaunchViaArm64Companion(int argc, wchar_t* argv[]);
#endif
bool IsRunningElevated();
std::wstring ResolveServiceImagePath(const std::wstring& imagePath);
bool VerifyDriverFilterIntegrity(const std::wstring& driverName, const std::wstring& classGuidString);
int RunVerify();
int RunCheckPending();

void ShowUsage()
{
    std::wcout << L"Usage: DriverManager.exe install [driver_type] [device_id_mode]" << std::endl;
    std::wcout << L"       DriverManager.exe uninstall [driver_type] [optional_inf_path]" << std::endl;
    std::wcout << L"       DriverManager.exe verify" << std::endl;
    std::wcout << L"       DriverManager.exe check-pending" << std::endl;
    std::wcout << L"Commands:" << std::endl;
    std::wcout << L"  install   - Installs the specified driver" << std::endl;
    std::wcout << L"  uninstall - Uninstalls the specified driver" << std::endl;
    std::wcout << L"  verify    - Checks UpperFilters registration against installed managed drivers" << std::endl;
    std::wcout << L"              and self-heals a stale entry if its driver file is missing." << std::endl;
    std::wcout << L"  check-pending - Exits with 1072 if the kbdlayer service is marked for deletion (a reboot" << std::endl;
    std::wcout << L"              is needed after a previous uninstall before it can be installed again)." << std::endl;
    std::wcout << L"Driver Types:" << std::endl;
    std::wcout << L"  keyboard" << std::endl;
    std::wcout << L"  nodokad  - keyboard upper filter (requires nodokad.sys and nodokad.inf in exe_dir\\nodokad\\)" << std::endl;
    std::wcout << L"Optional (install only):" << std::endl;
    std::wcout << L"  [device_id_mode] - 0 = Device Instance Path mode (default), 1 = Device ID mode." << std::endl;
    std::wcout << L"                     Writes Services\\<driver>\\Parameters\\DeviceIdMode (REG_DWORD)." << std::endl;
    std::wcout << L"                     Omit to leave the value untouched." << std::endl;
    std::wcout << L"Optional (uninstall only):" << std::endl;
    std::wcout << L"  [optional_inf_path] - Path to the INF file or folder containing the INF." << std::endl;
    std::wcout << L"                        Use this to uninstall an older driver by specifying" << std::endl;
    std::wcout << L"                        the INF used at install time." << std::endl;
}

int wmain(int argc, wchar_t* argv[])
{
    // check-pending: MSI がインストールの初期（ファイルを入れる前）に呼ぶ。SCM への問い合わせだけなので、
    // x64 エミュレーション下でも動く。ARM64 の相棒バイナリはこの時点ではまだ入っていないため、リレーより前に処理する。
    if (argc == 2 && std::wstring(argv[1]) == L"check-pending") {
        return RunCheckPending();
    }

#if !defined(_M_ARM64)
    // このビルド(x64)がARM64ホスト上で実行されている場合、SetupInstallServicesFromInfSectionW等の
    // SetupAPI呼び出しはx64エミュレーション下のプロセスからは拒否される(実機検証済み。DriverStoreへの
    // ステージング=DiInstallDriverWはエミュレーションでも成功するが、サービス登録はネイティブプロセス
    // が必須)。既存のInstallDriver/UninstallDriver等のロジックには一切分岐を入れず、丸ごと同フォルダの
    // ARM64ネイティブな相棒バイナリへリレーする。ARM64ネイティブビルド(_M_ARM64)ではこの分岐自体が
    // コンパイルされないため、常に素通しで以降のロジックがそのままネイティブに動く。
    if (IsNativeArm64()) {
        return RelaunchViaArm64Companion(argc, argv);
    }
#endif

    if (argc == 2 && std::wstring(argv[1]) == L"verify") {
        if (!IsRunningElevated()) {
            std::wcerr << L"This command must be run as Administrator." << std::endl;
            return 1;
        }
        return RunVerify();
    }

    if (argc < 3) {
        ShowUsage();
        return 1;
    }

    const std::wstring command = argv[1];
    const std::wstring driverType = argv[2];

    if (command != L"install" && command != L"uninstall") {
        ShowUsage();
        return 1;
    }

    // Only gate install: an unsupported OS/architecture risks a BSOD or silent input-handling
    // corruption from loading a kernel driver never built/tested for it. Uninstall is left
    // ungated -- CA_UninstallKbdDriver uses Return="ignore" in Product.wxs,
    // so refusing here would silently leave the driver behind rather than actually blocking
    // anything.
    if (command == L"install" && !IsSupportedWindowsEnvironment()) {
        std::wcerr << L"This is the wrong Windows version. It's for Windows 11." << std::endl;
        return 1;
    }

    if (command == L"install" && argc != 3 && argc != 4) {
        ShowUsage();
        return 1;
    }
    if (command == L"uninstall" && argc != 3 && argc != 4) {
        ShowUsage();
        return 1;
    }

    std::wstring driverName;
    std::wstring classGuidString;
    if (driverType == L"keyboard") {
        driverName = L"kbdlayer";
        classGuidString = L"{4D36E96B-E325-11CE-BFC1-08002BE10318}";
    } else if (driverType == L"nodokad") {
        driverName = L"nodokad";
        classGuidString = L"{4D36E96B-E325-11CE-BFC1-08002BE10318}";
    } else {
        ShowUsage();
        return 1;
    }

    // UpperFilters に登録されているのに実体ドライバファイルが無い状態
    // （前回の install/uninstall が中断した場合にのみ起こりうる）を、実際の
    // install/uninstall 処理に入る前に検出する。見つかった場合、
    // VerifyDriverFilterIntegrity が内部で filter 登録を削除して不整合を解消する。
    // install 中の検出はそのままではドライバが正しく動く保証がないためインストール失敗
    // として扱い、再実行を促す。uninstall 中の検出は実体が元々存在しない＝
    // 削除すべきものは既に無い状態なので、そのまま正常終了する。
    if (!VerifyDriverFilterIntegrity(driverName, classGuidString)) {
        if (command == L"install") {
            std::wcerr << L"Aborting install due to a stale filter registration. Please run the installer again."
                        << std::endl;
            return 1;
        }
        std::wcout << L"Stale filter registration removed; nothing left to uninstall." << std::endl;
        return 0;
    }

    std::wstring optionalInfPath;
    if (command == L"uninstall" && argc == 4) {
        optionalInfPath = argv[3];
    }

    std::wstring deviceIdMode;
    if (command == L"install" && argc == 4) {
        deviceIdMode = argv[3];
        if (deviceIdMode != L"0" && deviceIdMode != L"1") {
            std::wcerr << L"Invalid device_id_mode. Must be 0 or 1." << std::endl;
            return 1;
        }
    }

    WarnIfOtherManagedDriversInconsistent(driverName);

    bool result = false;
    if (command == L"install") {
        result = InstallDriver(driverName, classGuidString, deviceIdMode);
    } else {
        result = UninstallDriver(driverName, classGuidString, optionalInfPath);
    }

    if (result) {
        std::wcout << L"Operation completed successfully." << std::endl;
        std::wcout << L"A system restart is required for changes to take full effect." << std::endl;
    } else {
        std::wcout << L"Operation failed." << std::endl;
    }

    return result ? 0 : 1;
}

// このマシンの*ネイティブ*なプロセッサアーキテクチャを返す。
// GetNativeSystemInfo/GetSystemInfo は使えない: ARM64ホスト上でx64エミュレーション下にある
// プロセスから呼ぶと、エミュレーション先のPROCESSOR_ARCHITECTURE_AMD64を返す（実機検証済み。
// これが原因でx64版がARM64ホストを検出できず、RelaunchViaArm64Companionへリレーされないまま
// NTamd64.Servicesでのサービス登録がERROR_IN_WOW64(0xE0000235)で失敗していた）。
// IsWow64Process2 の nativeMachine はエミュレーション下でも実機の値を返す
// （Windows 10 1709以降。IsSupportedWindowsEnvironmentの下限1903より前なので常に使える）。
DWORD GetNativeProcessorArchitecture()
{
    USHORT processMachine = IMAGE_FILE_MACHINE_UNKNOWN;
    USHORT nativeMachine = IMAGE_FILE_MACHINE_UNKNOWN;
    if (IsWow64Process2(GetCurrentProcess(), &processMachine, &nativeMachine)) {
        switch (nativeMachine) {
        case IMAGE_FILE_MACHINE_ARM64:
            return PROCESSOR_ARCHITECTURE_ARM64;
        case IMAGE_FILE_MACHINE_AMD64:
            return PROCESSOR_ARCHITECTURE_AMD64;
        case IMAGE_FILE_MACHINE_I386:
            return PROCESSOR_ARCHITECTURE_INTEL;
        default:
            return PROCESSOR_ARCHITECTURE_UNKNOWN;
        }
    }
    return PROCESSOR_ARCHITECTURE_UNKNOWN;
}

// このマシンのネイティブアーキテクチャがARM64かどうか（IsSupportedWindowsEnvironmentと同じ
// IsWow64Process2ベースの判定）。RelaunchViaArm64Companionでのリレー判定、および
// InstallDriverでのINFセクション選択・パス解決に使う。
bool IsNativeArm64()
{
    return GetNativeProcessorArchitecture() == PROCESSOR_ARCHITECTURE_ARM64;
}

// ドライバパッケージのアーキ別サブフォルダ名（KasaneSetup.wixproj/Product.wxsが
// KBDDRIVERDIR配下をx64\/arm64\に分けてステージングする構成と対応）。
std::wstring ArchSubdirName()
{
    return IsNativeArm64() ? L"arm64" : L"x64";
}

#if !defined(_M_ARM64)
// x64ビルドがARM64ホスト上で実行されていると判定された場合に、既存ロジックを一切変更せず
// 丸ごと同フォルダのDriverManager_arm64.exe(ネイティブARM64ビルド、同じDriverManager.cppを
// 共有参照)へリレーする。同じ引数をそのまま渡し、その終了コードをそのまま返す。
int RelaunchViaArm64Companion(int argc, wchar_t* argv[])
{
    const std::filesystem::path companionPath =
        std::filesystem::path(GetExecutablePath()) / L"DriverManager_arm64.exe";
    if (!std::filesystem::exists(companionPath)) {
        std::wcerr << L"Native ARM64 host detected, but " << companionPath.wstring()
                   << L" is missing. Cannot proceed." << std::endl;
        return 1;
    }

    std::wstring commandLine = L"\"" + companionPath.wstring() + L"\"";
    for (int i = 1; i < argc; ++i) {
        commandLine += L" \"" + std::wstring(argv[i]) + L"\"";
    }

    std::wcout << L"Native ARM64 host detected; relaunching via " << companionPath.wstring() << std::endl;

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(companionPath.c_str(), commandLine.data(), nullptr, nullptr, FALSE, 0,
                         nullptr, nullptr, &si, &pi)) {
        std::wcerr << L"Failed to relaunch ARM64 companion. Error: " << GetLastError() << std::endl;
        return 1;
    }

    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD exitCode = 1;
    GetExitCodeProcess(pi.hProcess, &exitCode);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    return static_cast<int>(exitCode);
}
#endif

// ネイティブアーキテクチャ（エミュレーション下でも実機の値を返す IsWow64Process2 を使う）が
// x64 または ARM64 であり、かつ CurrentBuildNumber が Windows 10 1903（18362）以上であることを
// 確認する。レジストリ読み取りに失敗した場合も fail closed で false を返す。
bool IsSupportedWindowsEnvironment()
{
    const DWORD architecture = GetNativeProcessorArchitecture();
    if (architecture != PROCESSOR_ARCHITECTURE_AMD64 && architecture != PROCESSOR_ARCHITECTURE_ARM64) {
        return false;
    }

    HKEY key = nullptr;
    if (RegOpenKeyExW(
            HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", 0,
            KEY_QUERY_VALUE, &key) != ERROR_SUCCESS) {
        return false;
    }

    wchar_t buildNumberText[32]{};
    DWORD size = sizeof(buildNumberText);
    DWORD type = 0;
    LONG result = RegQueryValueExW(
        key, L"CurrentBuildNumber", nullptr, &type,
        reinterpret_cast<BYTE*>(buildNumberText), &size);
    RegCloseKey(key);

    if (result != ERROR_SUCCESS || type != REG_SZ) {
        return false;
    }

    constexpr unsigned long kMinimumSupportedBuildNumber = 18362; // Windows 10 1903.
    return wcstoul(buildNumberText, nullptr, 10) >= kMinimumSupportedBuildNumber;
}

// 現在のプロセストークンが昇格済みかどうかを確認する。
bool IsRunningElevated()
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        return false;
    }

    TOKEN_ELEVATION elevation{};
    DWORD returnedSize = 0;
    BOOL isElevated = FALSE;
    if (GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &returnedSize)) {
        isElevated = elevation.TokenIsElevated;
    }

    CloseHandle(token);
    return isElevated != FALSE;
}

// サービス kbdlayer が「削除予約済み」（前のアンインストールの後、再起動するまで残る。この状態では INF によるサービスの
// 作成が ERROR_SERVICE_MARKED_FOR_DELETE = 1072 で失敗し、インストールが巻き戻る）かを調べる。
// 0: 問題なし（サービスが無い・調べられなかった場合を含む）。1072: 削除予約済み（先に再起動が要る）。
// 調べ方: 設定を変えない ChangeServiceConfig（全て SERVICE_NO_CHANGE / NULL）が 1072 を返すかを見る。
// SERVICE_CHANGE_CONFIG で開けない（管理者でない）ときは判定できないので、問題なしとして通す（インストール自体を妨げない）。
int RunCheckPending()
{
    SC_HANDLE scm = OpenSCManagerW(NULL, NULL, SC_MANAGER_CONNECT);
    if (!scm) return 0;
    SC_HANDLE svc = OpenServiceW(scm, L"kbdlayer", SERVICE_CHANGE_CONFIG);
    if (!svc) {
        DWORD openError = GetLastError();
        CloseServiceHandle(scm);
        if (openError == ERROR_SERVICE_MARKED_FOR_DELETE) {
            std::wcerr << L"The kbdlayer service is marked for deletion. Restart the computer, then run the installer again." << std::endl;
            return ERROR_SERVICE_MARKED_FOR_DELETE;
        }
        return 0;
    }

    int result = 0;
    if (!ChangeServiceConfigW(svc, SERVICE_NO_CHANGE, SERVICE_NO_CHANGE, SERVICE_NO_CHANGE,
                              NULL, NULL, NULL, NULL, NULL, NULL, NULL)) {
        if (GetLastError() == ERROR_SERVICE_MARKED_FOR_DELETE) {
            std::wcerr << L"The kbdlayer service is marked for deletion. Restart the computer, then run the installer again." << std::endl;
            result = ERROR_SERVICE_MARKED_FOR_DELETE;
        }
    }
    CloseServiceHandle(svc);
    CloseServiceHandle(scm);
    return result;
}

// 指定サービスを停止し SERVICE_STOPPED になるまで待機する。
// サービスが存在しない場合は「既に停止済み」として true を返す。
bool stopAndWaitService(const std::wstring& serviceName, DWORD timeoutMs)
{
    SC_HANDLE scm = OpenSCManagerW(NULL, NULL, SC_MANAGER_CONNECT);
    if (!scm) return false;
    SC_HANDLE svc = OpenServiceW(scm, serviceName.c_str(), SERVICE_STOP | SERVICE_QUERY_STATUS);
    if (!svc) {
        bool notExist = (GetLastError() == ERROR_SERVICE_DOES_NOT_EXIST);
        CloseServiceHandle(scm);
        return notExist;
    }

    SERVICE_STATUS ss = {};
    bool stopped = QueryServiceStatus(svc, &ss) && ss.dwCurrentState == SERVICE_STOPPED;
    if (!stopped) {
        ControlService(svc, SERVICE_CONTROL_STOP, &ss);
        const DWORD intervalMs = 500;
        for (DWORD waited = 0; waited < timeoutMs; waited += intervalMs) {
            if (!QueryServiceStatus(svc, &ss)) break;
            if (ss.dwCurrentState == SERVICE_STOPPED) {
                stopped = true;
                break;
            }
            Sleep(intervalMs);
        }
    }

    CloseServiceHandle(svc);
    CloseServiceHandle(scm);
    return stopped;
}

// FileRepository\<driverName>* に一致するサブディレクトリ内の <driverName>.inf を全て列挙する。
std::vector<std::wstring> EnumerateDriverStoreInfs(const std::wstring& driverName)
{
    std::vector<std::wstring> result;
    wchar_t windir[MAX_PATH] = {};
    GetWindowsDirectoryW(windir, MAX_PATH);
    std::wstring repoBase = std::wstring(windir) + L"\\System32\\DriverStore\\FileRepository\\";
    std::wstring searchPattern = repoBase + driverName + L"*";

    WIN32_FIND_DATAW fd = {};
    HANDLE hFind = FindFirstFileW(searchPattern.c_str(), &fd);
    if (hFind == INVALID_HANDLE_VALUE) return result;
    do {
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        if (wcscmp(fd.cFileName, L".") == 0 || wcscmp(fd.cFileName, L"..") == 0) continue;
        std::wstring candidate = repoBase + fd.cFileName + L"\\" + driverName + L".inf";
        if (GetFileAttributesW(candidate.c_str()) != INVALID_FILE_ATTRIBUTES) {
            result.push_back(candidate);
        }
    } while (FindNextFileW(hFind, &fd));
    FindClose(hFind);
    return result;
}

// DriverStore内の対象ドライバの全パッケージ（全世代）を削除する。
bool RemoveDriverStorePackages(const std::wstring& driverName)
{
    bool allOk = true;
    for (const auto& infPath : EnumerateDriverStoreInfs(driverName)) {
        std::wcout << L"Uninstalling DriverStore package: " << infPath << std::endl;
        BOOL rebootReq = FALSE;
        if (!DiUninstallDriverW(NULL, infPath.c_str(), 0, &rebootReq)) {
            DWORD error = GetLastError();
            if (error != ERROR_NOT_FOUND) {
                std::wcerr << L"DiUninstallDriverW failed for " << infPath << L". Error: " << error << std::endl;
                allOk = false;
            }
        } else if (rebootReq) {
            std::wcout << L"A reboot is required to complete removal of: " << infPath << std::endl;
        }
    }
    return allOk;
}

// System32\drivers\ に残る <driverName>.sys / .inf を無条件削除する。
void RemoveLeftoverSystemFiles(const std::wstring& driverName)
{
    wchar_t sys32[MAX_PATH] = {};
    GetSystemDirectoryW(sys32, MAX_PATH);
    std::wstring driversDir = std::wstring(sys32) + L"\\drivers\\";

    for (const wchar_t* ext : { L".sys", L".inf" }) {
        std::wstring path = driversDir + driverName + ext;
        if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) continue;
        if (DeleteFileW(path.c_str())) {
            std::wcout << L"Removed leftover file: " << path << std::endl;
        } else {
            MoveFileExW(path.c_str(), NULL, MOVEFILE_DELAY_UNTIL_REBOOT);
            std::wcout << L"Leftover file scheduled for deletion on next reboot: " << path << std::endl;
        }
    }
}

// サービスキーが存在するかどうかを確認する。
bool ServiceExists(const std::wstring& serviceName)
{
    SC_HANDLE scm = OpenSCManagerW(NULL, NULL, SC_MANAGER_CONNECT);
    if (!scm) return false;
    SC_HANDLE svc = OpenServiceW(scm, serviceName.c_str(), SERVICE_QUERY_STATUS);
    bool exists = (svc != NULL);
    if (svc) CloseServiceHandle(svc);
    CloseServiceHandle(scm);
    return exists;
}

// クラスキーの UpperFilters に対象ドライバ名が何個含まれているかを数える（検証用）。
int CountUpperFiltersOccurrences(const std::wstring& classGuidString, const std::wstring& driverName)
{
    std::wstring regPath = L"SYSTEM\\CurrentControlSet\\Control\\Class\\" + classGuidString;
    HKEY hKey;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, regPath.c_str(), 0, KEY_READ, &hKey) != ERROR_SUCCESS) {
        return 0;
    }

    std::vector<wchar_t> buffer(4096);
    DWORD bufferSize = static_cast<DWORD>(buffer.size() * sizeof(wchar_t));
    DWORD type;
    int count = 0;
    if (RegQueryValueExW(hKey, L"UpperFilters", 0, &type, (LPBYTE)buffer.data(), &bufferSize) == ERROR_SUCCESS) {
        for (const wchar_t* p = buffer.data(); *p; p += wcslen(p) + 1) {
            if (driverName == p) ++count;
        }
    }
    RegCloseKey(hKey);
    return count;
}

// クラスキーの UpperFilters に対象ドライバ名が含まれているかを確認する（検証用）。
bool UpperFiltersContains(const std::wstring& classGuidString, const std::wstring& driverName)
{
    return CountUpperFiltersOccurrences(classGuidString, driverName) > 0;
}

// サービスの現在の ImagePath を取得する。取得できない場合は空文字列を返す。
std::wstring GetServiceImagePath(const std::wstring& serviceName)
{
    std::wstring imagePath;
    SC_HANDLE scm = OpenSCManagerW(NULL, NULL, SC_MANAGER_CONNECT);
    if (!scm) return imagePath;
    SC_HANDLE svc = OpenServiceW(scm, serviceName.c_str(), SERVICE_QUERY_CONFIG);
    if (svc) {
        DWORD bytesNeeded = 0;
        QueryServiceConfigW(svc, NULL, 0, &bytesNeeded);
        if (bytesNeeded > 0) {
            std::vector<BYTE> buffer(bytesNeeded);
            LPQUERY_SERVICE_CONFIGW cfg = reinterpret_cast<LPQUERY_SERVICE_CONFIGW>(buffer.data());
            if (QueryServiceConfigW(svc, cfg, bytesNeeded, &bytesNeeded) && cfg->lpBinaryPathName) {
                imagePath = cfg->lpBinaryPathName;
            }
        }
        CloseServiceHandle(svc);
    }
    CloseServiceHandle(scm);
    return imagePath;
}

// サービスの ImagePath（環境変数展開前・\??\ や \SystemRoot\ 等の NT パス形式のこともある）を
// 実際のファイルシステムパスへ解決する。実機の SetupInstallServicesFromInfSectionW の出力で
// 確認済みの形式（"\SystemRoot\System32\DriverStore\FileRepository\...\kbdlayer.sys" 等）を
// 優先的に処理し、"\??\" プレフィックスや Windows ディレクトリ相対のベアパスにも対応する。
std::wstring ResolveServiceImagePath(const std::wstring& imagePath)
{
    namespace fs = std::filesystem;

    wchar_t expanded[MAX_PATH * 2];
    std::wstring path = ExpandEnvironmentStringsW(imagePath.c_str(), expanded, static_cast<DWORD>(std::size(expanded))) > 0
        ? expanded
        : imagePath;

    wchar_t windowsDirectory[MAX_PATH];
    GetWindowsDirectoryW(windowsDirectory, MAX_PATH);

    const wchar_t systemRootPrefix[] = L"\\SystemRoot\\";
    const wchar_t ntPathPrefix[] = L"\\??\\";

    if (_wcsnicmp(path.c_str(), systemRootPrefix, wcslen(systemRootPrefix)) == 0) {
        path = std::wstring(windowsDirectory) + L"\\" + path.substr(wcslen(systemRootPrefix));
    } else if (path.rfind(ntPathPrefix, 0) == 0) {
        path = path.substr(wcslen(ntPathPrefix));
    }

    fs::path resolved(path);
    if (!resolved.is_absolute()) {
        resolved = fs::path(windowsDirectory) / path;
    }
    return resolved.wstring();
}

// 大文字小文字を無視した部分文字列検索（ImagePath の一致判定に使用）。
bool ContainsCaseInsensitive(const std::wstring& haystack, const std::wstring& needle)
{
    if (needle.empty()) return true;
    std::wstring h = haystack;
    std::wstring n = needle;
    std::transform(h.begin(), h.end(), h.begin(), [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
    std::transform(n.begin(), n.end(), n.begin(), [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
    return h.find(n) != std::wstring::npos;
}

// install 失敗時、直前にステージングした新パッケージのみを削除するロールバック。
void RollbackNewPackageOnly(const std::wstring& driverStoreInfPath)
{
    std::wcout << L"Rolling back newly staged package: " << driverStoreInfPath << std::endl;
    BOOL rebootReq = FALSE;
    if (!DiUninstallDriverW(NULL, driverStoreInfPath.c_str(), 0, &rebootReq)) {
        DWORD error = GetLastError();
        if (error != ERROR_NOT_FOUND) {
            std::wcerr << L"Rollback DiUninstallDriverW failed. Error: " << error << std::endl;
        }
    }
}

// uninstall 後に UpperFilters・サービス・DriverStore の 3 点が消えたことを確認する。
bool VerifyUninstalled(const std::wstring& classGuidString, const std::wstring& driverName)
{
    bool ok = true;
    if (UpperFiltersContains(classGuidString, driverName)) {
        std::wcerr << L"Verification failed: " << driverName << L" still present in UpperFilters." << std::endl;
        ok = false;
    }
    if (InfClassFilterRegistrations(classGuidString, driverName, false) != 0) {
        std::wcerr << L"Verification failed: " << driverName
                    << L" is still registered as an INF AddFilter class filter (Class\\{GUID}\\Filters)." << std::endl;
        ok = false;
    }
    if (ServiceExists(driverName)) {
        std::wcerr << L"Verification failed: service key " << driverName << L" still exists." << std::endl;
        ok = false;
    }
    auto remaining = EnumerateDriverStoreInfs(driverName);
    if (!remaining.empty()) {
        std::wcerr << L"Verification failed: " << remaining.size()
                    << L" DriverStore package(s) still present for " << driverName << L"." << std::endl;
        ok = false;
    }
    return ok;
}

// install 後に UpperFilters にちょうど 1 個・サービスキー・DriverStore が揃っていることを確認する。
bool VerifyInstalled(const std::wstring& classGuidString, const std::wstring& driverName)
{
    bool ok = true;
    int count = CountUpperFiltersOccurrences(classGuidString, driverName);
    if (count != 1) {
        std::wcerr << L"Verification failed: " << driverName << L" appears " << count
                    << L" time(s) in UpperFilters (expected exactly 1)." << std::endl;
        ok = false;
    }
    // AddFilter による登録が残っていると、UpperFilters と合わせて2段載る（InfClassFilterRegistrations参照）。
    if (InfClassFilterRegistrations(classGuidString, driverName, false) != 0) {
        std::wcerr << L"Verification failed: " << driverName
                    << L" is also registered as an INF AddFilter class filter (Class\\{GUID}\\Filters); "
                       L"it would be attached twice." << std::endl;
        ok = false;
    }
    if (!ServiceExists(driverName)) {
        std::wcerr << L"Verification failed: service key " << driverName << L" does not exist." << std::endl;
        ok = false;
    }
    if (EnumerateDriverStoreInfs(driverName).empty()) {
        std::wcerr << L"Verification failed: no DriverStore package found for " << driverName << L"." << std::endl;
        ok = false;
    }
    return ok;
}

// INF ファイルの [Version] セクションから DriverVer の値を取得する。
std::wstring GetInfDriverVer(const std::wstring& infPath)
{
    HINF hInf = SetupOpenInfFileW(infPath.c_str(), NULL, INF_STYLE_WIN4, NULL);
    if (hInf == INVALID_HANDLE_VALUE) return L"";
    wchar_t buffer[256] = {};
    std::wstring result;
    if (SetupGetLineTextW(NULL, hInf, L"Version", L"DriverVer", buffer, ARRAYSIZE(buffer), NULL)) {
        result = buffer;
    }
    SetupCloseInfFile(hInf);
    return result;
}

// DriverVer 文字列 "MM/DD/YYYY,a.b.c.d" を比較し、verA が verB 以上（同じか新しい）かを判定する。
bool IsDriverVerNewerOrEqual(const std::wstring& verA, const std::wstring& verB)
{
    auto parse = [](const std::wstring& v, unsigned __int64& dateKey, unsigned __int64& versionKey) -> bool {
        size_t comma = v.find(L',');
        std::wstring datePart = (comma == std::wstring::npos) ? v : v.substr(0, comma);
        std::wstring verPart = (comma == std::wstring::npos) ? L"" : v.substr(comma + 1);
        unsigned mm = 0, dd = 0, yyyy = 0;
        if (swscanf_s(datePart.c_str(), L"%u/%u/%u", &mm, &dd, &yyyy) != 3) return false;
        dateKey = (unsigned __int64)yyyy * 10000 + mm * 100 + dd;
        unsigned a = 0, b = 0, c = 0, d = 0;
        swscanf_s(verPart.c_str(), L"%u.%u.%u.%u", &a, &b, &c, &d);
        versionKey = (unsigned __int64)a * 1000000000000ULL + (unsigned __int64)b * 100000000ULL
                   + (unsigned __int64)c * 10000ULL + d;
        return true;
    };

    unsigned __int64 dateA = 0, verNumA = 0, dateB = 0, verNumB = 0;
    bool okA = parse(verA, dateA, verNumA);
    bool okB = parse(verB, dateB, verNumB);
    if (!okA) return false;
    if (!okB) return true;
    if (dateA != dateB) return dateA > dateB;
    return verNumA >= verNumB;
}

// 複数の DriverStore パッケージから DriverVer が最も新しいものを選ぶ。
std::wstring SelectNewestDriverStorePackage(const std::vector<std::wstring>& infPaths)
{
    std::wstring best;
    std::wstring bestVer;
    for (const auto& candidate : infPaths) {
        std::wstring ver = GetInfDriverVer(candidate);
        if (best.empty() || IsDriverVerNewerOrEqual(ver, bestVer)) {
            best = candidate;
            bestVer = ver;
        }
    }
    return best;
}

struct ManagedDriver { const wchar_t* name; const wchar_t* classGuid; };
static const ManagedDriver kManagedDrivers[] = {
    {L"nodokad",  L"{4D36E96B-E325-11CE-BFC1-08002BE10318}"},
    {L"nodokad2", L"{4D36E96B-E325-11CE-BFC1-08002BE10318}"},
    {L"kbdlayer", L"{4D36E96B-E325-11CE-BFC1-08002BE10318}"},
};

// 操作対象以外の管理ドライバについて、UpperFilters にあるがサービスキーが無い不整合を警告する。
void WarnIfOtherManagedDriversInconsistent(const std::wstring& excludeDriverName)
{
    for (const auto& d : kManagedDrivers) {
        if (excludeDriverName == d.name) continue;
        if (UpperFiltersContains(d.classGuid, d.name) && !ServiceExists(d.name)) {
            std::wcerr << L"Warning: " << d.name << L" is present in UpperFilters but its service "
                          L"does not exist. It may be incompletely installed or uninstalled. "
                          L"No automatic action was taken." << std::endl;
        }
    }
}

// UpperFilters に登録されているが、サービスの ImagePath が指すファイルが実在しない場合に、
// 次回再起動でキーボードが全滅する事故を避けるため、その場で UpperFilters から
// 該当エントリを削除する（インストールが不完全な状態でのみ起こりうる。正常に完了した
// インストールではこの状態には決してならない）。
bool VerifyDriverFilterIntegrity(const std::wstring& driverName, const std::wstring& classGuidString)
{
    if (!UpperFiltersContains(classGuidString, driverName)) {
        // 登録されていない = 未インストールか、既にアンインストール済み。確認事項なし。
        return true;
    }

    std::wstring imagePath = GetServiceImagePath(driverName);
    bool fileExists = !imagePath.empty() &&
        GetFileAttributesW(ResolveServiceImagePath(imagePath).c_str()) != INVALID_FILE_ATTRIBUTES;

    if (fileExists) {
        std::wcout << L"[OK] " << driverName << L" is registered as an upper filter and its driver file is present." << std::endl;
        return true;
    }

    std::wcerr << L"[ERROR] " << driverName << L" is registered as an upper filter, but its driver file is "
                  L"missing. Leaving this in place would make the corresponding device class stop working "
                  L"entirely after the next reboot, so the filter registration has been removed. This means "
                  L"the installation was not completed correctly -- please reinstall." << std::endl;
    ModifyUpperFilters(classGuidString, driverName, false);
    return false;
}

// スタンドアロンの検証コマンド（install/uninstall とは独立に、任意のタイミングで実行できる）。
// 管理下のドライバのうち、少なくとも UpperFilters かサービスキーのどちらかが存在するものだけを
// 対象にする（一度も導入されていないドライバは確認事項がないため）。
int RunVerify()
{
    bool ok = true;
    for (const auto& d : kManagedDrivers) {
        if (!ServiceExists(d.name) && !UpperFiltersContains(d.classGuid, d.name)) {
            continue;
        }
        ok = VerifyDriverFilterIntegrity(d.name, d.classGuid) && ok;
    }
    return ok ? 0 : 1;
}

// kbdlayer.sys / nodokad.sys は Services\<driverName>\Parameters\DeviceIdMode (REG_DWORD) を読み、
// 0=デバイスインスタンスパスモード（既定）、1=デバイスIDモードとして動作する。
// deviceIdMode が空（コマンドラインで未指定）の場合は何も書き込まず、既存値／ドライバの
// 既定動作をそのまま維持する。
void WriteDeviceIdMode(const std::wstring& driverName, const std::wstring& deviceIdMode)
{
    if (deviceIdMode.empty()) {
        return;
    }
    const std::wstring keyPath = L"SYSTEM\\CurrentControlSet\\Services\\" + driverName + L"\\Parameters";
    HKEY hKey = nullptr;
    LONG status = RegCreateKeyExW(
        HKEY_LOCAL_MACHINE, keyPath.c_str(), 0, nullptr,
        REG_OPTION_NON_VOLATILE, KEY_SET_VALUE, nullptr, &hKey, nullptr);
    if (status != ERROR_SUCCESS) {
        std::wcerr << L"Warning: failed to open/create " << keyPath << L" to set DeviceIdMode. Error: " << status << std::endl;
        return;
    }
    DWORD value = (deviceIdMode == L"1") ? 1 : 0;
    status = RegSetValueExW(hKey, L"DeviceIdMode", 0, REG_DWORD, reinterpret_cast<const BYTE*>(&value), sizeof(value));
    if (status != ERROR_SUCCESS) {
        std::wcerr << L"Warning: failed to write DeviceIdMode under " << keyPath << L". Error: " << status << std::endl;
    }
    RegCloseKey(hKey);
}

// kbdlayer.sys の VERSIONINFO リソース（.rc の FileVersion、例 "1.00"）を読み取る。
// .rc に書いたバージョン文字列はビルド時にそのままバイナリへ埋め込まれるため、ここで
// 取り出すだけで .rc の値と常に同期した文字列が得られる（別経路での二重管理を避ける）。
static std::wstring GetFileVersionString(const std::wstring& filePath)
{
    DWORD handle = 0;
    DWORD size = GetFileVersionInfoSizeW(filePath.c_str(), &handle);
    if (size == 0) {
        return L"";
    }

    std::vector<BYTE> buffer(size);
    if (!GetFileVersionInfoW(filePath.c_str(), handle, size, buffer.data())) {
        return L"";
    }

    struct LANGANDCODEPAGE { WORD wLanguage; WORD wCodePage; } *translations = nullptr;
    UINT translationsLen = 0;
    if (!VerQueryValueW(buffer.data(), L"\\VarFileInfo\\Translation",
                         reinterpret_cast<LPVOID*>(&translations), &translationsLen) ||
        translationsLen < sizeof(LANGANDCODEPAGE)) {
        return L"";
    }

    wchar_t subBlock[64];
    swprintf_s(subBlock, L"\\StringFileInfo\\%04x%04x\\FileVersion",
               translations[0].wLanguage, translations[0].wCodePage);

    LPVOID value = nullptr;
    UINT valueLen = 0;
    if (!VerQueryValueW(buffer.data(), subBlock, &value, &valueLen) || valueLen == 0) {
        return L"";
    }

    return std::wstring(static_cast<wchar_t*>(value));
}

// kbdlayer.sys は Services\<driverName>\Parameters\Version (REG_SZ) を読める。
// ドライババイナリの VERSIONINFO（.rc の FileVersion）から取り出した文字列（例 "1.00"）を
// そのままインストール時に書き込む。サポート/診断時に reg query だけで導入済みドライバの
// バージョンを確認できるようにするため。
void WriteDriverVersion(const std::wstring& driverName, const std::wstring& sysFilePath)
{
    std::wstring version = GetFileVersionString(sysFilePath);
    if (version.empty()) {
        std::wcerr << L"Warning: failed to read FileVersion from " << sysFilePath << std::endl;
        return;
    }

    const std::wstring keyPath = L"SYSTEM\\CurrentControlSet\\Services\\" + driverName + L"\\Parameters";
    HKEY hKey = nullptr;
    LSTATUS status = RegCreateKeyExW(
        HKEY_LOCAL_MACHINE, keyPath.c_str(), 0, nullptr,
        REG_OPTION_NON_VOLATILE, KEY_SET_VALUE, nullptr, &hKey, nullptr);
    if (status != ERROR_SUCCESS) {
        std::wcerr << L"Warning: failed to open/create " << keyPath << L" to set Version. Error: " << status << std::endl;
        return;
    }
    status = RegSetValueExW(hKey, L"Version", 0, REG_SZ,
                             reinterpret_cast<const BYTE*>(version.c_str()),
                             static_cast<DWORD>((version.size() + 1) * sizeof(wchar_t)));
    if (status != ERROR_SUCCESS) {
        std::wcerr << L"Warning: failed to write Version under " << keyPath << L". Error: " << status << std::endl;
    }
    RegCloseKey(hKey);
}

bool InstallDriver(const std::wstring& driverName, const std::wstring& classGuidString, const std::wstring& deviceIdMode)
{
    namespace fs = std::filesystem;
    const fs::path exePath = GetExecutablePath();
    // インストーラ(KasaneSetup.wixproj/Product.wxs)がKBDDRIVERDIR配下を
    // x64\/arm64\サブフォルダに分けてステージングしているため、ネイティブアーキで選択する。
    const fs::path infPath = exePath / driverName / ArchSubdirName() / (driverName + L".inf");

    if (!fs::exists(infPath)) {
        std::wcerr << L"INF file not found at: " << infPath << std::endl;
        return false;
    }

    std::wcout << L"Installing driver package from: " << infPath << std::endl;

    // アップグレードか新規かを記録する。ModifyUpperFilters 失敗時のロールバック判断に使う。
    bool serviceExistedBefore = ServiceExists(driverName);

    // SetupInstallServicesFromInfSectionW が安全に動作できるよう既存サービスを停止して待機する。
    if (!stopAndWaitService(driverName, 5000)) {
        std::wcerr << L"Service did not reach STOPPED state within timeout. Continuing anyway." << std::endl;
    }

    // install 開始時点の DriverStore 世代を「旧世代」として保持する。成功時に削除して蓄積を防ぐ。
    std::vector<std::wstring> oldGenerations = EnumerateDriverStoreInfs(driverName);

    BOOL rebootRequired = FALSE;
    if (!DiInstallDriverW(nullptr, infPath.c_str(), 0, &rebootRequired)) {
        std::wcerr << L"DiInstallDriverW failed. Error: " << GetLastError() << std::endl;
        return false;
    }

    // ステージング後に新規作成されたパッケージを特定する。
    // superseded（既存より古い）パッケージは即座にロールバックする。
    std::vector<std::wstring> postGenerations = EnumerateDriverStoreInfs(driverName);
    std::vector<std::wstring> newCandidates;
    for (const auto& candidate : postGenerations) {
        if (std::find(oldGenerations.begin(), oldGenerations.end(), candidate) == oldGenerations.end()) {
            newCandidates.push_back(candidate);
        }
    }

    std::wstring driverStoreInfPath;
    bool isNewPackage = false;
    if (!newCandidates.empty()) {
        std::wstring bestOldVer = oldGenerations.empty()
            ? L""
            : GetInfDriverVer(SelectNewestDriverStorePackage(oldGenerations));
        for (const auto& candidate : newCandidates) {
            std::wstring candidateVer = GetInfDriverVer(candidate);
            if (oldGenerations.empty() || IsDriverVerNewerOrEqual(candidateVer, bestOldVer)) {
                driverStoreInfPath = candidate;
                isNewPackage = true;
            } else {
                std::wcout << L"Staged package is older than the existing active package; "
                               L"rolling back superseded package: " << candidate << std::endl;
                RollbackNewPackageOnly(candidate);
            }
        }
    }

    if (driverStoreInfPath.empty()) {
        // 同一バージョン再インストール等で新規フォルダが作られなかった場合は既存の最新を使う。
        driverStoreInfPath = SelectNewestDriverStorePackage(EnumerateDriverStoreInfs(driverName));
        isNewPackage = false;
    }

    if (driverStoreInfPath.empty()) {
        std::wcerr << L"Staged INF not found in FileRepository for: " << driverName << std::endl;
        return false;
    }
    std::wcout << L"DriverStore INF: " << driverStoreInfPath
                << (isNewPackage ? L" (newly staged)" : L" (existing)") << std::endl;

    HINF hInf = SetupOpenInfFileW(driverStoreInfPath.c_str(), nullptr, INF_STYLE_WIN4, nullptr);
    if (hInf == INVALID_HANDLE_VALUE) {
        std::wcerr << L"SetupOpenInfFileW failed. Error: " << GetLastError() << std::endl;
        if (isNewPackage) RollbackNewPackageOnly(driverStoreInfPath);
        return false;
    }

    // kbdlayer.inf は DefaultInstall セクションのみ持つ。_WIN64 はx64ビルドでもARM64ビルドでも
    // 常に真になるビルド時マクロなので使えない(ARM64ビルドでも誤ってNTamd64.Servicesを選んでしまう)。
    // ネイティブアーキテクチャのランタイム判定で選ぶ。
    std::wstring serviceSection = L"DefaultInstall.";
    serviceSection += IsNativeArm64() ? L"NTarm64.Services" : L"NTamd64.Services";
    std::wcout << L"Creating service from section: " << serviceSection << std::endl;
    const BOOL svcResult = SetupInstallServicesFromInfSectionW(hInf, serviceSection.c_str(), 0);
    const DWORD svcErr = GetLastError();
    SetupCloseInfFile(hInf);
    if (!svcResult) {
        std::wcerr << L"SetupInstallServicesFromInfSectionW failed. Error: " << svcErr << std::endl;
        if (isNewPackage) RollbackNewPackageOnly(driverStoreInfPath);
        return false;
    }

    // サービスの ImagePath が新しい DriverStore パスへ更新されたことを確認した後にのみ
    // 旧 System32\drivers\<name>.sys を削除する。
    const fs::path newPackageDir = fs::path(driverStoreInfPath).parent_path();
    const std::wstring packageDirMarker = L"\\DriverStore\\FileRepository\\" + newPackageDir.filename().wstring();
    const std::wstring imagePath = GetServiceImagePath(driverName);
    bool imagePathUpdated = !imagePath.empty() && ContainsCaseInsensitive(imagePath, packageDirMarker);
    if (imagePathUpdated) {
        wchar_t sys32[MAX_PATH];
        GetSystemDirectoryW(sys32, MAX_PATH);
        std::wstring oldSys = std::wstring(sys32) + L"\\drivers\\" + driverName + L".sys";
        if (GetFileAttributesW(oldSys.c_str()) != INVALID_FILE_ATTRIBUTES) {
            if (!DeleteFileW(oldSys.c_str())) {
                MoveFileExW(oldSys.c_str(), nullptr, MOVEFILE_DELAY_UNTIL_REBOOT);
                std::wcout << L"Old sys scheduled for deletion on next reboot: " << oldSys << std::endl;
            } else {
                std::wcout << L"Old sys deleted: " << oldSys << std::endl;
            }
        }
    } else {
        std::wcout << L"Service ImagePath does not point to the new DriverStore package yet; "
                       L"skipping old System32\\drivers cleanup." << std::endl;
    }

    // kbdlayer must be placed BEFORE kbdclass in UpperFilters so it can intercept
    // IOCTL_INTERNAL_KEYBOARD_CONNECT sent down by the class driver.
    // nodokad reads from IRP completions and should remain AFTER kbdclass.
    const bool insertBeforeClass = (driverName == L"kbdlayer");

    // 以前の kbdlayer.inf（AddFilter あり）が一度でも INF 単体でインストールされていると、
    // Class\{GUID}\Filters にも登録が残り、UpperFilters と合わせて2段載ってしまう。
    // フィルタの登録は UpperFilters（順序表）に一本化するので、こちらは取り除く。
    // 反映はデバイスの再起動（またはOSの再起動）後。
    InfClassFilterRegistrations(classGuidString, driverName, true);

    std::wcout << L"Updating UpperFilters for " << driverName
               << (insertBeforeClass ? L" (before class driver)..." : L" (after class driver)...") << std::endl;
    if (!ModifyUpperFilters(classGuidString, driverName, true, insertBeforeClass)) {
        std::wcerr << L"Failed to modify UpperFilters registry." << std::endl;
        if (!serviceExistedBefore) {
            // 新規インストールが最終段で失敗：今回作成したサービスと新規パッケージをロールバック。
            std::wcerr << L"Fresh install: rolling back service and new package." << std::endl;
            stopAndWaitService(driverName, 5000);
            SC_HANDLE hScm = OpenSCManagerW(NULL, NULL, SC_MANAGER_ALL_ACCESS);
            if (hScm) {
                SC_HANDLE hSvc = OpenServiceW(hScm, driverName.c_str(), DELETE);
                if (hSvc) {
                    DeleteService(hSvc);
                    CloseServiceHandle(hSvc);
                }
                CloseServiceHandle(hScm);
            }
            if (isNewPackage) RollbackNewPackageOnly(driverStoreInfPath);
        } else {
            // アップグレード中の失敗：既存エントリの参照先を壊さないよう自動削除はしない。
            std::wcerr << L"Upgrade in progress: leaving the existing service/package intact to avoid "
                           L"breaking the still-registered UpperFilters entry. Manual review recommended."
                        << std::endl;
        }
        return false;
    }

    // 成功時、旧世代の DriverStore パッケージを削除して世代蓄積を防ぐ。
    for (const auto& oldInfPath : oldGenerations) {
        if (oldInfPath == driverStoreInfPath) continue;
        std::wcout << L"Removing superseded DriverStore package: " << oldInfPath << std::endl;
        BOOL rebootReq = FALSE;
        if (!DiUninstallDriverW(NULL, oldInfPath.c_str(), 0, &rebootReq)) {
            DWORD error = GetLastError();
            if (error != ERROR_NOT_FOUND) {
                std::wcerr << L"Failed to remove superseded package " << oldInfPath << L". Error: " << error << std::endl;
            }
        }
    }

    if (!VerifyInstalled(classGuidString, driverName)) {
        return false;
    }

    WriteDeviceIdMode(driverName, deviceIdMode);
    WriteDriverVersion(driverName, (newPackageDir / (driverName + L".sys")).wstring());

    return true;
}

bool UninstallDriver(
    const std::wstring& driverName,
    const std::wstring& classGuidString,
    const std::wstring& optionalInfPath)
{
    // 1. UpperFilters から先に削除する（リトライ付き）。
    //    これが失敗したままサービスを削除すると UpperFilters に死んだ参照が残り
    //    キーボードが Code 19 で全滅する事故につながるため、失敗時は即中断する。
    std::wcout << L"Removing " << driverName << L" from UpperFilters..." << std::endl;
    bool filtersRemoved = false;
    const int maxRetries = 3;
    for (int attempt = 1; attempt <= maxRetries; ++attempt) {
        if (ModifyUpperFilters(classGuidString, driverName, false)) {
            filtersRemoved = true;
            break;
        }
        std::wcerr << L"ModifyUpperFilters(remove) failed (attempt " << attempt << L"/" << maxRetries << L")." << std::endl;
        if (attempt < maxRetries) Sleep(200);
    }
    if (!filtersRemoved) {
        std::wcerr << L"Failed to remove " << driverName << L" from UpperFilters after " << maxRetries
                    << L" attempts. Aborting uninstall without touching the service or DriverStore." << std::endl;
        return false;
    }
    // INF の AddFilter による登録（Class\{GUID}\Filters）も先に取り除く。残したままサービスを
    // 削除すると、UpperFilters と同じく死んだ参照になり、キーボードが全滅し得る。
    InfClassFilterRegistrations(classGuidString, driverName, true);
    if (InfClassFilterRegistrations(classGuidString, driverName, false) != 0) {
        std::wcerr << L"Failed to remove the INF AddFilter registration of " << driverName
                    << L". Aborting uninstall without touching the service or DriverStore." << std::endl;
        return false;
    }

    // 2. サービスを停止してから削除する。
    std::wcout << L"Stopping service: " << driverName << std::endl;
    if (!stopAndWaitService(driverName, 5000)) {
        std::wcerr << L"Service did not reach STOPPED state within timeout (will be removed after reboot if still in use)." << std::endl;
    }
    SC_HANDLE hScm = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_ALL_ACCESS);
    if (hScm) {
        SC_HANDLE hSvc = OpenServiceW(hScm, driverName.c_str(), DELETE);
        if (hSvc) {
            if (!DeleteService(hSvc)) {
                std::wcerr << L"DeleteService failed. Error: " << GetLastError()
                           << L" (will be removed after reboot)" << std::endl;
            }
            CloseServiceHandle(hSvc);
        }
        CloseServiceHandle(hScm);
    }

    // 3. 旧形式等、DriverStore に存在しない古い INF が明示指定された場合は個別にアンインストールする。
    if (!optionalInfPath.empty()) {
        namespace fs = std::filesystem;
        const fs::path optionalPath(optionalInfPath);
        const fs::path legacyInfPath = fs::is_directory(optionalPath)
            ? optionalPath / (driverName + L".inf")
            : optionalPath;
        if (fs::exists(legacyInfPath)) {
            std::wcout << L"Uninstalling legacy driver package: " << legacyInfPath.wstring() << std::endl;
            BOOL rebootReq = FALSE;
            if (!DiUninstallDriverW(nullptr, legacyInfPath.c_str(), 0, &rebootReq)) {
                DWORD error = GetLastError();
                if (error != ERROR_NOT_FOUND) {
                    std::wcerr << L"DiUninstallDriverW failed for legacy INF. Error: " << error << std::endl;
                }
            }
        } else {
            std::wcout << L"Legacy INF not found at " << legacyInfPath.wstring() << L". Skipping." << std::endl;
        }
    }

    // 4. DriverStore から全世代のパッケージを削除する。
    RemoveDriverStorePackages(driverName);

    // 5. System32\drivers\ に残る .sys / .inf を無条件削除する。
    RemoveLeftoverSystemFiles(driverName);

    // 6. UpperFilters・サービスキー・DriverStore の 3 点が全て消えたことを確認する。
    if (!VerifyUninstalled(classGuidString, driverName)) {
        std::wcerr << L"Post-uninstall verification failed: residue still present for " << driverName << L"." << std::endl;
        return false;
    }

    return true;
}

static bool IsClassInstanceSubkeyName(const wchar_t* name, DWORD cchName)
{
    if (cchName != 4) {
        return false;
    }
    for (DWORD i = 0; i < 4; ++i) {
        const wchar_t ch = name[i];
        if (ch < L'0' || ch > L'9') {
            return false;
        }
    }
    return name[4] == L'\0';
}

static bool ModifyUpperFiltersAtKey(HKEY key, const std::wstring& driverName, bool add, bool insertBeforeClassDriver = false)
{
    std::vector<wchar_t> buffer(4096);
    DWORD bufferSize = static_cast<DWORD>(buffer.size() * sizeof(wchar_t));
    DWORD type = 0;
    const LSTATUS queryResult = RegQueryValueExW(
        key,
        L"UpperFilters",
        nullptr,
        &type,
        reinterpret_cast<LPBYTE>(buffer.data()),
        &bufferSize);

    std::vector<std::wstring> filters;
    if (queryResult == ERROR_SUCCESS && type == REG_MULTI_SZ && bufferSize >= sizeof(wchar_t)) {
        for (const wchar_t* cursor = buffer.data(); *cursor; cursor += wcslen(cursor) + 1) {
            filters.emplace_back(cursor);
        }
    }

    const auto existing = std::find(filters.begin(), filters.end(), driverName);
    if (add) {
        if (existing != filters.end()) {
            filters.erase(existing);
        }
        // Find the class driver (kbdclass) to determine the insertion point.
        // insertBeforeClassDriver=true:  insert BEFORE class driver -- for drivers hooking IOCTL_INTERNAL_*_CONNECT
        // insertBeforeClassDriver=false: insert AFTER  class driver -- for drivers reading from IRP completion callbacks
        static const wchar_t* const kClassDrivers[] = { L"kbdclass" };
        // Fixed relative order among drivers that sit immediately before kbdclass
        // (i.e. lower in the stack). Each array corresponds to the same index in kClassDrivers;
        // earlier entries sit farther from the class driver. Whether the legacy nodokad2 driver
        // or kbdlayer is installed first, the final order must converge to this table instead of
        // "whoever installed last ends up directly above the class driver".
        static const std::wstring kKeyboardPreClassOrder[] = { L"kbdlayer", L"nodokad2" };
        struct PreClassOrder { const std::wstring* list; size_t count; };
        static const PreClassOrder kPreClassOrders[] = {
            { kKeyboardPreClassOrder, sizeof(kKeyboardPreClassOrder) / sizeof(kKeyboardPreClassOrder[0]) },
        };

        bool classDriverFound = false;
        auto insertPos = filters.end();
        size_t busIndex = 0;
        for (size_t i = 0; i < sizeof(kClassDrivers) / sizeof(kClassDrivers[0]); ++i) {
            auto pos = std::find(filters.begin(), filters.end(), kClassDrivers[i]);
            if (pos != filters.end()) {
                insertPos = insertBeforeClassDriver ? pos : (pos + 1);
                classDriverFound = true;
                busIndex = i;
                break;
            }
        }
        // If no class driver was found in this key, skip it -- instance subkeys
        // (0000, 0001, ...) typically have no UpperFilters value of their own (they
        // just inherit the class key's default), so writing one here unconditionally
        // would create a class-driver-less UpperFilters that shadows the inherited
        // default and drops kbdclass for that one device (applies to both
        // insertion directions, not just insertBeforeClassDriver).
        if (!classDriverFound) {
            return true;
        }
        if (insertBeforeClassDriver) {
            // If a driver that must come after us is already present, insert right before it
            // instead of right before the class driver, so the result always matches
            // kKeyboardPreClassOrder regardless of which driver was installed first.
            const auto& order = kPreClassOrders[busIndex];
            const std::wstring* selfIt = std::find(order.list, order.list + order.count, driverName);
            if (selfIt != order.list + order.count) {
                for (const std::wstring* peerIt = selfIt + 1; peerIt != order.list + order.count; ++peerIt) {
                    auto peerPos = std::find(filters.begin(), insertPos, *peerIt);
                    if (peerPos != insertPos) {
                        insertPos = peerPos;
                        break;
                    }
                }
            }
        }
        filters.insert(insertPos, driverName);
    } else if (existing != filters.end()) {
        filters.erase(existing);
    }

    if (!add && filters.empty()) {
        const LSTATUS del = RegDeleteValueW(key, L"UpperFilters");
        return del == ERROR_SUCCESS || del == ERROR_FILE_NOT_FOUND;
    }

    std::vector<wchar_t> newBuffer;
    for (const auto& filter : filters) {
        newBuffer.insert(newBuffer.end(), filter.begin(), filter.end());
        newBuffer.push_back(L'\0');
    }
    newBuffer.push_back(L'\0');

    const LSTATUS writeStatus = RegSetValueExW(
        key,
        L"UpperFilters",
        0,
        REG_MULTI_SZ,
        reinterpret_cast<const BYTE*>(newBuffer.data()),
        static_cast<DWORD>(newBuffer.size() * sizeof(wchar_t)));
    return writeStatus == ERROR_SUCCESS;
}

bool ModifyUpperFilters(
    const std::wstring& classGuidString,
    const std::wstring& driverName,
    bool add,
    bool insertBeforeClassDriver)
{
    const std::wstring regPath = L"SYSTEM\\CurrentControlSet\\Control\\Class\\" + classGuidString;
    HKEY classKey = nullptr;
    if (RegOpenKeyExW(
            HKEY_LOCAL_MACHINE,
            regPath.c_str(),
            0,
            KEY_READ | KEY_WRITE | KEY_ENUMERATE_SUB_KEYS,
            &classKey) != ERROR_SUCCESS) {
        return false;
    }

    bool anyInstanceSuccess = false;
    DWORD instanceKeyCount = 0;

    for (DWORD index = 0;; ++index) {
        wchar_t subName[256] = {};
        DWORD cch = 255;
        const LONG enumResult =
            RegEnumKeyExW(classKey, index, subName, &cch, nullptr, nullptr, nullptr, nullptr);
        if (enumResult == ERROR_NO_MORE_ITEMS) {
            break;
        }
        if (enumResult != ERROR_SUCCESS || !IsClassInstanceSubkeyName(subName, cch)) {
            continue;
        }

        ++instanceKeyCount;
        HKEY instanceKey = nullptr;
        if (RegOpenKeyExW(classKey, subName, 0, KEY_READ | KEY_WRITE, &instanceKey) != ERROR_SUCCESS) {
            continue;
        }
        if (ModifyUpperFiltersAtKey(instanceKey, driverName, add, insertBeforeClassDriver)) {
            anyInstanceSuccess = true;
        }
        RegCloseKey(instanceKey);
    }

    // Always update the class key (new devices inherit it). Instance keys (0000, 0001, ...) are updated above.
    const bool parentSuccess = ModifyUpperFiltersAtKey(classKey, driverName, add, insertBeforeClassDriver);

    RegCloseKey(classKey);
    return anyInstanceSuccess || parentSuccess;
}

// INF の AddFilter（[DDInstall.Filters]、Windows 10 1903以降）で登録されたクラスフィルタは、
// UpperFilters 値とは別に Class\{GUID}\Filters\<位置>（例: "*Upper"）の下に、ドライバ名の値として
// 記録される。UpperFilters 値だけを見ていると気付けないが、PnP はこちらも読んでスタックに載せる。
// kbdlayer は UpperFilters（DriverManager が順序表で管理）と、以前の kbdlayer.inf の
// AddFilter（FilterPosition=Upper）の両方で登録されると、1つのキーボードに2段載ってキー入力を
// 2回処理してしまう（2026-09-28、--list の二重表示の調査で発見）。
// remove=false なら数えるだけ、true なら見つけた登録を削除する。戻り値は見つけた数。
static int InfClassFilterRegistrations(const std::wstring& classGuidString, const std::wstring& driverName, bool remove)
{
    const std::wstring regPath = L"SYSTEM\\CurrentControlSet\\Control\\Class\\" + classGuidString + L"\\Filters";
    HKEY filtersKey = nullptr;
    const REGSAM access = KEY_ENUMERATE_SUB_KEYS | KEY_READ | (remove ? KEY_WRITE : 0);
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, regPath.c_str(), 0, access, &filtersKey) != ERROR_SUCCESS) {
        return 0; // Filters キーが無い = AddFilter による登録は無い
    }

    int found = 0;
    for (DWORD index = 0;; ++index) {
        wchar_t subName[256] = {};
        DWORD cch = 255;
        const LONG enumResult =
            RegEnumKeyExW(filtersKey, index, subName, &cch, nullptr, nullptr, nullptr, nullptr);
        if (enumResult == ERROR_NO_MORE_ITEMS) {
            break;
        }
        if (enumResult != ERROR_SUCCESS) {
            continue;
        }
        HKEY levelKey = nullptr;
        if (RegOpenKeyExW(filtersKey, subName, 0, KEY_READ | (remove ? KEY_SET_VALUE : 0), &levelKey) != ERROR_SUCCESS) {
            continue;
        }
        if (RegQueryValueExW(levelKey, driverName.c_str(), nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS) {
            ++found;
            if (remove) {
                const LSTATUS del = RegDeleteValueW(levelKey, driverName.c_str());
                if (del == ERROR_SUCCESS) {
                    std::wcout << L"Removed INF AddFilter registration: Filters\\" << subName << L"\\" << driverName << std::endl;
                } else {
                    std::wcerr << L"Failed to remove INF AddFilter registration Filters\\" << subName << L"\\"
                               << driverName << L". Error: " << del << std::endl;
                }
            }
        }
        RegCloseKey(levelKey);
    }
    RegCloseKey(filtersKey);
    return found;
}

std::wstring GetExecutablePath()
{
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    return std::filesystem::path(path).parent_path().wstring();
}
