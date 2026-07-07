<#
.SYNOPSIS
Full smoke test of the DitherStatistics plugin inside a real NINA, driven entirely
through PHD2's event server API - no sequencer, no GUI automation.

.DESCRIPTION
Flow (see SmokeTest\README.md for the one-time setup):
  1. Close NINA (locks the plugin DLL), build + deploy the plugin
  2. Bring PHD2 (simulator profile) into the Guiding state
  3. Enable the plugin's statistics persistence, start NINA, wait until the
     plugin's log line confirms the PHD2 connection
  4. Restart guiding so the plugin sees a StartGuiding event, wait for the
     optimizer reference window to fill
  5. Trigger N dithers via the PHD2 'dither' RPC, waiting for each SettleDone
  6. Take screenshots for visual inspection, close NINA
  7. Assert: no plugin errors in the NINA log, dither_analysis diagnostic file
     written, exactly N new DitherEvents with plausible values in the profile JSON

Exit code 0 = all assertions passed.

.EXAMPLE
.\Run-SmokeTest.ps1 -DitherCount 5 -ReferenceWaitSec 30   # quick run
.\Run-SmokeTest.ps1                                       # full run, 20 dithers
#>
[CmdletBinding()]
param(
    [int]$DitherCount = 20,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$NinaPath = "$env:ProgramFiles\N.I.N.A. - Nighttime Imaging 'N' Astronomy\NINA.exe",
    [string]$Phd2Path = "${env:ProgramFiles(x86)}\PHDGuiding2\phd2.exe",
    [string]$Phd2ProfileName = 'Simulator',
    # Guiding time before the first dither so the optimizer's reference window
    # (min. 20 stable guide steps) is meaningful
    [int]$ReferenceWaitSec = 60,
    [double]$DitherAmount = 3.0,
    [double]$SettlePixels = 1.5,
    [int]$SettleTime = 8,
    [int]$SettleTimeout = 60,
    # Label of NINA's Imaging tab (adjust for localized NINA installations)
    [string]$ImagingTabLabel = 'Imaging',
    [switch]$SkipBuild,
    [switch]$KeepPhd2,
    [switch]$KeepNina
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Phd2Rpc.ps1"

$repoRoot = Split-Path $PSScriptRoot -Parent
$pluginDataDir = Join-Path $env:LOCALAPPDATA 'NINA\DitherStatistics'
$pluginDeployDir = Join-Path $env:LOCALAPPDATA 'NINA\Plugins\3.0.0\DitherStatistics'
$ninaLogDir = Join-Path $env:LOCALAPPDATA 'NINA\Logs'
$persistenceFile = Join-Path $pluginDataDir 'persistence_settings.txt'
$profileListFile = Join-Path $pluginDataDir 'profiles_list.txt'
$profilesDir = Join-Path $pluginDataDir 'profiles'
$artifactDir = Join-Path $PSScriptRoot ("artifacts\" + (Get-Date -Format 'yyyyMMdd_HHmmss'))
$profilesBackupDir = Join-Path $artifactDir 'profiles_backup'
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

$failures = New-Object System.Collections.Generic.List[string]
$report = New-Object System.Collections.Generic.List[string]

function Log([string]$Message) {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $Message
    Write-Host $line
    $report.Add($line)
}

function Fail([string]$Message) {
    $failures.Add($Message)
    Log "ASSERTION FAILED: $Message"
}

function Pass([string]$Message) {
    Log "OK: $Message"
}

# Read a file NINA may still hold open for writing
function Read-FileShared([string]$Path) {
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object System.IO.StreamReader($fs)
        return $reader.ReadToEnd()
    } finally {
        $fs.Dispose()
    }
}

function Stop-Nina {
    $procs = Get-Process -Name 'NINA' -ErrorAction SilentlyContinue
    if (-not $procs) { return }
    Log 'Closing NINA...'
    foreach ($p in $procs) { $p.CloseMainWindow() | Out-Null }
    if (-not ($procs | Wait-Process -Timeout 60 -ErrorAction SilentlyContinue)) {
        $procs | Where-Object { -not $_.HasExited } | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
    Log 'NINA closed.'
}

# Switch NINA to the Imaging tab via UI Automation - NINA always starts on the
# Equipment tab, but the plugin panel (and its lazily created charts) lives in the
# Imaging tab. The nav entries are WPF TabItems whose label is a Text child.
function Show-NinaImagingTab {
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $nina = Get-Process -Name 'NINA' -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $nina) { return }
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $procCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $nina.Id)
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $procCond)
        if (-not $window) { return }
        $tabCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
        $textCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
        foreach ($item in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)) {
            $labels = @($item.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond) | ForEach-Object { $_.Current.Name })
            if ($labels -contains $ImagingTabLabel) {
                $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                Start-Sleep -Seconds 2   # let the tab and the lazy-loaded charts render
                return
            }
        }
        Log "WARNING: NINA tab '$ImagingTabLabel' not found via UI Automation (localized NINA? pass -ImagingTabLabel)"
    } catch {
        Log "WARNING: could not switch NINA to the Imaging tab ($($_.Exception.Message))"
    }
}

