. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# インストーラーの起動画面（install-launcher）と、失敗の画面（install-launcher-failed）。
# 英語のときは、起動画面の右上のリンク（English）を押して、英語にする（起動画面は、OS の表示言語で決まるため）。
function Start-Launcher([string]$exe) {
    $p = Start-Process $exe -PassThru
    $h = Find-WinByTitle (T '*セットアップ*' '*Setup*') 20000
    if (-not $h) { throw "launcher window not found: $exe" }
    Start-Sleep 1
    if ($script:Lang -eq 'en') {
        try { Click-ByName (El $h) 'English' 3000; Start-Sleep 1 } catch { 'launcher is already in English' }
    }
    [pscustomobject]@{ Process = $p; Hwnd = $h }
}
# 1. 正常な起動画面
$l = Start-Launcher C:\kasane-dist\Install-kasane.exe
Snap $l.Hwnd 'install-launcher'
$l.Process | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 1
# 2. 失敗の画面（空の MSI を置いた別フォルダ）
New-Item -ItemType Directory -Force C:\kasane-fail | Out-Null
Copy-Item C:\kasane-dist\Install-kasane.exe C:\kasane-fail\ -Force
foreach ($n in 'KasaneSetup-ja-JP.msi','KasaneSetup-en-US.msi') { Set-Content C:\kasane-fail\$n 'dummy' }
$f = Start-Launcher C:\kasane-fail\Install-kasane.exe
Click-ByName (El $f.Hwnd) (T 'インストール' 'Install')
# msiexec が、ダミーの MSI を開けないというエラーを出す。前面にして Enter で閉じ、残っていれば OK を押す。
$m = Find-WinByTitle (T 'Windows インストーラー' 'Windows Installer') 20000
if ($m) {
    [void][VmLib.Native]::SetForegroundWindow($m); Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Start-Sleep 3
    $m2 = Find-WinByTitle (T 'Windows インストーラー' 'Windows Installer') 2000
    if ($m2) { $ok = Find-ByName (El $m2) 'OK'; if ($ok) { Click-Center $ok; Start-Sleep 3 } }
}
$e = Find-WinByTitle (T '*失敗*' '*Failed*') 60000
if ($e) { Start-Sleep 1; Snap $e 'install-launcher-failed' } else { 'failure dialog not found' }
Get-Process Install-kasane -ErrorAction SilentlyContinue | Stop-Process -Force
'done'
