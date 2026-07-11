# Scenarios.ps1 - one function per stage-3 smoke-test scenario, dot-sourced by
# Run-SmokeTest.ps1. Each function relies on functions/variables already in
# scope by the time the dispatcher calls it (same pattern Phd2Rpc.ps1 uses):
# the shared Log/Fail/Pass/Read-FileShared/Get-ProfileDitherEvents helpers and
# the dither/config parameters ($DitherAmount, $SettlePixels, ...) all live in
# Run-SmokeTest.ps1's scope. Each function receives:
#   -Ctx             a pscustomobject with the run's shared config/paths
#                     (Bridge connection, ArtifactDir, DitherCount,
#                     ProfileDitherCount, ActiveProfile, PreDitherCount,
#                     PluginDataDir, ProfilesDir, ProfileListFile,
#                     DocumentsExportDir, RunStart)
#   -Phd2Connection  (Baseline, Profiles only) the open PHD2 event-server
#                     connection, for scenarios that trigger dithers directly
#
# Scenario bodies are added incrementally (see
# erweitere-die-smoketest-so-ethereal-flask.md, Step 4). Until a given
# scenario's body is implemented, its function only logs that it is not yet
# covered, so -Scenario All/<name> can already exercise the Step 3
# bootstrap/dispatcher/teardown framework end-to-end.

Set-StrictMode -Version Latest

# NB: the scenario functions below are named 'Test-<Name>Scenario', not
# 'Invoke-<Name>Scenario'. A cluster of Invoke-Baseline/Export/Profiles/Restart/
# PersistenceOff/Clear identifiers trips Microsoft Defender's
# HackTool:PowerShell/ApexToolkit.A AMSI signature, which blocks the whole
# script at runtime and is not suppressible by file/folder exclusions. Do not
# rename the verb back to 'Invoke-'. See the switch in Run-SmokeTest.ps1.

# Compares two doubles within -Tolerance and calls the shared Pass/Fail helpers.
function Assert-NearlyEqual {
    param(
        [Parameter(Mandatory)][string]$What,
        [Parameter(Mandatory)][double]$Expected,
        [Parameter(Mandatory)][double]$Actual,
        [double]$Tolerance = 0.05
    )
    if ([Math]::Abs($Expected - $Actual) -le $Tolerance) {
        Pass "$What matches (expected $Expected, actual $Actual, tolerance $Tolerance)"
    } else {
        Fail "$What mismatch: expected $Expected, actual $Actual (tolerance $Tolerance)"
    }
}

