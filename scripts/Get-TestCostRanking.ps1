[CmdletBinding()]
param(
    [string]$InputRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) '.orchestrator\acceptance-gate-attempts'),
    [ValidateRange(0.001, 87600)]
    [double]$WindowHours = 24,
    [ValidateRange(0, [int]::MaxValue)]
    [int]$Top = 0
)

$ErrorActionPreference = 'Stop'
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$dateStyles = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor
    [System.Globalization.DateTimeStyles]::AdjustToUniversal
$failureOutcomes = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@('Failed', 'Error', 'Timeout', 'Aborted'),
    [System.StringComparer]::OrdinalIgnoreCase)
$excludedRateOutcomes = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@('NotExecuted', 'Inconclusive'),
    [System.StringComparer]::OrdinalIgnoreCase)
$knownOutcomes = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@('Passed', 'Failed', 'Error', 'Timeout', 'Aborted', 'NotExecuted', 'Inconclusive'),
    [System.StringComparer]::OrdinalIgnoreCase)

if (-not (Test-Path -LiteralPath $InputRoot -PathType Container)) {
    throw "TRX input root does not exist: $InputRoot"
}

$receipts = [System.Collections.Generic.List[object]]::new()
$files = @(Get-ChildItem -LiteralPath $InputRoot -Filter '*.trx' -File -Recurse)
if ($files.Count -eq 0) {
    throw "No TRX receipts found under: $InputRoot"
}

foreach ($file in $files) {
    try {
        $document = [System.Xml.XmlDocument]::new()
        $document.PreserveWhitespace = $false
        $document.Load($file.FullName)
    }
    catch {
        throw "Invalid TRX XML in '$($file.FullName)': $($_.Exception.Message)"
    }

    $testRun = $document.SelectSingleNode("/*[local-name()='TestRun']")
    $times = $document.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='Times']")
    if ($null -eq $testRun -or $null -eq $times -or [string]::IsNullOrWhiteSpace($times.GetAttribute('finish'))) {
        throw "Invalid TRX receipt '$($file.FullName)': missing TestRun/Times@finish."
    }

    $finish = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
        $times.GetAttribute('finish'),
        $invariant,
        $dateStyles,
        [ref]$finish)) {
        throw "Invalid TRX receipt '$($file.FullName)': malformed Times@finish."
    }

    $results = @($document.SelectNodes("/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']"))
    if ($results.Count -eq 0) {
        throw "Invalid TRX receipt '$($file.FullName)': no UnitTestResult entries."
    }

    $receipts.Add([pscustomobject]@{ File = $file; Finish = $finish; Results = $results })
}

$latestFinish = ($receipts | ForEach-Object Finish | Measure-Object -Maximum).Maximum
$windowStart = $latestFinish.Subtract([TimeSpan]::FromHours($WindowHours))
$aggregates = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)

foreach ($receipt in $receipts) {
    if ($receipt.Finish -lt $windowStart -or $receipt.Finish -gt $latestFinish) {
        continue
    }

    foreach ($result in $receipt.Results) {
        $name = $result.GetAttribute('testName')
        $outcome = $result.GetAttribute('outcome')
        $durationText = $result.GetAttribute('duration')
        if ([string]::IsNullOrWhiteSpace($name)) {
            throw "Invalid TRX receipt '$($receipt.File.FullName)': UnitTestResult is missing testName."
        }
        if (-not $knownOutcomes.Contains($outcome)) {
            throw "Invalid TRX receipt '$($receipt.File.FullName)': unknown outcome '$outcome' for '$name'."
        }

        $duration = [TimeSpan]::Zero
        if ([string]::IsNullOrWhiteSpace($durationText) -or
            -not [TimeSpan]::TryParse($durationText, $invariant, [ref]$duration) -or
            $duration -lt [TimeSpan]::Zero) {
            throw "Invalid TRX receipt '$($receipt.File.FullName)': malformed duration '$durationText' for '$name'."
        }

        if (-not $aggregates.ContainsKey($name)) {
            $aggregates[$name] = [pscustomobject]@{
                TestName = $name
                ExecutionCount = 0
                FailureCount = 0
                RateDenominator = 0
                TotalSeconds = 0.0
                MaximumSeconds = 0.0
            }
        }

        $aggregate = $aggregates[$name]
        $aggregate.ExecutionCount++
        $seconds = $duration.TotalSeconds
        $aggregate.TotalSeconds += $seconds
        $aggregate.MaximumSeconds = [Math]::Max($aggregate.MaximumSeconds, $seconds)
        if (-not $excludedRateOutcomes.Contains($outcome)) {
            $aggregate.RateDenominator++
            if ($failureOutcomes.Contains($outcome)) {
                $aggregate.FailureCount++
            }
        }
    }
}

if ($aggregates.Count -eq 0) {
    throw "No TRX results fall within the $WindowHours-hour window ending at $($latestFinish.ToString('O', $invariant))."
}

$cost = [System.Collections.Generic.List[object]]::new()
$failure = [System.Collections.Generic.List[object]]::new()
foreach ($aggregate in $aggregates.Values) {
    $failureRate = if ($aggregate.RateDenominator -eq 0) {
        0.0
    }
    else {
        $aggregate.FailureCount / $aggregate.RateDenominator
    }
    $aggregate | Add-Member -NotePropertyName FailureRate -NotePropertyValue $failureRate
    $cost.Add($aggregate)
    if ($aggregate.RateDenominator -gt 0) {
        $failure.Add($aggregate)
    }
}

$cost.Sort([System.Comparison[object]]{
    param($left, $right)
    $comparison = ([double]$right.TotalSeconds).CompareTo([double]$left.TotalSeconds)
    if ($comparison -ne 0) { return $comparison }
    return [System.StringComparer]::Ordinal.Compare($left.TestName, $right.TestName)
})
$failure.Sort([System.Comparison[object]]{
    param($left, $right)
    $comparison = ([double]$right.FailureRate).CompareTo([double]$left.FailureRate)
    if ($comparison -ne 0) { return $comparison }
    $comparison = ([int]$right.FailureCount).CompareTo([int]$left.FailureCount)
    if ($comparison -ne 0) { return $comparison }
    return [System.StringComparer]::Ordinal.Compare($left.TestName, $right.TestName)
})

$rows = [System.Collections.Generic.List[object]]::new()
foreach ($ranking in @(@{ Kind = 'cost'; Values = $cost }, @{ Kind = 'failure'; Values = $failure })) {
    $limit = if ($Top -eq 0) { $ranking.Values.Count } else { [Math]::Min($Top, $ranking.Values.Count) }
    for ($index = 0; $index -lt $limit; $index++) {
        $value = $ranking.Values[$index]
        $rows.Add([pscustomobject][ordered]@{
            ranking_kind = $ranking.Kind
            rank = $index + 1
            test_name = $value.TestName
            execution_count = $value.ExecutionCount
            failure_count = $value.FailureCount
            failure_rate = $value.FailureRate.ToString('0.000000', $invariant)
            total_seconds = $value.TotalSeconds.ToString('0.000000', $invariant)
            maximum_seconds = $value.MaximumSeconds.ToString('0.000000', $invariant)
        })
    }
}

$rows | ConvertTo-Csv -NoTypeInformation
