# GUI-T10: UI Automation test for the tray tool (kasane-tray-nua.exe; the non-uiAccess build): the icon exists with a tooltip, a left click opens the
# editor (and a second click only brings it to the front), the right-click menu has 設定 / 一時停止 / レイヤー / ライセンス / 終了 in this
# order, ライセンス opens the license manager, and 終了 ends the tray.
# It never presses 一時停止 (that changes the keyboards' ON/OFF state; see .plans/tray.md for the pause tests).
# NOTE: this moves the mouse pointer and opens the hidden-icons flyout of the taskbar, so do not touch the PC while it runs.
# The tray is searched in the "hidden indicators" flyout (a freshly started tray icon is hidden by default on Windows 11).
. "$PSScriptRoot\UiaLib.ps1"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace TrayT -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y); [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,UIntPtr e);'
$ErrorActionPreference = "Stop"
$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\kasane-tray\bin\Debug\net8.0-windows\win-x64\kasane-tray-nua.exe"))
$UIA = [System.Windows.Automation.AutomationElement]
$root = $UIA::RootElement
$fail = 0
function Chk([string]$label, [bool]$cond) {
    $v = if ($cond) { 'PASS' } else { 'FAIL'; $script:fail++ }
    "  [$v] $label"
}

function Find-TrayIcon {
    foreach ($w in $root.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($w.Current.ClassName -match "Overflow") {
            foreach ($e in $w.FindAll('Descendants', [System.Windows.Automation.Condition]::TrueCondition)) {
                # The tooltip popup and its text have the same name; only the button is the icon.
                if ($e.Current.Name -like "かさね*" -and $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) { return $e }
            }
        }
    }
}

function Get-TrayIcon {
    $icon = Find-TrayIcon
    # After a click the flyout closes but a stale element can remain; only an on-screen element with an Invoke pattern counts.
    $usable = $false
    if ($icon) { try { $usable = (-not $icon.Current.IsOffscreen) -and $icon.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern) } catch { $usable = $false } }
    if (-not $usable) {
        $tray = $root.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($UIA::ClassNameProperty, "Shell_TrayWnd")))
        $chev = $tray.FindFirst('Descendants', (New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, "SystemTrayIcon")))
        $chev.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep 1
        $icon = Find-TrayIcon
    }
    return $icon
}

function Right-Click-TrayIcon {
    $icon = Get-TrayIcon
    $r = $icon.Current.BoundingRectangle
    [TrayT.Native]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
    Start-Sleep -Milliseconds 300
    [TrayT.Native]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero)   # right down
    [TrayT.Native]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero)  # right up
    Start-Sleep 1
}

# The context menu is a separate top-level window of the tray process; UIA's RootElement.FindAll does not list it (see UiaLib.ps1).
function Get-MenuItems {
    $tp = Get-Process kasane-tray-nua
    $items = @()
    foreach ($h in (Get-AllTopWindowHandles $tp.Id)) {
        $el = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr][int64]$h)
        foreach ($e in $el.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)))) { $items += $e }
    }
    return $items
}

try {
    Get-Process kasane-tray-nua, kasane, kasane-license -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    $proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 2500
    Chk "tray process is running" (-not $proc.HasExited)

    $second = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 1500
    Chk "a second tray exits at once (single instance)" ($second.HasExited -and @(Get-Process kasane-tray-nua).Count -eq 1)

    $icon = Get-TrayIcon
    Chk "the tray icon exists in the notification area" ($null -ne $icon)
    "  tooltip: '$($icon.Current.Name)'"
    Chk "the tooltip starts with かさね" ($icon.Current.Name -match "かさね")

    # left click -> editor
    $icon.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep 3
    Chk "left click opens the editor (kasane.exe)" (@(Get-Process kasane -ErrorAction SilentlyContinue).Count -eq 1)
    $icon = Get-TrayIcon
    $icon.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep 2
    Chk "a second left click does not start another editor" (@(Get-Process kasane -ErrorAction SilentlyContinue).Count -eq 1)

    # right-click menu
    Right-Click-TrayIcon
    $items = @(Get-MenuItems)
    $names = @($items | ForEach-Object { $_.Current.Name })
    "  menu: $($names -join ' / ')"
    Chk "the menu is 設定 / 一時停止 / レイヤー / ライセンス / 終了 in this order" (($names -join ',') -eq "設定,一時停止,レイヤー,ライセンス,終了")

    $license = $items | Where-Object { $_.Current.Name -eq "ライセンス" }
    $license.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep 3
    Chk "ライセンス opens the license manager (kasane-license.exe)" (@(Get-Process kasane-license -ErrorAction SilentlyContinue).Count -ge 1)

    Right-Click-TrayIcon
    $exit = Get-MenuItems | Where-Object { $_.Current.Name -eq "終了" }
    $exit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep 2
    Chk "終了 ends the tray" (@(Get-Process kasane-tray-nua -ErrorAction SilentlyContinue).Count -eq 0)

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    Get-Process kasane-tray-nua, kasane, kasane-license -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
