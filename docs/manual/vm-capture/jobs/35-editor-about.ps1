. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
Get-Process kasane-license -ErrorAction SilentlyContinue | Stop-Process -Force
$exe = Get-ChildItem 'C:\Program Files\applet\kasane' -Recurse -Filter kasane.exe | Select-Object -First 1 -Expand FullName
"exe: $exe"
$null = Start-Process $exe -PassThru
$h = Find-WinByTitle 'かさね*' 25000; $w = El $h; Start-Sleep 3
"main title: $((El $h).Current.Name)"
Snap $h 'vm-editor-first'
# 設定 → バージョン情報
$before = Get-AllTopWindowHandles ((Get-Process kasane | Select-Object -First 1).Id)
Invoke-Element (Find-ChildByAutomationId $w 'BtnSettings')
$d = Find-NewTopWindow ((Get-Process kasane | Select-Object -First 1).Id) $before -timeoutMs 8000
Select-TabById $d 'TabAbout'; Start-Sleep 1
Snap $d 'settings-about'
# 表示言語: 全般タブで English に変えて OK → 確認の画面
Select-TabById $d 'TabGeneral'; Start-Sleep 500
$combo = Find-ChildByAutomationId $d 'LanguageBox'
Select-ComboItemByIndex $combo 2
Start-Sleep 1
$b2 = Get-AllTopWindowHandles ((Get-Process kasane | Select-Object -First 1).Id)
Invoke-Element (Find-ChildByAutomationId $d 'OkButton')
Start-Sleep 2
$c = Find-NewTopWindow ((Get-Process kasane | Select-Object -First 1).Id) $b2 -timeoutMs 8000
if ($c) { "confirm title: $($c.Current.Name)"; Snap $c 'settings-language-restart'
  (El ([System.IntPtr]$c.Current.NativeWindowHandle)).FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } | ForEach-Object { "  [{0}] {1}" -f $_.Current.ControlType.ProgrammaticName.Replace('ControlType.',''), $_.Current.Name.Substring(0,[math]::Min(80,$_.Current.Name.Length)) } } else { 'confirm dialog not found' }
'done'