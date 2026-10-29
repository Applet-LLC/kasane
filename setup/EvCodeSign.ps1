# EV code signing for KasaneSetup (USB token + subject name).
# Forked from kbdaddid_subscribe's setup/EvCodeSign.ps1 -- signing tool invocation logic is
# unchanged (the EV cert/USB token is shared across Applet LLC products); only cosmetic/path
# renames (kbdaddid -> kbdlayer, mouaddid removed -- kasane has no mouse component).
#
# Signing pipeline (called from KasaneSetup.csproj):
#   1. -StagingRoot  : sign .exe/.dll (app binaries) and .sys/.cat (driver packages) under staging\
#                      WHQL-pre-signed drivers (driver/signed/) are skipped automatically.
#   2. -InstallerMsi : sign the completed MSI file(s). Accepts one or more paths so that
#                      all language-variant MSIs (en-US/ja-JP) can be signed together in a
#                      single signtool invocation (fewer USB EV token prompts).
#
# Usage:
#   .\EvCodeSign.ps1 -StagingRoot "<setup>\staging"
#   .\EvCodeSign.ps1 -InstallerMsi "<setup>\bin\Release\KasaneSetup-en-US.msi;<setup>\bin\Release\KasaneSetup-ja-JP.msi"
#   (semicolon-delimited single string, not an array parameter: when invoked via `powershell -File`,
#    a second bare token after -InstallerMsi does NOT extend a [string[]] array -- it silently binds
#    to the next positional parameter (-StagingRoot) instead, so multiple paths must travel as one
#    argv token and get split inside the script.)
param(
    [string] $StagingRoot = "",
    [string] $InstallerMsi = ""
)

$installerFileList = @()
if ($InstallerMsi -ne "") {
    $installerFileList = @($InstallerMsi -split ";" | Where-Object { $_ -ne "" })
}

$ErrorActionPreference = "Stop"

$CertSubject = "Applet LLC"
$TimestampUrl = "http://timestamp.globalsign.com/tsa/r45standard"

function Get-SigntoolExe {
    $binRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path $binRoot) {
        $verDirs = Get-ChildItem -LiteralPath $binRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object { $_.Name } -Descending
        foreach ($d in $verDirs) {
            $candidate = Join-Path $d.FullName "x64\signtool.exe"
            if (Test-Path -LiteralPath $candidate) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }
    }
    $ack = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\App Certification Kit\signtool.exe"
    if (Test-Path -LiteralPath $ack) {
        return (Resolve-Path -LiteralPath $ack).Path
    }
    throw "signtool.exe not found under Windows Kits 10. Install the Windows SDK."
}

