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
    if ($sorted.Count -eq 0) {
        return $null
    }
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
        if ($interval.Start -ge $WindowStart -and $interval.Start -lt $WindowEnd -and
            $interval.End -gt $WindowStart) {
            $points.Add($interval.Start)
        }
    }

    $peak = 0
    foreach ($point in $points) {
        $active = @(
            $Intervals |
                Where-Object { $_.Start -le $point -and $_.End -gt $point }
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

function Get-NumberText {
    param([AllowNull()] [object] $Value)

    if ($null -eq $Value) {
        return 'not-available'
    }

    return '{0:F1}' -f [double] $Value
}

function ConvertFrom-TrxDuration {
    param([string] $Value)

    $parsed = [TimeSpan]::Zero
    if (-not [TimeSpan]::TryParse($Value, [Globalization.CultureInfo]::InvariantCulture, [ref] $parsed)) {
        return $null
    }

    return $parsed.TotalSeconds
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

    $attemptEndValue = if ($attempt.completedAt) { $attempt.completedAt } else { $attempt.lastHeartbeatAt }
    $attemptEnd = ConvertTo-DateTimeOffset $attemptEndValue
    $attempts[[string] $attempt.attemptId] = [pscustomobject]@{
        Data = $attempt
        Directory = $file.DirectoryName
        Eligible = $eligible
        Start = $startedAt
        End = $attemptEnd
    }

    if (-not $eligible) {
        continue
    }

    if ($file.Directory.Name -ne [string] $attempt.goalId) {
        $goalIdentityMismatches++
    }
}

# Parse each post-floor heartbeat once. The gate-concurrency signal uses the envelope
# of observed lane work, so preflight attempts that never started a lane do not count
# as active gates. Parse and timestamp failures remain visible instead of silently
# lowering an overlap count.
$eligibleAttempts = [ordered]@{}
$laneIntervalsByAttempt = @{}
$heartbeatNamesByAttempt = @{}
$gateIntervals = [System.Collections.Generic.List[object]]::new()
$siblingHeartbeatParseFailures = 0
$siblingHeartbeatInvalidTimestamps = 0

foreach ($attemptEntry in $attempts.GetEnumerator()) {
    if (-not $attemptEntry.Value.Eligible) {
        continue
    }

    $attemptId = [string] $attemptEntry.Key
    $eligibleAttempts[$attemptId] = $attemptEntry.Value
    $intervals = [System.Collections.Generic.List[object]]::new()
    $heartbeatNames = [System.Collections.Generic.List[string]]::new()
    $attemptHeartbeatFiles = @(
        Get-ChildItem -LiteralPath $attemptEntry.Value.Directory -Filter "$attemptId.*.gate-heartbeat.json" -File
    )
    foreach ($attemptHeartbeatFile in $attemptHeartbeatFiles) {
        $heartbeatNames.Add($attemptHeartbeatFile.Name)
        try {
            $candidate = Get-Content -Raw -LiteralPath $attemptHeartbeatFile.FullName |
                ConvertFrom-Json
        }
        catch {
            $siblingHeartbeatParseFailures++
            continue
        }

        $candidateStart = ConvertTo-DateTimeOffset $candidate.startedAt
        $candidateEnd = ConvertTo-DateTimeOffset $candidate.lastObservedAt
        if ($null -eq $candidateStart -or $null -eq $candidateEnd -or
            $candidateEnd -lt $candidateStart) {
            $siblingHeartbeatInvalidTimestamps++
            continue
        }

        $intervals.Add([pscustomobject]@{
            Start = $candidateStart
            End = $candidateEnd
        })
    }

    $laneIntervalsByAttempt[$attemptId] = $intervals
    $heartbeatNamesByAttempt[$attemptId] = $heartbeatNames
    if ($intervals.Count -gt 0) {
        $gateIntervals.Add([pscustomobject]@{
            AttemptId = $attemptId
            GoalId = [string] $attemptEntry.Value.Data.goalId
            Start = ($intervals | Measure-Object Start -Minimum).Minimum
            End = ($intervals | Measure-Object End -Maximum).Maximum
        })
    }
}

$qualifiedRows = [System.Collections.Generic.List[object]]::new()
$exclusions = [ordered]@{}

foreach ($entry in $laneSlugs.GetEnumerator()) {
    $laneName = $entry.Key
    $laneSlug = $entry.Value
    $counts = [ordered]@{
        Candidate = 0
        EligibleAttempt = $eligibleAttempts.Count
        Qualified = 0
        NoHeartbeat = 0
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
    foreach ($eligibleAttemptId in $eligibleAttempts.Keys) {
        $laneMarker = ".infrastructure-tests-$laneSlug-"
        $hasHeartbeat = @(
            $heartbeatNamesByAttempt[$eligibleAttemptId] |
                Where-Object { $_.IndexOf($laneMarker, [StringComparison]::Ordinal) -ge 1 }
        ).Count -gt 0
        if (-not $hasHeartbeat) {
            $counts.NoHeartbeat++
        }
    }

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

        $attempt = $attempts[$attemptId].Data
        $qualifiedRows.Add([pscustomobject]@{
            Lane = $laneName
            GoalPrefix = [string] $attempt.goalPrefix
            AttemptId = $attemptId
            HeartbeatPath = $file.FullName
            Start = $startedAt
            DurationSeconds = ($endedAt - $startedAt).TotalSeconds
            PeakConcurrentLanes = Get-PeakOverlap $startedAt $endedAt $laneIntervalsByAttempt[$attemptId]
            PeakConcurrentGates = Get-PeakOverlap $startedAt $endedAt $gateIntervals
        })
        $counts.Qualified++
    }

    $exclusions[$laneName] = $counts
}

Write-Output "floor_commit=$FloorCommit"
Write-Output "floor_timestamp_utc=$($floorTimestamp.UtcDateTime.ToString('o'))"
Write-Output "attempt_files=$($attemptFiles.Count) attempt_parse_failures=$attemptParseFailures eligible_attempts=$($eligibleAttempts.Count) work_bearing_gate_intervals=$($gateIntervals.Count) goal_identity_mismatches=$goalIdentityMismatches sibling_heartbeat_parse_failures=$siblingHeartbeatParseFailures sibling_heartbeat_timestamp_failures=$siblingHeartbeatInvalidTimestamps"
Write-Output 'NO_LANE_WORK_ATTEMPTS goal|n|duration_min_seconds|duration_max_seconds|invalid_attempt_intervals'
$workBearingAttemptIds = @($gateIntervals | ForEach-Object AttemptId)
$noLaneWorkAttempts = @(
    $eligibleAttempts.GetEnumerator() |
        Where-Object { $workBearingAttemptIds -notcontains $_.Key }
)
foreach ($group in @($noLaneWorkAttempts | Group-Object { [string] $_.Value.Data.goalPrefix } | Sort-Object Name)) {
    $durations = @(
        $group.Group |
            Where-Object { $null -ne $_.Value.End -and $_.Value.End -ge $_.Value.Start } |
            ForEach-Object { ($_.Value.End - $_.Value.Start).TotalSeconds }
    )
    $invalidIntervals = $group.Count - $durations.Count
    $durationMinimum = Get-NumberText (($durations | Measure-Object -Minimum).Minimum)
    $durationMaximum = Get-NumberText (($durations | Measure-Object -Maximum).Maximum)
    Write-Output "$($group.Name)|$($group.Count)|$durationMinimum|$durationMaximum|$invalidIntervals"
}
Write-Output ''
Write-Output 'DISTRIBUTIONS lane|n|mean|min|p50|p90|p99|max|shortfall_to_20'
foreach ($laneName in $laneSlugs.Keys) {
    $rows = @($qualifiedRows | Where-Object Lane -eq $laneName)
    $values = [double[]] @($rows | ForEach-Object DurationSeconds)
    $mean = Get-NumberText (($values | Measure-Object -Average).Average)
    $minimum = Get-NumberText (($values | Measure-Object -Minimum).Minimum)
    $p50 = Get-NumberText (Get-Percentile $values 0.50)
    $p90 = Get-NumberText (Get-Percentile $values 0.90)
    $p99 = Get-NumberText (Get-Percentile $values 0.99)
    $maximum = Get-NumberText (($values | Measure-Object -Maximum).Maximum)
    Write-Output "$laneName|$($rows.Count)|$mean|$minimum|$p50|$p90|$p99|$maximum|$(20 - $rows.Count)"
}

Write-Output ''
Write-Output 'POST_FLOOR_ATTEMPTS lane|eligible_attempts|with_heartbeat|no_heartbeat|qualified|heartbeat_parse|state|exit_code|timestamps'
foreach ($laneName in $laneSlugs.Keys) {
    $counts = $exclusions[$laneName]
    Write-Output "$laneName|$($counts.EligibleAttempt)|$($counts.EligibleAttempt - $counts.NoHeartbeat)|$($counts.NoHeartbeat)|$($counts.Qualified)|$($counts.HeartbeatParseFailure)|$($counts.StateNotCompleted)|$($counts.ExitCodeNotZero)|$($counts.InvalidTimestamps)"
}

Write-Output ''
Write-Output 'HEARTBEAT_FILE_WINDOW_EXCLUSIONS lane|candidate_files|missing_attempt|before_floor_or_unknown_commit'
foreach ($laneName in $laneSlugs.Keys) {
    $counts = $exclusions[$laneName]
    Write-Output "$laneName|$($counts.Candidate)|$($counts.MissingAttempt)|$($counts.BeforeFloorOrUnknownCommit)"
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
$cleanupRows = @($qualifiedRows | Where-Object Lane -eq 'Goal worktree cleanup')
$cleanupTrxRows = [System.Collections.Generic.List[object]]::new()
$testDurationsByName = @{}
$cleanupTrxMissing = 0
$cleanupTrxParseFailures = 0
foreach ($row in $cleanupRows) {
    $trxPath = $row.HeartbeatPath -replace '-[0-9a-fA-F]{16}\.gate-heartbeat\.json$', '.trx'
    if ($trxPath -eq $row.HeartbeatPath -or -not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
        $cleanupTrxMissing++
        continue
    }

    try {
        [xml] $trx = Get-Content -Raw -LiteralPath $trxPath
        $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
        if ($results.Count -eq 0) {
            throw "TRX has no UnitTestResult rows: $trxPath"
        }

        $testSeconds = 0.0
        foreach ($result in $results) {
            $durationSeconds = ConvertFrom-TrxDuration ([string] $result.duration)
            if ($null -eq $durationSeconds) {
                throw "TRX result has an invalid duration: $trxPath"
            }
            $testSeconds += $durationSeconds
            $testName = [string] $result.testName
            if (-not $testDurationsByName.ContainsKey($testName)) {
                $testDurationsByName[$testName] = [System.Collections.Generic.List[double]]::new()
            }
            $testDurationsByName[$testName].Add($durationSeconds)
        }

        $cleanupTrxRows.Add([pscustomobject]@{
            AttemptId = $row.AttemptId
            LaneSeconds = $row.DurationSeconds
            ResultCount = $results.Count
            TestSeconds = $testSeconds
            ResidualSeconds = $row.DurationSeconds - $testSeconds
        })
    }
    catch {
        $cleanupTrxParseFailures++
    }
}

$testRanges = @(
    foreach ($testEntry in $testDurationsByName.GetEnumerator()) {
        $minimum = ($testEntry.Value | Measure-Object -Minimum).Minimum
        $maximum = ($testEntry.Value | Measure-Object -Maximum).Maximum
        [pscustomobject]@{
            TestName = $testEntry.Key
            Count = $testEntry.Value.Count
            MinimumSeconds = $minimum
            MaximumSeconds = $maximum
            RangeSeconds = $maximum - $minimum
        }
    }
)

Write-Output 'CLEANUP_TRX attempt_suffix|lane_seconds|result_count|test_seconds|residual_seconds'
$cleanupTrxRows |
    Sort-Object LaneSeconds |
    ForEach-Object {
        $attemptSuffix = $_.AttemptId.Substring($_.AttemptId.Length - 8)
        Write-Output "$attemptSuffix|$(Get-NumberText $_.LaneSeconds)|$($_.ResultCount)|$(Get-NumberText $_.TestSeconds)|$(Get-NumberText $_.ResidualSeconds)"
    }
Write-Output "CLEANUP_TRX_SUMMARY qualified=$($cleanupRows.Count) parsed=$($cleanupTrxRows.Count) missing=$cleanupTrxMissing parse_failures=$cleanupTrxParseFailures"
Write-Output 'CLEANUP_TEST_RANGES rank|test|observations|min_seconds|max_seconds|range_seconds'
$rank = 0
foreach ($testRange in @($testRanges | Sort-Object RangeSeconds -Descending)) {
    $rank++
    if ($rank -gt 3) {
        break
    }
    $safeTestName = $testRange.TestName.Replace('|', '/')
    Write-Output "$rank|$safeTestName|$($testRange.Count)|$(Get-NumberText $testRange.MinimumSeconds)|$(Get-NumberText $testRange.MaximumSeconds)|$(Get-NumberText $testRange.RangeSeconds)"
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
