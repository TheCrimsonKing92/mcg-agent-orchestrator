[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Since,

    [string]$RepositoryRoot,
    [string]$ReceiptsRoot,
    [string]$ManifestPath,
    [string]$GitPath = 'git',
    [string]$Json,
    [string]$SaveBaseline,
    [string]$CompareTo,
    [switch]$IncludeUnqualified
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$exclusionOrder = @(
    'missingAttemptId',
    'nonCompletedState',
    'nonZeroExitCode',
    'unparseableTimestamps',
    'incompleteResultCount',
    'notInManifest'
)

function Resolve-AbsolutePath {
    param([string]$Path, [string]$BasePath)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Invoke-GitValue {
    param([string[]]$Arguments)

    $output = @(& $script:GitPath -C $script:RepositoryRoot @Arguments 2>&1)
    $commandSucceeded = $?
    $nativeExit = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    $exitCode = if ($null -ne $nativeExit) { [int]$nativeExit.Value } elseif ($commandSucceeded) { 0 } else { 1 }
    if ($exitCode -ne 0) {
        throw "git $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }

    return ($output -join [Environment]::NewLine).Trim()
}

function ConvertTo-UtcTimestamp {
    param([object]$Value)

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        return $null
    }

    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
        [string]$Value,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$parsed)) {
        return $null
    }

    return $parsed.ToUniversalTime()
}

function ConvertTo-LaneName {
    param([string]$Value)

    $prefix = 'infrastructure tests: '
    if ($Value.StartsWith($prefix, [StringComparison]::Ordinal)) {
        return $Value.Substring($prefix.Length)
    }

    return $Value
}

function ConvertTo-LaneSlug {
    param([string]$LaneName)

    $slug = [Text.RegularExpressions.Regex]::Replace(
        $LaneName.ToLowerInvariant(),
        '[^a-z0-9]+',
        '-').Trim('-')
    if ([string]::IsNullOrWhiteSpace($slug)) { return 'check' }
    return $slug
}

function Read-JsonFile {
    param([string]$Path)

    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -Depth 30
}

function Get-TrxMeasurement {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    try {
        [xml]$document = Get-Content -Raw -LiteralPath $Path
        if ($null -eq $document.DocumentElement -or $document.DocumentElement.LocalName -ne 'TestRun') {
            return $null
        }

        $namespaceManager = [Xml.XmlNamespaceManager]::new($document.NameTable)
        $namespaceManager.AddNamespace('trx', $document.DocumentElement.NamespaceURI)
        $counters = $document.SelectSingleNode('/trx:TestRun/trx:ResultSummary/trx:Counters', $namespaceManager)
        $results = @($document.SelectNodes('/trx:TestRun/trx:Results/trx:UnitTestResult', $namespaceManager))
        if ($null -eq $counters -or $results.Count -eq 0) {
            return $null
        }

        foreach ($required in @('total', 'executed', 'passed', 'failed')) {
            if (-not $counters.HasAttribute($required)) { return $null }
        }

        $total = 0
        $executed = 0
        $passed = 0
        $failed = 0
        if (-not [int]::TryParse($counters.GetAttribute('total'), [ref]$total) -or
            -not [int]::TryParse($counters.GetAttribute('executed'), [ref]$executed) -or
            -not [int]::TryParse($counters.GetAttribute('passed'), [ref]$passed) -or
            -not [int]::TryParse($counters.GetAttribute('failed'), [ref]$failed)) {
            return $null
        }

        $skippedText = if ($counters.HasAttribute('skipped')) {
            $counters.GetAttribute('skipped')
        } elseif ($counters.HasAttribute('notExecuted')) {
            $counters.GetAttribute('notExecuted')
        } else {
            return $null
        }
        $skipped = 0
        if (-not [int]::TryParse($skippedText, [ref]$skipped)) { return $null }

        if ($total -le 0 -or
            $executed -ne ($passed + $failed) -or
            $total -ne ($executed + $skipped) -or
            $results.Count -ne $total) {
            return $null
        }

        $duration = [TimeSpan]::Zero
        foreach ($result in $results) {
            if (-not $result.HasAttribute('duration')) { return $null }
            $itemDuration = [TimeSpan]::Zero
            if (-not [TimeSpan]::TryParse(
                $result.GetAttribute('duration'),
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$itemDuration)) {
                return $null
            }
            $duration += $itemDuration
        }

        return [pscustomobject]@{
            Total = $total
            Executed = $executed
            Passed = $passed
            Failed = $failed
            Skipped = $skipped
            TestSeconds = $duration.TotalSeconds
        }
    } catch {
        return $null
    }
}

function Add-Exclusion {
    param([System.Collections.IDictionary]$Counts, [string]$Reason)
    $Counts[$Reason] = [int]$Counts[$Reason] + 1
}

