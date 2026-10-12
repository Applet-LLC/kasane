# GUI-T9: UI Automation test for the settings dialog (feature/gui-settings): the Features tab round-trip,
# the NICOLA tab + NICOLA table editor (validation, round-trip of an edit), the Emergency tab's input
# validation and the simultaneous-press check window (opened and closed without pressing keys), and that
# the Scope tab shows a status line, and the i8042prt tab's controls exist.
# Nothing here writes to the driver: Features/NICOLA only change the in-memory project (applied by
# "キーボードに適用", which this test never presses); the Emergency/Scope "apply" buttons are NOT pressed.
. "$PSScriptRoot\UiaLib.ps1"
$ErrorActionPreference = "Stop"
$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\kasane\bin\Debug\net8.0-windows\win-x64\kasane.exe"))
$fail = 0
function Chk([string]$label, [bool]$cond) {
    $v = if ($cond) { 'PASS' } else { 'FAIL'; $script:fail++ }
    "  [$v] $label"
}

function Open-Settings($main, $procId) {
    $before = Get-AllTopWindowHandles $procId
    Invoke-Element (Find-ChildByAutomationId $main "BtnSettings")
    return Find-NewTopWindow $procId $before -timeoutMs 6000
}

function Close-WithOk($dlg, $procId) {
    $h = $dlg.Current.NativeWindowHandle.ToString()
    Invoke-Element (Find-ChildByAutomationId $dlg "OkButton")
    Start-Sleep -Milliseconds 600
    return ((Get-AllTopWindowHandles $procId) -notcontains $h)
}

