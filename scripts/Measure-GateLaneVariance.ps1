[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ReceiptRoot,

    [string] $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,

    [string] $FloorCommit = '1f66945092e8c30eee42b1316b7373708c61e69d'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$laneSlugs = [ordered]@{
    'Worker dispatch fixtures' = 'worker-dispatch-fixtures'
    'Goal acceptance build slots' = 'goal-acceptance-build-slots'
    'Goal worktree cleanup' = 'goal-worktree-cleanup'
    'Process spawning' = 'process-spawning'
    'Goal lifecycle commands' = 'goal-lifecycle-commands'
    'Dotnet build slots' = 'dotnet-build-slots'
}

function Get-Percentile {
    param(
        [double[]] $Values,
        [double] $Probability
    )

    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 1) {
        return [double] $sorted[0]
    }

    # R-7 / Excel PERCENTILE.INC linear interpolation.
    $position = ($sorted.Count - 1) * $Probability
    $lower = [Math]::Floor($position)
    $upper = [Math]::Ceiling($position)
    return [double] $sorted[$lower] +
        ($position - $lower) * ([double] $sorted[$upper] - [double] $sorted[$lower])
}

function Get-PearsonCorrelation {
    param(
        [object[]] $Rows,
        [string] $PredictorProperty
    )

    $predictor = @($Rows | ForEach-Object { [double] $_.$PredictorProperty })
    $duration = @($Rows | ForEach-Object { [double] $_.DurationSeconds })
    $predictorMean = ($predictor | Measure-Object -Average).Average
    $durationMean = ($duration | Measure-Object -Average).Average
    $predictorSquares = 0.0
    $durationSquares = 0.0
    $crossProducts = 0.0

    for ($index = 0; $index -lt $predictor.Count; $index++) {
        $predictorDelta = $predictor[$index] - $predictorMean
        $durationDelta = $duration[$index] - $durationMean
        $predictorSquares += $predictorDelta * $predictorDelta
        $durationSquares += $durationDelta * $durationDelta
        $crossProducts += $predictorDelta * $durationDelta
    }

    if ($predictorSquares -eq 0 -or $durationSquares -eq 0) {
        return $null
    }

    return $crossProducts / [Math]::Sqrt($predictorSquares * $durationSquares)
}

function Get-PeakOverlap {
    param(
        [DateTimeOffset] $WindowStart,
        [DateTimeOffset] $WindowEnd,
        [object[]] $Intervals
    )

    $points = [System.Collections.Generic.List[DateTimeOffset]]::new()
    $points.Add($WindowStart)
    foreach ($interval in $Intervals) {
        if ($interval.Start -ge $WindowStart -and $interval.Start -le $WindowEnd) {
            $points.Add($interval.Start)
        }
        if ($interval.End -ge $WindowStart -and $interval.End -le $WindowEnd) {
            $points.Add($interval.End)
        }
    }

    $peak = 0
    foreach ($point in $points) {
        $active = @(
            $Intervals |
                Where-Object { $_.Start -le $point -and $_.End -ge $point }
        ).Count
        if ($active -gt $peak) {
            $peak = $active
        }
    }

    return $peak
}

function ConvertTo-DateTimeOffset {
    param([object] $Value)

    if ($Value -is [DateTimeOffset]) {
        return [DateTimeOffset] $Value
    }
    if ($Value -is [DateTime]) {
        return [DateTimeOffset] ([DateTime] $Value)
    }

    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string] $Value, [ref] $parsed)) {
        return $null
    }
    return $parsed
}

function Get-MeanText {
    param([object[]] $Rows)

    return '{0:F1}' -f (($Rows.DurationSeconds | Measure-Object -Average).Average)
}

if (-not (Test-Path -LiteralPath $ReceiptRoot -PathType Container)) {
    throw "Receipt root does not exist: $ReceiptRoot"
}

$floorTimestampText = & git -C $RepositoryRoot show -s --format=%cI $FloorCommit
if ($LASTEXITCODE -ne 0) {
    throw "Floor commit is unavailable: $FloorCommit"
}
$floorTimestamp = ConvertTo-DateTimeOffset $floorTimestampText
if ($null -eq $floorTimestamp) {
    throw "Floor commit timestamp is invalid: $floorTimestampText"
}

$attemptFiles = @(Get-ChildItem -LiteralPath $ReceiptRoot -Recurse -Filter '*.attempt.json' -File)
$attempts = @{}
$commitEligibility = @{}
$gateIntervals = [System.Collections.Generic.List[object]]::new()
$attemptParseFailures = 0
$goalIdentityMismatches = 0

