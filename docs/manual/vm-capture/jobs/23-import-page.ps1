. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
function WizHwnd { $p = Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle } | Select-Object -First 1; if ($p) { $p.MainWindowHandle } }
function Names($w) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } }
$w = El (WizHwnd)
Click-Center (Names $w | Where-Object { $_.Current.Name -match '^次へ' } | Select-Object -First 1); Start-Sleep 2
$w = El (WizHwnd); Start-Sleep -Milliseconds 800
Snap (WizHwnd) 'install-wizard-import'
Names $w | ForEach-Object { "  [{0}] {1}  rect={2}  patterns={3}" -f $_.Current.ControlType.ProgrammaticName.Replace('ControlType.',''), $_.Current.Name.Substring(0,[math]::Min(70,$_.Current.Name.Length)), $_.Current.BoundingRectangle, (($_.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName.Replace('Identifiers.Pattern','') }) -join ',') }
'--- all (including unnamed edits)'
$w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { -not $_.Current.Name } | ForEach-Object { "  [{0}] (no name) class={1} rect={2}" -f $_.Current.ControlType.ProgrammaticName.Replace('ControlType.',''), $_.Current.ClassName, $_.Current.BoundingRectangle }
'done'