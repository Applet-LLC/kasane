<#
.SYNOPSIS
  マニュアル(docs/manual)用に、kasane の画面を撮影して docs/manual/img/<lang>/<画面ID>.png に保存する。
.DESCRIPTION
  kasane.exe(Debug|x64)を起動し、UI Automation で画面を開いて、ウィンドウごとに PNG へ保存する。
  手元の設定を汚さないよう、環境変数 KASANE で空の一時フォルダを設定フォルダにして起動する
  (tray.json の表示言語・色のモードも、そのフォルダに書く)。ドライバには触れない。
  キーボードは「ドライバ未接続」の仮想デバイスになるので、個人のデバイス ID・名前は写らない。
  インストーラー、アンインストール、ライセンスのエラー、トレイのバルーンなど、自動化できない画面は
  docs/manual/img/MANUAL-SHOTS.md の手順で手動で撮る。
.PARAMETER Lang   ja | en(表示言語。既定は ja)
.PARAMETER Only   画面IDの前方一致(複数可)。例: -Only settings-,assign-
.PARAMETER Admin  管理者モードの画面だけを撮る(画面IDに -admin を付ける)。昇格した PowerShell で実行する。
                 昇格したウィンドウは、昇格していない UIA からは操作できないため。キーボードへの適用や緊急解除の反映などは行わない。
.PARAMETER Dark   ダークモードで撮る(画面IDに -dark を付けて保存。settings-dark だけ撮りたいときは -Only settings-dark)
前提: kasane を Debug|x64 でビルド済み。昇格していない PowerShell で実行する(閲覧専用の帯が写る)。
      DPI 100% の表示で撮ると、画像の大きさがそろう。
#>
param(
    [ValidateSet('ja', 'en')][string]$Lang = 'ja',
    [string[]]$Only = @(),
    [switch]$Dark,
    [switch]$Admin
)

. "$PSScriptRoot\UiaLib.ps1"
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\kasane\bin\Debug\net8.0-windows\win-x64\kasane.exe'))
$OutDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\docs\manual\img\$Lang"))
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if (-not ('GuiUia.Shot' -as [type])) {
    Add-Type -Namespace GuiUia -Name Shot -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }
[System.Runtime.InteropServices.DllImport("dwmapi.dll")]
public static extern int DwmGetWindowAttribute(System.IntPtr hwnd, int attr, out RECT rect, int size);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDPIAware();
'@
    [void][GuiUia.Shot]::SetProcessDPIAware()
}

# 設定フォルダ(KASANE)を空の一時フォルダにして、表示言語と色のモードを書く。
$dataDir = "C:\kasane-data"   # 画面に出るので、短く自然なパスにする(撮影後に削除する)
# 注意: 「バージョン情報」(settings-about)は、実行ファイルのパスが写るので、このスクリプトでは撮らない。インストール版で手動で撮る。
if (Test-Path $dataDir) { throw "$dataDir がすでにあります。中身を確かめて、削除してから実行してください。" }
New-Item -ItemType Directory -Path $dataDir | Out-Null
function Write-TrayConfig([string]$colorMode) {
    $ui = if ($Lang -eq 'en') { 'English' } else { 'Japanese' }
    $json = @{ UiLanguage = $ui; ColorMode = $colorMode } | ConvertTo-Json
    [System.IO.File]::WriteAllText((Join-Path $dataDir 'tray.json'), $json, (New-Object System.Text.UTF8Encoding($false)))
}
Write-TrayConfig ($(if ($Dark) { 'Dark' } else { 'Light' }))
$env:KASANE = $dataDir

$script:ok = New-Object System.Collections.Generic.List[string]
$script:failed = New-Object System.Collections.Generic.List[string]

function Want([string]$id) {
    if ($Only.Count -eq 0) { return $true }
    foreach ($p in $Only) { if ($id.StartsWith($p) -or $p.StartsWith($id)) { return $true } }
    return $false
}

# ウィンドウの見える枠(影を除く)を画面から切り出して保存する。
function Save-Shot([System.Windows.Automation.AutomationElement]$window, [string]$id) {
    Set-ForegroundWindowFor $window
    Start-Sleep -Milliseconds 400
    $r = New-Object GuiUia.Shot+RECT
    $hwnd = [System.IntPtr]$window.Current.NativeWindowHandle
    [void][GuiUia.Shot]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, [System.Runtime.InteropServices.Marshal]::SizeOf($r))   # DWMWA_EXTENDED_FRAME_BOUNDS
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { throw "ウィンドウの大きさを取得できません: $id" }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir "$id.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $script:ok.Add($id)
    "  撮影: $id ($w x $h)"
}

# 1 つの画面の撮影を、失敗しても次へ進めるように包む。
function Run-Shot([string]$id, [scriptblock]$body) {
    if (-not (Want $id)) { return }
    try { & $body }
    catch { $script:failed.Add("${id}: $($_.Exception.Message)"); "  失敗: $id - $($_.Exception.Message)" }
}

