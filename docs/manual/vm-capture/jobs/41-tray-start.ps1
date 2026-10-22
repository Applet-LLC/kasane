. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
"kbdlayer: $((Get-Service kbdlayer -ErrorAction SilentlyContinue).Status)  license svc: $((Get-Service kasaneLicenseSvc).Status)"
"tray running: $([bool](Get-Process kasane-tray* -ErrorAction SilentlyContinue))"
if (-not (Get-Process kasane-tray* -ErrorAction SilentlyContinue)) { $null = Start-Process 'C:\Program Files\applet\kasane\tray\kasane-tray.exe'; Start-Sleep 6 }
Get-Process kasane-tray* -ErrorAction SilentlyContinue | Select-Object Name,Id | Out-String
"-- taskbar / tray elements containing kasane/かさね:"
$root = [System.Windows.Automation.AutomationElement]::RootElement
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.OrCondition(
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty,'Shell_TrayWnd')),
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty,'TopLevelWindowForOverflowXamlIsland'))))) 
"tray roots: $($all.Count)"
foreach ($t in $all) {
  $t.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -match 'kasane|かさね' } | ForEach-Object { "  [{0}] class={1} name={2} rect={3} offscreen={4}" -f $_.Current.ControlType.ProgrammaticName.Replace('ControlType.',''), $_.Current.ClassName, $_.Current.Name, $_.Current.BoundingRectangle, $_.Current.IsOffscreen }
}
'done'