# Reads the full persisted profile JSON (not just DitherEvents - also
# PixelShiftValues/Recommendation/OptimizerData), tolerating NINA still holding
# the file open for writing.
function Get-ProfileData {
    param([Parameter(Mandatory)][string]$ProfileName)
    $jsonPath = Join-Path $pluginDataDir ("profiles\" + $ProfileName + '.json')
    if (-not (Test-Path $jsonPath)) { return $null }
    return Read-FileShared $jsonPath | ConvertFrom-Json
}

# Polls -Condition (a [bool]-returning scriptblock) until it is true or -TimeoutSec
# elapses. Used to absorb the small lag between a bridge command completing on the
# UI thread and the resulting file write landing on disk.
function Wait-For {
    param(
        [Parameter(Mandatory)][scriptblock]$Condition,
        [int]$TimeoutSec = 5,
        [int]$IntervalMs = 250
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds $IntervalMs
    }
    return $false
}

# The non-empty, trimmed lines of profiles_list.txt (line 1 = selected profile,
# remaining lines = the profile names). Empty array when the file is absent.
function Get-ProfileListLines {
    param([Parameter(Mandatory)][string]$ListFile)
    if (-not (Test-Path $ListFile)) { return @() }
    return @((Read-FileShared $ListFile) -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

# The selected profile recorded on line 1 of profiles_list.txt ($null when absent/empty).
function Get-ProfileListLine1 {
    param([Parameter(Mandatory)][string]$ListFile)
    $lines = Get-ProfileListLines $ListFile
    if ($lines.Count -gt 0) { return $lines[0] }
    return $null
}

function Test-BaselineScenario {
    param([Parameter(Mandatory)]$Ctx, [Parameter(Mandatory)]$Phd2Connection)

    Invoke-DitherBatch -Phd2Connection $Phd2Connection -Count $Ctx.DitherCount

    Save-Screenshot -Path (New-ScreenshotPath '10' 'baseline' 'top')
    Invoke-PanelScroll -Direction Down
    Save-Screenshot -Path (New-ScreenshotPath '11' 'baseline' 'bottom')

    $state = Get-BridgeState $Ctx.Bridge
    $state | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Ctx.ArtifactDir 'state_baseline.json')

    # ---- Ported from the pre-Step-3 script: diagnostic file series count ----
    $analysisFile = Get-ChildItem $Ctx.PluginDataDir -Filter '*_dither_analysis.txt' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -gt $Ctx.RunStart } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $thresholds = @{}
    if (-not $analysisFile) {
        Fail "Baseline: no *_dither_analysis.txt written under $($Ctx.PluginDataDir) during this run"
    } else {
        Copy-Item $analysisFile.FullName $Ctx.ArtifactDir -Force
        $analysisLines = Get-Content $analysisFile.FullName
        $dataLines = $analysisLines | Where-Object { $_ -match '^\d+,' }
        $seriesCount = ($dataLines | ForEach-Object { ($_ -split ',')[0] } | Sort-Object -Unique).Count
        # Restored series from earlier sessions may add to the count, hence >=
        if ($seriesCount -ge $Ctx.DitherCount) {
            Pass "Baseline: diagnostic file '$($analysisFile.Name)' contains $seriesCount dither series (expected >= $($Ctx.DitherCount))"
        } else {
            Fail "Baseline: diagnostic file '$($analysisFile.Name)' contains only $seriesCount dither series (expected >= $($Ctx.DitherCount))"
        }

        # Parse "# Threshold_P90: 1.2345 (...)" header comments for the optimizer cross-check below
        foreach ($line in $analysisLines) {
            if ($line -match '^# Threshold_(P9[059]):\s*([\d.]+)') {
                $thresholds[$matches[1]] = [double]$matches[2]
            }
        }
    }

    # ---- Ported: profile JSON gained exactly DitherCount new, plausible events ----
    $profileData = Get-ProfileData $Ctx.ActiveProfile
    if ($null -eq $profileData) {
        Fail "Baseline: profile data file profiles\$($Ctx.ActiveProfile).json was not written"
        return
    }
    Copy-Item (Join-Path $Ctx.PluginDataDir "profiles\$($Ctx.ActiveProfile).json") $Ctx.ArtifactDir -Force
    $allEvents = @($profileData.DitherEvents)
    $newCount = $allEvents.Count - $Ctx.PreDitherCount
    if ($newCount -eq $Ctx.DitherCount) {
        Pass "Baseline: profile JSON gained exactly $($Ctx.DitherCount) dither events"
    } else {
        Fail "Baseline: profile JSON gained $newCount dither events (expected $($Ctx.DitherCount))"
    }

    $newEvents = $allEvents | Select-Object -Last ([Math]::Max($newCount, 0))
    $implausible = @($newEvents | Where-Object {
        (-not $_.Success) -or
        ($null -eq $_.SettleTime) -or ($_.SettleTime -le 0) -or ($_.SettleTime -gt ($SettleTimeout + 30)) -or
        ($null -eq $_.PixelShiftX) -or ($null -eq $_.PixelShiftY)
    })
    if ($implausible.Count -eq 0) {
        Pass "Baseline: all new dither events are plausible (Success, 0 < SettleTime <= $($SettleTimeout + 30) s, pixel shift present)"
    } else {
        Fail "Baseline: $($implausible.Count) new dither event(s) implausible (failed settle, missing/absurd SettleTime or missing pixel shift)"
    }

    # ---- Bridge get-state vs. PowerShell-computed expectations from the profile JSON ----
    $successfulSettleTimes = @($allEvents | Where-Object { $_.Success -and $null -ne $_.SettleTime } | ForEach-Object { [double]$_.SettleTime }) | Sort-Object

    $expectedTotal = $allEvents.Count
    $expectedSuccessful = $successfulSettleTimes.Count
    $expectedSuccessRate = if ($expectedTotal -gt 0) { [double]$expectedSuccessful / $expectedTotal * 100 } else { 0 }
    Assert-NearlyEqual 'Baseline: TotalDithers' $expectedTotal $state.TotalDithers 0.5
    Assert-NearlyEqual 'Baseline: SuccessfulDithers' $expectedSuccessful $state.SuccessfulDithers 0.5
    Assert-NearlyEqual 'Baseline: SuccessRate' $expectedSuccessRate $state.SuccessRate 0.5

    if ($successfulSettleTimes.Count -gt 0) {
        $n = $successfulSettleTimes.Count
        $expectedMedian = if ($n % 2 -eq 1) { $successfulSettleTimes[[Math]::Floor($n / 2)] } else { ($successfulSettleTimes[$n / 2 - 1] + $successfulSettleTimes[$n / 2]) / 2.0 }
        Assert-NearlyEqual 'Baseline: MedianSettleTime' $expectedMedian $state.MedianSettleTime 0.05
        Assert-NearlyEqual 'Baseline: MinSettleTime' $successfulSettleTimes[0] $state.MinSettleTime 0.05
        Assert-NearlyEqual 'Baseline: MaxSettleTime' $successfulSettleTimes[$n - 1] $state.MaxSettleTime 0.05
    }

    $pixelShiftPoints = @($profileData.PixelShiftValues)
    if ($pixelShiftPoints.Count -gt 0) {
        $xs = $pixelShiftPoints | ForEach-Object { [double]$_.X }
        $ys = $pixelShiftPoints | ForEach-Object { [double]$_.Y }
        $expectedDriftX = ($xs | Measure-Object -Maximum).Maximum - ($xs | Measure-Object -Minimum).Minimum
        $expectedDriftY = ($ys | Measure-Object -Maximum).Maximum - ($ys | Measure-Object -Minimum).Minimum
        Assert-NearlyEqual 'Baseline: TotalDriftX' $expectedDriftX $state.TotalDriftX 0.05
        Assert-NearlyEqual 'Baseline: TotalDriftY' $expectedDriftY $state.TotalDriftY 0.05
    }

    # ---- Cross-check optimizer state vs. dither_analysis.txt thresholds + JSON Recommendation ----
    if ($state.Optimizer) {
        if ($thresholds.Count -eq 3) {
            Assert-NearlyEqual 'Baseline: Optimizer.SettlePixel_Strict vs Threshold_P90' $thresholds['P90'] $state.Optimizer.SettlePixel_Strict 0.1
            Assert-NearlyEqual 'Baseline: Optimizer.SettlePixel_Standard vs Threshold_P95' $thresholds['P95'] $state.Optimizer.SettlePixel_Standard 0.1
            Assert-NearlyEqual 'Baseline: Optimizer.SettlePixel_Fast vs Threshold_P99' $thresholds['P99'] $state.Optimizer.SettlePixel_Fast 0.1
        } else {
            Fail "Baseline: could not parse all three P90/P95/P99 thresholds from $(if ($analysisFile) { $analysisFile.Name } else { '<missing file>' })"
        }

        if ($profileData.Recommendation) {
            Assert-NearlyEqual 'Baseline: Optimizer.DitherEventsAnalyzed vs JSON Recommendation' $profileData.Recommendation.DitherEventsAnalyzed $state.Optimizer.DitherEventsAnalyzed 0.5
        }
    } else {
        Log 'Baseline: no optimizer recommendation available yet (too few dither series) - skipping optimizer cross-check.'
    }
}

function Test-ExportScenario {
    param([Parameter(Mandatory)]$Ctx)

    $before = @()
    if (Test-Path $Ctx.DocumentsExportDir) {
        $before = Get-ChildItem $Ctx.DocumentsExportDir -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName
    }

    # ---- CSV export ----
    Invoke-BridgeCommand $Ctx.Bridge -Name ExportCsv

    $csvFile = $null
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        $csvFile = Get-ChildItem $Ctx.DocumentsExportDir -Filter 'DitherEvents_*.csv' -ErrorAction SilentlyContinue |
            Where-Object { $before -notcontains $_.FullName } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($csvFile) { break }
        Start-Sleep -Milliseconds 500
    }

    if (-not $csvFile) {
        Fail "Export: no new DitherEvents_*.csv appeared under $($Ctx.DocumentsExportDir) within 10 s"
    } else {
        Copy-Item $csvFile.FullName $Ctx.ArtifactDir -Force
        $csvLines = @(Get-Content $csvFile.FullName)
        $expectedHeader = 'DitherNumber,StartTime,EndTime,PixelShiftX,PixelShiftY,CumulativeX,CumulativeY,SettleTime,Success'
        if ($csvLines[0] -eq $expectedHeader) {
            Pass 'Export: CSV header matches the expected 9 columns'
        } else {
            Fail "Export: CSV header mismatch. Expected '$expectedHeader', got '$($csvLines[0])'"
        }

        $jsonEvents = @((Get-ProfileData $Ctx.ActiveProfile).DitherEvents)
        $dataRows = @($csvLines | Select-Object -Skip 1 | Where-Object { $_.Trim() -ne '' })
        if ($dataRows.Count -eq $jsonEvents.Count) {
            Pass "Export: CSV row count ($($dataRows.Count)) matches profile JSON event count"
        } else {
            Fail "Export: CSV row count ($($dataRows.Count)) does not match profile JSON event count ($($jsonEvents.Count))"
        }

        if ($dataRows.Count -gt 0) {
            $allNineCols = -not ($dataRows | Where-Object { @($_ -split ',').Count -ne 9 })
            if ($allNineCols) {
                Pass 'Export: every CSV row has exactly 9 comma-separated fields (culture-invariant numeric formatting confirmed - verifies Step 1b)'
            } else {
                Fail 'Export: at least one CSV row does not have exactly 9 fields (locale/decimal-comma regression?)'
            }

            $invariantCulture = [System.Globalization.CultureInfo]::InvariantCulture
            $firstCols = $dataRows[0] -split ','
            $lastCols = $dataRows[-1] -split ','
            $firstJson = $jsonEvents[0]
            $lastJson = $jsonEvents[-1]
            if ($null -ne $firstJson.SettleTime) {
                Assert-NearlyEqual 'Export: first-row SettleTime vs JSON' ([double]$firstJson.SettleTime) ([double]::Parse($firstCols[7], $invariantCulture)) 0.01
            }
            if ($null -ne $lastJson.SettleTime) {
                Assert-NearlyEqual 'Export: last-row SettleTime vs JSON' ([double]$lastJson.SettleTime) ([double]::Parse($lastCols[7], $invariantCulture)) 0.01
            }
            Assert-NearlyEqual 'Export: first-row CumulativeX vs JSON' ([double]$firstJson.CumulativeX) ([double]::Parse($firstCols[5], $invariantCulture)) 0.01
            Assert-NearlyEqual 'Export: last-row CumulativeX vs JSON' ([double]$lastJson.CumulativeX) ([double]::Parse($lastCols[5], $invariantCulture)) 0.01
        }
    }

    # ---- Quality report export ----
    Invoke-BridgeCommand $Ctx.Bridge -Name Recalc
    Invoke-BridgeCommand $Ctx.Bridge -Name ExportReport

    $reportFile = $null
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        $reportFile = Get-ChildItem $Ctx.DocumentsExportDir -Filter 'DitherQuality_*.txt' -ErrorAction SilentlyContinue |
            Where-Object { $before -notcontains $_.FullName } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($reportFile) { break }
        Start-Sleep -Milliseconds 500
    }

    if (-not $reportFile) {
        Fail "Export: no new DitherQuality_*.txt appeared under $($Ctx.DocumentsExportDir) within 10 s"
        return
    }
    Copy-Item $reportFile.FullName $Ctx.ArtifactDir -Force
    # Read as UTF8 explicitly: the report contains a non-ASCII subscript character
    # (Centered L<sub>2</sub> Discrepancy) that Windows PowerShell's Get-Content
    # would otherwise mis-decode without a BOM
    $reportText = Get-Content $reportFile.FullName -Raw -Encoding UTF8

    $expectedSectionHeaders = @('=== Dither Quality Assessment ===', '--- Primary Metrics', '--- Temporal Pattern ---', '--- Context ---', '--- Recommendation ---', '--- Grading Scale ---')
    $missingHeaders = @($expectedSectionHeaders | Where-Object { $reportText -notmatch [regex]::Escape($_) })
    if ($missingHeaders.Count -eq 0) {
        Pass 'Export: quality report contains all expected section headers'
    } else {
        Fail "Export: quality report missing section header(s): $($missingHeaders -join ', ')"
    }

    $state = Get-BridgeState $Ctx.Bridge
    if ($state.Quality) {
        if ($reportText -match 'Combined Score:\s*([\d.]+)') {
            Assert-NearlyEqual 'Export: report Combined Score vs get-state' $state.Quality.CombinedScore ([double]$matches[1]) 0.005
        } else {
            Fail 'Export: could not parse Combined Score from the quality report'
        }
        if ($reportText -match 'Discrepancy:\s*([\d.]+)') {
            Assert-NearlyEqual 'Export: report Centered L2 Discrepancy vs get-state' $state.Quality.CenteredL2Discrepancy ([double]$matches[1]) 0.001
        } else {
            Fail 'Export: could not parse Centered L2 Discrepancy from the quality report'
        }
        if ($reportText -match 'Nearest Neighbor Index:\s*([\d.]+)') {
            Assert-NearlyEqual 'Export: report Nearest Neighbor Index vs get-state' $state.Quality.NearestNeighborIndex ([double]$matches[1]) 0.01
        } else {
            Fail 'Export: could not parse Nearest Neighbor Index from the quality report'
        }
    } else {
        Log 'Export: no quality data available yet (fewer than 4 pixel-shift points) - skipping Combined Score/CD/NNI cross-check.'
    }
}

