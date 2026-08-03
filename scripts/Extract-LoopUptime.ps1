<#
.SYNOPSIS
  Extract conductor loop generations and stop-attributed gaps from typed event records.

.DESCRIPTION
  Reads LOOP_START/LOOP_STOP records from the current and rotated
  conduct-events*.log JSONL files. Embedded timestamps are authoritative; filenames and
  file mtimes are deliberately ignored. The output is one queryable object per observed
  generation boundary, including completeness, stop cause, uptime, and the gap before
  the next observed loop start.

  This script does not read operator-conduct-*.out.log files and does not calculate an
  uptime percentage. Missing start/stop boundaries remain explicit nulls so incomplete
  retention cannot be mistaken for measured uptime or downtime.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Extract-LoopUptime.ps1 |
    Where-Object Completeness -eq complete

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Extract-LoopUptime.ps1 -AsJson
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$LogDirectory,
    [switch]$AsJson,
    [switch]$SkipMalformed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $LogDirectory = Join-Path $repoRoot ".orchestrator\logs"
}

$resolvedLogDirectory = [System.IO.Path]::GetFullPath($LogDirectory)
if (-not (Test-Path -LiteralPath $resolvedLogDirectory -PathType Container)) {
    throw "Conduct event log directory does not exist: $resolvedLogDirectory"
}

$eventFiles = @(
    Get-ChildItem -LiteralPath $resolvedLogDirectory -File -Filter "conduct-events*.log" |
        Sort-Object -Property FullName
)
if ($eventFiles.Count -eq 0) {
    throw "No conduct-events*.log files were found in: $resolvedLogDirectory"
}

$events = [System.Collections.Generic.List[object]]::new()
$malformedCount = 0
foreach ($file in $eventFiles) {
    $lineNumber = 0
    foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $record = $line | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            $message = "Malformed conduct event JSON at $($file.FullName):${lineNumber}: $($_.Exception.Message)"
            if (-not $SkipMalformed) {
                throw $message
            }

            $malformedCount++
            Write-Warning $message
            continue
        }

        $eventKindProperty = $record.PSObject.Properties['eventKind']
        if ($null -eq $eventKindProperty -or [string]::IsNullOrWhiteSpace([string]$eventKindProperty.Value)) {
            $message = "Conduct event record has no eventKind at $($file.FullName):${lineNumber}"
            if (-not $SkipMalformed) {
                throw $message
            }

            $malformedCount++
            Write-Warning $message
            continue
        }

        $eventKind = [string]$eventKindProperty.Value
        if ($eventKind -notin @("loop-start", "loop-stop")) {
            continue
        }

        try {
            $timestamp = [DateTimeOffset]::Parse(
                [string]$record.timestamp,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::RoundtripKind)
        }
        catch {
            $message = "Invalid loop event timestamp at $($file.FullName):${lineNumber}: '$($record.timestamp)'"
            if (-not $SkipMalformed) {
                throw $message
            }

            $malformedCount++
            Write-Warning $message
            continue
        }

        $events.Add([pscustomobject]@{
            Timestamp = $timestamp
            EventKind = $eventKind
            Detail = [string]$record.detail
            SourceFile = $file.FullName
            SourceLine = $lineNumber
        })
    }
}

if ($events.Count -eq 0) {
    throw "No typed loop-start or loop-stop events were found in: $resolvedLogDirectory"
}

$orderedEvents = @(
    $events |
        Sort-Object -Property Timestamp, SourceFile, SourceLine
)

function Get-StopCause {
    param([string]$Detail)

    $match = [regex]::Match($Detail, '(?:^|\s)reason=(?<reason>\S+)')
    if ($match.Success) {
        return $match.Groups['reason'].Value
    }

    return "unknown"
}

function New-LoopBoundary {
    param(
        $StartEvent,
        $StopEvent,
        [string]$Completeness
    )

    $uptimeMinutes = $null
    if ($null -ne $StartEvent -and $null -ne $StopEvent) {
        $uptimeMinutes = [Math]::Round(
            ($StopEvent.Timestamp - $StartEvent.Timestamp).TotalMinutes,
            3)
    }

    return [pscustomobject]@{
        StartUtc = if ($null -eq $StartEvent) { $null } else { $StartEvent.Timestamp }
        StopUtc = if ($null -eq $StopEvent) { $null } else { $StopEvent.Timestamp }
        NextStartUtc = $null
        UptimeMinutes = $uptimeMinutes
        GapMinutes = $null
        StopCause = if ($null -eq $StopEvent) { $null } else { Get-StopCause $StopEvent.Detail }
        Completeness = $Completeness
        StartSource = if ($null -eq $StartEvent) { $null } else { "$($StartEvent.SourceFile):$($StartEvent.SourceLine)" }
        StopSource = if ($null -eq $StopEvent) { $null } else { "$($StopEvent.SourceFile):$($StopEvent.SourceLine)" }
    }
}

$boundaries = [System.Collections.Generic.List[object]]::new()
$openStart = $null
foreach ($event in $orderedEvents) {
    if ($event.EventKind -eq "loop-start") {
        if ($null -ne $openStart) {
            $boundaries.Add((New-LoopBoundary -StartEvent $openStart -StopEvent $null -Completeness "missing-stop"))
        }

        $openStart = $event
        continue
    }

    if ($null -eq $openStart) {
        $boundaries.Add((New-LoopBoundary -StartEvent $null -StopEvent $event -Completeness "missing-start"))
        continue
    }

    $boundaries.Add((New-LoopBoundary -StartEvent $openStart -StopEvent $event -Completeness "complete"))
    $openStart = $null
}

if ($null -ne $openStart) {
    $boundaries.Add((New-LoopBoundary -StartEvent $openStart -StopEvent $null -Completeness "open-at-extraction-boundary"))
}

$startEvents = @($orderedEvents | Where-Object EventKind -eq "loop-start")
foreach ($boundary in $boundaries) {
    if ($null -eq $boundary.StopUtc) {
        continue
    }

    $nextStart = $startEvents |
        Where-Object Timestamp -ge $boundary.StopUtc |
        Select-Object -First 1
    if ($null -eq $nextStart) {
        continue
    }

    $boundary.NextStartUtc = $nextStart.Timestamp
    $boundary.GapMinutes = [Math]::Round(
        ($nextStart.Timestamp - $boundary.StopUtc).TotalMinutes,
        3)
}

$result = @(
    $boundaries |
        Sort-Object -Property @{ Expression = {
            if ($null -ne $_.StartUtc) { $_.StartUtc } else { $_.StopUtc }
        } }
)

if ($malformedCount -gt 0) {
    Write-Warning "Skipped $malformedCount malformed conduct event record(s); results are incomplete."
}

if ($AsJson) {
    ConvertTo-Json -InputObject $result -Depth 4
}
else {
    $result
}
