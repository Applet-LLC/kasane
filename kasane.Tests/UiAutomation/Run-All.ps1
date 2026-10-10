# UI Automation の試験 T1〜T13 を順に実行して、結果をまとめる（2026-10-10、P7）。
# 使い方: powershell -ExecutionPolicy Bypass -File Run-All.ps1 [-Only T11,T12]
# 前提: kasane.exe がビルド済み（Debug|x64）で、昇格していない PowerShell から実行する。各試験は自分で kasane.exe を起動して終了する。
# 各試験は、最後に "=== FAIL COUNT: N ===" を出す。N が 0 なら合格。"EXCEPTION:" や FAIL COUNT が無いものは失敗とする。
param([string[]]$Only)

$ErrorActionPreference = "Continue"
$scripts = Get-ChildItem -Path $PSScriptRoot -Filter "T*.ps1" |
    Sort-Object { [int]([regex]::Match($_.BaseName, '^T(\d+)').Groups[1].Value) }
if ($Only) { $scripts = $scripts | Where-Object { $name = $_.BaseName; $Only | Where-Object { $name -like "$_*" } } }

$failed = @()
foreach ($s in $scripts) {
    Write-Host "=== $($s.BaseName) ===" -ForegroundColor Cyan
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $s.FullName 2>&1 | Out-String
    $output.TrimEnd() | Write-Host
    $m = [regex]::Match($output, 'FAIL COUNT:\s*(\d+)')
    if (-not $m.Success -or [int]$m.Groups[1].Value -ne 0 -or $output -match 'EXCEPTION:') { $failed += $s.BaseName }
}

Write-Host ""
if ($failed.Count -eq 0) { Write-Host "すべて合格（$($scripts.Count) 件）" -ForegroundColor Green; exit 0 }
Write-Host "失敗: $($failed -join ', ')" -ForegroundColor Red
exit 1