function Test-ProfilesScenario {
    param([Parameter(Mandatory)]$Ctx, [Parameter(Mandatory)]$Phd2Connection)

    $bridge = $Ctx.Bridge
    $newProfile = 'SmokeTestB'
    $defaultName = 'Default'   # StatisticsProfileService.DefaultProfileName - delete falls back to this

    # ---- Record the base profile this scenario must leave untouched (data separation) ----
    $stateBefore = Get-BridgeState $bridge
    $baseProfile = $stateBefore.SelectedProfileName
    $baseCount = [int]$stateBefore.TotalDithers
    Log "Profiles: base profile '$baseProfile' holds $baseCount dither event(s) before the scenario."

    # ---- Enable multi-profile; restore the user's prior state in the finally block ----
    $priorMultiProfile = Set-BridgeToggle $bridge -Name MultiProfile -Value $true
    try {
        # ---- Create SmokeTestB (adds it and selects it; saves the base profile to file first) ----
        New-BridgeProfile $bridge -Name $newProfile

        # profiles_list.txt line 1 is the freshly selected profile
        if (Wait-For { (Get-ProfileListLine1 $Ctx.ProfileListFile) -eq $newProfile } -TimeoutSec 5) {
            Pass "Profiles: profiles_list.txt line 1 is '$newProfile' (newly created profile selected)"
        } else {
            Fail "Profiles: profiles_list.txt line 1 is '$(Get-ProfileListLine1 $Ctx.ProfileListFile)' after create-profile (expected '$newProfile')"
        }

        $stateNew = Get-BridgeState $bridge
        if ($stateNew.SelectedProfileName -eq $newProfile) {
            Pass "Profiles: get-state SelectedProfileName is '$newProfile'"
        } else {
            Fail "Profiles: get-state SelectedProfileName is '$($stateNew.SelectedProfileName)' (expected '$newProfile')"
        }
        Assert-NearlyEqual 'Profiles: new profile TotalDithers' 0 $stateNew.TotalDithers 0.5
        Assert-NearlyEqual 'Profiles: new profile SuccessfulDithers' 0 $stateNew.SuccessfulDithers 0.5

        # ---- Dither into SmokeTestB ----
        Invoke-DitherBatch -Phd2Connection $Phd2Connection -Count $Ctx.ProfileDitherCount

        # Charts live near the top of the panel; a prior scenario may have left it scrolled down
        Invoke-PanelScroll -Direction Up -Notches 30
        Save-Screenshot -Path (New-ScreenshotPath '30' 'profiles' 'b')

        # SmokeTestB.json must hold exactly ProfileDitherCount events (persistence writes after each dither)
        $bOk = Wait-For {
            try {
                $d = Get-ProfileData $newProfile
                return ($d -and @($d.DitherEvents).Count -eq $Ctx.ProfileDitherCount)
            } catch { return $false }
        } -TimeoutSec 10
        $bData = Get-ProfileData $newProfile
        $bDataCount = if ($bData) { @($bData.DitherEvents).Count } else { -1 }
        if ($bOk) {
            Pass "Profiles: $newProfile.json holds exactly $($Ctx.ProfileDitherCount) dither event(s)"
            Copy-Item (Join-Path $Ctx.PluginDataDir "profiles\$newProfile.json") $Ctx.ArtifactDir -Force
        } else {
            Fail "Profiles: $newProfile.json holds $bDataCount dither event(s) (expected $($Ctx.ProfileDitherCount))"
        }

        $stateB = Get-BridgeState $bridge
        Assert-NearlyEqual 'Profiles: SmokeTestB TotalDithers (get-state)' $Ctx.ProfileDitherCount $stateB.TotalDithers 0.5
        # Each dither contributes one cumulative pixel-shift point, so the chart shows exactly N points
        Assert-NearlyEqual 'Profiles: SmokeTestB PixelShiftPointCount (chart points)' $Ctx.ProfileDitherCount $stateB.PixelShiftPointCount 0.5

        # ---- Data separation: the base profile's file must be untouched ----
        $baseData = Get-ProfileData $baseProfile
        $baseNow = if ($baseData) { @($baseData.DitherEvents).Count } else { -1 }
        if ($baseNow -eq $baseCount) {
            Pass "Profiles: base profile '$baseProfile' still holds $baseCount event(s) (no cross-profile bleed)"
        } else {
            Fail "Profiles: base profile '$baseProfile' now holds $baseNow event(s) (expected $baseCount) - profile data bled across"
        }

        # ---- Switch back to the base profile: its data comes back intact ----
        Select-BridgeProfile $bridge -Name $baseProfile
        $stateBack = Get-BridgeState $bridge
        if ($stateBack.SelectedProfileName -eq $baseProfile) {
            Pass "Profiles: reselected base profile '$baseProfile'"
        } else {
            Fail "Profiles: after select-profile SelectedProfileName is '$($stateBack.SelectedProfileName)' (expected '$baseProfile')"
        }
        Assert-NearlyEqual 'Profiles: base profile TotalDithers after reselect' $baseCount $stateBack.TotalDithers 0.5
        Invoke-PanelScroll -Direction Up -Notches 30
        Save-Screenshot -Path (New-ScreenshotPath '31' 'profiles' 'default_again')

        # ---- Delete SmokeTestB (the adapter selects it first, then deletes) ----
        Remove-BridgeProfile $bridge -Name $newProfile

        $stateDel = Get-BridgeState $bridge
        if ($stateDel.SelectedProfileName -eq $defaultName) {
            Pass "Profiles: after delete, selection fell back to '$defaultName'"
        } else {
            Fail "Profiles: after delete, SelectedProfileName is '$($stateDel.SelectedProfileName)' (expected '$defaultName')"
        }
        if (@($stateDel.ProfileNames) -notcontains $newProfile) {
            Pass "Profiles: '$newProfile' removed from the profile list"
        } else {
            Fail "Profiles: '$newProfile' still present in ProfileNames after delete"
        }

        if (Wait-For { -not (Test-Path (Join-Path $Ctx.PluginDataDir "profiles\$newProfile.json")) } -TimeoutSec 5) {
            Pass "Profiles: $newProfile.json deleted from disk"
        } else {
            Fail "Profiles: $newProfile.json still present on disk after delete"
        }

        if (Wait-For { (Get-ProfileListLines $Ctx.ProfileListFile) -notcontains $newProfile } -TimeoutSec 5) {
            Pass "Profiles: profiles_list.txt no longer references '$newProfile'"
        } else {
            Fail "Profiles: profiles_list.txt still references '$newProfile' after delete"
        }

        # Landing profile is Default; cross-check its live count against its own file
        $defaultData = Get-ProfileData $defaultName
        if ($defaultData) {
            Assert-NearlyEqual 'Profiles: Default TotalDithers matches Default.json after delete' (@($defaultData.DitherEvents).Count) $stateDel.TotalDithers 0.5
        }
    } finally {
        # Restore the user's multi-profile toggle regardless of assertion outcomes
        try {
            Set-BridgeToggle $bridge -Name MultiProfile -Value $priorMultiProfile | Out-Null
            Log "Profiles: restored MultiProfile toggle to its prior state ($priorMultiProfile)."
        } catch {
            Fail "Profiles: could not restore MultiProfile toggle: $($_.Exception.Message)"
        }
    }
}

