. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
function Texts($w) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' -and $_.Current.Name } | ForEach-Object { $n = $_.Current.Name; if ($_.Current.AutomationId -match 'User') { $n = '(masked)' }; "  {0} = {1}" -f $_.Current.AutomationId, $n } }
function Reopen { Get-Process kasane-license -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
    $null = Start-Process 'C:\Program Files\applet\kasane\license-gui\kasane-license.exe' -PassThru; $h = Find-WinByTitle '*License Manager*' 20000; Start-Sleep 3; $h }
function Activate($w, $key) { Set-ElementValue (Find-ChildByAutomationId $w 'TxtLicenseKey') $key; Invoke-Element (Find-ChildByAutomationId $w 'BtnActivate'); Start-Sleep 12 }
# 試用
$h = Reopen; $w = El $h
Activate $w '<LICENSE_KEY>'
$mask = @((Find-ChildByAutomationId $w 'TxtUser' 3000))
SnapMasked $h 'license-trial' $mask; Texts $w
# サブスク(状態を戻してから)
Get-Process kasane-license -ErrorAction SilentlyContinue | Stop-Process -Force
Stop-Service kasaneLicenseSvc -Force
Remove-Item 'C:\ProgramData\kbdlayer-license\license-svc\licensestate.json' -Force -ErrorAction SilentlyContinue
Start-Service kasaneLicenseSvc; Start-Sleep 3
$h = Reopen; $w = El $h
Activate $w '<LICENSE_KEY>'
"-- sub"; Texts $w
$maskEls = @()
foreach ($id in 'TxtUser','TxtActivatedKey','TxtLicenseKey') { try { $e = Find-ChildByAutomationId $w $id 2000; if ($e -and -not $e.Current.IsOffscreen) { $maskEls += $e; "  mask: $id" } } catch { } }
'done'