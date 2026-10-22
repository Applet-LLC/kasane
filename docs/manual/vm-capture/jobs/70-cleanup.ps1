$res = '\\100.64.1.22\kasanevm\out\results'
function L($s) { $s }
Get-Process kasane,kasane-license,kasane-tray,SystemSettings -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2
# 作業用フォルダを削除
foreach ($d in 'C:\kasane-dist','C:\kasane-fail','C:\kasane-sample') { if (Test-Path $d) { Remove-Item $d -Recurse -Force; L "removed $d" } }
# 以前のテストの設定を、元の場所に戻す(applet\kasane は、撮影中に新しく作られたものなので消す)
$la = $env:LOCALAPPDATA
if (Test-Path "$la\applet\kasane") { Remove-Item "$la\applet\kasane" -Recurse -Force; L "removed $la\applet\kasane (created during capture)" }
if (Test-Path "$la\applet" -PathType Container) { if (-not (Get-ChildItem "$la\applet" -Force)) { Remove-Item "$la\applet" -Force; L "removed empty $la\applet" } }
$old = 'C:\kasane-old-settings\LayerDriver_30180000'
if (Test-Path $old) { New-Item -ItemType Directory -Force "$la\LayerDriver" | Out-Null; Move-Item $old "$la\LayerDriver\30180000" -Force; L "restored $la\LayerDriver\30180000" }
$old2 = 'C:\kasane-old-settings\applet_kasane_30180000'; if (Test-Path $old2) { Remove-Item $old2 -Recurse -Force; L "dropped migrated copy (it was a copy of the LayerDriver one)" }
if ((Test-Path 'C:\kasane-old-settings') -and -not (Get-ChildItem 'C:\kasane-old-settings' -Force)) { Remove-Item 'C:\kasane-old-settings' -Force }
L "LayerDriver settings now: $((Get-ChildItem "$la\LayerDriver" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -Expand FullName) -join '; ')"
L "left by design: ProgramData kbdlayer-license = $(Test-Path 'C:\ProgramData\kbdlayer-license')"
# 後片付けのあと、再起動してドライバの削除を完了させる
New-Item -ItemType File "$res\rebooting-final.txt" -Force | Out-Null
shutdown /r /t 10 /c "kasane manual capture: finish driver removal"
L 'rebooting in 10 s'