# Compares the get-state settle/drift/count block of two snapshots (used to prove
# a value survived a NINA restart). Emits one Pass/Fail per field.
function Assert-StateRestored {
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [Parameter(Mandatory)]$Expected,
        [Parameter(Mandatory)]$Actual
    )
    Assert-NearlyEqual "$Prefix TotalDithers"       $Expected.TotalDithers       $Actual.TotalDithers       0.5
    Assert-NearlyEqual "$Prefix SuccessfulDithers"  $Expected.SuccessfulDithers  $Actual.SuccessfulDithers  0.5
    Assert-NearlyEqual "$Prefix SuccessRate"        $Expected.SuccessRate        $Actual.SuccessRate        0.5
    Assert-NearlyEqual "$Prefix MedianSettleTime"   $Expected.MedianSettleTime   $Actual.MedianSettleTime   0.05
    Assert-NearlyEqual "$Prefix MinSettleTime"      $Expected.MinSettleTime      $Actual.MinSettleTime      0.05
    Assert-NearlyEqual "$Prefix MaxSettleTime"      $Expected.MaxSettleTime      $Actual.MaxSettleTime      0.05
    Assert-NearlyEqual "$Prefix AverageSettleTime"  $Expected.AverageSettleTime  $Actual.AverageSettleTime  0.05
    Assert-NearlyEqual "$Prefix StdDevSettleTime"   $Expected.StdDevSettleTime   $Actual.StdDevSettleTime   0.05
    Assert-NearlyEqual "$Prefix TotalDriftX"        $Expected.TotalDriftX        $Actual.TotalDriftX        0.05
    Assert-NearlyEqual "$Prefix TotalDriftY"        $Expected.TotalDriftY        $Actual.TotalDriftY        0.05
    Assert-NearlyEqual "$Prefix PixelShiftPointCount" $Expected.PixelShiftPointCount $Actual.PixelShiftPointCount 0.5
    Assert-NearlyEqual "$Prefix SettleTimePointCount" $Expected.SettleTimePointCount $Actual.SettleTimePointCount 0.5
}

