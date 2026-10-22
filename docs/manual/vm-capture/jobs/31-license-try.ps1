. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
function Texts($w) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' -and $_.Current.Name } | ForEach-Object { "  {0} = {1}" -f $_.Current.AutomationId, $_.Current.Name } }
$h = Find-WinByTitle '*License Manager*' 10000; $w = El $h
function Activate($key) {
    $edit = Find-ChildByAutomationId $w 'TxtLicenseKey'; Set-ElementValue $edit $key
    Invoke-Element (Find-ChildByAutomationId $w 'BtnActivate'); Start-Sleep 10
}
# 1. 無効なキー
Activate '<LICENSE_KEY>'
Snap $h 'license-error'; "-- after invalid key"; Texts $w
# 2. 試用のキー
Activate '<LICENSE_KEY>'
Snap $h 'license-trial'; "-- after trial key"; Texts $w
'done'