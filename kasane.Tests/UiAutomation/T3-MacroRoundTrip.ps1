# GUI-T3: UI Automation round-trip test for the Macro editor -- type a modded-key macro
# (LCTL(KC_C) KC_A LCTL(LSFT(KC_T))), click OK, reopen, and confirm the same text is shown.
. "$PSScriptRoot\UiaLib.ps1"
$ErrorActionPreference = "Stop"
$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\kasane\bin\Debug\net8.0-windows\win-x64\kasane.exe"))
$fail = 0
function Chk([string]$label, [bool]$cond) {
    $v = if ($cond) { 'PASS' } else { 'FAIL'; $script:fail++ }
    "  [$v] $label"
}

$proc = $null
try {
    $proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 1500
    $main = Find-TopWindowByProcess $proc.Id -timeoutMs 8000
    Set-ForegroundWindowFor $main

    $btnMacros = Find-ChildByAutomationId $main "BtnMacros"
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btnMacros
    $mw1 = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    "  macro editor opened: $($mw1.Current.Name)"

    $idList = Find-ChildByAutomationId $mw1 "IdList"
    Select-ListItemByIndex $idList 0
    Start-Sleep -Milliseconds 300

    $macroText = "LCTL(KC_C) KC_A LCTL(LSFT(KC_T))"
    Set-ElementValue (Find-ChildByAutomationId $mw1 "MacroBox") $macroText
    Start-Sleep -Milliseconds 300

    $validationText = (Find-ChildByAutomationId $mw1 "ValidationText").Current.Name
    "  validation text: '$validationText'"
    Chk "modded-key macro validates OK" ($validationText -eq "OK")

    $mw1Handle = $mw1.Current.NativeWindowHandle.ToString()
    Invoke-Element (Find-ChildByAutomationId $mw1 "OkButton")
    Start-Sleep -Milliseconds 600
    Chk "macro editor closed after OK" ((Get-AllTopWindowHandles $proc.Id) -notcontains $mw1Handle)

    $before2 = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btnMacros
    $mw2 = Find-NewTopWindow $proc.Id $before2 -timeoutMs 6000
    Select-ListItemByIndex (Find-ChildByAutomationId $mw2 "IdList") 0
    Start-Sleep -Milliseconds 300
    $reopened = Get-ElementValue (Find-ChildByAutomationId $mw2 "MacroBox")
    "  reopened macro text: '$reopened'"
    Chk "macro (incl. modded keys) round-trips through the actual GUI" ($reopened -eq $macroText)

    Invoke-Element (Find-ChildByAutomationId $mw2 "CancelButton")
    Start-Sleep -Milliseconds 300

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
