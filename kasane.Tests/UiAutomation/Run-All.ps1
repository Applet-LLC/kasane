# UI Automation の試験 T1〜T13 を順に実行して、結果をまとめる（2026-10-10、P7）。
# 使い方: powershell -ExecutionPolicy Bypass -File Run-All.ps1 [-Only T11-,T12-]（前方一致。T1 だけを選ぶには T1-）
# 前提: kasane.exe がビルド済み（Debug|x64）で、昇格していない PowerShell から実行する。各試験は自分で kasane.exe を起動して終了する。
# 注意: T10 はトレイ（kasane-tray-nua.exe。uiAccess の無い版）を終了させる。実行後にトレイが必要なら、起動し直す。
# 前提: T10 は kasane-tray-nua.exe の Debug ビルドを使う（先に dotnet build kasane-tray -c Debug）。インストール済みの kasane-tray.exe（uiAccess 版）が動いていると、単一起動の mutex で T10 が失敗するので、先に終了させる。
# 各試験は、PowerShell 7（pwsh）があればそれで、無ければ Windows PowerShell 5.1 で動かす（.ps1 は UTF-8 の BOM つきなので、どちらでも日本語が読める）。
# 各試験は、最後に "=== FAIL COUNT: N ===" を出す。N が 0 なら合格。"EXCEPTION:" や FAIL COUNT が無いものは失敗とする。
param([string[]]$Only)

$ErrorActionPreference = "Continue"
$scripts = Get-ChildItem -Path $PSScriptRoot -Filter "T*.ps1" |
    Sort-Object { [int]([regex]::Match($_.BaseName, '^T(\d+)').Groups[1].Value) }
if ($Only) { $Only = @($Only | ForEach-Object { $_ -split "," } | Where-Object { $_ }) # powershell -File ではカンマ区切りが1つの文字列になるので分ける
    $scripts = $scripts | Where-Object { $name = $_.BaseName; $Only | Where-Object { $name -like "$_*" } } }

$ps = if (Get-Command pwsh -ErrorAction SilentlyContinue) { "pwsh" } else { "powershell" }
$failed = @()
foreach ($s in $scripts) {
    Write-Host "=== $($s.BaseName) ===" -ForegroundColor Cyan
    $output = & $ps -NoProfile -ExecutionPolicy Bypass -File $s.FullName 2>&1 | Out-String
    $output.TrimEnd() | Write-Host
    $m = [regex]::Match($output, 'FAIL COUNT:\s*(\d+)')
    if (-not $m.Success -or [int]$m.Groups[1].Value -ne 0 -or $output -match 'EXCEPTION:') { $failed += $s.BaseName }
}

Write-Host ""
if (-not (Get-Process -Name "kasane-tray-nua" -ErrorAction SilentlyContinue)) { Write-Host "注意: kasane-tray-nua.exe は動いていません（T10 が終了させます）。必要なら起動し直してください。" -ForegroundColor Yellow }
if ($failed.Count -eq 0) { Write-Host "すべて合格（$($scripts.Count) 件）" -ForegroundColor Green; exit 0 }
Write-Host "失敗: $($failed -join ', ')" -ForegroundColor Red
exit 1