function New-Statistics {
    param([double[]]$Values)

    $ordered = @($Values | Sort-Object)
    return [ordered]@{
        mean = [Math]::Round((($ordered | Measure-Object -Average).Average), 3, [MidpointRounding]::AwayFromZero)
        min = [Math]::Round($ordered[0], 3, [MidpointRounding]::AwayFromZero)
        max = [Math]::Round($ordered[-1], 3, [MidpointRounding]::AwayFromZero)
    }
}

function Write-JsonDocument {
    param([object]$Document, [string]$Path)

    $resolvedPath = Resolve-AbsolutePath $Path $script:RepositoryRoot
    $parent = Split-Path -Parent $resolvedPath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [IO.Directory]::CreateDirectory($parent) | Out-Null
    }
    $content = ($Document | ConvertTo-Json -Depth 30) + "`n"
    [IO.File]::WriteAllText($resolvedPath, $content, [Text.UTF8Encoding]::new($false))
}

function Get-LaneNames {
    param([object]$Lanes)

    if ($Lanes -is [Collections.IDictionary]) {
        return @($Lanes.Keys)
    }
    return @($Lanes.psobject.Properties.Name)
}

function Get-LaneValue {
    param([object]$Lanes, [string]$Name)

    if ($Lanes -is [Collections.IDictionary]) {
        return $Lanes[$Name]
    }
    $property = $Lanes.psobject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Write-MeasurementTable {
    param([object]$Measurement)

    $prefix = if ($Measurement.qualification.qualified) { '' } else { 'UNQUALIFIED ' }
    $emit = { param([string]$Line) Write-Output ($prefix + $Line) }
    & $emit "Qualification: $(if ($Measurement.qualification.qualified) { 'QUALIFIED' } else { 'UNQUALIFIED' })"
    & $emit "Window commit: $($Measurement.window.commitSha) ($($Measurement.window.commitTimestampUtc))"
    & $emit "Receipts: total=$($Measurement.qualification.totalReceipts) included=$($Measurement.qualification.includedReceipts) excluded=$($Measurement.qualification.excludedReceipts)"
    $reasonText = @($script:exclusionOrder | ForEach-Object {
        "$_=$($Measurement.qualification.exclusionReasons.$_)"
    }) -join ' '
    & $emit "Exclusions: $reasonText"
    & $emit ''

    $laneNames = @(Get-LaneNames $Measurement.lanes)
    $laneWidth = [Math]::Max(4, ($laneNames | ForEach-Object Length | Measure-Object -Maximum).Maximum)
    $format = "{0,-$laneWidth} {1,4} {2,16} {3,15} {4,15} {5,13} {6,12} {7,12}"
    & $emit ($format -f 'Lane', 'Runs', 'Process mean (s)', 'Process min (s)', 'Process max (s)', 'Test mean (s)', 'Test min (s)', 'Test max (s)')
    & $emit ($format -f ('-' * $laneWidth), '----', '----------------', '---------------', '---------------', '-------------', '------------', '------------')
    foreach ($laneName in $laneNames) {
        $lane = Get-LaneValue $Measurement.lanes $laneName
        & $emit ($format -f $laneName, $lane.runs, $lane.processSeconds.mean, $lane.processSeconds.min, $lane.processSeconds.max, $lane.testSeconds.mean, $lane.testSeconds.min, $lane.testSeconds.max)
    }
}

function Write-ComparisonTable {
    param([object]$Baseline, [object]$Current)

    $prefix = if ($Current.qualification.qualified) { '' } else { 'UNQUALIFIED ' }
    Write-Output "${prefix}Comparison: baseline $($Baseline.window.commitSha) -> current $($Current.window.commitSha)"
    Write-Output "${prefix}Qualification counts: baseline included=$($Baseline.qualification.includedReceipts) excluded=$($Baseline.qualification.excludedReceipts); current included=$($Current.qualification.includedReceipts) excluded=$($Current.qualification.excludedReceipts)"
    Write-Output "${prefix}Lane | Status | Baseline process min-max | Current process min-max | Process mean delta | Baseline test min-max | Current test min-max | Test mean delta"

    $names = @(@(Get-LaneNames $Baseline.lanes) + @(Get-LaneNames $Current.lanes) | Sort-Object -Unique)
    foreach ($name in $names) {
        $before = Get-LaneValue $Baseline.lanes $name
        $after = Get-LaneValue $Current.lanes $name
        if ($null -eq $before) {
            Write-Output "${prefix}$name | added | - | $($after.processSeconds.min)-$($after.processSeconds.max) | - | - | $($after.testSeconds.min)-$($after.testSeconds.max) | -"
            continue
        }
        if ($null -eq $after) {
            Write-Output "${prefix}$name | removed | $($before.processSeconds.min)-$($before.processSeconds.max) | - | - | $($before.testSeconds.min)-$($before.testSeconds.max) | - | -"
            continue
        }

        $processDelta = [Math]::Round($after.processSeconds.mean - $before.processSeconds.mean, 3, [MidpointRounding]::AwayFromZero)
        $testDelta = [Math]::Round($after.testSeconds.mean - $before.testSeconds.mean, 3, [MidpointRounding]::AwayFromZero)
        Write-Output "${prefix}$name | present | $($before.processSeconds.min)-$($before.processSeconds.max) | $($after.processSeconds.min)-$($after.processSeconds.max) | $processDelta | $($before.testSeconds.min)-$($before.testSeconds.max) | $($after.testSeconds.min)-$($after.testSeconds.max) | $testDelta"
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        $RepositoryRoot = Split-Path -Parent $PSScriptRoot
    }
    $script:RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
    $script:GitPath = $GitPath
    $ReceiptsRoot = if ([string]::IsNullOrWhiteSpace($ReceiptsRoot)) {
        Join-Path $script:RepositoryRoot '.orchestrator/acceptance-gate-attempts'
    } else {
        Resolve-AbsolutePath $ReceiptsRoot $script:RepositoryRoot
    }
    $ManifestPath = if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
        Join-Path $script:RepositoryRoot 'config/acceptance-manifest.json'
    } else {
        Resolve-AbsolutePath $ManifestPath $script:RepositoryRoot
    }

    if (-not (Test-Path -LiteralPath $ReceiptsRoot -PathType Container)) {
        throw "Acceptance receipt root does not exist: $ReceiptsRoot"
    }
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "Acceptance manifest does not exist: $ManifestPath"
    }

    $commitSha = Invoke-GitValue @('rev-parse', '--verify', $Since)
    $commitTimestamp = ConvertTo-UtcTimestamp (Invoke-GitValue @('show', '-s', '--format=%cI', $commitSha))
    if ($null -eq $commitTimestamp) {
        throw "Commit '$commitSha' did not provide a parseable commit timestamp."
    }
    $windowObservedAt = [DateTimeOffset]::UtcNow

    $manifest = Read-JsonFile $ManifestPath
    $manifestLanes = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($entry in @($manifest.engine.infrastructureTestLanes)) {
        $name = ConvertTo-LaneName ([string]$entry.name)
        if ([string]::IsNullOrWhiteSpace($name)) { throw 'Acceptance manifest contains a blank infrastructure lane name.' }
        if (-not $manifestLanes.TryAdd($name, $entry)) {
            throw "Acceptance manifest contains duplicate normalized lane '$name'."
        }
    }
    if ($manifestLanes.Count -eq 0) { throw 'Acceptance manifest contains no infrastructure test lanes.' }

    $exclusions = [ordered]@{}
    foreach ($reason in $exclusionOrder) { $exclusions[$reason] = 0 }
    $samplesByLane = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($name in $manifestLanes.Keys) { $samplesByLane[$name] = [Collections.Generic.List[object]]::new() }
    $totalReceipts = 0
    $includedReceipts = 0

    $heartbeats = @(Get-ChildItem -LiteralPath $ReceiptsRoot -Recurse -File -Filter '*.gate-heartbeat.json' | Sort-Object FullName)
    foreach ($heartbeatFile in $heartbeats) {
        try { $heartbeat = Read-JsonFile $heartbeatFile.FullName } catch { continue }
        $target = [string]$heartbeat.currentTarget
        if ([string]::IsNullOrWhiteSpace($target) -or
            -not $target.StartsWith('infrastructure tests: ', [StringComparison]::Ordinal)) {
            continue
        }

        $laneName = ConvertTo-LaneName $target
        $dotIndex = $heartbeatFile.Name.IndexOf('.')
        $attemptId = if ($dotIndex -gt 0) { $heartbeatFile.Name.Substring(0, $dotIndex) } else { '' }
        $attemptPath = Join-Path $heartbeatFile.DirectoryName "$attemptId.attempt.json"
        $attempt = $null
        if (-not [string]::IsNullOrWhiteSpace($attemptId) -and (Test-Path -LiteralPath $attemptPath -PathType Leaf)) {
            try { $attempt = Read-JsonFile $attemptPath } catch { $attempt = $null }
        }

        $startedAt = ConvertTo-UtcTimestamp $heartbeat.startedAt
        $lastObservedAt = ConvertTo-UtcTimestamp $heartbeat.lastObservedAt
        $windowTimestamp = $startedAt
        if ($null -eq $windowTimestamp -and $null -ne $attempt) {
            $windowTimestamp = ConvertTo-UtcTimestamp $attempt.startedAt
        }
        if ($null -eq $windowTimestamp) {
            $windowTimestamp = [DateTimeOffset]$heartbeatFile.LastWriteTimeUtc
        }
        if ($windowTimestamp -lt $commitTimestamp -or $windowTimestamp -gt $windowObservedAt) { continue }

        $totalReceipts++
        $reason = $null
        $attemptJoined = $null -ne $attempt -and
            -not [string]::IsNullOrWhiteSpace([string]$attempt.attemptId) -and
            ([string]$attempt.attemptId).Equals($attemptId, [StringComparison]::Ordinal)
        if (-not $attemptJoined) {
            $reason = 'missingAttemptId'
        } elseif (-not ([string]$heartbeat.state).Equals('completed', [StringComparison]::Ordinal)) {
            $reason = 'nonCompletedState'
        } elseif ($null -eq $heartbeat.exitCode -or [int]$heartbeat.exitCode -ne 0) {
            $reason = 'nonZeroExitCode'
        } elseif ($null -eq $startedAt -or $null -eq $lastObservedAt -or $lastObservedAt -lt $startedAt) {
            $reason = 'unparseableTimestamps'
        }

        $slug = ConvertTo-LaneSlug $laneName
        $trxPath = Join-Path $heartbeatFile.DirectoryName "$attemptId.infrastructure-tests-$slug.trx"
        $trx = Get-TrxMeasurement $trxPath
        if ($null -eq $reason -and $null -eq $trx) {
            $reason = 'incompleteResultCount'
        } elseif ($null -eq $reason -and -not $manifestLanes.ContainsKey($laneName)) {
            $reason = 'notInManifest'
        }

        if ($null -ne $reason) { Add-Exclusion $exclusions $reason }
        $canMeasure = $manifestLanes.ContainsKey($laneName) -and
            $null -ne $startedAt -and $null -ne $lastObservedAt -and $lastObservedAt -ge $startedAt -and
            $null -ne $trx
        if (($null -eq $reason -or $IncludeUnqualified) -and $canMeasure) {
            $includedReceipts++
            $samplesByLane[$laneName].Add([ordered]@{
                attemptId = $attemptId
                processSeconds = [Math]::Round(($lastObservedAt - $startedAt).TotalSeconds, 3, [MidpointRounding]::AwayFromZero)
                testSeconds = [Math]::Round($trx.TestSeconds, 3, [MidpointRounding]::AwayFromZero)
                resultCount = $trx.Total
            })
        }
    }

    $missingLanes = @($manifestLanes.Keys | Where-Object { $samplesByLane[$_].Count -eq 0 } | Sort-Object)
    if ($missingLanes.Count -gt 0) {
        $kind = if ($IncludeUnqualified) { 'included' } else { 'qualified' }
        throw "Manifest lane(s) with no $kind receipts: $($missingLanes -join ', ')"
    }

    $laneDocument = [ordered]@{}
    foreach ($name in @($manifestLanes.Keys | Sort-Object)) {
        $samples = @($samplesByLane[$name] | Sort-Object { $_.attemptId })
        $laneDocument[$name] = [ordered]@{
            runs = $samples.Count
            processSeconds = New-Statistics @($samples | ForEach-Object { [double]$_.processSeconds })
            testSeconds = New-Statistics @($samples | ForEach-Object { [double]$_.testSeconds })
            samples = $samples
        }
    }

    $excludedReceipts = [int](($exclusionOrder | ForEach-Object { [int]$exclusions[$_] } | Measure-Object -Sum).Sum)

    $measurement = [ordered]@{
        schemaVersion = 1
        measurementType = 'qualified-lane-timings'
        window = [ordered]@{
            commitSha = $commitSha
            commitTimestampUtc = $commitTimestamp.ToString('O')
        }
        qualification = [ordered]@{
            qualified = -not [bool]$IncludeUnqualified
            totalReceipts = $totalReceipts
            includedReceipts = $includedReceipts
            excludedReceipts = $excludedReceipts
            exclusionReasons = $exclusions
        }
        lanes = $laneDocument
    }

    $baseline = $null
    if (-not [string]::IsNullOrWhiteSpace($CompareTo)) {
        $baselinePath = Resolve-AbsolutePath $CompareTo $script:RepositoryRoot
        $baseline = Read-JsonFile $baselinePath
        if ($baseline.schemaVersion -ne 1 -or $baseline.measurementType -ne 'qualified-lane-timings') {
            throw "Baseline '$baselinePath' is not a supported lane-timing measurement."
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Json)) { Write-JsonDocument $measurement $Json }
    if (-not [string]::IsNullOrWhiteSpace($SaveBaseline)) { Write-JsonDocument $measurement $SaveBaseline }

    Write-MeasurementTable $measurement
    if ($null -ne $baseline) {
        Write-Output ''
        Write-ComparisonTable $baseline $measurement
    }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
