<#
.SYNOPSIS
Brings PHD2 into the "Guiding" state using the simulator equipment profile - no GUI
interaction, everything through PHD2's event server API (port 4400).

.DESCRIPTION
Replaces the manual steps "start PHD2, connect hardware, loop, autoselect star,
start guiding": the single 'guide' RPC does star autoselect, calibration (unless
restored) and guiding in one call. Idempotent: if PHD2 is already guiding, does
nothing.

One-time setup (see SmokeTest\README.md): a PHD2 equipment profile with the
simulator camera, server enabled, ideally with a stored calibration and
"Auto restore calibration" so runs skip the ~1-2 min calibration.

.EXAMPLE
.\Start-Phd2Guiding.ps1 -ProfileName Simulator -Verbose
#>
[CmdletBinding()]
param(
    [string]$Phd2Path = "${env:ProgramFiles(x86)}\PHDGuiding2\phd2.exe",
    [string]$ProfileName = 'Simulator',
    [int]$ExposureMs = 1000,
    [double]$SettlePixels = 1.5,
    [int]$SettleTime = 8,
    [int]$SettleTimeout = 60,
    # Generous default: first run may include a full calibration
    [int]$GuidingTimeoutSec = 300
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\Phd2Rpc.ps1"

# 1. Start PHD2 if its server is not reachable
if (-not (Test-Phd2Port)) {
    if (-not (Test-Path $Phd2Path)) {
        throw "PHD2 not found at '$Phd2Path' - pass -Phd2Path"
    }
    Write-Host "Starting PHD2: $Phd2Path"
    Start-Process -FilePath $Phd2Path | Out-Null

    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Phd2Port)) {
        if ((Get-Date) -gt $deadline) { throw 'PHD2 started but its server (port 4400) did not come up within 60 s. Is "Enable Server" active in the profile?' }
        Start-Sleep -Seconds 1
    }
    Write-Host 'PHD2 server is up.'
} else {
    Write-Host 'PHD2 is already running.'
}

$phd2 = Connect-Phd2
try {
    # 2. Already guiding? Then there is nothing to do.
    $state = Invoke-Phd2Rpc $phd2 'get_app_state'
    Write-Host "PHD2 app state: $state"
    if ($state -eq 'Guiding') {
        Write-Host 'PHD2 is already guiding - nothing to do.'
        return
    }

    # 3. Select the simulator profile (needs equipment disconnected)
    $currentProfile = Invoke-Phd2Rpc $phd2 'get_profile'
    if ($currentProfile.name -ne $ProfileName) {
        $profiles = Invoke-Phd2Rpc $phd2 'get_profiles'
        $target = $profiles | Where-Object { $_.name -eq $ProfileName }
        if (-not $target) {
            $names = ($profiles | ForEach-Object { $_.name }) -join ', '
            throw "PHD2 profile '$ProfileName' not found. Available: $names (see SmokeTest\README.md for the one-time setup)"
        }
        Invoke-Phd2Rpc $phd2 'set_connected' @($false) | Out-Null
        Invoke-Phd2Rpc $phd2 'set_profile' @([int]$target.id) | Out-Null
        Write-Host "Switched to PHD2 profile '$ProfileName'."
    }

    # 4. Connect equipment (simulator camera/mount), set exposure
    Invoke-Phd2Rpc $phd2 'set_connected' @($true) -TimeoutSec 60 | Out-Null
    Write-Host 'Equipment connected.'
    Invoke-Phd2Rpc $phd2 'set_exposure' @([int]$ExposureMs) | Out-Null

    # 5. One call does it all: loop, autoselect star, calibrate (unless restored), guide
    $settle = @{ pixels = $SettlePixels; time = $SettleTime; timeout = $SettleTimeout }
    Invoke-Phd2Rpc $phd2 'guide' @{ settle = $settle; recalibrate = $false } | Out-Null
    Write-Host 'Guide command sent - waiting for guiding to start (includes calibration on first run)...'

    Wait-Phd2AppState $phd2 'Guiding' -TimeoutSec $GuidingTimeoutSec
    Write-Host 'PHD2 is guiding.'
} finally {
    Disconnect-Phd2 $phd2
}