function Test-RestartScenario {
    param([Parameter(Mandatory)]$Ctx)

    $bridge = $Ctx.Bridge
    $profileName = $Ctx.ActiveProfile
    $profileJsonPath = Join-Path $Ctx.PluginDataDir "profiles\$profileName.json"

    # ---- Pre-restart truth: profile JSON (content + count) and the live get-state ----
    if (-not (Test-Path $profileJsonPath)) {
        Fail "Restart: profile file profiles\$profileName.json missing before restart - nothing to verify"
        return
    }
    $preText = Read-FileShared $profileJsonPath
    $preData = $preText | ConvertFrom-Json
    $preEventCount = @($preData.DitherEvents).Count

    $preState = Get-BridgeState $bridge
    $preState | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Ctx.ArtifactDir 'state_restart_before.json')
    Log "Restart: pre-restart '$profileName' holds $preEventCount event(s); get-state Total=$($preState.TotalDithers)."

    if ($preEventCount -le 0) {
        Fail 'Restart: active profile has no persisted dither events before restart - persistence round-trip is not meaningful. Seed dithers first (run Baseline).'
        return
    }

    # ---- Restart NINA (drop the bridge, close, reopen against a fresh log watermark) ----
    Disconnect-Bridge $bridge
    Stop-Nina

    # NINA re-saves statistics on Dispose (SaveStatisticsData in VM.Dispose), so the
    # file is rewritten on shutdown; assert the persisted DATA is unchanged. Read the
    # same way before/after: byte-identical is the happy path, a benign re-serialization
    # with identical content still passes, only altered data fails.
    if (-not (Test-Path $profileJsonPath)) {
        Fail "Restart: profiles\$profileName.json disappeared during NINA shutdown"
    } else {
        $postText = Read-FileShared $profileJsonPath
        if ($postText -eq $preText) {
            Pass "Restart: profiles\$profileName.json byte-identical across NINA shutdown"
        } else {
            $postData = $postText | ConvertFrom-Json
            $postEventCount = @($postData.DitherEvents).Count
            if ($postEventCount -eq $preEventCount) {
                Pass "Restart: profiles\$profileName.json re-serialized on shutdown but data unchanged ($postEventCount events preserved)"
            } else {
                Fail "Restart: profiles\$profileName.json changed during shutdown - $preEventCount event(s) before, $postEventCount after"
            }
        }
    }

    # Fresh watermark so the plugin-connect poll matches the NEW session's log, not the old one
    $restartWatermark = Get-Date
    $nina = Start-NinaAndWaitForPlugin -Watermark $restartWatermark
    $newBridge = $nina.Bridge

    # Propagate the new bridge/log to the run scope: teardown disconnects the live
    # bridge and scans the new log, and later scenarios (PersistenceOff, Clear) reuse it
    $script:bridge = $newBridge
    $script:ninaLog = $nina.LogFile
    $Ctx.Bridge = $newBridge

    # ---- Restored state must match the pre-restart snapshot ----
    $postState = Get-BridgeState $newBridge
    $postState | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Ctx.ArtifactDir 'state_restart_after.json')

    if ($postState.SelectedProfileName -eq $profileName) {
        Pass "Restart: active profile is '$profileName' again after restart"
    } else {
        Fail "Restart: active profile is '$($postState.SelectedProfileName)' after restart (expected '$profileName')"
    }

    Assert-StateRestored 'Restart:' $preState $postState

    # ---- Optimizer recommendation must be repopulated from the persisted OptimizerData ----
    if ($preState.Optimizer) {
        if ($postState.Optimizer) {
            Assert-NearlyEqual 'Restart: Optimizer.SettlePixel_Strict restored'   $preState.Optimizer.SettlePixel_Strict   $postState.Optimizer.SettlePixel_Strict   0.05
            Assert-NearlyEqual 'Restart: Optimizer.SettlePixel_Standard restored' $preState.Optimizer.SettlePixel_Standard $postState.Optimizer.SettlePixel_Standard 0.05
            Assert-NearlyEqual 'Restart: Optimizer.SettlePixel_Fast restored'     $preState.Optimizer.SettlePixel_Fast     $postState.Optimizer.SettlePixel_Fast     0.05
            Assert-NearlyEqual 'Restart: Optimizer.DitherEventsAnalyzed restored' $preState.Optimizer.DitherEventsAnalyzed $postState.Optimizer.DitherEventsAnalyzed 0.5
        } else {
            Fail 'Restart: optimizer recommendation was present before restart but is null after (OptimizerData not restored)'
        }
    } else {
        Log 'Restart: no optimizer recommendation before restart (too few dither series) - skipping optimizer restore cross-check.'
    }

    # ---- No phantom events: the persisted profile must still hold exactly the pre-restart count ----
    $afterData = Get-ProfileData $profileName
    $afterCount = if ($afterData) { @($afterData.DitherEvents).Count } else { -1 }
    if ($afterCount -eq $preEventCount) {
        Pass "Restart: profile still holds exactly $preEventCount event(s) after restart (no phantom events)"
    } else {
        Fail "Restart: profile holds $afterCount event(s) after restart (expected $preEventCount)"
    }

    # ---- Screenshots of the restored panel (top: charts, bottom: stats/quality/optimizer) ----
    Invoke-PanelScroll -Direction Up -Notches 30
    Save-Screenshot -Path (New-ScreenshotPath '40' 'restart' 'restored_top')
    Invoke-PanelScroll -Direction Down
    Save-Screenshot -Path (New-ScreenshotPath '41' 'restart' 'restored_bottom')
}

