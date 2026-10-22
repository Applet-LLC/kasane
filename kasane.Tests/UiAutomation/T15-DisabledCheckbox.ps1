# GUI-T15: UI Automation test for the "Disable" check box in the Macro editor -- type a macro, check
# "ItemDisabled", click OK, reopen, and confirm the check box is still checked and the list row is tagged.
# 前提: 試験用の設定フォルダ（環境変数 KASANE）で動かすのが安全。他の試験（T3 など）と同じく、保存はしない（OK は、メモリ上のプロジェクトへの反映）。
. "$PSScriptRoot\UiaLib.ps1"
$ErrorActionPreference = "Stop"
$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\kasane\bin\Debug\net8.0-windows\win-x64\kasane.exe"))
$fail = 0
$IDX = 0
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

    $idList = Find-ChildByAutomationId $mw1 "IdList"
    Select-ListItemByIndex $idList $IDX
    Start-Sleep -Milliseconds 300

    # 中身が空の番号は、チェックボックスを押せない（手元の設定に中身があっても、先に空にする。保存はしない）。
    Set-ElementValue (Find-ChildByAutomationId $mw1 "MacroBox") ""
    Start-Sleep -Milliseconds 300
    $box0 = Find-ChildByAutomationId $mw1 "ItemDisabled"
    Chk "check box is disabled while the macro is empty" (-not $box0.Current.IsEnabled)

    Set-ElementValue (Find-ChildByAutomationId $mw1 "MacroBox") "KC_H KC_I"
    Start-Sleep -Milliseconds 300
    $box = Find-ChildByAutomationId $mw1 "ItemDisabled"
    Chk "check box becomes enabled once the macro has content" $box.Current.IsEnabled
    Chk "check box starts unchecked" ((Get-ToggleState $box) -eq 'Off')

    Set-ToggleState $box $true
    Start-Sleep -Milliseconds 300
    Chk "check box is checked" ((Get-ToggleState (Find-ChildByAutomationId $mw1 "ItemDisabled")) -eq 'On')

    $mw1Handle = $mw1.Current.NativeWindowHandle.ToString()
    Invoke-Element (Find-ChildByAutomationId $mw1 "OkButton")
    Start-Sleep -Milliseconds 600
    Chk "macro editor closed after OK" ((Get-AllTopWindowHandles $proc.Id) -notcontains $mw1Handle)

    $before2 = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btnMacros
    $mw2 = Find-NewTopWindow $proc.Id $before2 -timeoutMs 6000
    Start-Sleep -Milliseconds 800   # 画面の一覧が作られるのを待つ
    Select-ListItemByIndex (Find-ChildByAutomationId $mw2 "IdList") $IDX
    Start-Sleep -Milliseconds 300
    Chk "check box is still checked after reopening" ((Get-ToggleState (Find-ChildByAutomationId $mw2 "ItemDisabled")) -eq 'On')

    $names = $mw2.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ }
    Chk "list row shows the disabled tag" ([bool]($names | Where-Object { $_ -match '\[無効\]|\[Disabled\]' }))

    # 後片付け: 無効を外して、内容を消す（保存はしないが、念のため）。
    Set-ToggleState (Find-ChildByAutomationId $mw2 "ItemDisabled") $false
    Invoke-Element (Find-ChildByAutomationId $mw2 "CancelButton")
    Start-Sleep -Milliseconds 300

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