function Open-Child($main, [string]$buttonId) {
    $before = Get-AllTopWindowHandles $script:proc.Id
    Invoke-Element (Find-ChildByAutomationId $main $buttonId)
    return Find-NewTopWindow $script:proc.Id $before -timeoutMs 6000
}

function Close-Top($win) {
    try {
        $h = $win.Current.NativeWindowHandle.ToString()
        $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Start-Sleep -Milliseconds 500
    } catch { }
}

# モーダルなダイアログを開くボタンは、非同期で Invoke する(T7 と同じ。Invoke がダイアログを閉じるまで戻らないため)。
function Open-Settings($main) { Open-Child $main 'BtnSettings' }

function Shoot-Tabs($dlg, [hashtable]$map) {
    foreach ($tab in $map.Keys) {
        $id = $map[$tab]
        Run-Shot $id { Select-TabById $dlg $tab; Start-Sleep -Milliseconds 400; Save-Shot $dlg $id }.GetNewClosure()
    }
}

# ---- 管理者モード(-Admin): 昇格した PowerShell で実行する ----
if ($Admin) {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '-Admin は、管理者として実行した PowerShell で使います。' }
    function Select-Radio($win, [string]$id) {
        $el = Find-ChildByAutomationId $win $id
        $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 500
    }
    $script:proc = $null
    try {
        $script:proc = Start-Process $EXE -PassThru
        Start-Sleep -Milliseconds 2500
        $main = Find-TopWindowByProcess $script:proc.Id -timeoutMs 10000
        Set-ForegroundWindowFor $main
        "起動(管理者): $($main.Current.Name)"

        Run-Shot 'editor-main-admin' { Save-Shot $main 'editor-main-admin' }

        Run-Shot 'calib-main-admin' {
            $w = Open-Child $main 'BtnCalibrateAll'
            Save-Shot $w 'calib-main-admin'
            Select-Radio $w 'ModeSingleKey'; Save-Shot $w 'calib-single'
            Select-Radio $w 'ModeAllKeys';   Save-Shot $w 'calib-all'
            Close-Top $w
        }

        # 設定ダイアログ。緊急解除のタブは、別のタブに移ると反映されるので、最後に撮って、閉じるだけにする(OK も押さない)。
        Run-Shot 'settings-admin' {
            $d = Open-Settings $main
            foreach ($pair in @(@('TabScope', 'settings-scope-admin'), @('TabI8042', 'settings-i8042-admin'), @('TabDataDir', 'settings-datadir-admin'))) {
                Run-Shot $pair[1] { Select-TabById $d $pair[0]; Start-Sleep -Milliseconds 400; Save-Shot $d $pair[1] }.GetNewClosure()
            }
            Run-Shot 'emergency-tab-admin' { Select-TabById $d 'TabEmergency'; Start-Sleep -Milliseconds 400; Save-Shot $d 'emergency-tab-admin' }
            Run-Shot 'emergency-check-admin' {
                $before = Get-AllTopWindowHandles $script:proc.Id
                Invoke-Element (Find-ChildByAutomationId $d 'EmergencyCheckButton')
                $w = Find-NewTopWindow $script:proc.Id $before -timeoutMs 6000
                Save-Shot $w 'emergency-check-admin'; Close-Top $w
            }
            Close-Top $d
        }
    }
    finally {
        if ($script:proc -and -not $script:proc.HasExited) { $script:proc.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 800; if (-not $script:proc.HasExited) { $script:proc.Kill() } }
    }
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    "撮影できた画面: $($script:ok.Count) 枚 -> $OutDir"
    if ($script:failed.Count -gt 0) { $script:failed | ForEach-Object { "  失敗: $_" }; exit 1 }
    exit 0
}