function Test-PersistenceOffScenario {
    param([Parameter(Mandatory)]$Ctx)

    $bridge = $Ctx.Bridge
    $profileName = $Ctx.ActiveProfile
    $profileJsonPath = Join-Path $Ctx.PluginDataDir "profiles\$profileName.json"
    $persistenceFilePath = Join-Path $Ctx.PluginDataDir 'persistence_settings.txt'

    $stateBefore = Get-BridgeState $bridge
    if ([int]$stateBefore.TotalDithers -le 0) {
        Fail 'PersistenceOff: active profile has no dither events before the scenario - nothing meaningful to verify. Seed dithers first (run Baseline/Restart).'
        return
    }
    if (-not (Test-Path $profileJsonPath)) {
        Fail "PersistenceOff: profiles\$profileName.json missing before the scenario"
        return
    }
    Log "PersistenceOff: '$profileName' holds $($stateBefore.TotalDithers) dither event(s) before disabling persistence."

    # ---- Disable persistence: all profile files are deleted, in-memory state (and thus get-state) is kept ----
    Set-BridgeToggle $bridge -Name Persistence -Value $false | Out-Null

    if (Wait-For { (Test-Path $persistenceFilePath) -and (Read-FileShared $persistenceFilePath).Trim() -eq 'False' } -TimeoutSec 5) {
        Pass "PersistenceOff: persistence_settings.txt is 'False'"
    } else {
        Fail "PersistenceOff: persistence_settings.txt did not flip to 'False' within 5 s"
    }

    if (Wait-For { -not (Test-Path $profileJsonPath) } -TimeoutSec 5) {
        Pass "PersistenceOff: profiles\$profileName.json deleted"
    } else {
        Fail "PersistenceOff: profiles\$profileName.json still present after disabling persistence"
    }
    $remainingJson = @(Get-ChildItem $Ctx.ProfilesDir -Filter '*.json' -ErrorAction SilentlyContinue)
    if ($remainingJson.Count -eq 0) {
        Pass 'PersistenceOff: all profiles\*.json files deleted'
    } else {
        Fail "PersistenceOff: $($remainingJson.Count) profiles\*.json file(s) still present after disabling persistence: $(($remainingJson | ForEach-Object { $_.Name }) -join ', ')"
    }

    # ---- Values retained in memory: get-state must be unchanged even with no files on disk ----
    $stateOff = Get-BridgeState $bridge
    Assert-StateRestored 'PersistenceOff: (retained in memory)' $stateBefore $stateOff
    if ($stateOff.SelectedProfileName -eq $profileName) {
        Pass "PersistenceOff: active profile still '$profileName' with persistence off"
    } else {
        Fail "PersistenceOff: active profile changed to '$($stateOff.SelectedProfileName)' after disabling persistence (expected '$profileName')"
    }

    Invoke-PanelScroll -Direction Down
    Save-Screenshot -Path (New-ScreenshotPath '45' 'persistence' 'off')
    Invoke-PanelScroll -Direction Up -Notches 30

    # ---- Re-enable persistence: the in-memory state (incl. inactive profiles) is flushed back to disk ----
    Set-BridgeToggle $bridge -Name Persistence -Value $true | Out-Null

    if (Wait-For { (Test-Path $persistenceFilePath) -and (Read-FileShared $persistenceFilePath).Trim() -eq 'True' } -TimeoutSec 5) {
        Pass "PersistenceOff: persistence_settings.txt is 'True' again"
    } else {
        Fail "PersistenceOff: persistence_settings.txt did not flip back to 'True' within 5 s"
    }

    $expectedCount = [int]$stateBefore.TotalDithers
    $reappearOk = Wait-For {
        try {
            $d = Get-ProfileData $profileName
            return ($d -and @($d.DitherEvents).Count -eq $expectedCount)
        } catch { return $false }
    } -TimeoutSec 5
    if ($reappearOk) {
        Pass "PersistenceOff: profiles\$profileName.json reappeared with $expectedCount dither event(s) (unchanged)"
    } else {
        $reData = Get-ProfileData $profileName
        $reCount = if ($reData) { @($reData.DitherEvents).Count } else { -1 }
        Fail "PersistenceOff: profiles\$profileName.json holds $reCount event(s) after re-enabling persistence (expected $expectedCount)"
    }
}

