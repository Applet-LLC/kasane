. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
Get-Process kasane-license -ErrorAction SilentlyContinue | Stop-Process -Force
Stop-Service kasaneLicenseSvc -Force
$state = Get-ChildItem 'C:\ProgramData\kbdlayer-license' -Recurse -File -ErrorAction SilentlyContinue
"before: " + (($state | Select-Object -Expand FullName) -join ' ; ')
Remove-Item 'C:\ProgramData\kbdlayer-license\license-svc\licensestate.json' -Force -ErrorAction SilentlyContinue
Remove-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\kbdlayer\Parameters' -Name 'License*' -ErrorAction SilentlyContinue
Start-Service kasaneLicenseSvc; Start-Sleep 3
$null = Start-Process 'C:\Program Files\applet\kasane\license-gui\kasane-license.exe' -PassThru
$h = Find-WinByTitle '*License Manager*' 20000; $w = El $h; Start-Sleep 3
"status now: $((Find-ChildByAutomationId $w 'TxtStatus').Current.Name)"
Set-ElementValue (Find-ChildByAutomationId $w 'TxtLicenseKey') '<LICENSE_KEY>'
Invoke-Element (Find-ChildByAutomationId $w 'BtnActivate')
for ($i = 0; $i -lt 12; $i++) { Start-Sleep 5; $s = (Find-ChildByAutomationId $w 'TxtStatus').Current.Name; "t=$($i*5+5)s status=$s"; if ($s -notmatch 'アクティベート中') { break } }
Snap $h 'license-error'
'done'