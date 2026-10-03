# Forked from kbdaddid_subscribe's setup/Package-Dist.ps1 (cosmetic/path renames only).
# Here we bundle setup\bin\<Configuration>\ launcher exe + both-language MSIs, and the
# dist-readme\ bilingual READMEs into a single distributable zip.
#
# param() ブロックはスクリプトの最初の実行文でなければならない(PowerShellの制約)ため、
# $ErrorActionPreference の設定より前に置く必要がある。
param(
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$setupDir = $PSScriptRoot
$binDir = Join-Path $setupDir "bin\$Configuration"
$readmeDir = Join-Path $setupDir "dist-readme"
$licenseDir = Join-Path $setupDir "License"
$distDir = Join-Path $setupDir "dist"
$stagingDir = Join-Path $distDir "_staging"
$zipPath = Join-Path $distDir "kasane-subscription.zip"

$requiredFiles = @(
    "Install-kasane.exe",
    "KasaneSetup-ja-JP.msi",
    "KasaneSetup-en-US.msi"
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

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $zipPath -Force

Remove-Item -Path $stagingDir -Recurse -Force

Write-Host "Package-Dist: created $zipPath"