foreach ($file in $attemptFiles) {
    try {
        $attempt = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
        $startedAt = ConvertTo-DateTimeOffset $attempt.startedAt
    }
    catch {
        $attemptParseFailures++
        continue
    }

    if (-not $attempt.attemptId -or $null -eq $startedAt) {
        $attemptParseFailures++
        continue
    }

    # This timestamp check only bounds expensive git calls. Ancestry below is the
    # actual floor qualification, so clock representation cannot admit a receipt.
    $eligible = $false
    if ($startedAt -ge $floorTimestamp) {
        $branchHead = [string] $attempt.branchHeadSha
        if (-not $commitEligibility.ContainsKey($branchHead)) {
            & git -C $RepositoryRoot merge-base --is-ancestor $FloorCommit $branchHead 2>$null
            $commitEligibility[$branchHead] = $LASTEXITCODE -eq 0
        }
        $eligible = $commitEligibility[$branchHead]
    }

    $attempts[[string] $attempt.attemptId] = [pscustomobject]@{
        Data = $attempt
        Directory = $file.DirectoryName
        Eligible = $eligible
        Start = $startedAt
        End = $null
    }

    if (-not $eligible) {
        continue
    }

    $endValue = if ($attempt.completedAt) { $attempt.completedAt } else { $attempt.lastHeartbeatAt }
    $endedAt = ConvertTo-DateTimeOffset $endValue
    if ($null -eq $endedAt -or $endedAt -lt $startedAt) {
        continue
    }

    if ($file.Directory.Name -ne [string] $attempt.goalId) {
        $goalIdentityMismatches++
    }
    $attempts[[string] $attempt.attemptId].End = $endedAt
    $gateIntervals.Add([pscustomobject]@{
        AttemptId = [string] $attempt.attemptId
        GoalId = [string] $attempt.goalId
        Start = $startedAt
        End = $endedAt
    })
}

$qualifiedRows = [System.Collections.Generic.List[object]]::new()
$exclusions = [ordered]@{}

foreach ($entry in $laneSlugs.GetEnumerator()) {
    $laneName = $entry.Key
    $laneSlug = $entry.Value
    $counts = [ordered]@{
        Candidate = 0
        Qualified = 0
        MissingAttempt = 0
        BeforeFloorOrUnknownCommit = 0
        HeartbeatParseFailure = 0
        StateNotCompleted = 0
        ExitCodeNotZero = 0
        InvalidTimestamps = 0
    }

    $heartbeatFiles = @(
        Get-ChildItem -LiteralPath $ReceiptRoot -Recurse -Filter "*$laneSlug*.gate-heartbeat.json" -File
    )
    foreach ($file in $heartbeatFiles) {
        $counts.Candidate++
        $marker = ".infrastructure-tests-$laneSlug-"
        $markerIndex = $file.Name.IndexOf($marker, [StringComparison]::Ordinal)
        if ($markerIndex -lt 1) {
            $counts.MissingAttempt++
            continue
        }

        $attemptId = $file.Name.Substring(0, $markerIndex)
        if (-not $attempts.ContainsKey($attemptId)) {
            $counts.MissingAttempt++
            continue
        }
        if (-not $attempts[$attemptId].Eligible) {
            $counts.BeforeFloorOrUnknownCommit++
            continue
        }

        try {
            $heartbeat = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
        }
        catch {
            $counts.HeartbeatParseFailure++
            continue
        }

        if ([string] $heartbeat.state -ne 'completed') {
            $counts.StateNotCompleted++
            continue
        }
        if ($null -eq $heartbeat.exitCode -or [int] $heartbeat.exitCode -ne 0) {
            $counts.ExitCodeNotZero++
            continue
        }

        $startedAt = ConvertTo-DateTimeOffset $heartbeat.startedAt
        $endedAt = ConvertTo-DateTimeOffset $heartbeat.lastObservedAt
        if ($null -eq $startedAt -or $null -eq $endedAt -or $endedAt -lt $startedAt) {
            $counts.InvalidTimestamps++
            continue
        }

        $laneIntervals = [System.Collections.Generic.List[object]]::new()
        $attemptDirectory = $attempts[$attemptId].Directory
        $attemptHeartbeatFiles = @(
            Get-ChildItem -LiteralPath $attemptDirectory -Filter "$attemptId.*.gate-heartbeat.json" -File
        )
        foreach ($attemptHeartbeatFile in $attemptHeartbeatFiles) {
            try {
                $candidate = Get-Content -Raw -LiteralPath $attemptHeartbeatFile.FullName |
                    ConvertFrom-Json
                $candidateStart = ConvertTo-DateTimeOffset $candidate.startedAt
                $candidateEnd = ConvertTo-DateTimeOffset $candidate.lastObservedAt
                if ($null -ne $candidateStart -and $null -ne $candidateEnd -and
                    $candidateEnd -ge $candidateStart) {
                    $laneIntervals.Add([pscustomobject]@{
                        Start = $candidateStart
                        End = $candidateEnd
                    })
                }
            }
            catch {
                # Invalid sibling intervals cannot contribute to overlap.
            }
        }

        $attempt = $attempts[$attemptId].Data
        $qualifiedRows.Add([pscustomobject]@{
            Lane = $laneName
            GoalPrefix = [string] $attempt.goalPrefix
            AttemptId = $attemptId
            Start = $startedAt
            DurationSeconds = ($endedAt - $startedAt).TotalSeconds
            PeakConcurrentLanes = Get-PeakOverlap $startedAt $endedAt $laneIntervals
            PeakConcurrentGates = Get-PeakOverlap $startedAt $endedAt $gateIntervals
        })
        $counts.Qualified++
    }

    $exclusions[$laneName] = $counts
}