$script:proc = $null
try {
    $script:proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 2500
    $main = Find-TopWindowByProcess $script:proc.Id -timeoutMs 10000
    Set-ForegroundWindowFor $main
    "起動: $($main.Current.Name)  (言語=$Lang 保存先=$dataDir)"

    # ---- エディタの基本画面(05) ----
    Run-Shot 'editor-main'     { Save-Shot $main 'editor-main' }
    Run-Shot 'editor-readonly' { Save-Shot $main 'editor-readonly' }   # 昇格していなければ、閲覧専用の帯が写る

    Run-Shot 'editor-kle' {
        $w = Open-Child $main 'BtnAddKle'; Save-Shot $w 'editor-kle'; Close-Top $w
    }

    # ---- キーの割り当て(08) ----
    Run-Shot 'assign-main' {
        $w = Open-Child $main 'Key_0'; Save-Shot $w 'assign-main'
        $cats = Find-ChildByAutomationId $w 'LstCategories'
        Select-ListItemByIndex $cats 10; Start-Sleep -Milliseconds 400; Save-Shot $w 'assign-layer'
        Select-ListItemByIndex $cats 11; Start-Sleep -Milliseconds 400; Save-Shot $w 'assign-modtap'
        Close-Top $w
    }

    # ---- 編集画面(09) ----
    Run-Shot 'macro-editor'       { $w = Open-Child $main 'BtnMacros';       Save-Shot $w 'macro-editor';       Close-Top $w }
    Run-Shot 'combo-editor'       { $w = Open-Child $main 'BtnCombos';       Save-Shot $w 'combo-editor';       Close-Top $w }
    Run-Shot 'tapdance-editor'    { $w = Open-Child $main 'BtnTapDances';    Save-Shot $w 'tapdance-editor';    Close-Top $w }
    Run-Shot 'keyoverride-editor' { $w = Open-Child $main 'BtnKeyOverrides'; Save-Shot $w 'keyoverride-editor'; Close-Top $w }

    # ---- キャリブレーション(06) ----
    Run-Shot 'calib-main' { $w = Open-Child $main 'BtnCalibrateAll'; Save-Shot $w 'calib-main'; Close-Top $w }

    # ---- 設定ダイアログ(07, 10, 11, 12) ----
    $dlg = $null
    if ((Want 'settings-') -or (Want 'emergency-') -or (Want 'ime-') -or (Want 'nicola-') -or (Want 'helper-tab') -or (Want 'apprules-tab')) {
        $dlg = Open-Settings $main
        Shoot-Tabs $dlg ([ordered]@{
            TabGeneral   = 'settings-general';  TabFeatures = 'settings-features'; TabTiming  = 'settings-timing'
            TabScope     = 'settings-scope';    TabDataDir  = 'settings-datadir';  TabI8042   = 'settings-i8042'
            TabDesktop   = 'settings-desktop';  TabEmergency = 'emergency-tab'
            TabImeLayer  = 'ime-tab';           TabHelper   = 'helper-tab';        TabAppRules = 'apprules-tab'
        })
        Run-Shot 'nicola-section' {
            Select-TabById $dlg 'TabImeLayer'
            # タブの中身はスクロールできる。下まで送って、NICOLA の部分を撮る。
            $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)
            $sv = $dlg.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
            if (-not $sv) { throw 'スクロールできる領域が見つかりません' }
            $sp = $sv.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
            Start-Sleep -Milliseconds 400
            Save-Shot $dlg 'nicola-section'
        }
        Run-Shot 'emergency-check' {
            Select-TabById $dlg 'TabEmergency'
            $before = Get-AllTopWindowHandles $script:proc.Id
            Invoke-Element (Find-ChildByAutomationId $dlg 'EmergencyCheckButton')
            $w = Find-NewTopWindow $script:proc.Id $before -timeoutMs 6000
            Save-Shot $w 'emergency-check'; Close-Top $w
        }
        Run-Shot 'nicola-editor' {
            Select-TabById $dlg 'TabImeLayer'
            Select-ComboItemByIndex (Find-ChildByAutomationId $dlg 'NicolaModeBox') 1
            $before = Get-AllTopWindowHandles $script:proc.Id
            Invoke-Element (Find-ChildByAutomationId $dlg 'NicolaEditButton')
            $w = Find-NewTopWindow $script:proc.Id $before -timeoutMs 6000
            Save-Shot $w 'nicola-editor'; Close-Top $w
        }
        Close-Top $dlg
    }

    # ---- インポート／エクスポート(13) ----
    if (Want 'impexp-') {
        $w = Open-Child $main 'BtnImportExport'
        Shoot-Tabs $w ([ordered]@{ TabExport = 'impexp-export'; TabImport = 'impexp-import'; TabClear = 'impexp-clear' })
        Close-Top $w
    }
}
finally {
    if ($script:proc -and -not $script:proc.HasExited) { $script:proc.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 800; if (-not $script:proc.HasExited) { $script:proc.Kill() } }
}

# ダークモードの 1 枚(12 章)。別のプロセスで、色のモードだけ変えて撮る。
if (-not $Dark -and (Want 'settings-dark')) {
    Write-TrayConfig 'Dark'
    $script:proc = Start-Process $EXE -PassThru
    try {
        Start-Sleep -Milliseconds 2500
        $main = Find-TopWindowByProcess $script:proc.Id -timeoutMs 10000
        Run-Shot 'settings-dark' {
            $d = Open-Settings $main; Select-TabById $d 'TabGeneral'; Start-Sleep -Milliseconds 400
            Save-Shot $d 'settings-dark'; Close-Top $d
        }
    }
    finally { if (-not $script:proc.HasExited) { $script:proc.Kill() } }
}

''
"撮影できた画面: $($script:ok.Count) 枚 -> $OutDir"
if ($script:failed.Count -gt 0) {
    "撮影できなかった画面: $($script:failed.Count) 枚"
    $script:failed | ForEach-Object { "  $_" }
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
}

# 撮影用の保存先を片付ける(このスクリプトが作ったフォルダだけ)。
Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
