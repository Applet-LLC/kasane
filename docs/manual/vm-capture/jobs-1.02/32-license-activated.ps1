. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# License Manager。サブスクリプションのキーを認証した状態（license-activated）。接続の診断（license-diagnostics）は、この画像から、annotate-images.ps1 が切り出す。
# 試用の状態を戻してから行う。ユーザーと、キー（途中を伏せたもの）は塗りつぶす。
function Get-Key([string]$name) { $p = "$script:ShareOut\keys\$name.txt"; if (-not (Test-Path $p)) { throw "key file not found: $p" }; (Get-Content $p -Raw).Trim() }
function Reopen { Get-Process kasane, kasane-license -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
    $null = Start-Process 'C:\Program Files\applet\kasane\license-gui\kasane-license.exe' -PassThru; $h = Find-WinByTitle '*License Manager*' 20000; [void][VmLib.Native]::SetForegroundWindow($h); Start-Sleep 3; $h }
Get-Process kasane-license -ErrorAction SilentlyContinue | Stop-Process -Force
Stop-Service kasaneLicenseSvc -Force
Remove-Item 'C:\ProgramData\kbdlayer-license\license-svc\licensestate.dat' -Force -ErrorAction SilentlyContinue
Start-Service kasaneLicenseSvc; Start-Sleep 3
$h = Reopen; $w = El $h
Set-ElementValue (Find-ChildByAutomationId $w 'TxtLicenseKey') (Get-Key 'subscription')
Invoke-Element (Find-ChildByAutomationId $w 'BtnActivate'); Start-Sleep 12
"status: $((Find-ChildByAutomationId $w 'TxtStatus').Current.Name)"
$masks = @((Find-ChildByAutomationId $w 'TxtUser' 3000), (Find-ChildByAutomationId $w 'TxtActivatedKey' 3000))
SnapMasked $h 'license-activated' $masks
'done'
