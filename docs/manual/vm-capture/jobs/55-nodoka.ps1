. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
New-Item -ItemType Directory -Force C:\kasane-sample | Out-Null
@"
def key A = 0x1e
def key B = 0x30
def key CapsLock = 0x3a
def key LCtrl = 0x1d
def key F1 = 0x3b
def key F2 = 0x3c
keymap Global
key *CapsLock = *LCtrl
key A = B
key C-F2 = F1
key F1 = &WindowMaximize
"@ | Set-Content -Path C:\kasane-sample\sample.nodoka -Encoding UTF8
$kp = (Get-Process kasane | Select-Object -First 1).Id
$h = Find-WinByTitle 'かさね*' 15000; $w = El $h
$before = Get-AllTopWindowHandles $kp
Invoke-Element (Find-ChildByAutomationId $w 'BtnImportExport'); $ie = Find-NewTopWindow $kp $before -timeoutMs 8000
Select-TabById $ie 'TabImport'; Start-Sleep 1
$b2 = Get-AllTopWindowHandles $kp
Invoke-Element (Find-ChildByAutomationId $ie 'ImportNodokaButton'); $nd = Find-NewTopWindow $kp $b2 -timeoutMs 8000
Start-Sleep 1
Set-ElementValue (Find-ChildByAutomationId $nd 'NodokaPath') 'C:\kasane-sample\sample.nodoka'; Start-Sleep -Milliseconds 600
Snap $nd 'nodoka-import'
Invoke-Element (Find-ChildByAutomationId $nd 'NodokaConvertButton'); Start-Sleep 2
"summary: $((Find-ChildByAutomationId $nd 'NodokaSummary').Current.Name)"
Snap $nd 'nodoka-result'
Invoke-Element (Find-ChildByAutomationId $nd 'NodokaCancelButton'); Start-Sleep 1
Invoke-Element (Find-ChildByAutomationId $ie 'CloseButton'); Start-Sleep 1
'done'