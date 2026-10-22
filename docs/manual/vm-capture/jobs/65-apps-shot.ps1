. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
$prog = '\\100.64.1.22\kasanevm\out\results\65.progress'; Remove-Item $prog -ErrorAction SilentlyContinue
function P($s) { Add-Content $prog $s }
try {
$sh = Find-WinByTitle '設定' 10000; $sw = El $sh; [void][VmLib.Native]::SetForegroundWindow($sh); Start-Sleep 1
$edits = $sw.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit)))
$top = $edits | Where-Object { $_.Current.AutomationId -eq 'CommandSearchTextBox' } | Select-Object -First 1
if ($top) { try { Set-ElementValue $top '' } catch { } }
$app = $edits | Where-Object { $_.Current.AutomationId -like '*AppSizesListFilter*' } | Select-Object -First 1
if (-not $app) { throw 'app search box not found' }
Set-ElementValue $app 'かさね'; Start-Sleep 4
$r = $sw.Current.BoundingRectangle
$x = [int]($r.X + 300); $y = [int]($r.Y + 40); $w = [int]($r.Width - 300); $hgt = [int]($r.Height - 40)
SnapRegion $x $y $w $hgt 'uninstall-apps'
# 「…」→ アンインストール のメニュー
$more = $sw.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -match 'その他のオプション' -and $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and -not $_.Current.IsOffscreen } | Select-Object -First 1
if ($more) { Click-Center $more; Start-Sleep 2
  SnapRegion $x $y $w $hgt 'uninstall-apps-menu'
  [System.Windows.Forms.SendKeys]::SendWait('{ESC}') }
Get-Process | Where-Object { $_.ProcessName -eq 'SystemSettings' } | Stop-Process -Force -ErrorAction SilentlyContinue
} catch { P "ERROR: $_" }
'done'