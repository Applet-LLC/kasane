# Shared UI Automation helpers for driving kasane.exe.
#
# IMPORTANT (discovered empirically, 2026-09-30): AutomationElement.RootElement.FindAll(Children,
# processIdCondition) does NOT reliably enumerate a process's top-level windows -- a newly-opened
# WPF dialog can exist (confirmed visible via a plain Win32 EnumWindows call) while completely
# invisible to that FindAll query, seemingly regardless of how long you wait. New/closed top-level
# windows must be detected via raw Win32 EnumWindows instead; AutomationElement.FromHandle(hwnd) is
# then used to get a working AutomationElement for descendant queries once a window's HWND is known.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase

function Find-ChildByAutomationId([System.Windows.Automation.AutomationElement]$parent, [string]$id, [int]$timeoutMs = 5000) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $el = $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 150
    }
    throw "element with AutomationId='$id' not found under $($parent.Current.Name) within ${timeoutMs}ms"
}

if (-not ("GuiUiaEnum" -as [type])) {
    Add-Type -Namespace GuiUia -Name Enum -MemberDefinition @'
public delegate bool EnumWindowsProc(System.IntPtr hWnd, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint processId);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool IsWindowVisible(System.IntPtr hWnd);
'@
}

# Raw Win32 top-level window handles for a process (see note above: UIA's own FindAll is unreliable
# for this). Only visible windows are returned.
function Get-AllTopWindowHandles([int]$processId) {
    $result = New-Object System.Collections.Generic.List[string]
    $cb = {
        param($hWnd, $lParam)
        $wpid = 0
        [GuiUia.Enum]::GetWindowThreadProcessId($hWnd, [ref]$wpid) | Out-Null
        if ($wpid -eq $processId -and [GuiUia.Enum]::IsWindowVisible($hWnd)) {
            $result.Add($hWnd.ToString())
        }
        return $true
    }
    [GuiUia.Enum]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
    return @($result)
}

function Find-TopWindowByProcess([int]$processId, [string]$titleContains = $null, [int]$timeoutMs = 8000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        foreach ($h in (Get-AllTopWindowHandles $processId)) {
            $el = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr][long]$h)
            if (-not $titleContains -or $el.Current.Name -match [regex]::Escape($titleContains)) { return $el }
        }
        Start-Sleep -Milliseconds 150
    }
    throw "top-level window for process $processId (title contains '$titleContains') not found within ${timeoutMs}ms"
}

function Find-NewTopWindow([int]$processId, [string[]]$excludeHandles, [int]$timeoutMs = 6000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        foreach ($h in (Get-AllTopWindowHandles $processId)) {
            if ($excludeHandles -notcontains $h) {
                return [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr][long]$h)
            }
        }
        Start-Sleep -Milliseconds 150
    }
    throw "no new top-level window appeared for process $processId within ${timeoutMs}ms"
}

if (-not ("GuiUiaWin32" -as [type])) {
    Add-Type -Namespace GuiUia -Name Win32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
'@
}

function Set-ForegroundWindowFor([System.Windows.Automation.AutomationElement]$window) {
    $hwnd = [IntPtr]$window.Current.NativeWindowHandle
    [GuiUia.Win32]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
    [GuiUia.Win32]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 200
}

function Invoke-Element([System.Windows.Automation.AutomationElement]$el) {
    $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pat.Invoke()
}

function Set-ElementValue([System.Windows.Automation.AutomationElement]$el, [string]$value) {
    $pat = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pat.SetValue($value)
}

function Get-ElementValue([System.Windows.Automation.AutomationElement]$el) {
    $pat = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    return $pat.Current.Value
}

function Select-ListItemByIndex([System.Windows.Automation.AutomationElement]$listBox, [int]$index) {
    $items = $listBox.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    $item = $items[$index]
    $pat = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pat.Select()
}

function Select-ComboItemByIndex([System.Windows.Automation.AutomationElement]$combo, [int]$index) {
    $expandPat = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expandPat.Expand()
    Start-Sleep -Milliseconds 200
    $items = $combo.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    $item = $items[$index]
    $pat = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pat.Select()
    Start-Sleep -Milliseconds 200
}

function Get-ToggleState([System.Windows.Automation.AutomationElement]$el) {
    $pat = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return $pat.Current.ToggleState
}

function Set-ToggleState([System.Windows.Automation.AutomationElement]$el, [bool]$checked) {
    $pat = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $wantOn = if ($checked) { [System.Windows.Automation.ToggleState]::On } else { [System.Windows.Automation.ToggleState]::Off }
    if ($pat.Current.ToggleState -ne $wantOn) { $pat.Toggle() }
}

# Select a TabItem (by AutomationId) in a TabControl. WPF only puts the selected tab's content into the
# UIA tree, so select the tab before looking for the controls inside it.
function Select-TabById([System.Windows.Automation.AutomationElement]$window, [string]$tabId) {
    $tab = Find-ChildByAutomationId $window $tabId
    $pat = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pat.Select()
    Start-Sleep -Milliseconds 300
}

# Name (display text) of the selected item of a ComboBox, or $null if nothing is selected.
function Get-ComboSelectedName([System.Windows.Automation.AutomationElement]$combo) {
    $sel = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($sel.Count -eq 0) { return $null }
    return $sel[0].Current.Name
}
