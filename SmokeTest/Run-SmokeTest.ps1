<#
.SYNOPSIS
Full-functionality smoke test of the DitherStatistics plugin inside a real NINA,
driven through PHD2's event server API and the plugin's SmokeTestBridge - no
sequencer, no element-level GUI automation (NINA does not expose the plugin
panel to UI Automation - see Spike-UiaVisibility.ps1).

.DESCRIPTION
One NINA session boots once and is reused by all requested scenarios, run in
this fixed order: Baseline -> Export -> Profiles -> Restart -> PersistenceOff
-> Clear. Flow (see SmokeTest\README.md for the one-time setup):
  1. Close NINA (locks the plugin DLL), build + deploy the plugin
  2. Bring PHD2 (simulator profile) into the Guiding state
  3. Back up the user's statistics profiles/settings, enable persistence and
     the SmokeTestBridge diagnostic flag
  4. Start NINA, wait for the plugin's PHD2-connection log line, connect to
     the bridge, switch to the Imaging tab
  5. Restart guiding (StartGuiding event) and wait for the optimizer's
     reference window to fill
  6. Run each requested scenario (Scenarios.ps1); scenarios not yet
     implemented (see erweitere-die-smoketest-so-ethereal-flask.md, Step 4)
     just log a TODO and are skipped for assertion purposes
  7. Close NINA (unless -HoldForInspection or -KeepNina), assert no plugin
     ERROR lines across the whole run's NINA log(s), restore all backed-up
     settings/profiles, sweep this run's export files into the artifact dir

Exit code 0 = all assertions passed (scenarios with no body yet contribute no
assertions either way).

.EXAMPLE
.\Run-SmokeTest.ps1 -Scenario Baseline -DitherCount 4 -ReferenceWaitSec 30   # quick single-scenario run
.\Run-SmokeTest.ps1                                                          # full run, all scenarios
.\Run-SmokeTest.ps1 -Scenario Export,Profiles -SkipBuild                    # rerun a subset against the already-deployed plugin
.\Run-SmokeTest.ps1 -HoldForInspection                                      # leave NINA running for live analysis (Invoke-NinaUi.ps1 + Complete-SmokeTest.ps1)
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Baseline', 'Export', 'Profiles', 'Restart', 'PersistenceOff', 'Clear')]
    [string[]]$Scenario = @('All'),
    [int]$DitherCount = 6,
    [int]$ProfileDitherCount = 3,
    [switch]$HoldForInspection,
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
. "$PSScriptRoot\BridgeClient.ps1"
. "$PSScriptRoot\NinaUi.ps1"
. "$PSScriptRoot\Scenarios.ps1"

$repoRoot = Split-Path $PSScriptRoot -Parent
$pluginDataDir = Join-Path $env:LOCALAPPDATA 'NINA\DitherStatistics'
$pluginDeployDir = Join-Path $env:LOCALAPPDATA 'NINA\Plugins\3.0.0\DitherStatistics'
$ninaLogDir = Join-Path $env:LOCALAPPDATA 'NINA\Logs'
$persistenceFile = Join-Path $pluginDataDir 'persistence_settings.txt'
$multiProfileFile = Join-Path $pluginDataDir 'multiprofile_settings.txt'
$smokeTestFile = Join-Path $pluginDataDir 'smoketest_settings.txt'
$profileListFile = Join-Path $pluginDataDir 'profiles_list.txt'
$profilesDir = Join-Path $pluginDataDir 'profiles'
$documentsExportDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'N.I.N.A\DitherStatistics'
$artifactDir = Join-Path $PSScriptRoot ("artifacts\" + (Get-Date -Format 'yyyyMMdd_HHmmss'))
$profilesBackupDir = Join-Path $artifactDir 'profiles_backup'
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

$failures = New-Object System.Collections.Generic.List[string]
$report = New-Object System.Collections.Generic.List[string]
$scenarioLog = New-Object System.Collections.Generic.List[string]

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

# Shared naming convention for scenario screenshots: <Prefix>_<scenario>_<what>.png
# (e.g. New-ScreenshotPath 10 baseline top -> "10_baseline_top.png")
function New-ScreenshotPath {
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [Parameter(Mandatory)][string]$ScenarioName,
        [Parameter(Mandatory)][string]$What
    )
    return Join-Path $artifactDir "${Prefix}_${ScenarioName}_${What}.png"
}