# Scroll the plugin panel to the bottom so a second screenshot can capture the
# lower sections (quality metrics, Dither Settings Optimizer, Actions).
# NINA does not expose its WPF tree to UI Automation beyond the main tabs, so
# ScrollPattern is unavailable; instead, wheel events are sent to a text-only
# region of the panel (45% window width / 72% height). Everything that slides
# under the cursor while scrolling DOWN is text - never a ScottPlot chart,
# which would zoom instead of scroll. There is deliberately no scroll-back:
# this runs only right before NINA is closed.
function Invoke-PanelScrollToBottom {
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        if (-not ('SmokeTestNative.User32' -as [type])) {
            Add-Type -Namespace SmokeTestNative -Name User32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, int dwData, UIntPtr dwExtraInfo);
'@
        }
        $nina = Get-Process -Name 'NINA' -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $nina) { return }
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $procCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $nina.Id)
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $procCond)
        if (-not $window) { return }
        $r = $window.Current.BoundingRectangle
        [SmokeTestNative.User32]::SetCursorPos([int]($r.X + $r.Width * 0.45), [int]($r.Y + $r.Height * 0.72)) | Out-Null
        Start-Sleep -Milliseconds 300
        for ($n = 0; $n -lt 30; $n++) {
            [SmokeTestNative.User32]::mouse_event(0x0800, 0, 0, -120, [UIntPtr]::Zero)  # MOUSEEVENTF_WHEEL, one notch down
            Start-Sleep -Milliseconds 50
        }
        Start-Sleep -Seconds 1
    } catch {
        Log "WARNING: could not scroll the plugin panel ($($_.Exception.Message))"
    }
}

# Bring the NINA main window to the foreground so the screenshot shows it
function Show-NinaWindow {
    try {
        $nina = Get-Process -Name 'NINA' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($nina) {
            (New-Object -ComObject WScript.Shell).AppActivate($nina.Id) | Out-Null
            Start-Sleep -Seconds 2
        }
    } catch { }
}

function Save-Screenshot([string]$Path) {
    Show-NinaWindow
    Show-NinaImagingTab
    try {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
        $gfx = [System.Drawing.Graphics]::FromImage($bmp)
        $gfx.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bmp.Size)
        $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        $gfx.Dispose(); $bmp.Dispose()
        Log "Screenshot saved: $Path"
    } catch {
        Log "WARNING: screenshot failed ($($_.Exception.Message)) - non-interactive session?"
    }
}

# Active plugin statistics profile = first line of profiles_list.txt (default 'Default')
function Get-ActiveStatisticsProfile {
    if (Test-Path $profileListFile) {
        $first = (Get-Content $profileListFile | Where-Object { $_.Trim() } | Select-Object -First 1)
        if ($first) { return $first.Trim() }
    }
    return 'Default'
}

