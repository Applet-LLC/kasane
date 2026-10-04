# Forked from kbdaddid_subscribe's setup/Prepare-Staging.ps1 for this project (formerly LayerDriver, now kasane).
# Renames: kbdaddid -> kbdlayer; addid/<name>/... source-tree paths -> driver/<name>/... (this
# repo's actual layout, driver/kbdlayer/ + driver/DriverManager/, set up by the lead agent).
# mouaddid handling removed entirely (kasane has no mouse component). DriverTestApp staging
# removed (superseded by KeymapTool.exe, driver/KeymapTool/ -- staged below alongside DriverManager.exe).

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$stagingRoot = Join-Path $PSScriptRoot "staging"

function Copy-FolderContent {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDir,
        [Parameter(Mandatory = $true)][string]$DestDir
    )

    if (!(Test-Path $SourceDir)) {
        throw "Source directory not found: $SourceDir"
    }

    New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    Copy-Item -Path (Join-Path $SourceDir "*") -Destination $DestDir -Recurse -Force
}

function Copy-RequiredToolExe {
    param(
        [Parameter(Mandatory = $true)][string]$DestDir,
        [Parameter(Mandatory = $true)][string]$ExeFileName,
        [Parameter(Mandatory = $true)][string[]]$CandidatePaths
    )

    $found = $null
    foreach ($p in $CandidatePaths) {
        if (Test-Path -LiteralPath $p) {
            $found = $p
            break
        }
    }

    if (-not $found) {
        $listed = ($CandidatePaths | ForEach-Object { "  $_" }) -join "`n"
        throw @"
Required installer payload missing: $ExeFileName
Searched:`n$listed
Build prerequisites: build $ExeFileName project (Release x64).
"@
    }

    New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    Copy-Item -LiteralPath $found -Destination $DestDir -Force
}

# .NET single-file publish 成果物をステージングにコピーする。
# 優先順位:
#   1. <repoRoot>\<projectName>\bin\x64\Release\net8.0-windows\win-x64\publish\  (.csproj publish 出力)
#   2. <repoRoot>\x64\Release\<exeName>  (手動配置)
# 成果物が見つからない場合は警告のみで続行（ビルド前に dotnet publish が必要）。
# svc は appsettings.json も同梱する（publish 出力の .pdb 以外を全コピー）。
#
# 対象プロジェクト（svc/gui/launcher）はいずれも <Platforms>AnyCPU;x64</Platforms> で、
# Platform=x64 でビルドすると MSBuild が出力パスに "x64" を1階層挟む
# （bin\Release\... ではなく bin\x64\Release\...）。x64 以外の構成は使っていないため、
# x64 側のパスのみを見る。
function Stage-DotNetPublish {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ProjectName,
        [Parameter(Mandatory = $true)][string]$ExeFileName,
        [Parameter(Mandatory = $true)][string]$DestDir
    )

    # publish 出力ディレクトリ候補。Platform=x64 で publish した場合は bin\x64\Release\...、
    # 手動で Platform 指定なしに publish した場合は bin\Release\... になるため、x64 側を優先して両方を見る。
    $publishDir = Join-Path $RepoRoot "$ProjectName\bin\x64\Release\net8.0-windows\win-x64\publish"
    if (-not (Test-Path -LiteralPath $publishDir)) {
        $publishDir = Join-Path $RepoRoot "$ProjectName\bin\Release\net8.0-windows\win-x64\publish"
    }
    $fallbackExe = Join-Path $RepoRoot "x64\Release\$ExeFileName"

    if (Test-Path -LiteralPath $publishDir) {
        $files = Get-ChildItem -LiteralPath $publishDir -File |
                 Where-Object { $_.Extension -notin @('.pdb') }
        if ($files.Count -gt 0) {
            New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
            foreach ($f in $files) {
                Copy-Item -LiteralPath $f.FullName -Destination $DestDir -Force
                Write-Host "[$ExeFileName] staged: $($f.Name)"
            }
            return
        }
    }

    if (Test-Path -LiteralPath $fallbackExe) {
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
        Copy-Item -LiteralPath $fallbackExe -Destination $DestDir -Force
        Write-Host "[$ExeFileName] staged from fallback: $fallbackExe"
        return
    }

    Write-Warning @"
$ExeFileName not found -- skipping (MSI will not include this component).
Run 'dotnet publish $ProjectName\$ProjectName.csproj -c Release -r win-x64 -p:Platform=x64 /p:PublishSingleFile=true' first.
"@
}

function Remove-PdbRecursive {
    param([Parameter(Mandatory = $true)][string]$DirectoryPath)
    if (!(Test-Path -LiteralPath $DirectoryPath)) {
        return
    }
    Get-ChildItem -LiteralPath $DirectoryPath -Recurse -File -Filter *.pdb -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

# ドライバの pdb をシンボル保管用に staging\Symbol\ へコピーする。SymbolFileName は x64 では
# "<name>.pdb"（既存の命名を維持）、ARM64 では "<name>_arm64.pdb"（x64 版と同名衝突を避けるための
# 命名。kbdaddid_subscribeのpackaging/Symbolでも同じ命名を使っていた）。
# 探索順:
#   1. 選ばれたドライバソース（driver/signed/<name>/ 等）に既にコピー済みの <name>.pdb
#      （Signed フォルダに pdb を置いてあれば、これで拾われる）
#   2. x64/Release/<name>.pdb                        （x64: ソリューション統一出力）
#   3. driver/<name>/x64/Release/<name>.pdb           （x64: プロジェクトローカルビルド出力）
#   4. driver/<name>/ARM64/Release/<name>.pdb         （arm64: kbdlayer_arm64.vcxprojのOutDir。
#      Platform=ARM64|Release固定でMSBuildProjectDirectory相対、常にこの1箇所に出力される）
# 見つからなければ何もしない（そのドライバの pdb 無しで続行）。
function Copy-PdbToSymbol {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$StagedDriverDir,
        [Parameter(Mandatory = $true)][string]$DriverName,
        [Parameter(Mandatory = $true)][string]$Arch,
        [Parameter(Mandatory = $true)][string]$SymbolDir
    )
    $candidates = if ($Arch -eq "arm64") {
        @(
            (Join-Path $StagedDriverDir "$DriverName.pdb")
            (Join-Path $RepoRoot "driver/$DriverName/ARM64/Release/$DriverName.pdb")
        )
    } else {
        @(
            (Join-Path $StagedDriverDir "$DriverName.pdb")
            (Join-Path $RepoRoot "x64/Release/$DriverName.pdb")
            (Join-Path $RepoRoot "driver/$DriverName/x64/Release/$DriverName.pdb")
        )
    }
    $symbolFileName = if ($Arch -eq "arm64") { "${DriverName}_arm64.pdb" } else { "$DriverName.pdb" }
    foreach ($src in $candidates) {
        if (Test-Path -LiteralPath $src) {
            New-Item -ItemType Directory -Path $SymbolDir -Force | Out-Null
            Copy-Item -LiteralPath $src -Destination (Join-Path $SymbolDir $symbolFileName) -Force
            Write-Host "[$DriverName/$Arch] Symbol staged: $symbolFileName (from $src)"
            return
        }
    }
    Write-Host "[$DriverName/$Arch] No PDB found to stage as symbol (looked in: $($candidates -join ', '))"
}

# Heat/WiX はステージング直下をそのまま MSI に入れる。.tlog や .obj が混ざるとカタログ検証が壊れ DiInstallDriver が失敗する。
function Keep-DriverPayloadOnly {
    param(
        [Parameter(Mandatory = $true)][string]$DriverDir,
        [Parameter(Mandatory = $true)][string]$DriverName
    )
    if (-not (Test-Path -LiteralPath $DriverDir)) {
        return
    }
    $resolved = (Resolve-Path -LiteralPath $DriverDir).Path
    $allowed = @("$DriverName.inf", "$DriverName.sys", "$DriverName.cat")
    foreach ($item in Get-ChildItem -LiteralPath $resolved -Force) {
        if ($item.PSIsContainer) {
            Write-Host "Removing build output directory from driver staging: $($item.Name)"
            Remove-Item -LiteralPath $item.FullName -Recurse -Force
            continue
        }
        if ($allowed -notcontains $item.Name) {
            Write-Host "Removing stray file from driver staging: $($item.Name)"
            Remove-Item -LiteralPath $item.FullName -Force
        }
    }
}

function Assert-KernelDriverPackage {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$StagedDriverDir,
        [Parameter(Mandatory = $true)][string]$DriverName,
        [Parameter(Mandatory = $true)][string]$Arch
    )
    $inf = Join-Path $StagedDriverDir "$DriverName.inf"
    $sys = Join-Path $StagedDriverDir "$DriverName.sys"
    $cat = Join-Path $StagedDriverDir "$DriverName.cat"

    if (-not (Test-Path -LiteralPath $inf)) {
        throw "Staged driver package missing INF: $inf"
    }

    # x64: ソリューション統一出力にフラット (x64\Release\*.sys) と、ビルドが出力するサブフォルダ
    # (x64\Release\<name>\) の両方があり得る。arm64: kbdlayer_arm64.vcxprojはOutDirを
    # MSBuildProjectDirectory相対に固定しているため、常に1箇所（driver/<name>/ARM64/Release/<name>/）。
    $sysFallbacks = if ($Arch -eq "arm64") {
        @((Join-Path $RepoRoot "driver/$DriverName/ARM64/Release/$DriverName/$DriverName.sys"))
    } else {
        @(
            (Join-Path $RepoRoot "x64/Release/$DriverName/$DriverName.sys")
            (Join-Path $RepoRoot "x64/Release/$DriverName.sys")
            (Join-Path $RepoRoot "driver/x64/Release/$DriverName.sys")
        )
    }
    $sysSupplementedFromDir = $null
    if (-not (Test-Path -LiteralPath $sys)) {
        foreach ($src in $sysFallbacks) {
            if (Test-Path -LiteralPath $src) {
                Copy-Item -LiteralPath $src -Destination $sys -Force
                Write-Host "Supplemented $DriverName.sys from: $src"
                $sysSupplementedFromDir = Split-Path -Parent $src
                break
            }
        }
        if (-not (Test-Path -LiteralPath $sys)) {
            throw @"
Staged driver package missing kernel binary ($Arch): $sys
WiX will ship INF without SYS and DiInstallDriverW will fail during MSI (error 1722).

Fix: build the driver project (Release, Platform=$(if ($Arch -eq "arm64") { "ARM64" } else { "x64" })) before building the installer:
  msbuild "$RepoRoot/driver/$DriverName/$DriverName$(if ($Arch -eq "arm64") { "_arm64" } else { "" }).vcxproj" /p:Configuration=Release /p:Platform=$(if ($Arch -eq "arm64") { "ARM64" } else { "x64" })

If your layout outputs SYS elsewhere, add that path to sysFallbacks in Prepare-Staging.ps1.
"@
        }
    }

    $catFallbacks = [System.Collections.Generic.List[string]]::new()
    if ($null -ne $sysSupplementedFromDir) {
        $catFallbacks.Add((Join-Path $sysSupplementedFromDir "$DriverName.cat"))
    }
    if ($Arch -eq "arm64") {
        $catFallbacks.Add((Join-Path $RepoRoot "driver/$DriverName/ARM64/Release/$DriverName/$DriverName.cat"))
    } else {
        foreach ($p in @(
                (Join-Path $RepoRoot "driver/$DriverName/x64/Release/$DriverName/$DriverName.cat")
                (Join-Path $RepoRoot "driver/$DriverName/x64/Release/$DriverName.cat")
                (Join-Path $RepoRoot "x64/Release/$DriverName/$DriverName.cat")
                (Join-Path $RepoRoot "x64/Release/$DriverName.cat")
            )) {
            if ($catFallbacks -notcontains $p) { $catFallbacks.Add($p) }
        }
    }

    if (-not (Test-Path -LiteralPath $cat)) {
        $copied = $false
        foreach ($src in $catFallbacks) {
            if (Test-Path -LiteralPath $src) {
                Copy-Item -LiteralPath $src -Destination $cat -Force
                Write-Host "Supplemented $DriverName.cat from: $src"
                $copied = $true
                break
            }
        }
        if (-not $copied) {
            throw @"
Staged driver package missing catalog ($Arch): $cat
The INF references CatalogFile=$DriverName.cat - packaging without it breaks driver install.

Rebuild the driver project (Release, Platform=$(if ($Arch -eq "arm64") { "ARM64" } else { "x64" })) so the vcxproj Inf2Cat step emits $DriverName.cat, then rebuild the installer.
"@
        }
    }
}

# WHQL 署名済みパッケージ（x64: driver/signed/<name>/、arm64: driver/signed/<name>-arm64/。
# 別フォルダ名にしたのは、x64署名済み運用（手動配置の実体）を一切動かさずに済ませるため）が
# 揃っていればそちらを優先し、揃っていなければビルド出力にフォールバックする。
# 戻り値: @{ Path = <ソースディレクトリ>; PreSigned = <$true|$false> }
function Get-KernelDriverSource {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$DriverName,
        [Parameter(Mandatory = $true)][string]$Arch
    )
    $signedDirName = if ($Arch -eq "arm64") { "$DriverName-arm64" } else { $DriverName }
    $signedDir = Join-Path $RepoRoot "driver/signed/$signedDirName"
    $required = @("$DriverName.inf", "$DriverName.sys", "$DriverName.cat")
    if (Test-Path -LiteralPath $signedDir) {
        $missing = $required | Where-Object {
            -not (Test-Path -LiteralPath (Join-Path $signedDir $_))
        }
        if ($missing.Count -eq 0) {
            Write-Host "[$DriverName/$Arch] WHQL-signed package found: $signedDir"
            return @{ Path = $signedDir; PreSigned = $true }
        }
        Write-Warning "[$DriverName/$Arch] driver/signed/$signedDirName/ is incomplete (missing: $($missing -join ', ')) -- falling back to build output"
    }

    if ($Arch -eq "arm64") {
        # kbdlayer_arm64.vcxprojはOutDirをMSBuildProjectDirectory相対に固定しているため、
        # ビルド出力は常にこの1箇所（ソリューション経由でもスタンドアロンでも同じ）。
        $buildDir = Join-Path $RepoRoot "driver/$DriverName/ARM64/Release/$DriverName"
        $missing = $required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $buildDir $_)) }
        if ($missing.Count -eq 0) {
            Write-Host "[$DriverName/arm64] Using build output: $buildDir"
        } else {
            Write-Host "[$DriverName/arm64] Using build output (missing files will be supplemented if possible): $buildDir"
        }
        return @{ Path = $buildDir; PreSigned = $false }
    }

    # ビルド出力の候補を優先順で試す。
    # 1. driver/<name>/x64/Release/<name>/  (EWDK Inf2Cat パッケージ出力。inf/sys/cat が揃った完全パッケージ)
    # 2. driver/<name>/x64/Release/         (プロジェクトローカルビルドのフラット出力。StampInf/Link 直後のコピーで .cat が無いことがある)
    # 3. x64/Release/<name>/               (ソリューション統一出力・コピー済みバイナリ)
    $buildCandidates = @(
        (Join-Path $RepoRoot "driver/$DriverName/x64/Release/$DriverName")
        (Join-Path $RepoRoot "driver/$DriverName/x64/Release")
        (Join-Path $RepoRoot "x64/Release/$DriverName")
    )
    # inf/sys/cat が揃っている候補を優先する（.cat が無いフラット出力を誤って選ばないように）
    foreach ($candidate in $buildCandidates) {
        $missing = $required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $candidate $_)) }
        if ($missing.Count -eq 0) {
            Write-Host "[$DriverName/x64] Using build output: $candidate"
            return @{ Path = $candidate; PreSigned = $false }
        }
    }
    # 完全な組がどこにも無い場合は .inf があるディレクトリを使い、Assert-KernelDriverPackage に補完させる
    foreach ($candidate in $buildCandidates) {
        if (Test-Path -LiteralPath (Join-Path $candidate "$DriverName.inf")) {
            Write-Host "[$DriverName/x64] Using partial build output (missing files will be supplemented): $candidate"
            return @{ Path = $candidate; PreSigned = $false }
        }
    }
    # フォールバック: .inf がなくても Assert-KernelDriverPackage が補完する
    $buildDir = Join-Path $RepoRoot "driver/$DriverName/x64/Release"
    Write-Host "[$DriverName/x64] Using build output (fallback): $buildDir"
    return @{ Path = $buildDir; PreSigned = $false }
}

# --- ステージング開始 ---

if (Test-Path $stagingRoot) {
    Remove-Item -Path $stagingRoot -Recurse -Force
}

# kbdlayer: x64・arm64それぞれでWHQL署名済みがあればそちらを優先。ステージング先は
# drivers/kbdlayer/<arch>/ （DriverManager.cppのArchSubdirName()ベースのパス解決・
# KasaneSetup.wixprojのHarvestDirectory・Product.wxsのディレクトリツリーがすべて
# この<arch>サブフォルダ構成に揃えて更新済み）。
$symbolDir = Join-Path $stagingRoot "Symbol"
New-Item -ItemType Directory -Path $symbolDir -Force | Out-Null

$preSignedEntries = [System.Collections.Generic.List[string]]::new()

foreach ($arch in @("x64", "arm64")) {
    $source = Get-KernelDriverSource -RepoRoot $repoRoot -DriverName "kbdlayer" -Arch $arch
    if ($source.PreSigned) { $preSignedEntries.Add("kbdlayer-$arch") }
    $destDir = Join-Path $stagingRoot "drivers/kbdlayer/$arch"
    Copy-FolderContent -SourceDir $source.Path -DestDir $destDir
    Copy-PdbToSymbol -RepoRoot $repoRoot -StagedDriverDir $destDir -DriverName "kbdlayer" -Arch $arch -SymbolDir $symbolDir
    Assert-KernelDriverPackage -RepoRoot $repoRoot -StagedDriverDir $destDir -DriverName "kbdlayer" -Arch $arch
    Keep-DriverPayloadOnly -DriverDir $destDir -DriverName "kbdlayer"
    Remove-PdbRecursive -DirectoryPath $destDir
}

# DriverManager.exe: MSI 内で DriverManager install/uninstall コマンドを呼ぶために必要。
Copy-RequiredToolExe -ExeFileName "DriverManager.exe" -DestDir (Join-Path $stagingRoot "tools/Driver") -CandidatePaths @(
    (Join-Path $repoRoot "x64/Release/DriverManager.exe")
    (Join-Path $repoRoot "driver/DriverManager/x64/Release/DriverManager.exe")
)

# DriverManager_arm64.exe: DriverManager.exe(x64)がARM64ホストで自己検出した際にリレーする相棒
# バイナリ。DriverManager_arm64.vcxprojはOutDirの明示指定が無いため、ソリューション経由ビルドでは
# ソリューションルート相対（<repoRoot>/ARM64/Release/）、スタンドアロンビルドではプロジェクト
# フォルダ相対（driver/DriverManager/ARM64/Release/）のどちらかになる。
Copy-RequiredToolExe -ExeFileName "DriverManager_arm64.exe" -DestDir (Join-Path $stagingRoot "tools/Driver") -CandidatePaths @(
    (Join-Path $repoRoot "ARM64/Release/DriverManager_arm64.exe")
    (Join-Path $repoRoot "driver/DriverManager/ARM64/Release/DriverManager_arm64.exe")
)

# KeymapTool.exe: keymap.json/info.jsonの読み込み・キャリブレーションウィザード・
# IOCTLクライアント（--list/--get/--calibrate/--apply等）を提供するCLI。
# kbdaddid_subscribeのDriverTestApp.exeに相当する動作確認ツールの後継（x64のみ、ARM64版は無し）。
Copy-RequiredToolExe -ExeFileName "KeymapTool.exe" -DestDir (Join-Path $stagingRoot "tools/Driver") -CandidatePaths @(
    (Join-Path $repoRoot "x64/Release/KeymapTool.exe")
    (Join-Path $repoRoot "driver/KeymapTool/x64/Release/KeymapTool.exe")
)

# kasane-license-svc: .NET 8 Worker Service の publish 成果物（exe + appsettings.json）
Stage-DotNetPublish -RepoRoot $repoRoot -ProjectName "kasane-license-svc" `
    -ExeFileName "kasane-license-svc.exe" `
    -DestDir (Join-Path $stagingRoot "app/license-svc")

# kasane-license: .NET 8 WPF の publish 成果物（exe のみ）
Stage-DotNetPublish -RepoRoot $repoRoot -ProjectName "kasane-license" `
    -ExeFileName "kasane-license.exe" `
    -DestDir (Join-Path $stagingRoot "app/license-gui")

# kasane: キーマップ編集GUI（.NET 8 WPF）。Stage-DotNetPublish は publish 出力の
# 直下のファイルしかコピーしないので、KLEレイアウト（Layouts\*.json）はソースから別途コピーする
# （publish時にもCopyToOutputDirectoryで同じファイルが出力されるが、ソースを正とする）。
$keymapGuiStaging = Join-Path $stagingRoot "app/keymap-gui"
Stage-DotNetPublish -RepoRoot $repoRoot -ProjectName "kasane" `
    -ExeFileName "kasane.exe" `
    -DestDir $keymapGuiStaging
New-Item -ItemType Directory -Path (Join-Path $keymapGuiStaging "Layouts") -Force | Out-Null
Copy-Item -Path (Join-Path $repoRoot "kasane/Layouts/*.json") -Destination (Join-Path $keymapGuiStaging "Layouts") -Force
Write-Host "[kasane] staged layouts: $((Get-ChildItem (Join-Path $keymapGuiStaging 'Layouts')).Name -join ', ')"

# kasane-tray: タスクトレイ常駐ツール（.NET 8 WinForms、exe のみ）。ログイン時の自動起動は Product.wxs が登録する。
Stage-DotNetPublish -RepoRoot $repoRoot -ProjectName "kasane-tray" `
    -ExeFileName "kasane-tray.exe" `
    -DestDir (Join-Path $stagingRoot "app/tray")

# KeymapToolのサンプルキーマップ（docs/TUTORIAL-CLI.md で使う tutorial-*.json、動作確認用 test-keymap-*.json、
# README.md）。samples/temp/ は作業用の参照資料なので含めない。
$samplesStaging = Join-Path $stagingRoot "samples"
New-Item -ItemType Directory -Path $samplesStaging -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repoRoot "driver/KeymapTool/samples") -File |
    Where-Object { $_.Extension -in @('.json', '.md') } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $samplesStaging -Force }
Write-Host "[samples] staged: $((Get-ChildItem $samplesStaging).Count) files"

# kasane-launcher: ユーザーが最初に実行する WinForms ランチャー（kbdaddid-license-launcher
# / Install-kbdaddidLicense.exe と同じ方針）。ここで staging/launcher に置いておくことで、
# 他のアプリバイナリと同じ EV 署名パス（Step 3 -StagingRoot）に自然に乗る。
# ビルド後に KasaneSetup.csproj の最終 Copy が staging/launcher から
# bin\<Configuration>\ へコピーし、両言語のMSIと同じフォルダに配置する。
Stage-DotNetPublish -RepoRoot $repoRoot -ProjectName "kasane-launcher" `
    -ExeFileName "Install-kasane.exe" `
    -DestDir (Join-Path $stagingRoot "launcher")

# EvCodeSign.ps1 向けに WHQL 署名済みドライバ一覧を staging 直下に書き出す。
# 一覧のエントリはカーネル EV 署名をスキップ（Inf2Cat / EV は driver/signed 側で済ませたパッケージ）。
# "<name>-<arch>" 形式（例: "kbdlayer-x64"）。x64とarm64でWHQL取得状況が別々になり得るため
# （ARM64は別途独立したHLK/WHQL申請が必要）アーキ別にエントリを分けている。
$markerPath = Join-Path $stagingRoot ".pre-signed-drivers"
if ($preSignedEntries.Count -gt 0) {
    $preSignedEntries | Set-Content -Path $markerPath -Encoding utf8
} else {
    Set-Content -Path $markerPath -Value "" -Encoding utf8
}

Write-Host "Staging prepared at: $stagingRoot"
