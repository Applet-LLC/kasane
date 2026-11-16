. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# インストーラーを起動し、ウィザードの最初の 4 ページ（ようこそ、使用許諾、機能の選択、設定フォルダ）を撮る。
# 設定フォルダのページ（install-wizard-datadir）には、1.02 で足した「参照...」ボタンが写る。
Get-Process Install-kasane -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
$null = Start-Process C:\kasane-dist\Install-kasane.exe -PassThru
$h = Find-LauncherWin 20000; Start-Sleep 1
if ($script:Lang -eq 'en') { Switch-LauncherToEnglish $h }
Click-ByName (El $h) (T 'インストール' 'Install')
Start-Sleep 12

function WizHwnd { $p = Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle } | Select-Object -First 1; if ($p) { $p.MainWindowHandle } }
function Names($w) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } }
$ids = 'install-wizard-welcome', 'install-wizard-license', 'install-wizard-features', 'install-wizard-datadir'
$dataDirTitle = T '設定フォルダ' 'Settings Folder'
for ($i = 0; $i -lt $ids.Count; $i++) {
    $h = WizHwnd; if (-not $h) { "page $($i + 1): no wizard window"; break }
    $w = El $h; Start-Sleep -Milliseconds 800
    Snap $h $ids[$i]
    $d = Names $w | ForEach-Object { $_.Current.Name }
    "== page $($i + 1): " + (($d | ForEach-Object { $_.Substring(0, [math]::Min(60, $_.Length)) }) -join ' / ')
    if ($i -gt 0 -and ($d -match $dataDirTitle)) { '  -> data dir page (stop here)'; break }
    $agree = Names $w | Where-Object { $_.Current.Name -match (T '同意' 'accept') } | Select-Object -First 1
    if ($agree) { Click-Center $agree; '  -> agreed'; Start-Sleep -Milliseconds 600 }
    $next = Names $w | Where-Object { $_.Current.Name -match (T '^次へ' '^&?Next') } | Select-Object -First 1
    if (-not $next) { '  -> no next button'; break }
    Click-Center $next; Start-Sleep 2
}
'done'