$proc = $null
try {
    $proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 1500
    $main = Find-TopWindowByProcess $proc.Id -timeoutMs 8000
    Set-ForegroundWindowFor $main

    # ---- Features tab ----
    $dlg = Open-Settings $main $proc.Id
    Select-TabById $dlg "TabFeatures"
    $comboBefore = Get-ToggleState (Find-ChildByAutomationId $dlg "FeatureCombo")
    $want = if ($comboBefore -eq [System.Windows.Automation.ToggleState]::On) { $false } else { $true }
    Set-ToggleState (Find-ChildByAutomationId $dlg "FeatureCombo") $want
    Chk "settings dialog closes on OK (features)" (Close-WithOk $dlg $proc.Id)

    $dlg = Open-Settings $main $proc.Id
    Select-TabById $dlg "TabFeatures"
    $comboAfter = Get-ToggleState (Find-ChildByAutomationId $dlg "FeatureCombo")
    "  FeatureCombo: before=$comboBefore after=$comboAfter"
    Chk "FeatureCombo toggle round-trips" ($comboAfter -ne $comboBefore)
    Chk "the other feature checkboxes exist (Macro/TapDance/KeyOverride)" (
        (Find-ChildByAutomationId $dlg "FeatureMacro") -and (Find-ChildByAutomationId $dlg "FeatureTapDance") -and (Find-ChildByAutomationId $dlg "FeatureKeyOverride"))

    # ---- NICOLA tab ----
    Select-TabById $dlg "TabNicola"
    # IME link checkbox (saved to tray.json on OK only when changed; this test never toggles it). It reflects tray.json (default: on).
    $imeLinkState = Get-ToggleState (Find-ChildByAutomationId $dlg "NicolaImeLinkCheck")
    $trayJson = Join-Path $env:LOCALAPPDATA "applet\kasane\tray.json"
    $expectedOn = $true
    if (Test-Path $trayJson) { try { $expectedOn = [bool](Get-Content $trayJson -Raw | ConvertFrom-Json).ImeLink } catch { $expectedOn = $true } }
    Chk "IME link checkbox exists and reflects tray.json (default on)" (($imeLinkState -eq [System.Windows.Automation.ToggleState]::On) -eq $expectedOn)
    Select-ComboItemByIndex (Find-ChildByAutomationId $dlg "NicolaModeBox") 1   # この設定を使う
    $summary = (Find-ChildByAutomationId $dlg "NicolaSummary").Current.Name
    "  NICOLA summary: '$summary'"
    Chk "choosing 'use' fills in the standard layout (romaji, 32 keys)" ($summary -match "ローマ字入力" -and $summary -match "32")

    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $dlg "NicolaEditButton")
    $ed = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    "  NICOLA editor opened: $($ed.Current.Name)"
    Start-Sleep -Milliseconds 300
    Chk "editor starts valid ('OK' or warnings only)" ((Find-ChildByAutomationId $ed "ValidationText").Current.Name -notmatch "^NICOLA:")

    Select-ComboItemByIndex (Find-ChildByAutomationId $ed "OutputBox") 1    # jis-kana
    Select-ListItemByIndex (Find-ChildByAutomationId $ed "KeyList") 0       # KC_Q
    Start-Sleep -Milliseconds 200
    Chk "selecting the first key shows KC_Q" ((Get-ElementValue (Find-ChildByAutomationId $ed "KeyBox")) -eq "KC_Q")
    Set-ElementValue (Find-ChildByAutomationId $ed "SingleBox") "ぬ"
    Start-Sleep -Milliseconds 200
    $v1 = (Find-ChildByAutomationId $ed "ValidationText").Current.Name
    "  validation after edit: '$v1'"
    Chk "jis-kana output with the standard table has no warnings ('OK')" ($v1 -eq "OK")

    Set-ElementValue (Find-ChildByAutomationId $ed "ThumbRightBox") "KC_MHEN"   # same as the left thumb
    Start-Sleep -Milliseconds 200
    $v2 = (Find-ChildByAutomationId $ed "ValidationText").Current.Name
    "  validation with same thumbs: '$v2'"
    Chk "same left/right thumb key is an error" ($v2 -match "別のキー")
    $edHandle = $ed.Current.NativeWindowHandle.ToString()
    Invoke-Element (Find-ChildByAutomationId $ed "OkButton")
    Start-Sleep -Milliseconds 600
    Chk "editor stays open while invalid" ((Get-AllTopWindowHandles $proc.Id) -contains $edHandle)

    Set-ElementValue (Find-ChildByAutomationId $ed "ThumbRightBox") "KC_HENK"
    Start-Sleep -Milliseconds 200
    Chk "editor closes on OK once fixed" (Close-WithOk $ed $proc.Id)

    $summary2 = (Find-ChildByAutomationId $dlg "NicolaSummary").Current.Name
    "  NICOLA summary after edit: '$summary2'"
    Chk "summary reflects jis-kana" ($summary2 -match "JISかな入力")
    Chk "settings dialog closes on OK (nicola)" (Close-WithOk $dlg $proc.Id)

    $dlg = Open-Settings $main $proc.Id
    Select-TabById $dlg "TabNicola"
    Chk "NICOLA mode round-trips ('use')" ((Get-ComboSelectedName (Find-ChildByAutomationId $dlg "NicolaModeBox")) -match "この設定を使う")
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $dlg "NicolaEditButton")
    $ed = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 300
    Chk "editor reopens with jis-kana" ((Get-ComboSelectedName (Find-ChildByAutomationId $ed "OutputBox")) -match "jis-kana")
    Select-ListItemByIndex (Find-ChildByAutomationId $ed "KeyList") 0
    Start-Sleep -Milliseconds 200
    Chk "edited kana round-trips (KC_Q single = ぬ)" ((Get-ElementValue (Find-ChildByAutomationId $ed "SingleBox")) -eq "ぬ")
    Invoke-Element (Find-ChildByAutomationId $ed "CancelButton")
    Start-Sleep -Milliseconds 400

    # back to "not managed" so the project is left as it was
    Select-ComboItemByIndex (Find-ChildByAutomationId $dlg "NicolaModeBox") 0

    # ---- Emergency tab (validation only; EmergencyApplyButton is NOT pressed) ----
    Select-TabById $dlg "TabEmergency"
    1..6 | ForEach-Object { Set-ElementValue (Find-ChildByAutomationId $dlg "EmergencyKey$_") "" }
    Set-ElementValue (Find-ChildByAutomationId $dlg "EmergencyKey1") "KC_LCTL"
    Invoke-Element (Find-ChildByAutomationId $dlg "EmergencyCheckButton")
    Start-Sleep -Milliseconds 400
    $s1 = (Find-ChildByAutomationId $dlg "EmergencyStatus").Current.Name
    "  emergency status (1 key): '$s1'"
    Chk "one key is rejected (needs 2 or more)" ($s1 -match "2個以上")

    Set-ElementValue (Find-ChildByAutomationId $dlg "EmergencyKey2") "KC_LCTL"
    Invoke-Element (Find-ChildByAutomationId $dlg "EmergencyCheckButton")
    Start-Sleep -Milliseconds 400
    Chk "duplicate key is rejected" ((Find-ChildByAutomationId $dlg "EmergencyStatus").Current.Name -match "2回")

    Set-ElementValue (Find-ChildByAutomationId $dlg "EmergencyKey2") "MO(1)"
    Invoke-Element (Find-ChildByAutomationId $dlg "EmergencyCheckButton")
    Start-Sleep -Milliseconds 400
    Chk "non-plain key is rejected" ((Find-ChildByAutomationId $dlg "EmergencyStatus").Current.Name -match "単純なキー")

    Set-ElementValue (Find-ChildByAutomationId $dlg "EmergencyKey2") "KC_RCTL"
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $dlg "EmergencyCheckButton")
    $chk = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 300
    Chk "check window opens with nothing pressed yet" ((Find-ChildByAutomationId $chk "CheckResult").Current.Name -match "まだ押されていません")
    Invoke-Element (Find-ChildByAutomationId $chk "CloseButton")
    Start-Sleep -Milliseconds 500
    Chk "after closing without pressing, status says not detected" ((Find-ChildByAutomationId $dlg "EmergencyStatus").Current.Name -match "検出できませんでした")

    # ---- Scope tab (display only) ----
    Select-TabById $dlg "TabScope"
    $scope = (Find-ChildByAutomationId $dlg "ScopeStatus").Current.Name
    "  scope status: '$scope'"
    Chk "scope tab shows a status line" ($scope.Length -gt 0)
    # The three choices exist (ScopeApplyButton is NOT pressed: it would change the keyboard's persisted scope).
    Chk "scope tab has the three radio buttons (model / instance / global)" (
        (Find-ChildByAutomationId $dlg "ScopeModel") -and (Find-ChildByAutomationId $dlg "ScopeInstance") -and (Find-ChildByAutomationId $dlg "ScopeGlobal"))

    # ---- Data folder tab (display only) ----
    Select-TabById $dlg "TabDataDir"
    $dataDir = (Find-ChildByAutomationId $dlg "DataDirEffective").Current.Name
    "  data dir: '$dataDir'"
    Chk "data dir tab shows the folder in use and the KASANE value" (
        $dataDir.Length -gt 0 -and (Find-ChildByAutomationId $dlg "DataDirVariable") -and (Find-ChildByAutomationId $dlg "DataDirOpenButton"))

    # ---- i8042prt tab (display only; I8042ApplyButton is NOT pressed: it would write the driver-wide setting) ----
    Select-TabById $dlg "TabI8042"
    $i8042 = (Find-ChildByAutomationId $dlg "I8042Status").Current.Name
    "  i8042 status: '$i8042'"
    Chk "i8042 tab shows a status line" ($i8042.Length -gt 0)
    Chk "i8042 tab has the enable / profile checkboxes and the apply button" (
        (Find-ChildByAutomationId $dlg "I8042Enabled") -and (Find-ChildByAutomationId $dlg "I8042ProfileEnabled") -and (Find-ChildByAutomationId $dlg "I8042ApplyButton"))
    Chk "i8042 tab has the preset and the five value boxes" (
        (Find-ChildByAutomationId $dlg "I8042Preset") -and (Find-ChildByAutomationId $dlg "I8042Type") -and (Find-ChildByAutomationId $dlg "I8042Subtype") -and
        (Find-ChildByAutomationId $dlg "I8042Identifier") -and (Find-ChildByAutomationId $dlg "I8042LayerJpn") -and (Find-ChildByAutomationId $dlg "I8042LayerKor"))

    Invoke-Element (Find-ChildByAutomationId $dlg "CancelButton")
    Start-Sleep -Milliseconds 300

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
