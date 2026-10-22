. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
$kp = (Get-Process kasane | Select-Object -First 1).Id
$h = Find-WinByTitle 'かさね*' 10000; $w = El $h
$before = Get-AllTopWindowHandles $kp
Invoke-Element (Find-ChildByAutomationId $w 'BtnSettings'); $d = Find-NewTopWindow $kp $before -timeoutMs 8000
Select-TabById $d 'TabGeneral'; Start-Sleep 1
$cb = Find-ChildByAutomationId $d 'GeneralRemapOnlyWhileTrayCheck'
"before: $(Get-ToggleState $cb)"
Set-ToggleState $cb $false; Start-Sleep 1
Invoke-Element (Find-ChildByAutomationId $d 'OkButton'); Start-Sleep 3
Invoke-Element (Find-ChildByAutomationId $w 'BtnApply'); Start-Sleep 5
"title: $((El $h).Current.Name)"
"tray.json: $((Get-Content (Join-Path $env:LOCALAPPDATA 'applet\kasane\tray.json') -Raw) -replace '\s+',' ')"
Start-Sleep 6
$t = Find-TrayButton; "tray tooltip: $($t.Current.Name)"
'done'