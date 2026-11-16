. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# 後片付け。作業用のフォルダと、撮影中に作った設定を消して、再起動する（ドライバの削除が、再起動後に完了する）。
Get-Process kasane,kasane-license,kasane-tray,SystemSettings -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2
foreach ($d in 'C:\kasane-dist', 'C:\kasane-fail', 'C:\kasane-sample', 'C:\kasane-capture', 'C:\kasane-data') { if (Test-Path $d) { Remove-Item $d -Recurse -Force; "removed $d" } }
$la = $env:LOCALAPPDATA
if (Test-Path "$la\applet\kasane") { Remove-Item "$la\applet\kasane" -Recurse -Force; "removed $la\applet\kasane (created during capture)" }
if (Test-Path "$la\applet" -PathType Container) { if (-not (Get-ChildItem "$la\applet" -Force)) { Remove-Item "$la\applet" -Force; "removed empty $la\applet" } }
"left by design: ProgramData kbdlayer-license = $(Test-Path 'C:\ProgramData\kbdlayer-license')"
New-Item -ItemType File "$script:ShareOut\results\rebooting-final.txt" -Force | Out-Null
shutdown /r /t 10 /c 'kasane manual capture: finish driver removal'
'rebooting in 10 s'
