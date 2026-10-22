. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
$root = [System.Windows.Automation.AutomationElement]::RootElement
function Menus { $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Menu))) }
function MenuItemByName($n) { foreach ($m in (Menus)) { $i = $m.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$n))); if ($i) { return $i } } }
function Strip($id) { $t = Find-TrayButton; $r = $t.Current.BoundingRectangle; "tooltip: $($t.Current.Name)"; SnapRegion ([int]($r.X - 120)) ([int]$r.Y) 330 ([int]$r.Height) $id }
function SnapMenus($id) {
    $rs = @(Menus | ForEach-Object { $_.Current.BoundingRectangle }); if (-not $rs.Count) { "no menu"; return }
    $x1 = ($rs | ForEach-Object { $_.X } | Measure-Object -Minimum).Minimum; $y1 = ($rs | ForEach-Object { $_.Y } | Measure-Object -Minimum).Minimum
    $x2 = ($rs | ForEach-Object { $_.X + $_.Width } | Measure-Object -Maximum).Maximum; $y2 = ($rs | ForEach-Object { $_.Y + $_.Height } | Measure-Object -Maximum).Maximum
    SnapRegion ([int]($x1 - 8)) ([int]($y1 - 8)) ([int]($x2 - $x1 + 16)) ([int]($y2 - $y1 + 16)) $id
}
function OpenMenu { Right-Click (Find-TrayButton); Start-Sleep -Seconds 2 }
Strip 'tray-strip-active'
OpenMenu; "items enabled: " + ((Menus | ForEach-Object { $_.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition) } | Where-Object { $_.Current.Name } | ForEach-Object { "$($_.Current.Name)=$($_.Current.IsEnabled)" }) -join ', ')
SnapMenus 'tray-menu'
$layer = MenuItemByName 'レイヤー'
if ($layer -and $layer.Current.IsEnabled) {
    try { $layer.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() } catch { Click-Center $layer }
    Start-Sleep -Seconds 2; "menus now: $(@(Menus).Count)"
    SnapMenus 'tray-layers'
} else { 'layer item missing or disabled' }
[System.Windows.Forms.SendKeys]::SendWait('{ESC}{ESC}'); Start-Sleep 1
# 一時停止
OpenMenu; $p = MenuItemByName '一時停止'
if ($p) { try { $p.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { Click-Center $p } }
Start-Sleep -Seconds 6
Strip 'tray-strip-paused'
OpenMenu; SnapMenus 'tray-menu-paused'
$p = MenuItemByName '一時停止'
if ($p) { try { $p.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { Click-Center $p } }
Start-Sleep -Seconds 6
Strip 'tray-strip-resumed'
'done'