Write-Output "floor_commit=$FloorCommit"
Write-Output "floor_timestamp_utc=$($floorTimestamp.UtcDateTime.ToString('o'))"
Write-Output "attempt_files=$($attemptFiles.Count) attempt_parse_failures=$attemptParseFailures eligible_gate_intervals=$($gateIntervals.Count) goal_identity_mismatches=$goalIdentityMismatches"
Write-Output ''
Write-Output 'DISTRIBUTIONS lane|n|mean|min|p50|p90|p99|max|shortfall_to_20'
foreach ($laneName in $laneSlugs.Keys) {
    $rows = @($qualifiedRows | Where-Object Lane -eq $laneName)
    $values = [double[]] @($rows.DurationSeconds)
    $mean = '{0:F1}' -f (($values | Measure-Object -Average).Average)
    $minimum = '{0:F1}' -f (($values | Measure-Object -Minimum).Minimum)
    $p50 = '{0:F1}' -f (Get-Percentile $values 0.50)
    $p90 = '{0:F1}' -f (Get-Percentile $values 0.90)
    $p99 = '{0:F1}' -f (Get-Percentile $values 0.99)
    $maximum = '{0:F1}' -f (($values | Measure-Object -Maximum).Maximum)
    Write-Output "$laneName|$($rows.Count)|$mean|$minimum|$p50|$p90|$p99|$maximum|$(20 - $rows.Count)"
}

Write-Output ''
Write-Output 'EXCLUSIONS lane|candidate|qualified|missing_attempt|before_floor_or_unknown_commit|heartbeat_parse|state|exit_code|timestamps'
foreach ($laneName in $laneSlugs.Keys) {
    $counts = $exclusions[$laneName]
    Write-Output "$laneName|$($counts.Candidate)|$($counts.Qualified)|$($counts.MissingAttempt)|$($counts.BeforeFloorOrUnknownCommit)|$($counts.HeartbeatParseFailure)|$($counts.StateNotCompleted)|$($counts.ExitCodeNotZero)|$($counts.InvalidTimestamps)"
}

Write-Output ''
Write-Output 'CORRELATIONS lane|peak_lanes_r|peak_gates_r|lane_partitions|gate_partitions'
foreach ($laneName in $laneSlugs.Keys) {
    $rows = @($qualifiedRows | Where-Object Lane -eq $laneName)
    $laneCorrelation = Get-PearsonCorrelation $rows 'PeakConcurrentLanes'
    $gateCorrelation = Get-PearsonCorrelation $rows 'PeakConcurrentGates'
    $laneCorrelationText = if ($null -eq $laneCorrelation) { 'not-estimable' } else { '{0:F3}' -f $laneCorrelation }
    $gateCorrelationText = if ($null -eq $gateCorrelation) { 'not-estimable' } else { '{0:F3}' -f $gateCorrelation }
    $lanePartitions = @(
        $rows |
            Group-Object PeakConcurrentLanes |
            Sort-Object { [int] $_.Name } |
            ForEach-Object { "$($_.Name):n$($_.Count):mean$(Get-MeanText $_.Group)" }
    ) -join ','
    $gatePartitions = @(
        $rows |
            Group-Object PeakConcurrentGates |
            Sort-Object { [int] $_.Name } |
            ForEach-Object { "$($_.Name):n$($_.Count):mean$(Get-MeanText $_.Group)" }
    ) -join ','
    Write-Output "$laneName|$laneCorrelationText|$gateCorrelationText|$lanePartitions|$gatePartitions"
}

Write-Output ''
Write-Output 'RUNS lane|goal|attempt_suffix|seconds|peak_lanes|peak_gates|start_utc'
$qualifiedRows |
    Sort-Object Lane, Start |
    ForEach-Object {
        $duration = '{0:F1}' -f $_.DurationSeconds
        $attemptSuffix = $_.AttemptId.Substring($_.AttemptId.Length - 8)
        $startUtc = $_.Start.UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')
        Write-Output "$($_.Lane)|$($_.GoalPrefix)|$attemptSuffix|$duration|$($_.PeakConcurrentLanes)|$($_.PeakConcurrentGates)|$startUtc"
    }
