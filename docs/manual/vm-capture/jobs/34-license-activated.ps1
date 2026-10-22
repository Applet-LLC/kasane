. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
$h = Find-WinByTitle '*License Manager*' 10000; $w = El $h
$m = @((Find-ChildByAutomationId $w 'TxtUser' 3000), (Find-ChildByAutomationId $w 'TxtActivatedKey' 3000))
SnapMasked $h 'license-activated' $m
'done'