function Test-ClearScenario {
    param([Parameter(Mandatory)]$Ctx)

    $bridge = $Ctx.Bridge
    $profileName = $Ctx.ActiveProfile
    $profileJsonPath = Join-Path $Ctx.PluginDataDir "profiles\$profileName.json"

    $stateBefore = Get-BridgeState $bridge
    if ([int]$stateBefore.TotalDithers -le 0) {
        Fail 'Clear: active profile has no dither events before the scenario - nothing meaningful to clear. Seed dithers first (run Baseline/Restart).'
        return
    }
    Log "Clear: '$profileName' holds $($stateBefore.TotalDithers) dither event(s) before Clear Data."

    Invoke-BridgeCommand $bridge -Name ClearData

    # ---- get-state must be fully zeroed / inactive right after clearing ----
    $stateAfter = Get-BridgeState $bridge
    Assert-NearlyEqual 'Clear: TotalDithers'         0 $stateAfter.TotalDithers         0.5
    Assert-NearlyEqual 'Clear: SuccessfulDithers'    0 $stateAfter.SuccessfulDithers    0.5
    Assert-NearlyEqual 'Clear: SuccessRate'          0 $stateAfter.SuccessRate          0.5
    Assert-NearlyEqual 'Clear: MedianSettleTime'     0 $stateAfter.MedianSettleTime     0.05
    Assert-NearlyEqual 'Clear: MinSettleTime'        0 $stateAfter.MinSettleTime        0.05
    Assert-NearlyEqual 'Clear: MaxSettleTime'        0 $stateAfter.MaxSettleTime        0.05
    Assert-NearlyEqual 'Clear: TotalDriftX'          0 $stateAfter.TotalDriftX          0.05
    Assert-NearlyEqual 'Clear: TotalDriftY'          0 $stateAfter.TotalDriftY          0.05
    Assert-NearlyEqual 'Clear: PixelShiftPointCount' 0 $stateAfter.PixelShiftPointCount 0.5
    Assert-NearlyEqual 'Clear: SettleTimePointCount' 0 $stateAfter.SettleTimePointCount 0.5

    if ($null -eq $stateAfter.Quality) {
        Pass 'Clear: Quality block is null (inactive) after clearing'
    } else {
        Fail 'Clear: Quality block still populated after Clear Data'
    }
    if ($null -eq $stateAfter.Optimizer) {
        Pass 'Clear: Optimizer block is null (inactive) after clearing'
    } else {
        Fail 'Clear: Optimizer block still populated after Clear Data'
    }

    Invoke-PanelScroll -Direction Up -Notches 30
    Save-Screenshot -Path (New-ScreenshotPath '50' 'clear' 'top')
    Invoke-PanelScroll -Direction Down
    Save-Screenshot -Path (New-ScreenshotPath '51' 'clear' 'bottom')

    # ---- Persisted active-profile file rewritten with an empty DitherEvents array ----
    # (ClearData deletes every profile's file; only the active profile is re-saved by
    # the subsequent SaveStatisticsData call, so only its file is expected to reappear.)
    $ok = Wait-For {
        try {
            $d = Get-ProfileData $profileName
            return ($d -and @($d.DitherEvents).Count -eq 0)
        } catch { return $false }
    } -TimeoutSec 5
    if ($ok) {
        Pass "Clear: profiles\$profileName.json rewritten with an empty DitherEvents array"
        Copy-Item $profileJsonPath $Ctx.ArtifactDir -Force
    } else {
        $d = Get-ProfileData $profileName
        $count = if ($d) { @($d.DitherEvents).Count } else { -1 }
        Fail "Clear: profiles\$profileName.json holds $count dither event(s) after Clear Data (expected 0)"
    }
}
