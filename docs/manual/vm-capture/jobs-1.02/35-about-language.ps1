. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# インストール版のエディタで、バージョン情報（settings-about）と、表示言語を変えたときの確認（settings-language-restart）を撮る。
# 表示言語は、日本語のときは English に、英語のときは日本語に変えて、確認の画面（現在の言語で出る）を撮り、再起動はせずに、もとの言語に戻す。
function Names($w) { try { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } } catch { @() } }
Get-Process kasane-license,kasane -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
$exe = 'C:\Program Files\applet\kasane\keymap-gui\kasane.exe'
$p = Start-Process $exe -PassThru; $kp = $p.Id
$h = Find-WinByTitle (T 'かさね*' 'kasane 1*') 30000; $w = El $h; Start-Sleep 4
"main title: $((El $h).Current.Name)"
function Open-Settings { $before = Get-AllTopWindowHandles $kp; Invoke-Element (Find-ChildByAutomationId $w 'BtnSettings'); Find-NewTopWindow $kp $before -timeoutMs 8000 }
# 1. バージョン情報
$d = Open-Settings
Select-TabById $d 'TabAbout'; Start-Sleep 1
Snap $d 'settings-about'
# 2. 表示言語を変えて OK → 確認の画面（0 = 自動、1 = 日本語、2 = English）
Select-TabById $d 'TabGeneral'; Start-Sleep 1
$toIndex = if ($script:Lang -eq 'en') { 1 } else { 2 }
Select-ComboItemByIndex (Find-ChildByAutomationId $d 'LanguageBox') $toIndex; Start-Sleep 1
$b2 = Get-AllTopWindowHandles $kp
Invoke-Element (Find-ChildByAutomationId $d 'OkButton'); Start-Sleep 2
$c = Find-NewTopWindow $kp $b2 -timeoutMs 8000
if ($c) {
    "confirm title: $($c.Current.Name)"
    Snap $c 'settings-language-restart'
    $cw = El ([System.IntPtr]$c.Current.NativeWindowHandle)
    $no = Names $cw | Where-Object { $_.Current.Name -match (T '^いいえ' '^&?No') } | Select-Object -First 1
    if ($no) { Click-Center $no; 'clicked No (no restart)' }
    Start-Sleep 1
} else { 'confirm dialog not found' }
# 3. もとの言語に戻す（日本語の撮影は自動、英語の撮影は English）。
$d = Open-Settings
Select-TabById $d 'TabGeneral'; Start-Sleep 1
$backIndex = if ($script:Lang -eq 'en') { 2 } else { 0 }
Select-ComboItemByIndex (Find-ChildByAutomationId $d 'LanguageBox') $backIndex; Start-Sleep 1
$b3 = Get-AllTopWindowHandles $kp
Invoke-Element (Find-ChildByAutomationId $d 'OkButton'); Start-Sleep 2
$cf = Find-NewTopWindow $kp $b3 -timeoutMs 6000
if ($cf) { $no = Names (El ([System.IntPtr]$cf.Current.NativeWindowHandle)) | Where-Object { $_.Current.Name -match (T '^いいえ' '^&?No') } | Select-Object -First 1; if ($no) { Click-Center $no; 'language restored' } }
"tray.json: $((Get-Content (Join-Path $env:LOCALAPPDATA 'applet\kasane\tray.json') -Raw -ErrorAction SilentlyContinue) -replace '\s+',' ')"
Get-Process kasane -ErrorAction SilentlyContinue | Stop-Process -Force
'done'