function Get-ProfileDitherEvents([string]$ProfileName) {
    $jsonPath = Join-Path $pluginDataDir ("profiles\" + $ProfileName + '.json')
    if (-not (Test-Path $jsonPath)) { return $null }
    $data = Read-FileShared $jsonPath | ConvertFrom-Json
    if ($data.PSObject.Properties['DitherEvents'] -and $null -ne $data.DitherEvents) {
        return @($data.DitherEvents)
    }
    return @()
}

$runStart = Get-Date
$phd2 = $null
$persistenceBackup = $null
$persistenceExisted = $false
$persistenceModified = $false
$profilesBackedUp = $false
$profilesDirExisted = $false
$profileListBackup = $null
$profileListExisted = $false

try {
    # ---- 1. Close NINA, build + deploy the plugin -------------------------------
    Stop-Nina

    if (-not $SkipBuild) {
        Log "Building solution ($Configuration)..."
        & dotnet build (Join-Path $repoRoot 'DitherStatistics.sln') -c $Configuration --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)" }
        $dll = Join-Path $pluginDeployDir 'ThierryTschanz.NINA.Ditherstatistics.dll'
        if (-not (Test-Path $dll) -or (Get-Item $dll).LastWriteTime -lt $runStart.AddMinutes(-1)) {
            throw "PostBuild deploy did not refresh $dll"
        }
        Log 'Build OK, plugin deployed.'
    } else {
        Log 'Build skipped (-SkipBuild).'
    }

    # ---- 2. PHD2 simulator guiding (in-process call, its throws propagate) -------
    & "$PSScriptRoot\Start-Phd2Guiding.ps1" -Phd2Path $Phd2Path -ProfileName $Phd2ProfileName `
        -SettlePixels $SettlePixels -SettleTime $SettleTime -SettleTimeout $SettleTimeout

    # ---- 3. Back up the user's statistics profiles, enable persistence ----------
    # (both are restored in the teardown - the simulator dithers this run
    # triggers must not permanently pollute the user's real profile data)
    if (Test-Path $profilesDir) {
        $profilesDirExisted = $true
        New-Item -ItemType Directory -Path $profilesBackupDir -Force | Out-Null
        Copy-Item (Join-Path $profilesDir '*.json') $profilesBackupDir -ErrorAction SilentlyContinue
    }
    if (Test-Path $profileListFile) {
        $profileListExisted = $true
        $profileListBackup = Get-Content $profileListFile -Raw
    }
    $profilesBackedUp = $true
    Log 'Backed up statistics profiles for restoration after the run.'

    if (Test-Path $persistenceFile) {
        $persistenceExisted = $true
        $persistenceBackup = Get-Content $persistenceFile -Raw
    }
    New-Item -ItemType Directory -Path $pluginDataDir -Force | Out-Null
    $persistenceModified = $true
    Set-Content -Path $persistenceFile -Value 'True' -NoNewline
    Log 'Statistics persistence enabled for this run.'

    $activeProfile = Get-ActiveStatisticsProfile
    $preEvents = Get-ProfileDitherEvents $activeProfile
    $preCount = 0
    if ($null -ne $preEvents) { $preCount = @($preEvents).Count }
    Log "Active statistics profile: '$activeProfile' ($preCount persisted dither events before the run)"

    if (-not (Test-Path $NinaPath)) { throw "NINA not found at '$NinaPath' - pass -NinaPath" }
    Log 'Starting NINA...'
    $ninaProc = Start-Process -FilePath $NinaPath -PassThru

    $ninaLog = $null
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $ninaLog = Get-ChildItem $ninaLogDir -Filter '*.log' -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -gt $runStart } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($ninaLog -and (Read-FileShared $ninaLog.FullName) -match 'Successfully connected to PHD2') { break }
        $ninaLog = $null
    }
    if (-not $ninaLog) { throw 'Plugin did not report a PHD2 connection in the NINA log within 120 s' }
    Log "Plugin connected to PHD2 (log: $($ninaLog.Name))."

    # Show the plugin panel right away: NINA always starts on the Equipment tab,
    # and the ScottPlot charts are only created once the Imaging tab is visible -
    # this way chart/binding errors surface in the log during the run
    Show-NinaImagingTab

    # ---- 4. Restart guiding so the plugin sees StartGuiding, fill reference window
    $phd2 = Connect-Phd2
    Log 'Restarting guiding (gives the plugin a StartGuiding event)...'
    Invoke-Phd2Rpc $phd2 'stop_capture' | Out-Null
    Wait-Phd2AppState $phd2 'Stopped' -TimeoutSec 30
    Invoke-Phd2Rpc $phd2 'guide' @{ settle = @{ pixels = $SettlePixels; time = $SettleTime; timeout = $SettleTimeout }; recalibrate = $false } | Out-Null
    Wait-Phd2AppState $phd2 'Guiding' -TimeoutSec 120
    Disconnect-Phd2 $phd2
    $phd2 = $null

    Log "Guiding for $ReferenceWaitSec s to fill the optimizer reference window..."
    Start-Sleep -Seconds $ReferenceWaitSec

    # ---- 5. Dither loop ----------------------------------------------------------
    # Fresh connection: only events from now on, so the guide-settle SettleDone
    # from the restart above cannot be mistaken for a dither settle
    $phd2 = Connect-Phd2
    for ($i = 1; $i -le $DitherCount; $i++) {
        Invoke-Phd2Rpc $phd2 'dither' @{ amount = $DitherAmount; raOnly = $false; settle = @{ pixels = $SettlePixels; time = $SettleTime; timeout = $SettleTimeout } } | Out-Null
        $settle = Wait-Phd2Event $phd2 'SettleDone' -TimeoutSec ($SettleTimeout + 60)
        $status = 'ok'
        if ($settle.PSObject.Properties['Status'] -and $settle.Status -ne 0) { $status = "FAILED (status $($settle.Status))" }
        Log "Dither $i/${DitherCount}: settle $status"
        # Let the 10 post-settle guide steps close the plugin's collection window
        Start-Sleep -Seconds 15
        if ($i -eq [Math]::Min(5, $DitherCount)) {
            Save-Screenshot (Join-Path $artifactDir "nina_after_${i}_dithers.png")
        }
    }
    Disconnect-Phd2 $phd2
    $phd2 = $null

    # Two final screenshots: top of the panel (charts) and, after scrolling,
    # the lower sections (quality metrics, optimizer recommendation, actions)
    Save-Screenshot (Join-Path $artifactDir 'nina_final_top.png')
    Show-NinaWindow
    Invoke-PanelScrollToBottom
    Save-Screenshot (Join-Path $artifactDir 'nina_final_bottom.png')

    # ---- 6. Close NINA (flushes the log; data is persisted after every settle) ---
    if (-not $KeepNina) { Stop-Nina }

    # ---- 7. Assertions ------------------------------------------------------------
    Log '--- Assertions ---'

    # 7a. No plugin errors in the NINA log
    $logText = Read-FileShared $ninaLog.FullName
    Copy-Item $ninaLog.FullName (Join-Path $artifactDir 'nina.log')
    # Match ERROR lines whose source is one of the plugin's classes/files
    $pluginSources = 'DitherStatistics|Ditherstatistics|DitherOptimizer|DitherAnalysis|DitherQuality|PHD2Client|Phd2Connection|StatisticsProfile|PluginSettings|PixelScale|ChartRenderer|ChartTooltip|NinaThemeWatcher|ExportService'
    $errorLines = $logText -split "`r?`n" | Where-Object {
        $_ -match '\|ERROR\|' -and $_ -match $pluginSources
    }
    if ($errorLines) {
        Fail "NINA log contains $(@($errorLines).Count) plugin ERROR line(s):"
        $errorLines | Select-Object -First 10 | ForEach-Object { Log "    $_" }
    } else {
        Pass 'no plugin ERROR lines in the NINA log'
    }

    # 7b. Optimizer diagnostic file written during this run with >= DitherCount series
    $analysisFile = Get-ChildItem $pluginDataDir -Filter '*_dither_analysis.txt' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -gt $runStart } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $analysisFile) {
        Fail "no *_dither_analysis.txt written under $pluginDataDir during this run"
    } else {
        Copy-Item $analysisFile.FullName $artifactDir
        $dataLines = Get-Content $analysisFile.FullName | Where-Object { $_ -match '^\d+,' }
        $seriesCount = ($dataLines | ForEach-Object { ($_ -split ',')[0] } | Sort-Object -Unique).Count
        # Restored series from earlier sessions may add to the count, hence >=
        if ($seriesCount -ge $DitherCount) {
            Pass "diagnostic file '$($analysisFile.Name)' contains $seriesCount dither series (expected >= $DitherCount)"
        } else {
            Fail "diagnostic file '$($analysisFile.Name)' contains only $seriesCount dither series (expected >= $DitherCount)"
        }
    }

    # 7c. Profile JSON: exactly DitherCount new events with plausible values
    $postEvents = Get-ProfileDitherEvents $activeProfile
    if ($null -eq $postEvents) {
        Fail "profile data file profiles\$activeProfile.json was not written"
    } else {
        Copy-Item (Join-Path $pluginDataDir "profiles\$activeProfile.json") $artifactDir
        $newCount = @($postEvents).Count - $preCount
        if ($newCount -eq $DitherCount) {
            Pass "profile JSON gained exactly $DitherCount dither events"
        } else {
            Fail "profile JSON gained $newCount dither events (expected $DitherCount)"
        }

        $newEvents = @($postEvents) | Select-Object -Last ([Math]::Max($newCount, 0))
        $implausible = @($newEvents | Where-Object {
            (-not $_.Success) -or
            ($null -eq $_.SettleTime) -or ($_.SettleTime -le 0) -or ($_.SettleTime -gt ($SettleTimeout + 30)) -or
            ($null -eq $_.PixelShiftX) -or ($null -eq $_.PixelShiftY)
        })
        if ($implausible.Count -eq 0) {
            Pass "all new dither events are plausible (Success, 0 < SettleTime <= $($SettleTimeout + 30) s, pixel shift present)"
        } else {
            Fail "$($implausible.Count) new dither event(s) implausible (failed settle, missing/absurd SettleTime or missing pixel shift)"
        }
    }

} catch {
    Fail "Smoke test aborted: $($_.Exception.Message)"
} finally {
    if ($phd2) { Disconnect-Phd2 $phd2 }

    # Restore the user's persistence setting - but only if this run changed it
    # (an abort before the enable step must not touch the user's file)
    try {
        if ($persistenceModified) {
            if ($persistenceExisted) {
                Set-Content -Path $persistenceFile -Value $persistenceBackup -NoNewline
            } elseif (Test-Path $persistenceFile) {
                Remove-Item $persistenceFile
            }
        }
    } catch { Log "WARNING: could not restore persistence setting: $($_.Exception.Message)" }

    # Restore the user's statistics profiles - discards this run's simulator
    # dithers and any profile created/switched-to while the plugin ran
    try {
        if ($profilesBackedUp) {
            if (Test-Path $profilesDir) { Remove-Item $profilesDir -Recurse -Force }
            if ($profilesDirExisted) {
                New-Item -ItemType Directory -Path $profilesDir -Force | Out-Null
                if (Test-Path $profilesBackupDir) {
                    Copy-Item (Join-Path $profilesBackupDir '*.json') $profilesDir -ErrorAction SilentlyContinue
                }
            }
            if ($profileListExisted) {
                Set-Content -Path $profileListFile -Value $profileListBackup -NoNewline
            } elseif (Test-Path $profileListFile) {
                Remove-Item $profileListFile
            }
            Log 'Restored original statistics profiles.'
        }
    } catch { Log "WARNING: could not restore statistics profiles: $($_.Exception.Message)" }

    if (-not $KeepPhd2) {
        try {
            if (Test-Phd2Port) {
                $conn = Connect-Phd2
                try { Invoke-Phd2Rpc $conn 'shutdown' | Out-Null } finally { Disconnect-Phd2 $conn }
                Log 'PHD2 shut down.'
            }
        } catch { Log "WARNING: could not shut down PHD2: $($_.Exception.Message)" }
    }

    $report | Set-Content (Join-Path $artifactDir 'report.txt')
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host "SMOKE TEST PASSED ($DitherCount dithers). Artifacts: $artifactDir" -ForegroundColor Green
    exit 0
} else {
    Write-Host "SMOKE TEST FAILED - $($failures.Count) assertion(s):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host "Artifacts: $artifactDir"
    exit 1
}
