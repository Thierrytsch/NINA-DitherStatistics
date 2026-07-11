<#
.SYNOPSIS
Finishes the deferred teardown of a Run-SmokeTest.ps1 -HoldForInspection run.

.DESCRIPTION
Run-SmokeTest.ps1 -HoldForInspection leaves NINA running and writes
'<ArtifactDir>\hold_state.json' instead of restoring settings/profiles and
closing NINA/PHD2, so Claude can drive the live session with
Invoke-NinaUi.ps1 (state/invoke/toggle/select-profile/scroll/screenshot)
during post-run analysis. Call this script once that analysis is done to
perform exactly the restore/close steps Run-SmokeTest.ps1's normal teardown
would have done:
  1. Restore persistence_settings.txt / multiprofile_settings.txt /
     smoketest_settings.txt to their pre-run state (or remove them if this
     run created them).
  2. Restore the user's original statistics profiles (profiles\*.json,
     profiles_list.txt), discarding this run's simulator dithers.
  3. Close NINA (unless -KeepNina).
  4. Collect any exports produced since hold-state was written into the
     artifact dir's 'exports' subfolder and remove them from Documents.
  5. Shut down PHD2 (unless -KeepPhd2, or the original run already used it).
  6. Remove hold_state.json so this script isn't accidentally re-run twice.

.EXAMPLE
.\Complete-SmokeTest.ps1 -ArtifactDir .\artifacts\20260710_141905
.\Complete-SmokeTest.ps1 -ArtifactDir .\artifacts\20260710_141905 -KeepNina
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDir,
    [switch]$KeepNina,
    [switch]$KeepPhd2
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Phd2Rpc.ps1"

$ArtifactDir = (Resolve-Path $ArtifactDir).Path
$holdStatePath = Join-Path $ArtifactDir 'hold_state.json'
if (-not (Test-Path $holdStatePath)) {
    throw "No hold_state.json found under '$ArtifactDir' - was this run started with -HoldForInspection, and is teardown already complete?"
}
$hold = Get-Content $holdStatePath -Raw | ConvertFrom-Json

function Log([string]$Message) {
    Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $Message)
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

# ---- 1. Restore persistence / multi-profile / SmokeTestBridge flag ----------
try {
    if ($hold.PersistenceModified) {
        if ($hold.PersistenceExisted) {
            Set-Content -Path $hold.PersistenceFile -Value $hold.PersistenceBackup -NoNewline
        } elseif (Test-Path $hold.PersistenceFile) {
            Remove-Item $hold.PersistenceFile
        }
        Log 'Restored statistics persistence setting.'
    }
} catch { Log "WARNING: could not restore persistence setting: $($_.Exception.Message)" }

try {
    if ($hold.MultiProfileExisted) {
        Set-Content -Path $hold.MultiProfileFile -Value $hold.MultiProfileBackup -NoNewline
        Log 'Restored multi-profile setting.'
    } elseif (Test-Path $hold.MultiProfileFile) {
        Remove-Item $hold.MultiProfileFile
        Log 'Removed multi-profile setting created during this run.'
    }
} catch { Log "WARNING: could not restore multi-profile setting: $($_.Exception.Message)" }

try {
    if ($hold.SmokeTestModified) {
        if ($hold.SmokeTestExisted) {
            Set-Content -Path $hold.SmokeTestFile -Value $hold.SmokeTestBackup -NoNewline
        } elseif (Test-Path $hold.SmokeTestFile) {
            Remove-Item $hold.SmokeTestFile
        }
        Log 'Restored SmokeTestBridge diagnostic flag.'
    }
} catch { Log "WARNING: could not restore SmokeTestBridge flag: $($_.Exception.Message)" }

# ---- 2. Restore the user's original statistics profiles ---------------------
try {
    if ($hold.ProfilesBackedUp) {
        if (Test-Path $hold.ProfilesDir) { Remove-Item $hold.ProfilesDir -Recurse -Force }
        if ($hold.ProfilesDirExisted) {
            New-Item -ItemType Directory -Path $hold.ProfilesDir -Force | Out-Null
            if (Test-Path $hold.ProfilesBackupDir) {
                Copy-Item (Join-Path $hold.ProfilesBackupDir '*.json') $hold.ProfilesDir -ErrorAction SilentlyContinue
            }
        }
        if ($hold.ProfileListExisted) {
            Set-Content -Path $hold.ProfileListFile -Value $hold.ProfileListBackup -NoNewline
        } elseif (Test-Path $hold.ProfileListFile) {
            Remove-Item $hold.ProfileListFile
        }
        Log 'Restored original statistics profiles.'
    }
} catch { Log "WARNING: could not restore statistics profiles: $($_.Exception.Message)" }

# ---- 3. Close NINA ------------------------------------------------------------
if (-not $KeepNina) { Stop-Nina } else { Log '-KeepNina: leaving NINA running.' }

# ---- 4. Collect any exports produced since hold-state was written -----------
try {
    if (Test-Path $hold.DocumentsExportDir) {
        $runStart = [datetime]$hold.RunStart
        $files = Get-ChildItem $hold.DocumentsExportDir -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt $runStart }
        if ($files) {
            $exportsOutDir = Join-Path $ArtifactDir 'exports'
            New-Item -ItemType Directory -Path $exportsOutDir -Force | Out-Null
            foreach ($f in $files) {
                Copy-Item $f.FullName $exportsOutDir -Force
                Remove-Item $f.FullName -Force
            }
            Log "Collected $($files.Count) export file(s) into $exportsOutDir and removed them from Documents."
        }
    }
} catch { Log "WARNING: could not sync Documents exports: $($_.Exception.Message)" }

# ---- 5. Shut down PHD2 ---------------------------------------------------------
$effectiveKeepPhd2 = $KeepPhd2.IsPresent -or [bool]$hold.KeepPhd2
if (-not $effectiveKeepPhd2) {
    try {
        if (Test-Phd2Port) {
            $conn = Connect-Phd2
            try { Invoke-Phd2Rpc $conn 'shutdown' | Out-Null } finally { Disconnect-Phd2 $conn }
            Log 'PHD2 shut down.'
        }
    } catch { Log "WARNING: could not shut down PHD2: $($_.Exception.Message)" }
} else {
    Log '-KeepPhd2 (or the original run requested it): leaving PHD2 running.'
}

# ---- 6. Remove hold_state.json so this script cannot be double-run ----------
Remove-Item $holdStatePath -Force -ErrorAction SilentlyContinue
Log 'Deferred teardown complete.'