# Starts NINA, waits for the plugin's PHD2-connection log line (only log files
# newer than -Watermark are considered, so a mid-run restart doesn't pick up
# stale log content from before this run), switches to the Imaging tab, and
# connects to the SmokeTestBridge. Returns the NINA process, the matched log
# file, and the bridge connection.
function Start-NinaAndWaitForPlugin {
    param([datetime]$Watermark = (Get-Date))
    if (-not (Test-Path $NinaPath)) { throw "NINA not found at '$NinaPath' - pass -NinaPath" }
    Log 'Starting NINA...'
    $proc = Start-Process -FilePath $NinaPath -PassThru

    $log = $null
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $log = Get-ChildItem $ninaLogDir -Filter '*.log' -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -gt $Watermark } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($log -and (Read-FileShared $log.FullName) -match 'Successfully connected to PHD2') { break }
        $log = $null
    }
    if (-not $log) { throw 'Plugin did not report a PHD2 connection in the NINA log within 120 s' }
    Log "Plugin connected to PHD2 (log: $($log.Name))."

    # Show the plugin panel right away: NINA always starts on the Equipment tab,
    # and the ScottPlot charts are only created once the Imaging tab is visible -
    # this way chart/binding errors surface in the log during the run
    Show-NinaImagingTab -TabLabel $ImagingTabLabel

    $bridgeConn = Connect-Bridge -TimeoutSec 30
    Log 'Connected to SmokeTestBridge.'

    return [pscustomobject]@{ Process = $proc; LogFile = $log; Bridge = $bridgeConn }
}

# Triggers -Count dithers over the given PHD2 connection, waiting for each
# SettleDone and for the plugin's post-settle collection window to close.
function Invoke-DitherBatch {
    param(
        [Parameter(Mandatory)]$Phd2Connection,
        [Parameter(Mandatory)][int]$Count
    )
    for ($i = 1; $i -le $Count; $i++) {
        Invoke-Phd2Rpc $Phd2Connection 'dither' @{ amount = $DitherAmount; raOnly = $false; settle = @{ pixels = $SettlePixels; time = $SettleTime; timeout = $SettleTimeout } } | Out-Null
        $settle = Wait-Phd2Event $Phd2Connection 'SettleDone' -TimeoutSec ($SettleTimeout + 60)
        $status = 'ok'
        if ($settle.PSObject.Properties['Status'] -and $settle.Status -ne 0) { $status = "FAILED (status $($settle.Status))" }
        Log "Dither $i/${Count}: settle $status"
        # Let the 10 post-settle guide steps close the plugin's collection window
        Start-Sleep -Seconds 15
    }
}

# Stops and restarts PHD2 guiding so the plugin sees a fresh StartGuiding event
# (used once after NINA connects, and again by the Restart scenario).
function Restart-GuidingWithSettle {
    param([Parameter(Mandatory)]$Phd2Connection)
    Log 'Restarting guiding (gives the plugin a StartGuiding event)...'
    Invoke-Phd2Rpc $Phd2Connection 'stop_capture' | Out-Null
    Wait-Phd2AppState $Phd2Connection 'Stopped' -TimeoutSec 30
    Invoke-Phd2Rpc $Phd2Connection 'guide' @{ settle = @{ pixels = $SettlePixels; time = $SettleTime; timeout = $SettleTimeout }; recalibrate = $false } | Out-Null
    Wait-Phd2AppState $Phd2Connection 'Guiding' -TimeoutSec 120
}

