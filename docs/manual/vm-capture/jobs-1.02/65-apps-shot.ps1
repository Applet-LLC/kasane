. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# Windows の設定の「インストールされているアプリ」（uninstall-apps、uninstall-apps-menu）。日本語の OS の画面なので、日本語のときだけ撮る。
# 英語のヘルプは、この 2 枚に、日本語の画像を使う（Windows の画面は、英語の OS がないと撮れない）。
if ($script:Lang -ne 'ja') { 'skipped: Windows Settings is shown in the OS language (Japanese); the English help uses the Japanese images'; return }
Get-Process SystemSettings -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
Start-Process 'ms-settings:appsfeatures'; Start-Sleep 6
$sh = Find-WinByTitle '設定' 20000; $sw = El $sh; [void][VmLib.Native]::SetForegroundWindow($sh); Start-Sleep 1
$edits = $sw.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
$top = $edits | Where-Object { $_.Current.AutomationId -eq 'CommandSearchTextBox' } | Select-Object -First 1
if ($top) { try { Set-ElementValue $top '' } catch { } }
$app = $edits | Where-Object { $_.Current.AutomationId -like '*AppSizesListFilter*' } | Select-Object -First 1
if (-not $app) { throw 'app search box not found' }
Set-ElementValue $app 'かさね'; Start-Sleep 4
# 左のプロフィール欄（アカウント名とメールアドレス）が写らないように、右側だけを切り出す。
$r = $sw.Current.BoundingRectangle
$x = [int]($r.X + 300); $y = [int]($r.Y + 40); $w = [int]($r.Width - 300); $hgt = [int]($r.Height - 40)
SnapRegion $x $y $w $hgt 'uninstall-apps'
$more = $sw.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -match 'その他のオプション' } | Select-Object -First 1
if ($more) { Click-Center $more; Start-Sleep 2; SnapRegion $x $y $w $hgt 'uninstall-apps-menu'; [System.Windows.Forms.SendKeys]::SendWait('{ESC}') }
Get-Process SystemSettings -ErrorAction SilentlyContinue | Stop-Process -Force
'done'