function Invoke-EvSign {
    param([string[]] $Paths)
    if ($Paths.Count -eq 0) {
        return
    }

    # USB トークン未接続など証明書が見つからない場合はスキップ（署名なし成果物を生成して続行）
    $cert = Get-ChildItem -Path @("Cert:\CurrentUser\My", "Cert:\LocalMachine\My") `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -like "*$CertSubject*" -and $_.NotAfter -gt (Get-Date) } |
        Select-Object -First 1
    if ($null -eq $cert) {
        Write-Warning "EV certificate '$CertSubject' not found (USB token disconnected?). Skipping code signing — artifacts will be UNSIGNED."
        return
    }

    $signtool = Get-SigntoolExe
    $baseArgs = @(
        "sign", "/v", "/a",
        "/n", $CertSubject,
        "/tr", $TimestampUrl,
        "/td", "sha256",
        "/fd", "sha256"
    )
    # Avoid Windows command-line length limits when many DLLs are staged (.NET publish layout).
    $chunkSize = 40
    Write-Host "Signing $($Paths.Count) file(s) with signtool..."
    for ($i = 0; $i -lt $Paths.Count; $i += $chunkSize) {
        $take = [Math]::Min($chunkSize, $Paths.Count - $i)
        $chunk = $Paths[$i..($i + $take - 1)]
        $batchNo = [int]($i / $chunkSize) + 1
        $batches = [int][Math]::Ceiling($Paths.Count / $chunkSize)
        Write-Host "  batch $batchNo / $batches ($($chunk.Count) file(s))"
        $toolArgs = $baseArgs + $chunk
        & $signtool @toolArgs
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed with exit code $LASTEXITCODE"
        }
    }
}

if ($StagingRoot -ne "" -and $installerFileList.Count -gt 0) {
    throw "Specify only one of -StagingRoot or -InstallerMsi"
}
if ($StagingRoot -eq "" -and $installerFileList.Count -eq 0) {
    throw "Specify -StagingRoot or -InstallerMsi"
}

if ($StagingRoot -ne "") {
    $fullStaging = Resolve-Path -LiteralPath $StagingRoot -ErrorAction Stop

    # Prepare-Staging.ps1 が書き出した WHQL 署名済みドライバ一覧を読み込む。
    # 一覧にあるドライバはカーネル EV 署名をスキップする（Inf2Cat / EV はビルド時に済ませたパッケージ）。
    $preSignedFile = Join-Path $fullStaging.Path ".pre-signed-drivers"
    $preSignedDrivers = @()
    if (Test-Path -LiteralPath $preSignedFile) {
        $preSignedDrivers = @(Get-Content -LiteralPath $preSignedFile -Encoding utf8 |
            Where-Object { $_ -ne "" })
        if ($preSignedDrivers.Count -gt 0) {
            Write-Host "WHQL-signed drivers (skip kernel EV sign): $($preSignedDrivers -join ', ')"
        }
    }

    # Release ビルドの kbdlayer: .cat は vcxproj の Inf2Cat で既に生成済み。ここでは .sys/.cat に EV 署名のみ。
    $extensionsApp = @(".exe", ".dll")
    $extensionsKernel = @(".sys", ".cat")

    # staging\Subscribe は WHQL 提出用に退避したコピーなので、署名の対象にしない。
    $submitCopyPrefix = (Join-Path $fullStaging.Path "Subscribe").TrimEnd('\') + "\"

    $toSignApp = @(Get-ChildItem -LiteralPath $fullStaging.Path -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object {
                $extensionsApp -contains $_.Extension.ToLowerInvariant() -and
                -not $_.FullName.StartsWith($submitCopyPrefix, [System.StringComparison]::OrdinalIgnoreCase)
            } |
            ForEach-Object { $_.FullName })

    if ($toSignApp.Count -gt 0) {
        Invoke-EvSign -Paths $toSignApp
    }
    else {
        Write-Host "EvCodeSign: no .exe/.dll under staging to sign."
    }

    # Prepare-Staging.ps1はdrivers\<name>\<arch>\にステージングし（x64とarm64は別々にWHQL
    # 取得され得るため）、.pre-signed-driversのエントリも"<name>-<arch>"形式（例: "kbdlayer-x64"）。
    foreach ($driverLeaf in @("kbdlayer")) {
        foreach ($arch in @("x64", "arm64")) {
            $driverDir = Join-Path $fullStaging.Path (Join-Path "drivers" (Join-Path $driverLeaf $arch))
            if (-not (Test-Path -LiteralPath $driverDir)) { continue }
            if ($preSignedDrivers -contains "$driverLeaf-$arch") { continue }
            $cat = Join-Path $driverDir "$driverLeaf.cat"
            $sys = Join-Path $driverDir "$driverLeaf.sys"
            if ((Test-Path -LiteralPath $sys) -and -not (Test-Path -LiteralPath $cat)) {
                throw "[$driverLeaf/$arch] Missing $driverLeaf.cat under $driverDir. Run Inf2Cat in the driver project build (Release) before packaging; KasaneSetup does not regenerate catalogs."
            }
        }
    }

    # カーネルファイル（.sys / .cat）の EV 署名。WHQL 署名済みドライバ(アーキ別)は除外する。
    $kernelSignExcludedPrefixes = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $preSignedDrivers) {
        $parts = $entry -split '-', 2
        if ($parts.Count -ne 2) { continue }
        $dir = Join-Path $fullStaging.Path "drivers\$($parts[0])\$($parts[1])"
        if (Test-Path -LiteralPath $dir) {
            $kernelSignExcludedPrefixes.Add(
                (Resolve-Path -LiteralPath $dir).Path.TrimEnd('\') + "\"
            )
        }
    }

    $toSignKernel = @(Get-ChildItem -LiteralPath $fullStaging.Path -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object {
                $ext = $_.Extension.ToLowerInvariant()
                if ($extensionsKernel -notcontains $ext) { return $false }
                if ($_.FullName.StartsWith($submitCopyPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
                foreach ($prefix in $kernelSignExcludedPrefixes) {
                    if ($_.FullName.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                        return $false
                    }
                }
                return $true
            } | ForEach-Object { $_.FullName })

    if ($toSignKernel.Count -gt 0) {
        Invoke-EvSign -Paths $toSignKernel
    }
    else {
        Write-Host "EvCodeSign: no .sys/.cat to sign (all drivers are WHQL-signed)."
    }

    exit 0
}

if ($installerFileList.Count -gt 0) {
    $resolved = @()
    foreach ($p in $installerFileList) {
        if (-not (Test-Path -LiteralPath $p)) {
            throw "Installer artifact not found: $p"
        }
        $resolved += (Resolve-Path -LiteralPath $p).Path
    }
    # 複数ファイルでも Invoke-EvSign 内で1回の signtool 呼び出し（チャンク単位）にまとめて署名するため、
    # USBトークンへのアクセス回数は増えない。
    Invoke-EvSign -Paths $resolved
    exit 0
}
