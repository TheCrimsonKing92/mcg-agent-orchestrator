# Extracts conductor loop uptime/restart intervals from historical operator-conduct logs. Read-only.
param([string]$LogDir = ".orchestrator/logs")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$rows = New-Object System.Collections.Generic.List[object]

foreach ($f in Get-ChildItem -Path $LogDir -Filter "operator-conduct-*.out.log") {
    # filename tail encodes launch time: ...-yyyyMMddHHmmss.out.log
    $startTs = $null
    if ($f.Name -match '-(\d{14})\.out\.log$') {
        $s = $matches[1]
        try {
            $startTs = [datetime]::ParseExact($s, "yyyyMMddHHmmss", $null)
        } catch { $startTs = $null }
    }
    if ($null -eq $startTs) { continue }

    $ticks = $null
    $reason = ""
    $started = $false
    $lastTick = 0
    $stream = $null
    $reader = $null
    try {
        $stream = [System.IO.File]::Open($f.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $reader = New-Object System.IO.StreamReader($stream)
        while ($null -ne ($line = $reader.ReadLine())) {
            if ($line -like "*LOOP_START*") { $started = $true }
            if ($line -match 'LOOP_STOP tick=(\d+) reason=([a-zA-Z-]+)') {
                $ticks = [int]$matches[1]
                $reason = $matches[2]
            }
            elseif ($line -match 'TICK_END tick=(\d+)') {
                $t = [int]$matches[1]
                if ($t -gt $lastTick) { $lastTick = $t }
            }
        }
    }
    catch {
        $reason = "unreadable"
    }
    finally {
        if ($reader) { $reader.Dispose() }
        if ($stream) { $stream.Dispose() }
    }

    $stopTs = $f.LastWriteTime

    # Some older launchers wrote the filename stamp in UTC while file mtime is local,
    # which yields a negative interval. Detect and correct, and FLAG it - do not silently fix.
    $offsetCorrected = $false
    if ($stopTs -lt $startTs) {
        $candidate = $startTs.AddHours(-5)
        if ($stopTs -ge $candidate) {
            $startTs = $candidate
            $offsetCorrected = $true
        }
    }

    $upMin = [Math]::Round(($stopTs - $startTs).TotalMinutes, 1)
    if ($upMin -lt 0) { $upMin = 0 }

    # A row is a real conductor GENERATION only if the loop actually started. One-shot
    # conduct commands and stubs share the filename prefix and must not be counted as uptime.
    $isGeneration = $started -and ($lastTick -gt 0 -or $null -ne $ticks)

    $rows.Add([pscustomobject]@{
        launchedLocal   = $startTs.ToString("yyyy-MM-ddTHH:mm:ss")
        endedLocal      = $stopTs.ToString("yyyy-MM-ddTHH:mm:ss")
        uptimeMin       = $upMin
        isGeneration    = $isGeneration
        offsetCorrected = $offsetCorrected
        loopStarted     = $started
        stopTicks       = if ($null -ne $ticks) { $ticks } else { "" }
        maxTickSeen     = $lastTick
        stopReason      = $reason
        sizeKb          = [Math]::Round($f.Length / 1024.0, 1)
        name            = $f.Name
    })
}

$rows | Sort-Object launchedLocal | ConvertTo-Csv -NoTypeInformation
