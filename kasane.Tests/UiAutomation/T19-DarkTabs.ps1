# GUI-T19: in the dark color mode, the contents of the settings dialog's tabs are visible to UI Automation
# (2026-11-09: the dark TabControl template lacked PART_SelectedContentHost, so only the tab headers appeared).
# Runs with an empty temporary data folder (KASANE) whose tray.json selects the dark mode; nothing of the user's is touched.
. "$PSScriptRoot\UiaLib.ps1"
$ErrorActionPreference = "Stop"
$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\kasane\bin\Debug\net8.0-windows\win-x64\kasane.exe"))
$fail = 0
function Chk([string]$label, [bool]$cond) {
    $v = if ($cond) { 'PASS' } else { 'FAIL'; $script:fail++ }
    "  [$v] $label"
}
$dataDir = Join-Path ([System.IO.Path]::GetTempPath()) ("kasane-t19-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $dataDir | Out-Null
[System.IO.File]::WriteAllText((Join-Path $dataDir "tray.json"), '{"UiLanguage":"Japanese","ColorMode":"Dark"}', (New-Object System.Text.UTF8Encoding($false)))
$oldEnv = $env:KASANE
$proc = $null
try {
    $env:KASANE = $dataDir
    $proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 2000
    $main = Find-TopWindowByProcess $proc.Id -timeoutMs 8000
    Set-ForegroundWindowFor $main
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $main "BtnSettings")
    $dlg = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 600
    Select-TabById $dlg "TabGeneral"
    Chk "dark: the general tab's contents are visible to UI Automation" ([bool](Find-ChildByAutomationId $dlg "GeneralRemapOnlyWhileTrayCheck"))
    Select-TabById $dlg "TabFeatures"
    Chk "dark: another tab's contents are visible too" ([bool](Find-ChildByAutomationId $dlg "FeatureCombo"))
    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    $env:KASANE = $oldEnv
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 300
    Remove-Item -Recurse -Force $dataDir -ErrorAction SilentlyContinue
}
