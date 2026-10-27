# Forked from kbdaddid_subscribe's setup/Package-Dist.ps1 (cosmetic/path renames only).
# Here we bundle setup\bin\<Configuration>\ launcher exe + both-language MSIs, and the
# dist-readme\ bilingual READMEs into a single distributable zip.
#
# param() ブロックはスクリプトの最初の実行文でなければならない(PowerShellの制約)ため、
# $ErrorActionPreference の設定より前に置く必要がある。
param(
    [string] $Configuration = "Release",
    # Subscription（kasane-subscription.zip）または Pro（買い切り版。kasane-pro.zip）
    [ValidateSet("Subscription", "Pro")][string] $Edition = "Subscription"
)

$ErrorActionPreference = "Stop"

$setupDir = $PSScriptRoot
$binDir = Join-Path $setupDir "bin\$Configuration"
$isPro = ($Edition -eq "Pro")
$readmeDir = Join-Path $setupDir $(if ($isPro) { "dist-readme-pro" } else { "dist-readme" })
$licenseDir = Join-Path $setupDir $(if ($isPro) { "License-Pro" } else { "License" })
$launcherName = if ($isPro) { "Install-kasane-pro.exe" } else { "Install-kasane.exe" }
$msiPrefix = if ($isPro) { "KasaneProSetup" } else { "KasaneSetup" }
$distDir = Join-Path $setupDir "dist"
$stagingDir = Join-Path $distDir "_staging"
$zipPath = Join-Path $distDir $(if ($isPro) { "kasane-pro.zip" } else { "kasane-subscription.zip" })

$requiredFiles = @(
    $launcherName,
    "$msiPrefix-ja-JP.msi",
    "$msiPrefix-en-US.msi"
)

foreach ($name in $requiredFiles) {
    $p = Join-Path $binDir $name
    if (-not (Test-Path -LiteralPath $p)) {
        throw "Package-Dist: required file missing: $p`nBuild KasaneSetup.csproj (Release) first."
    }
}

if (Test-Path -LiteralPath $stagingDir) {
    Remove-Item -Path $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

foreach ($name in $requiredFiles) {
    Copy-Item -LiteralPath (Join-Path $binDir $name) -Destination $stagingDir -Force
}

Copy-Item -LiteralPath (Join-Path $readmeDir "README.ja-JP.txt") -Destination $stagingDir -Force
Copy-Item -LiteralPath (Join-Path $readmeDir "README.en-US.txt") -Destination $stagingDir -Force
Copy-Item -LiteralPath (Join-Path $licenseDir "License.ja-JP.rtf") -Destination $stagingDir -Force
Copy-Item -LiteralPath (Join-Path $licenseDir "License.en-US.rtf") -Destination $stagingDir -Force

# サードパーティノーティス: zip の直下に THIRD-PARTY-NOTICES.txt と、各ライセンスのファイルを入れた third-party\ を置く（インストール先と同じ並び）。
$repoRoot = Split-Path -Parent $setupDir
$noticesFile = Join-Path $repoRoot "THIRD-PARTY-NOTICES.txt"
$thirdPartyDir = Join-Path $repoRoot "third-party"
foreach ($p in @($noticesFile, $thirdPartyDir)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Package-Dist: third-party notice material missing: $p" }
}
Copy-Item -LiteralPath $noticesFile -Destination $stagingDir -Force
$thirdPartyStagingDir = Join-Path $stagingDir "third-party"
New-Item -ItemType Directory -Path $thirdPartyStagingDir -Force | Out-Null
Get-ChildItem -LiteralPath $thirdPartyDir -File |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $thirdPartyStagingDir -Force }

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
# Compress-Archive は、全体をメモリに載せるため、1GB 級の MSI を含むとき OutOfMemoryException で失敗する（Windows PowerShell 5.1）。
# ZipArchive は、ファイルを順に書き出すので、メモリを使わない。エントリ名は、Windows PowerShell 5.1 の CreateFromDirectory だと「\」区切りになるので、
# 1つずつ「/」区切りで追加する（zip の規格どおり。他の展開ツールでもフォルダとして扱える）。
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipStream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Create)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $rootLength = (Get-Item -LiteralPath $stagingDir).FullName.TrimEnd([char]92).Length + 1
        Get-ChildItem -LiteralPath $stagingDir -Recurse -File | ForEach-Object {
            $entryName = $_.FullName.Substring($rootLength).Replace([string][char]92, "/")
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
} finally { $zipStream.Dispose() }

Remove-Item -Path $stagingDir -Recurse -Force

Write-Host "Package-Dist: created $zipPath"
