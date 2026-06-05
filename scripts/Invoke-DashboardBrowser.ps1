param(
    [Parameter(Mandatory = $true)]
    [string]$Expression,
    [string]$Url = "http://localhost:5087/",
    [int]$Port = 9222,
    [int]$CdpTimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

$edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")
$profile = Join-Path $repoRoot ".scratch\edge-profile"
New-Item -ItemType Directory -Force -Path $profile | Out-Null

function Test-DevTools {
    try {
        Invoke-WebRequest -UseBasicParsing "http://localhost:$Port/json/version" -TimeoutSec 1 | Out-Null
        return $true
    } catch {
        return $false
    }
}

if (-not (Test-DevTools)) {
    Start-Process `
        -FilePath $edge `
        -ArgumentList @(
            "--headless=new",
            "--disable-gpu",
            "--remote-debugging-port=$Port",
            "--user-data-dir=$profile",
            $Url
        ) `
        -WindowStyle Hidden

    $ready = $false
    for ($i = 0; $i -lt 25; $i++) {
        Start-Sleep -Milliseconds 200
        if (Test-DevTools) {
            $ready = $true
            break
        }
    }

    if (-not $ready) {
        throw "Edge DevTools endpoint did not become ready on port $Port."
    }
}

function New-DashboardTarget {
    $escaped = [Uri]::EscapeDataString($Url)
    $created = $null
    try {
        $created = Invoke-WebRequest -UseBasicParsing -Method Put "http://localhost:$Port/json/new?$escaped" |
            Select-Object -ExpandProperty Content |
            ConvertFrom-Json
    } catch {
        $created = Invoke-WebRequest -UseBasicParsing "http://localhost:$Port/json/new?$escaped" |
            Select-Object -ExpandProperty Content |
            ConvertFrom-Json
    }

    if ($null -ne $created -and ($created.PSObject.Properties.Name -contains "webSocketDebuggerUrl")) {
        return $created
    }

    Start-Sleep -Milliseconds 500
    $pages = Invoke-WebRequest -UseBasicParsing "http://localhost:$Port/json/list" | Select-Object -ExpandProperty Content | ConvertFrom-Json
    return $pages |
        Where-Object { $_.type -eq "page" -and $_.url -like "$Url*" } |
        Sort-Object id -Descending |
        Select-Object -First 1
}

$target = New-DashboardTarget
if ($null -eq $target) {
    throw "Dashboard tab was not found for $Url."
}

$socket = [System.Net.WebSockets.ClientWebSocket]::new()
[void]$socket.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()

$script:id = 0
function Send-Cdp {
    param(
        [string]$Method,
        [hashtable]$Params = @{}
    )

    $script:id += 1
    $payload = @{
        id = $script:id
        method = $Method
        params = $Params
    } | ConvertTo-Json -Depth 20 -Compress

    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
    [void]$socket.SendAsync(
        [ArraySegment[byte]]::new($bytes),
        [System.Net.WebSockets.WebSocketMessageType]::Text,
        $true,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()

    $buffer = New-Object byte[] 65536
    $stream = [IO.MemoryStream]::new()
    $cts = [Threading.CancellationTokenSource]::new()
    $cts.CancelAfter([TimeSpan]::FromSeconds($CdpTimeoutSeconds))
    try {
        while ($true) {
            try {
                $result = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), $cts.Token).GetAwaiter().GetResult()
            } catch [OperationCanceledException] {
                throw "Timed out waiting for Chrome DevTools response to $Method after $CdpTimeoutSeconds seconds."
            }

            $stream.Write($buffer, 0, $result.Count)
            if ($result.EndOfMessage) {
                $message = [Text.Encoding]::UTF8.GetString($stream.ToArray())
                $stream.Dispose()
                $response = $message | ConvertFrom-Json
                if (($response.PSObject.Properties.Name -contains "id") -and $response.id -eq $script:id) {
                    if (($response.PSObject.Properties.Name -contains "error") -and $null -ne $response.error) {
                        throw "Chrome DevTools $Method failed: $($response.error.message)"
                    }

                    return $response
                }

                $stream = [IO.MemoryStream]::new()
            }
        }
    } finally {
        $stream.Dispose()
        $cts.Dispose()
    }
}

function Invoke-RuntimeEvaluate {
    param([hashtable]$Params)

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            return Send-Cdp "Runtime.evaluate" $Params
        } catch {
            $message = $_.Exception.Message
            if ($message -notlike "*Execution context was destroyed*" -or $attempt -eq 3) {
                throw
            }

            Start-Sleep -Milliseconds (250 * $attempt)
        }
    }
}

try {
    [void](Send-Cdp "Runtime.enable")
    [void](Invoke-RuntimeEvaluate @{
        expression = "(async () => { for (let i = 0; i < 80; i++) { if (window.__dashboardReady === true && document.querySelector('#dashboard-content') && document.querySelector('form[data-action=""/api/goals""]')) return true; await new Promise(resolve => setTimeout(resolve, 125)); } return false; })()"
        awaitPromise = $true
        returnByValue = $true
    })
    $response = Invoke-RuntimeEvaluate @{
        expression = $Expression
        awaitPromise = $true
        returnByValue = $true
    }

    $hasTopLevelException = ($response.PSObject.Properties.Name -contains "exceptionDetails") -and $null -ne $response.exceptionDetails
    $hasResult = ($response.PSObject.Properties.Name -contains "result") -and $null -ne $response.result
    $hasResultException = $hasResult -and ($response.result.PSObject.Properties.Name -contains "exceptionDetails") -and $null -ne $response.result.exceptionDetails

    $exceptionDetails = if ($hasTopLevelException) {
        $response.exceptionDetails
    } elseif ($hasResultException) {
        $response.result.exceptionDetails
    } else {
        $null
    }

    if ($null -ne $exceptionDetails) {
        $description = $exceptionDetails.exception.description
        if ([string]::IsNullOrWhiteSpace($description)) {
            $description = $exceptionDetails.text
        }

        throw "Dashboard browser script failed: $description"
    }

    $response | ConvertTo-Json -Depth 20
} finally {
    if ($socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
        try {
            [void]$socket.CloseOutputAsync(
                [System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
                "done",
                [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        } catch {
            $socket.Abort()
        }
    }

    $socket.Dispose()
    if ($target.PSObject.Properties.Name -contains "id") {
        try {
            Invoke-WebRequest -UseBasicParsing "http://localhost:$Port/json/close/$($target.id)" | Out-Null
        } catch {
        }
    }
}
