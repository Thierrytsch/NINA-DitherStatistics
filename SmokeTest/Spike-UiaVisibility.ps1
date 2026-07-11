<#
.SYNOPSIS
Step-0 diagnostic for the extended smoke test: determines how much of the
DitherStatistics plugin panel is reachable through managed UI Automation
(System.Windows.Automation, UIA2) inside a running NINA.

.DESCRIPTION
Background: Run-SmokeTest.ps1 historically claimed that "NINA does not expose
its WPF tree to UI Automation beyond the main tabs". That claim is suspect
(WPF auto-generates automation peers for its whole visual tree, and the same
script successfully finds TabItem descendants), so this spike re-verifies it
before the extended smoke test commits to a UI-driving technology.

Probes, against a live NINA with the plugin panel on the Imaging tab:
  1. NINA main window lookup by process id (same pattern the smoke test uses)
  2. Global button sweep: is the plugin's 'Clear Data' button a UIA descendant?
  3. Plugin panel root discovery: nearest ScrollPattern-capable ancestor of
     the 'Clear Data' button (= the panel's ScrollViewer)
  4. Panel-scoped dumps of Button / CheckBox / ComboBox / Edit / Text elements
     (names, automation ids, class names, supported patterns) - this shows
     what UIA reports as Name for emoji-content buttons and for TextBlocks
     composed of <Run> elements
  5. Interaction probes, all state-restoring:
     - InvokePattern availability on buttons (never invoked)
     - TogglePattern on the Quality Assessment toggle: double-flip, restored
       (deliberately NOT the persistence toggle - flipping that deletes files)
     - ValuePattern on the profile ComboBox's editable text: set + restored
     - ScrollPattern on the panel ScrollViewer: SetScrollPercent + restored
Writes a report to -OutFile and prints a final OUTCOME line for the decision
ladder in the refactoring plan:
  A = managed UIA2 sees and drives the panel -> build NinaUia.ps1 on it, no deps
  B = elements missing under UIA2            -> try FlaUI/UIA3
  C = no element access at all               -> coordinate fallback

.EXAMPLE
# NINA must already be running with the plugin deployed:
powershell -ExecutionPolicy Bypass -File .\Spike-UiaVisibility.ps1

.OUTCOME
2026-07-10 (NINA 3.1 HF2, Windows 11, plugin 1.6.0): OUTCOME C - CONFIRMED.
Two runs: one with the RDP session display detached, one with an active,
visible session in which a screenshot proved the plugin panel fully rendered
on the Imaging tab. Identical results in both runs:
- Managed UIA2 and FlaUI/UIA3 both see only the window shell: title bar,
  MainTabControl with the tab HEADERS, status-bar controls - 54 raw elements,
  9 buttons in the whole application. No tab CONTENT of any tab is exposed;
  the plugin panel is invisible to UIA even while visibly on screen. The
  truncation is provider-side (identical tree in both client stacks), so
  FlaUI/UIA3 does not help - Outcome B is ruled out. Evidence:
  artifacts/20260710_141905_uia_spike/ (spike reports of both runs, raw-tree
  dump, FlaUI probe script, panel screenshot).
- NINA source shows no deliberate suppression (no AutomationPeer overrides,
  no WM_GETOBJECT handling); suspected cause is NINA's restyled main
  TabControl and/or AvalonDock automation peers not exposing content
  children.
- Environment rule learned along the way: screen capture (CopyFromScreen and
  PrintWindow alike) FAILS while the RDP session has no active display -
  smoke-test runs that take screenshots require a connected, visible session.
- Consequence for the extended smoke test: element-level UIA (AutomationIds,
  text reads, Invoke/Toggle/Value/Scroll patterns) is NOT available from
  outside the process. Decision (maintainer, 2026-07-10): plugin-side
  diagnostic channel (SmokeTest bridge) for interactions and value reads;
  blind wheel scrolling + screenshots for the visual layer.
#>
[CmdletBinding()]
param(
    # Label of NINA's Imaging tab (adjust for localized NINA installations)
    [string]$ImagingTabLabel = 'Imaging',
    [string]$OutFile = "$PSScriptRoot\spike_uia_report.txt",
    # How long to keep retrying element lookups (WPF realizes elements lazily)
    [int]$FindTimeoutSec = 45
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$reportLines = New-Object System.Collections.Generic.List[string]
function Rep([string]$Message) {
    Write-Host $Message
    $reportLines.Add($Message)
}
# Verbose element dumps go to the report file only, not the console
function RepFile([string]$Message) {
    $reportLines.Add($Message)
}

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function New-TypeCondition($ControlType) {
    New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ControlType)
}

function Get-PatternNames($Element) {
    try {
        $names = $Element.GetSupportedPatterns() | ForEach-Object {
            $_.ProgrammaticName -replace 'PatternIdentifiers\.Pattern$', '' -replace 'Pattern$', ''
        }
        return ($names -join ',')
    } catch { return '?' }
}

function Describe-Element($Element) {
    $c = $Element.Current
    $r = $c.BoundingRectangle
    "name='{0}' autoId='{1}' class='{2}' enabled={3} rect=({4:F0},{5:F0} {6:F0}x{7:F0}) patterns=[{8}]" -f `
        $c.Name, $c.AutomationId, $c.ClassName, $c.IsEnabled, $r.X, $r.Y, $r.Width, $r.Height, (Get-PatternNames $Element)
}

# Retry wrapper: WPF creates automation peers lazily, so a single FindFirst
# right after a tab switch can miss elements that appear a second later.
function Find-WithRetry([scriptblock]$Finder, [string]$What, [int]$TimeoutSec = $FindTimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $result = & $Finder
        if ($result) { return $result }
        Start-Sleep -Milliseconds 1000
    } while ((Get-Date) -lt $deadline)
    Rep "NOT FOUND after ${TimeoutSec}s: $What"
    return $null
}

$outcomeFlags = @{
    WindowFound   = $false
    ButtonsFound  = $false
    TextReadable  = $false
    InvokeAvail   = $false
    ToggleWorks   = $false
    ComboValue    = $false
    ScrollWorks   = $false
}

Rep ("=== DitherStatistics UIA visibility spike, {0:yyyy-MM-dd HH:mm:ss} ===" -f (Get-Date))
Rep ("PowerShell {0}, CLR {1}" -f $PSVersionTable.PSVersion, [System.Environment]::Version)

# --- 1. NINA main window ---------------------------------------------------
$nina = Get-Process -Name 'NINA' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $nina) { Rep 'FATAL: NINA is not running.'; Rep 'OUTCOME: INCONCLUSIVE (start NINA first)'; exit 2 }
Rep "NINA process id: $($nina.Id)"

$procCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $nina.Id)
$window = Find-WithRetry {
    # NINA can have several top-level windows (splash, main); pick the largest
    $candidates = @([System.Windows.Automation.AutomationElement]::RootElement.FindAll($TS::Children, $procCond))
    $best = $null; $bestArea = 0
    foreach ($w in $candidates) {
        $r = $w.Current.BoundingRectangle
        $area = $r.Width * $r.Height
        if ($area -gt $bestArea) { $best = $w; $bestArea = $area }
    }
    $best
} 'NINA top-level window'
if (-not $window) { Rep 'OUTCOME: C (window itself not found - UIA unusable)'; exit 2 }
$outcomeFlags.WindowFound = $true
Rep ("Main window: " + (Describe-Element $window))

# --- 2. Switch to the Imaging tab (plugin panel lives there) ----------------
$tabCond = New-TypeCondition ([System.Windows.Automation.ControlType]::TabItem)
$textCond = New-TypeCondition ([System.Windows.Automation.ControlType]::Text)
$switched = $false
foreach ($item in $window.FindAll($TS::Descendants, $tabCond)) {
    $labels = @($item.FindAll($TS::Descendants, $textCond) | ForEach-Object { $_.Current.Name })
    if ($labels -contains $ImagingTabLabel) {
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Seconds 3   # let the tab and lazily created charts render
        $switched = $true
        break
    }
}
Rep "Imaging tab switch: $(if ($switched) { 'OK' } else { "FAILED (label '$ImagingTabLabel' not found)" })"

# --- 3. Global sweep: find the plugin's 'Clear Data' button -----------------
$buttonCond = New-TypeCondition ([System.Windows.Automation.ControlType]::Button)
$clearBtn = Find-WithRetry {
    $nameCond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Clear Data')
    $window.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition($buttonCond, $nameCond)))
} "plugin button 'Clear Data'"

if (-not $clearBtn) {
    # Diagnostic context: how many buttons does UIA see at all, and which names?
    $allButtons = @($window.FindAll($TS::Descendants, $buttonCond))
    Rep "Buttons visible under the NINA window: $($allButtons.Count)"
    $named = @($allButtons | Where-Object { $_.Current.Name } | Select-Object -First 60)
    foreach ($b in $named) { RepFile ("  button: " + (Describe-Element $b)) }
    Rep "OUTCOME: B/C - plugin panel not reachable via managed UIA2 (window/tabs are)."
    Rep "Next step: retry with FlaUI (UIA3); if that also fails, coordinate fallback."
    $reportLines | Out-File -FilePath $OutFile -Encoding utf8
    exit 1
}
$outcomeFlags.ButtonsFound = $true
Rep ("'Clear Data' button FOUND: " + (Describe-Element $clearBtn))

# --- 4. Plugin panel root = nearest ScrollPattern-capable ancestor ----------
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$panelScroll = $null
$node = $clearBtn
for ($depth = 0; $depth -lt 25 -and $node; $depth++) {
    if ($node.GetSupportedPatterns() -contains [System.Windows.Automation.ScrollPattern]::Pattern) {
        $panelScroll = $node
        break
    }
    $node = $walker.GetParent($node)
}
if ($panelScroll) {
    Rep ("Panel ScrollViewer (ancestor depth $depth): " + (Describe-Element $panelScroll))
} else {
    Rep 'Panel ScrollViewer: no ScrollPattern-capable ancestor found'
}
# Scope all further searches to the panel; fall back to the window
$panelRoot = if ($panelScroll) { $panelScroll } else { $window }

# --- 5. Panel-scoped element dumps ------------------------------------------
foreach ($probe in @(
        @{ Label = 'Button';   Type = [System.Windows.Automation.ControlType]::Button },
        @{ Label = 'CheckBox'; Type = [System.Windows.Automation.ControlType]::CheckBox },
        @{ Label = 'ComboBox'; Type = [System.Windows.Automation.ControlType]::ComboBox },
        @{ Label = 'Edit';     Type = [System.Windows.Automation.ControlType]::Edit },
        @{ Label = 'Text';     Type = [System.Windows.Automation.ControlType]::Text }
    )) {
    $found = @($panelRoot.FindAll($TS::Descendants, (New-TypeCondition $probe.Type)))
    Rep "Panel $($probe.Label) elements: $($found.Count)"
    foreach ($el in $found) { RepFile ("  $($probe.Label): " + (Describe-Element $el)) }
}

# Do TextBlocks composed of <Run> elements report their aggregated text?
$panelTexts = @($panelRoot.FindAll($TS::Descendants, $textCond))
$dithersText = $panelTexts | Where-Object { $_.Current.Name -match '^Dithers:' } | Select-Object -First 1
if ($dithersText) {
    $outcomeFlags.TextReadable = $true
    Rep ("Run-composed TextBlock readable: '" + $dithersText.Current.Name + "'")
} else {
    Rep "Run-composed 'Dithers:' TextBlock not found (no data yet, or Runs not aggregated into Name)"
    # Weaker evidence: any plugin-owned label at all?
    $anyLabel = $panelTexts | Where-Object { $_.Current.Name -match 'Keep across sessions|Profiles|Settle|Drift' } | Select-Object -First 1
    if ($anyLabel) {
        $outcomeFlags.TextReadable = $true
        Rep ("Plugin label readable instead: '" + $anyLabel.Current.Name + "'")
    }
}

# --- 6. Interaction probes (state-restoring) --------------------------------

# 6a. InvokePattern availability (do NOT invoke - Clear Data is destructive)
$outcomeFlags.InvokeAvail = $clearBtn.GetSupportedPatterns() -contains [System.Windows.Automation.InvokePattern]::Pattern
Rep "InvokePattern on 'Clear Data': $(if ($outcomeFlags.InvokeAvail) { 'available' } else { 'MISSING' })"

# 6b. TogglePattern: double-flip the Quality Assessment toggle and restore.
# CheckBoxes carry no Name (labels are separate TextBlocks), so pair each
# CheckBox with the horizontally nearest Text element on the same row.
$panelChecks = @($panelRoot.FindAll($TS::Descendants, (New-TypeCondition ([System.Windows.Automation.ControlType]::CheckBox))))
$qualityToggle = $null
foreach ($cb in $panelChecks) {
    $cbRect = $cb.Current.BoundingRectangle
    $rowTexts = $panelTexts | Where-Object {
        $tr = $_.Current.BoundingRectangle
        [math]::Abs(($tr.Y + $tr.Height / 2) - ($cbRect.Y + $cbRect.Height / 2)) -lt ($cbRect.Height * 0.9)
    }
    $rowLabel = ($rowTexts | ForEach-Object { $_.Current.Name }) -join ' | '
    RepFile ("  CheckBox row pairing: rect=({0:F0},{1:F0}) labels: {2}" -f $cbRect.X, $cbRect.Y, $rowLabel)
    if (-not $qualityToggle -and $rowLabel -match 'Quality') { $qualityToggle = $cb }
}
if ($qualityToggle) {
    try {
        $tp = $qualityToggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $before = $tp.Current.ToggleState
        $tp.Toggle(); Start-Sleep -Milliseconds 500
        $mid = $tp.Current.ToggleState
        $tp.Toggle(); Start-Sleep -Milliseconds 500
        $after = $tp.Current.ToggleState
        $outcomeFlags.ToggleWorks = ($mid -ne $before) -and ($after -eq $before)
        Rep "TogglePattern double-flip on Quality toggle: $before -> $mid -> $after ($(if ($outcomeFlags.ToggleWorks) { 'WORKS, state restored' } else { 'UNEXPECTED sequence' }))"
    } catch {
        Rep "TogglePattern probe failed: $($_.Exception.Message)"
    }
} else {
    Rep 'Quality toggle not identified by row label - TogglePattern flip probe skipped'
}

# 6c. ValuePattern on the profile ComboBox's editable text (set + restore).
# Only visible when multi-profile is enabled; skip gracefully otherwise.
$panelCombo = $panelRoot.FindFirst($TS::Descendants, (New-TypeCondition ([System.Windows.Automation.ControlType]::ComboBox)))
if ($panelCombo) {
    Rep ("Profile ComboBox: " + (Describe-Element $panelCombo))
    $editChild = $panelCombo.FindFirst($TS::Descendants, (New-TypeCondition ([System.Windows.Automation.ControlType]::Edit)))
    if ($editChild) {
        try {
            $vp = $editChild.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $orig = $vp.Current.Value
            $vp.SetValue('UiaSpikeProbe'); Start-Sleep -Milliseconds 500
            $set = $vp.Current.Value
            $vp.SetValue($orig); Start-Sleep -Milliseconds 300
            $restored = $vp.Current.Value
            $outcomeFlags.ComboValue = ($set -eq 'UiaSpikeProbe') -and ($restored -eq $orig)
            Rep "ValuePattern on ComboBox edit: '$orig' -> '$set' -> '$restored' ($(if ($outcomeFlags.ComboValue) { 'WORKS, restored' } else { 'UNEXPECTED' }))"
        } catch {
            Rep "ValuePattern probe failed: $($_.Exception.Message)"
        }
    } else {
        Rep 'ComboBox has no Edit child visible to UIA (PART_EditableTextBox not exposed)'
    }
} else {
    Rep 'Profile ComboBox not found (multi-profile disabled? probe skipped)'
}

# 6d. ScrollPattern on the panel ScrollViewer (scroll to bottom, restore)
if ($panelScroll) {
    try {
        $sp = $panelScroll.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        $origV = $sp.Current.VerticalScrollPercent
        $noH = [System.Windows.Automation.ScrollPattern]::NoScroll
        $sp.SetScrollPercent($noH, 100); Start-Sleep -Milliseconds 500
        $atBottom = $sp.Current.VerticalScrollPercent
        $restoreTo = if ($origV -lt 0) { 0 } else { $origV }
        $sp.SetScrollPercent($noH, $restoreTo); Start-Sleep -Milliseconds 300
        $outcomeFlags.ScrollWorks = ($atBottom -gt 90)
        Rep ("ScrollPattern: {0:F0}% -> set 100 -> {1:F0}% -> restored ({2})" -f $origV, $atBottom, $(if ($outcomeFlags.ScrollWorks) { 'WORKS' } else { 'DID NOT MOVE' }))
    } catch {
        Rep "ScrollPattern probe failed: $($_.Exception.Message)"
    }
}

# --- 7. Verdict --------------------------------------------------------------
Rep '--- Flag summary ---'
foreach ($k in $outcomeFlags.Keys | Sort-Object) { Rep ("  {0}: {1}" -f $k, $outcomeFlags[$k]) }

$core = $outcomeFlags.ButtonsFound -and $outcomeFlags.TextReadable -and $outcomeFlags.InvokeAvail
$interactions = $outcomeFlags.ToggleWorks -and $outcomeFlags.ScrollWorks
if ($core -and $interactions) {
    Rep 'OUTCOME: A - managed System.Windows.Automation (UIA2) fully drives the plugin panel. Build NinaUia.ps1 on it; no external dependencies needed.'
    $exit = 0
} elseif ($core) {
    Rep 'OUTCOME: A(partial) - elements and text visible, but some interaction pattern probes failed or were skipped. Review flags above before committing.'
    $exit = 0
} else {
    Rep 'OUTCOME: B/C - see flags; try FlaUI (UIA3) next, coordinate fallback last.'
    $exit = 1
}

$reportLines | Out-File -FilePath $OutFile -Encoding utf8
Rep "Report written: $OutFile"
exit $exit
