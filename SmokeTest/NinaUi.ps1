# NinaUi.ps1 - dot-sourceable window/tab/scroll/screenshot helpers for driving
# the live NINA window during the smoke test. Extracted from Run-SmokeTest.ps1
# so Scenarios.ps1 and Invoke-NinaUi.ps1 can share them.
#
# NINA exposes no tab *content* to UI Automation (UIA2 and UIA3 both truncate
# at the main-tab/status-bar level - see Spike-UiaVisibility.ps1 and its
# recorded outcome). That means: no element-level clicking/reading of the
# plugin panel is possible from outside the process - that is what
# SmokeTestBridge.cs / BridgeClient.ps1 exist for. What UI Automation *can*
# still do is select the top-level tab (TabItem/SelectionItemPattern) and
# report the window's bounding rectangle, which is all the functions below
# use it for; interaction with panel content is blind mouse-wheel scrolling
# aimed at a text-only region, and whole-window screenshots.
#
# Usage:
#   . "$PSScriptRoot\NinaUi.ps1"
#   if (-not (Test-ScreenCaptureAvailable)) { throw 'No active display' }
#   Show-NinaImagingTab
#   Invoke-PanelScroll -Direction Down -Notches 30
#   Save-Screenshot 'C:\...\out.png'

Set-StrictMode -Version Latest

# Resolves the live NINA process + its top-level AutomationElement window, or
# $null if NINA isn't running / the window can't be found. Shared lookup for
# Show-NinaImagingTab and Invoke-PanelScroll so both act on the same window rect.
function Get-NinaWindow {
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $nina = Get-Process -Name 'NINA' -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $nina) { return $null }
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $procCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $nina.Id)
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $procCond)
        if (-not $window) { return $null }
        return [pscustomobject]@{ Process = $nina; Element = $window }
    } catch {
        return $null
    }
}

# Brings the NINA main window to the foreground so a screenshot/scroll actually
# targets it (and not whatever window currently has focus).
function Show-NinaWindow {
    try {
        $nina = Get-Process -Name 'NINA' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($nina) {
            (New-Object -ComObject WScript.Shell).AppActivate($nina.Id) | Out-Null
            Start-Sleep -Seconds 2
        }
    } catch { }
}

# Switches NINA to the Imaging tab via UI Automation - NINA always starts on
# the Equipment tab, but the plugin panel (and its lazily created charts)
# lives on the Imaging tab. The nav entries are WPF TabItems whose label is a
# Text child.
function Show-NinaImagingTab {
    param([string]$TabLabel = 'Imaging')
    $win = Get-NinaWindow
    if (-not $win) { return }
    try {
        $tabCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
        $textCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
        foreach ($item in $win.Element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)) {
            $labels = @($item.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond) | ForEach-Object { $_.Current.Name })
            if ($labels -contains $TabLabel) {
                $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                Start-Sleep -Seconds 2   # let the tab and the lazy-loaded charts render
                return
            }
        }
        Write-Warning "NINA tab '$TabLabel' not found via UI Automation (localized NINA? pass -TabLabel)"
    } catch {
        Write-Warning "could not switch NINA to the '$TabLabel' tab ($($_.Exception.Message))"
    }
}

# Sends mouse-wheel events to a text-only region of the plugin panel (45%
# window width / 72% height - proven safe spot: everything that slides under
# the cursor there is text, never a ScottPlot chart, which would zoom instead
# of scroll). Positive wheel delta (Up) scrolls the panel content up (toward
# the charts); negative delta (Down) scrolls toward the lower sections
# (quality metrics, Dither Settings Optimizer, Actions).
function Invoke-PanelScroll {
    param(
        [ValidateSet('Down', 'Up')][string]$Direction = 'Down',
        [int]$Notches = 30
    )
    $win = Get-NinaWindow
    if (-not $win) { return }
    try {
        if (-not ('SmokeTestNative.User32' -as [type])) {
            Add-Type -Namespace SmokeTestNative -Name User32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, int dwData, UIntPtr dwExtraInfo);
'@
        }
        $r = $win.Element.Current.BoundingRectangle
        [SmokeTestNative.User32]::SetCursorPos([int]($r.X + $r.Width * 0.45), [int]($r.Y + $r.Height * 0.72)) | Out-Null
        Start-Sleep -Milliseconds 300
        $wheelDelta = if ($Direction -eq 'Up') { 120 } else { -120 }
        for ($n = 0; $n -lt $Notches; $n++) {
            [SmokeTestNative.User32]::mouse_event(0x0800, 0, 0, $wheelDelta, [UIntPtr]::Zero)  # MOUSEEVENTF_WHEEL
            Start-Sleep -Milliseconds 50
        }
        Start-Sleep -Seconds 1
    } catch {
        Write-Warning "could not scroll the plugin panel ($($_.Exception.Message))"
    }
}

# 1x1 px CopyFromScreen probe - screen capture (CopyFromScreen / PrintWindow)
# fails while the RDP session has no actively displayed console (spike
# finding, 2026-07-10). Callers should check this early and fail fast with a
# clear message rather than silently producing blank/failed screenshots deep
# into a run.
function Test-ScreenCaptureAvailable {
    try {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $bmp = New-Object System.Drawing.Bitmap(1, 1)
        $gfx = [System.Drawing.Graphics]::FromImage($bmp)
        $gfx.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
        $gfx.Dispose(); $bmp.Dispose()
        return $true
    } catch {
        return $false
    }
}

function Save-Screenshot {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$TabLabel = 'Imaging'
    )
    Show-NinaWindow
    Show-NinaImagingTab -TabLabel $TabLabel
    try {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
        $gfx = [System.Drawing.Graphics]::FromImage($bmp)
        $gfx.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bmp.Size)
        $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        $gfx.Dispose(); $bmp.Dispose()
        Write-Verbose "Screenshot saved: $Path"
    } catch {
        Write-Warning "screenshot failed ($($_.Exception.Message)) - non-interactive session?"
    }
}
