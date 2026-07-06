# Phd2Rpc.ps1 - dot-sourceable helpers for talking to PHD2's event server
# (TCP, one JSON object per line; see https://github.com/OpenPHDGuiding/phd2/wiki/EventMonitoring)
#
# Usage:
#   . "$PSScriptRoot\Phd2Rpc.ps1"
#   $phd2 = Connect-Phd2
#   Invoke-Phd2Rpc $phd2 'get_app_state'
#   Invoke-Phd2Rpc $phd2 'dither' @{ amount = 3.0; raOnly = $false; settle = @{ pixels = 1.5; time = 8; timeout = 60 } }
#   Wait-Phd2Event $phd2 'SettleDone' -TimeoutSec 90
#   Disconnect-Phd2 $phd2

Set-StrictMode -Version Latest

$script:Phd2RpcId = 0

function Test-Phd2Port {
    param(
        [string]$HostName = '127.0.0.1',
        [int]$Port = 4400,
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

function Connect-Phd2 {
    param(
        [string]$HostName = '127.0.0.1',
        [int]$Port = 4400
    )
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect($HostName, $Port)
    $stream = $tcp.GetStream()
    $stream.ReadTimeout = 1000   # per-ReadLine timeout; waiting loops handle the deadline
    $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
    $writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($false)))
    $writer.AutoFlush = $true
    [pscustomobject]@{
        Client = $tcp
        Reader = $reader
        Writer = $writer
    }
}

function Disconnect-Phd2 {
    param([Parameter(Mandatory)]$Connection)
    try { $Connection.Reader.Dispose() } catch { }
    try { $Connection.Writer.Dispose() } catch { }
    try { $Connection.Client.Close() } catch { }
}

# Read one line, $null on read timeout (so callers can enforce their own deadline)
function Read-Phd2Line {
    param([Parameter(Mandatory)]$Connection)
    try {
        return $Connection.Reader.ReadLine()
    } catch [System.IO.IOException] {
        return $null
    }
}

function Invoke-Phd2Rpc {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][string]$Method,
        $Params = $null,
        [int]$TimeoutSec = 15
    )
    $script:Phd2RpcId++
    $id = $script:Phd2RpcId

    $request = @{ method = $Method; id = $id }
    if ($null -ne $Params) { $request.params = $Params }
    $json = $request | ConvertTo-Json -Depth 6 -Compress
    $Connection.Writer.WriteLine($json)

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $line = Read-Phd2Line $Connection
        if ($null -eq $line) { continue }
        try { $msg = $line | ConvertFrom-Json } catch { continue }

        # Skip event notifications; we only want the response with our id
        if ($msg.PSObject.Properties['Event']) { continue }
        if (-not $msg.PSObject.Properties['id'] -or $msg.id -ne $id) { continue }

        if ($msg.PSObject.Properties['error']) {
            throw "PHD2 RPC '$Method' failed: $($msg.error.message) (code $($msg.error.code))"
        }
        return $msg.result
    }
    throw "PHD2 RPC '$Method' timed out after $TimeoutSec s"
}

# Wait until an event with the given name arrives; returns the parsed event object
function Wait-Phd2Event {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][string]$Name,
        [int]$TimeoutSec = 60
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $line = Read-Phd2Line $Connection
        if ($null -eq $line) { continue }
        try { $msg = $line | ConvertFrom-Json } catch { continue }
        if ($msg.PSObject.Properties['Event'] -and $msg.Event -eq $Name) {
            return $msg
        }
    }
    throw "Timed out after $TimeoutSec s waiting for PHD2 event '$Name'"
}

# Poll get_app_state until PHD2 reaches the given state (e.g. 'Guiding', 'Looping')
function Wait-Phd2AppState {
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][string]$State,
        [int]$TimeoutSec = 300
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $current = Invoke-Phd2Rpc $Connection 'get_app_state'
        if ($current -eq $State) { return }
        Write-Verbose "PHD2 app state: $current (waiting for $State)"
        Start-Sleep -Seconds 2
    }
    throw "Timed out after $TimeoutSec s waiting for PHD2 app state '$State'"
}