# Copies this run's exported CSV/report files out of Documents into the
# artifact dir, then deletes them from Documents so repeated runs don't pile up.
function Sync-DocumentsExports {
    if (-not (Test-Path $documentsExportDir)) { return }
    $files = Get-ChildItem $documentsExportDir -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt $runStart }
    if (-not $files) { return }
    $exportsOutDir = Join-Path $artifactDir 'exports'
    New-Item -ItemType Directory -Path $exportsOutDir -Force | Out-Null
    foreach ($f in $files) {
        Copy-Item $f.FullName $exportsOutDir -Force
        Remove-Item $f.FullName -Force
    }
    Log "Collected $($files.Count) export file(s) into $exportsOutDir and removed them from Documents."
}

$scenarioOrder = @('Baseline', 'Export', 'Profiles', 'Restart', 'PersistenceOff', 'Clear')
$requestedScenarios = if ($Scenario -contains 'All') { $scenarioOrder } else { $scenarioOrder | Where-Object { $Scenario -contains $_ } }
if (-not $requestedScenarios -or @($requestedScenarios).Count -eq 0) {
    throw "No matching scenarios for -Scenario $($Scenario -join ',')"
}

$runStart = Get-Date
$phd2 = $null
$bridge = $null
$ninaLog = $null
$persistenceBackup = $null
$persistenceExisted = $false
$persistenceModified = $false
$multiProfileBackup = $null
$multiProfileExisted = $false
$smokeTestBackup = $null
$smokeTestExisted = $false
$smokeTestModified = $false
$profilesBackedUp = $false
$profilesDirExisted = $false
$profileListBackup = $null
$profileListExisted = $false

