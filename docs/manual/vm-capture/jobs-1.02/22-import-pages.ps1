. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# 設定フォルダのページの次から、「設定ファイルの取り込み」の 3 つの画面（入力前、入力後、見つからない警告）と、準備完了のページを撮る。
function WizHwnd { $p = Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle } | Select-Object -First 1; if ($p) { $p.MainWindowHandle } }
function Names($w) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } }
$nextPat = T '^次へ' '^&?Next'
$importCheck = T '設定ファイルを取り込む' 'Import a settings file'
# 1. 取り込みのページ
$h = WizHwnd; $w = El $h
Click-Center (Names $w | Where-Object { $_.Current.Name -match $nextPat } | Select-Object -First 1); Start-Sleep 2
$h = WizHwnd; $w = El $h; Start-Sleep -Milliseconds 800
Snap $h 'install-wizard-import'
# 2. チェックを付けて、存在しないパスを入れる
Click-Center (Names $w | Where-Object { $_.Current.Name -eq $importCheck } | Select-Object -First 1); Start-Sleep -Milliseconds 700
$edit = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ClassName -eq 'RichEdit20W' } | Select-Object -First 1
Click-Center $edit; Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait('D:\kasane-backup\settings.kasane.json'); Start-Sleep -Milliseconds 600
Snap $h 'install-wizard-import-filled'
# 3. 次へ → 見つからない警告
Click-Center (Names $w | Where-Object { $_.Current.Name -match $nextPat } | Select-Object -First 1); Start-Sleep 3
$m = Find-WinByTitle (T '*見つかりません*' '*not found*') 8000
if ($m) { Snap $m 'install-wizard-import-missing'; Click-Center (Names (El $m) | Where-Object { $_.Current.Name -eq 'OK' } | Select-Object -First 1); Start-Sleep 1 } else { 'missing-file dialog not found' }
# 4. チェックを外して、次へ → 準備完了のページ
$h = Find-WinByTitle (T '設定ファイルの取り込み' 'Import Settings File') 5000; $w = El $h
Click-Center (Names $w | Where-Object { $_.Current.Name -eq $importCheck } | Select-Object -First 1); Start-Sleep -Milliseconds 600
Click-Center (Names $w | Where-Object { $_.Current.Name -match $nextPat } | Select-Object -First 1); Start-Sleep 3
$h2 = WizHwnd
Snap $h2 'install-wizard-ready'
'done'
