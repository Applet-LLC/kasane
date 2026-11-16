. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# License Manager。試用のキーを入れた状態（license-trial）。ユーザー（メールアドレス）は塗りつぶす。
function Get-Key([string]$name) { $p = "$script:ShareOut\keys\$name.txt"; if (-not (Test-Path $p)) { throw "key file not found: $p" }; (Get-Content $p -Raw).Trim() }
function Reopen { Get-Process kasane, kasane-license -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
    $null = Start-Process 'C:\Program Files\applet\kasane\license-gui\kasane-license.exe' -PassThru; $h = Find-WinByTitle '*License Manager*' 20000; [void][VmLib.Native]::SetForegroundWindow($h); Start-Sleep 3; $h }
$h = Reopen; $w = El $h
Set-ElementValue (Find-ChildByAutomationId $w 'TxtLicenseKey') (Get-Key 'trial')
Invoke-Element (Find-ChildByAutomationId $w 'BtnActivate'); Start-Sleep 12
"status: $((Find-ChildByAutomationId $w 'TxtStatus').Current.Name)"
$mask = @((Find-ChildByAutomationId $w 'TxtUser' 3000))
SnapMasked $h 'license-trial' $mask
'done'
