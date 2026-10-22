# VM 内で使う共通部品。ジョブの先頭で . \\100.64.1.22\kasanevm\out\lib\vmlib.ps1 として読み込む。
$script:ShareOut = '\\100.64.1.22\kasanevm\out'
. "$script:ShareOut\lib\UiaLib.ps1"
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
if (-not ('VmLib.Native' -as [type])) {
    Add-Type -Namespace VmLib -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }
[System.Runtime.InteropServices.DllImport("dwmapi.dll")]
public static extern int DwmGetWindowAttribute(System.IntPtr hwnd, int attr, out RECT rect, int size);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr h, int cmd);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetWindowText(System.IntPtr h, System.Text.StringBuilder s, int n);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int GetWindowTextLength(System.IntPtr h);
'@
    [void][VmLib.Native]::SetProcessDPIAware()
}
# 指定したウィンドウ(AutomationElement または HWND)を、PNG で保存する。
function Snap($win, [string]$id) {
    $hwnd = if ($win -is [System.IntPtr]) { $win } else { [System.IntPtr]$win.Current.NativeWindowHandle }
    [void][VmLib.Native]::ShowWindow($hwnd, 9); [void][VmLib.Native]::SetForegroundWindow($hwnd); Start-Sleep -Milliseconds 500
    $r = New-Object VmLib.Native+RECT
    [void][VmLib.Native]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, [System.Runtime.InteropServices.Marshal]::SizeOf($r))
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { throw "ウィンドウの大きさを取得できません: $id" }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h))); $g.Dispose()
    $bmp.Save("$script:ShareOut\img\$id.png", [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    "snap: $id ($w x $h)"
}
# タイトルが一致するトップレベルウィンドウの HWND を返す(Win32 の EnumWindows。UIA の FindAll は当てにならないため)。
function Find-WinByTitle([string]$like, [int]$timeoutMs = 15000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $script:foundHwnd = $null
        $cb = [GuiUia.Enum+EnumWindowsProc] { param($h, $l)
            if ([GuiUia.Enum]::IsWindowVisible($h)) {
                $n = [VmLib.Native]::GetWindowTextLength($h)
                if ($n -gt 0) { $sb = New-Object System.Text.StringBuilder ($n + 1); [void][VmLib.Native]::GetWindowText($h, $sb, $sb.Capacity); if ($sb.ToString() -like $like) { $script:foundHwnd = $h } }
            }
            return $true }
        [void][GuiUia.Enum]::EnumWindows($cb, [System.IntPtr]::Zero)
        if ($script:foundHwnd) { return $script:foundHwnd }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function El([System.IntPtr]$hwnd) { [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) }
function Click-ByName($win, [string]$name, [int]$timeoutMs = 8000) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return }
        Start-Sleep -Milliseconds 200
    }
    throw "ボタンが見つかりません: $name"
}

Add-Type -Namespace VmLib -Name Mouse -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y); [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern void mouse_event(uint f,int dx,int dy,uint d,System.UIntPtr e);' -ErrorAction SilentlyContinue
# 要素の中央をマウスでクリックする(Invoke に対応していない部品用)
function Click-Center($el) {
    $r = $el.Current.BoundingRectangle
    [void][VmLib.Mouse]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)); Start-Sleep -Milliseconds 300
    [VmLib.Mouse]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [VmLib.Mouse]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
}
function Find-ByName($win, [string]$name, [int]$timeoutMs = 8000) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) { $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond); if ($el) { return $el }; Start-Sleep -Milliseconds 200 }
    return $null
}
# 指定した要素(AutomationElement の配列)を黒く塗りつぶして、ウィンドウを保存する。個人情報やキーを写さないため。
function SnapMasked($win, [string]$id, $maskElements) {
    $hwnd = if ($win -is [System.IntPtr]) { $win } else { [System.IntPtr]$win.Current.NativeWindowHandle }
    [void][VmLib.Native]::ShowWindow($hwnd, 9); [void][VmLib.Native]::SetForegroundWindow($hwnd); Start-Sleep -Milliseconds 500
    $r = New-Object VmLib.Native+RECT
    [void][VmLib.Native]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, [System.Runtime.InteropServices.Marshal]::SizeOf($r))
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    foreach ($m in $maskElements) {
        if (-not $m) { continue }
        $b = $m.Current.BoundingRectangle
        $g.FillRectangle([System.Drawing.Brushes]::Black, [int]($b.X - $r.Left - 3), [int]($b.Y - $r.Top - 2), [int]($b.Width + 6), [int]($b.Height + 4))
    }
    $g.Dispose(); $bmp.Save("$script:ShareOut\img\$id.png", [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    "snap(masked): $id"
}
# 画面の領域を指定して保存する(トレイのアイコンやメニュー用)。
function SnapRegion([int]$x, [int]$y, [int]$w, [int]$h, [string]$id) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h))); $g.Dispose()
    $bmp.Save("$script:ShareOut\img\$id.png", [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    "snap(region): $id ($w x $h)"
}
function Right-Click($el) {
    $r = $el.Current.BoundingRectangle
    [void][VmLib.Mouse]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)); Start-Sleep -Milliseconds 400
    [VmLib.Mouse]::mouse_event(8,0,0,0,[UIntPtr]::Zero); [VmLib.Mouse]::mouse_event(16,0,0,0,[UIntPtr]::Zero)
}
function Find-TrayButton {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'SystemTray.NormalButton')
    $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) | Where-Object { $_.Current.Name -match 'かさね|kasane' } | Select-Object -First 1
}