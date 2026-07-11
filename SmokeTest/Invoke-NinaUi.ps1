<#
.SYNOPSIS
Ad-hoc CLI for driving a live NINA + SmokeTestBridge session during post-run
analysis (e.g. after Run-SmokeTest.ps1 -HoldForInspection left NINA running).

.DESCRIPTION
Self-contained wrapper over BridgeClient.ps1 + NinaUi.ps1: each invocation
connects to the bridge fresh, runs exactly one command, and disconnects again
- there is no session state carried between invocations. Requires NINA to
already be running with the SmokeTestBridge flag enabled (smoketest_settings.txt
= True) and the plugin's Imaging-tab panel loaded, which is what
Run-SmokeTest.ps1 -HoldForInspection / -KeepNina leaves behind.

Exits non-zero with a clear stderr message when NINA is not running or the
bridge cannot be reached, so it is safe to script around.

When done, run Complete-SmokeTest.ps1 to finish the deferred teardown
(restores settings/profiles, optionally closes NINA/PHD2).

.EXAMPLE
.\Invoke-NinaUi.ps1 state
.\Invoke-NinaUi.ps1 invoke -Name Recalc
.\Invoke-NinaUi.ps1 toggle -Name Quality -State On
.\Invoke-NinaUi.ps1 select-profile -Name Default
.\Invoke-NinaUi.ps1 scroll -Direction Down -Notches 30
.\Invoke-NinaUi.ps1 screenshot -Path C:\temp\out.png -ScrollDirection Down
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('state', 'invoke', 'toggle', 'select-profile', 'scroll', 'screenshot')]
    [string]$Command,

    # invoke: ClearData|ExportCsv|ExportReport|Recalc
    # toggle: Persistence|MultiProfile|Quality|Optimizer
    # select-profile: the profile name to switch to
    [string]$Name,

    # toggle only
    [ValidateSet('On', 'Off')]
    [string]$State,

    # scroll (and optionally screenshot)
    [ValidateSet('Down', 'Up')]
    [string]$Direction = 'Down',
    [int]$Notches = 30,

    # screenshot
    [string]$Path,
    [ValidateSet('Down', 'Up')]
    [string]$ScrollDirection,

    [string]$HostName = '127.0.0.1',
    [int]$Port = 4406,
    [int]$TimeoutSec = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\BridgeClient.ps1"
. "$PSScriptRoot\NinaUi.ps1"

if (-not (Get-Process -Name 'NINA' -ErrorAction SilentlyContinue)) {
    Write-Error "NINA is not running. Start it (e.g. Run-SmokeTest.ps1 -HoldForInspection) before using Invoke-NinaUi.ps1."
    exit 1
}

$bridgeCommands = @('state', 'invoke', 'toggle', 'select-profile')
$bridge = $null
try {
    if ($Command -in $bridgeCommands) {
        try {
            $bridge = Connect-Bridge -HostName $HostName -Port $Port -TimeoutSec $TimeoutSec
        } catch {
            Write-Error "Could not connect to SmokeTestBridge at ${HostName}:${Port} ($($_.Exception.Message)). Is smoketest_settings.txt set to True and NINA fully started?"
            exit 1
        }
    }

    switch ($Command) {
        'state' {
            Get-BridgeState $bridge | ConvertTo-Json -Depth 8
        }
        'invoke' {
            if ($Name -notin @('ClearData', 'ExportCsv', 'ExportReport', 'Recalc')) {
                throw "-Name must be one of ClearData|ExportCsv|ExportReport|Recalc for 'invoke' (got '$Name')"
            }
            Invoke-BridgeCommand $bridge -Name $Name
            Write-Host "OK: invoked $Name"
        }
        'toggle' {
            if ($Name -notin @('Persistence', 'MultiProfile', 'Quality', 'Optimizer')) {
                throw "-Name must be one of Persistence|MultiProfile|Quality|Optimizer for 'toggle' (got '$Name')"
            }
            if (-not $State) { throw "-State On|Off is required for 'toggle'" }
            $value = $State -eq 'On'
            $prior = Set-BridgeToggle $bridge -Name $Name -Value $value
            Write-Host "OK: $Name set to $value (was $prior)"
        }
        'select-profile' {
            if (-not $Name) { throw "-Name <profile name> is required for 'select-profile'" }
            Select-BridgeProfile $bridge -Name $Name
            Write-Host "OK: selected profile '$Name'"
        }
        'scroll' {
            Invoke-PanelScroll -Direction $Direction -Notches $Notches
            Write-Host "OK: scrolled $Direction ($Notches notches)"
        }
        'screenshot' {
            if (-not $Path) { throw "-Path <output file> is required for 'screenshot'" }
            if (-not (Test-ScreenCaptureAvailable)) {
                throw 'Screen capture unavailable (CopyFromScreen failed) - the RDP session must be actively displayed.'
            }
            if ($ScrollDirection) { Invoke-PanelScroll -Direction $ScrollDirection }
            Save-Screenshot -Path $Path
            Write-Host "OK: screenshot saved to $Path"
        }
    }
} catch {
    Write-Error $_.Exception.Message
    exit 1
} finally {
    if ($bridge) { Disconnect-Bridge $bridge }
}