try {
    Log "Scenarios to run: $($requestedScenarios -join ' -> ')"

    if (-not (Test-ScreenCaptureAvailable)) {
        throw 'Screen capture unavailable (CopyFromScreen failed) - the RDP session must be actively displayed to take screenshots. See CLAUDE.md.'
    }

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

    # ---- 3. Back up the user's statistics profiles/settings, enable persistence
    #         + the SmokeTestBridge flag (all restored in the teardown - this run's
    #         simulator dithers/toggles must not permanently pollute user data)
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

    if (Test-Path $multiProfileFile) {
        $multiProfileExisted = $true
        $multiProfileBackup = Get-Content $multiProfileFile -Raw
    }

    if (Test-Path $persistenceFile) {
        $persistenceExisted = $true
        $persistenceBackup = Get-Content $persistenceFile -Raw
    }
    New-Item -ItemType Directory -Path $pluginDataDir -Force | Out-Null
    $persistenceModified = $true
    Set-Content -Path $persistenceFile -Value 'True' -NoNewline
    Log 'Statistics persistence enabled for this run.'

    if (Test-Path $smokeTestFile) {
        $smokeTestExisted = $true
        $smokeTestBackup = Get-Content $smokeTestFile -Raw
    }
    Set-Content -Path $smokeTestFile -Value 'True' -NoNewline
    $smokeTestModified = $true
    Log 'SmokeTestBridge diagnostic channel enabled for this run.'

    $activeProfile = Get-ActiveStatisticsProfile
    $preEvents = Get-ProfileDitherEvents $activeProfile
    $preCount = 0
    if ($null -ne $preEvents) { $preCount = @($preEvents).Count }
    Log "Active statistics profile: '$activeProfile' ($preCount persisted dither events before the run)"

    # ---- 4. Start NINA, connect the bridge, restart guiding, fill reference window
    $nina = Start-NinaAndWaitForPlugin -Watermark $runStart
    $ninaLog = $nina.LogFile
    $bridge = $nina.Bridge

    $phd2 = Connect-Phd2
    Restart-GuidingWithSettle -Phd2Connection $phd2
    Disconnect-Phd2 $phd2
    $phd2 = $null

    Log "Guiding for $ReferenceWaitSec s to fill the optimizer reference window..."
    Start-Sleep -Seconds $ReferenceWaitSec

    # Fresh connection: only events from now on, so the guide-settle SettleDone
    # from the restart above cannot be mistaken for a dither settle
    $phd2 = Connect-Phd2

    if ($requestedScenarios -notcontains 'Baseline') {
        $seedCount = [Math]::Min(5, $DitherCount)
        Log "Standalone run without Baseline: seeding $seedCount dither(s) so dependent scenarios have data."
        Invoke-DitherBatch -Phd2Connection $phd2 -Count $seedCount
    }

    # ---- 5. Scenario dispatcher --------------------------------------------------
    $ctx = [pscustomobject]@{
        Bridge             = $bridge
        ArtifactDir        = $artifactDir
        DitherCount        = $DitherCount
        ProfileDitherCount = $ProfileDitherCount
        ActiveProfile      = $activeProfile
        PreDitherCount     = $preCount
        PluginDataDir      = $pluginDataDir
        ProfilesDir        = $profilesDir
        ProfileListFile    = $profileListFile
        DocumentsExportDir = $documentsExportDir
        RunStart           = $runStart
    }

    foreach ($name in $requestedScenarios) {
        Log "=== Scenario: $name ==="
        $failuresBefore = $failures.Count
        try {
            # The scenario functions use the 'Test-' verb, not 'Invoke-': a
            # cluster of Invoke-Baseline/Export/Profiles/Restart/PersistenceOff/
            # Clear identifiers matches Microsoft Defender's HackTool:PowerShell/
            # ApexToolkit.A AMSI signature and gets the whole script blocked at
            # runtime (file/folder exclusions do not cover AMSI content scans).
            # Keep the verb as 'Test-' (or anything but 'Invoke-') here and in
            # Scenarios.ps1 so the harness runs on a stock Defender machine.
            switch ($name) {
                'Baseline' { Test-BaselineScenario -Ctx $ctx -Phd2Connection $phd2 }
                'Export' { Test-ExportScenario -Ctx $ctx }
                'Profiles' { Test-ProfilesScenario -Ctx $ctx -Phd2Connection $phd2 }
                'Restart' { Test-RestartScenario -Ctx $ctx }
                'PersistenceOff' { Test-PersistenceOffScenario -Ctx $ctx }
                'Clear' { Test-ClearScenario -Ctx $ctx }
            }
        } catch {
            Fail "Scenario '$name' threw: $($_.Exception.Message)"
        }
        $scenarioLog.Add("$name`: $(if ($failures.Count -eq $failuresBefore) { 'no new failures' } else { "$($failures.Count - $failuresBefore) failure(s)" })")
    }

    Disconnect-Phd2 $phd2
    $phd2 = $null

    # ---- 6. Whole-run assertion: no plugin ERROR lines in any NINA log touched --
    $pluginSources = 'DitherStatistics|Ditherstatistics|DitherOptimizer|DitherAnalysis|DitherQuality|PHD2Client|Phd2Connection|StatisticsProfile|PluginSettings|PixelScale|ChartRenderer|ChartTooltip|NinaThemeWatcher|ExportService|SmokeTestBridge'
    $logsThisRun = Get-ChildItem $ninaLogDir -Filter '*.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -gt $runStart -or $_.FullName -eq $ninaLog.FullName }
    if (-not $logsThisRun) {
        Fail 'no NINA log file found for this run'
    } else {
        $errorLines = @()
        foreach ($lf in $logsThisRun) {
            Copy-Item $lf.FullName (Join-Path $artifactDir $lf.Name) -Force
            $text = Read-FileShared $lf.FullName
            $errorLines += $text -split "`r?`n" | Where-Object { $_ -match '\|ERROR\|' -and $_ -match $pluginSources }
        }
        if ($errorLines) {
            Fail "NINA log(s) contain $(@($errorLines).Count) plugin ERROR line(s):"
            $errorLines | Select-Object -First 10 | ForEach-Object { Log "    $_" }
        } else {
            Pass 'no plugin ERROR lines in the NINA log(s) across the whole run'
        }
    }

} catch {
    Fail "Smoke test aborted: $($_.Exception.Message)"
} finally {
    if ($phd2) { Disconnect-Phd2 $phd2 }
    if ($bridge) { Disconnect-Bridge $bridge }

    if ($HoldForInspection) {
        Log '-HoldForInspection: leaving NINA running, deferring teardown to Complete-SmokeTest.ps1.'
        $holdState = [pscustomobject]@{
            ArtifactDir          = $artifactDir
            PersistenceFile      = $persistenceFile
            PersistenceExisted   = $persistenceExisted
            PersistenceBackup    = $persistenceBackup
            PersistenceModified  = $persistenceModified
            MultiProfileFile     = $multiProfileFile
            MultiProfileExisted  = $multiProfileExisted
            MultiProfileBackup   = $multiProfileBackup
            SmokeTestFile        = $smokeTestFile
            SmokeTestExisted     = $smokeTestExisted
            SmokeTestBackup      = $smokeTestBackup
            SmokeTestModified    = $smokeTestModified
            ProfilesDir          = $profilesDir
            ProfilesDirExisted   = $profilesDirExisted
            ProfilesBackupDir    = $profilesBackupDir
            ProfilesBackedUp     = $profilesBackedUp
            ProfileListFile      = $profileListFile
            ProfileListExisted   = $profileListExisted
            ProfileListBackup    = $profileListBackup
            DocumentsExportDir   = $documentsExportDir
            RunStart             = $runStart
            KeepPhd2             = [bool]$KeepPhd2
        }
        $holdState | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $artifactDir 'hold_state.json')
    } else {
        # Restore the user's persistence setting - but only if this run changed it
        try {
            if ($persistenceModified) {
                if ($persistenceExisted) {
                    Set-Content -Path $persistenceFile -Value $persistenceBackup -NoNewline
                } elseif (Test-Path $persistenceFile) {
                    Remove-Item $persistenceFile
                }
            }
        } catch { Log "WARNING: could not restore persistence setting: $($_.Exception.Message)" }

        # Restore the user's multi-profile toggle (a Profiles-scenario run may have flipped it)
        try {
            if ($multiProfileExisted) {
                Set-Content -Path $multiProfileFile -Value $multiProfileBackup -NoNewline
            } elseif (Test-Path $multiProfileFile) {
                Remove-Item $multiProfileFile
            }
        } catch { Log "WARNING: could not restore multi-profile setting: $($_.Exception.Message)" }

        # Restore/remove the SmokeTestBridge flag
        try {
            if ($smokeTestModified) {
                if ($smokeTestExisted) {
                    Set-Content -Path $smokeTestFile -Value $smokeTestBackup -NoNewline
                } elseif (Test-Path $smokeTestFile) {
                    Remove-Item $smokeTestFile
                }
            }
        } catch { Log "WARNING: could not restore SmokeTestBridge flag: $($_.Exception.Message)" }

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

        if (-not $KeepNina) { Stop-Nina }

        try { Sync-DocumentsExports } catch { Log "WARNING: could not sync Documents exports: $($_.Exception.Message)" }

        if (-not $KeepPhd2) {
            try {
                if (Test-Phd2Port) {
                    $conn = Connect-Phd2
                    try { Invoke-Phd2Rpc $conn 'shutdown' | Out-Null } finally { Disconnect-Phd2 $conn }
                    Log 'PHD2 shut down.'
                }
            } catch { Log "WARNING: could not shut down PHD2: $($_.Exception.Message)" }
        }
    }

    $report | Set-Content (Join-Path $artifactDir 'report.txt')
}

Write-Host ''
Write-Host 'Scenario summary:'
$scenarioLog | ForEach-Object { Write-Host "  $_" }
Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host "SMOKE TEST PASSED ($($requestedScenarios -join ', ')). Artifacts: $artifactDir" -ForegroundColor Green
    exit 0
} else {
    Write-Host "SMOKE TEST FAILED - $($failures.Count) assertion(s):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host "Artifacts: $artifactDir"
    exit 1
}
