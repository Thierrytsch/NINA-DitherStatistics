# BridgeClient.ps1 - dot-sourceable helpers for talking to the plugin's
# SmokeTestBridge (Services/SmokeTestBridge.cs): a localhost-only, line-delimited
# JSON diagnostic channel that reads the exact values the panel binds to and
# drives the same commands/toggles the panel buttons/checkboxes offer. Disabled
# by default; only listens when %LocalAppData%\NINA\DitherStatistics\smoketest_settings.txt
# line 1 is 'True'. See CLAUDE.md / SmokeTest/README.md for the security framing.
#
# Usage:
#   . "$PSScriptRoot\BridgeClient.ps1"
#   $bridge = Connect-Bridge
#   $state = Get-BridgeState $bridge
#   Invoke-BridgeCommand $bridge -Name Recalc
#   $prior = Set-BridgeToggle $bridge -Name Quality -Value $true
#   New-BridgeProfile $bridge -Name SmokeTestB
#   Select-BridgeProfile $bridge -Name Default
#   Remove-BridgeProfile $bridge -Name SmokeTestB
#   Disconnect-Bridge $bridge

Set-StrictMode -Version Latest

function Test-BridgePort {
    param(
        [string]$HostName = '127.0.0.1',
        [int]$Port = 4406,
        [int]$TimeoutMs = 1000
    )
    $tcp = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $tcp.BeginConnect($HostName, $Port, $null, $null)
        if ($async.AsyncWaitHandle.WaitOne($TimeoutMs) -and $tcp.Connected) {
            $tcp.EndConnect($async)
            return $true
        }
        return $false
    } catch {
        return $false
    } finally {
        $tcp.Close()
    }
}

# Connects, retrying until -TimeoutSec elapses - the bridge only starts listening
# once the VM constructor runs, which happens some seconds after the NINA process
# is up, so callers waiting right after Start-Process need the retry.
function Connect-Bridge {
    param(
        [string]$HostName = '127.0.0.1',
        [int]$Port = 4406,
        [int]$TimeoutSec = 30
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $lastError = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $tcp = New-Object System.Net.Sockets.TcpClient
            $tcp.Connect($HostName, $Port)
            $stream = $tcp.GetStream()
            $stream.ReadTimeout = 10000
            $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
            $writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($false)))
            $writer.AutoFlush = $true
            return [pscustomobject]@{
                Client = $tcp
                Reader = $reader
                Writer = $writer
            }
        } catch {
            $lastError = $_
            Start-Sleep -Seconds 1
        }
    }
    throw "Could not connect to SmokeTestBridge at ${HostName}:${Port} within $TimeoutSec s (last error: $($lastError.Exception.Message))"
}

function Disconnect-Bridge {
    param([Parameter(Mandatory)]$Connection)
    try { $Connection.Reader.Dispose() } catch { }
    try { $Connection.Writer.Dispose() } catch { }
    try { $Connection.Client.Close() } catch { }
}

# Sends {"cmd":$Cmd, ...$Params} as one JSON line and returns the parsed response
# object. Throws if the bridge responded with ok:false.
function Invoke-Bridge {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][string]$Cmd,
        [hashtable]$Params = $null
    )
    $request = @{ cmd = $Cmd }
    if ($Params) {
        foreach ($key in $Params.Keys) { $request[$key] = $Params[$key] }
    }
    $json = $request | ConvertTo-Json -Depth 6 -Compress
    $Connection.Writer.WriteLine($json)

    $line = $Connection.Reader.ReadLine()
    if ($null -eq $line) { throw "SmokeTestBridge '$Cmd' failed: connection closed without a response" }
    $response = $line | ConvertFrom-Json
    if (-not $response.ok) {
        throw "SmokeTestBridge '$Cmd' failed: $($response.error)"
    }
    return $response
}

# The exact raw values the view binds to (TotalDithers, SuccessRate, Quality
# block, Optimizer block, Toggles, ProfileNames, ...). See SmokeTestBridge.cs /
# DitherStatisticsVM.SmokeTestBridgeAdapter.GetState for the full key contract.
function Get-BridgeState {
    param([Parameter(Mandatory)]$Connection)
    return (Invoke-Bridge $Connection 'get-state').state
}

# Executes one of the panel's RelayCommands: ClearData, ExportCsv, ExportReport, Recalc
function Invoke-BridgeCommand {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][ValidateSet('ClearData', 'ExportCsv', 'ExportReport', 'Recalc')][string]$Name
    )
    Invoke-Bridge $Connection 'invoke' @{ name = $Name } | Out-Null
}

# Sets one of the panel's checkbox-bound toggles: Persistence, MultiProfile, Quality, Optimizer.
# Returns the prior value so callers can restore it afterwards.
function Set-BridgeToggle {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][ValidateSet('Persistence', 'MultiProfile', 'Quality', 'Optimizer')][string]$Name,
        [Parameter(Mandatory)][bool]$Value
    )
    return (Invoke-Bridge $Connection 'set-toggle' @{ name = $Name; value = $Value }).prior
}

function New-BridgeProfile {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][string]$Name
    )
    Invoke-Bridge $Connection 'create-profile' @{ name = $Name } | Out-Null
}

function Select-BridgeProfile {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][string]$Name
    )
    Invoke-Bridge $Connection 'select-profile' @{ name = $Name } | Out-Null
}

function Remove-BridgeProfile {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][string]$Name
    )
    Invoke-Bridge $Connection 'delete-profile' @{ name = $Name } | Out-Null
}
