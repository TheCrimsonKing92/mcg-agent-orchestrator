<#
.SYNOPSIS
  Print the slowest test durations from one or more TRX files.

.DESCRIPTION
  Reads explicit TRX files, directories, wildcard paths, or recent
  acceptance-gate attempts and prints a compact globally ranked table. Output
  is capped to the top 20 tests by default.

  Use -ByClass to sum test durations by class within each gate receipt set and
  report the maximum serial duration across the most recent three sets. CSV
  output is intended as input to deterministic lane-planning tools.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Show-TestDurations.ps1 .\.test-results\*.trx

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Show-TestDurations.ps1 .\.test-results\*.trx -Top 50

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Show-TestDurations.ps1 -Goal b0137830 -ByClass -Format Csv -Top 10000
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Path,

    [ValidateRange(1, 10000)]
    [int]$Top = 20,

    [string]$Goal,

    [string]$AttemptsRoot,

    [switch]$ByClass,

    [ValidateSet('Table', 'Csv')]
    [string]$Format = 'Table',

    [ValidateRange(1, 3)]
    [int]$RecentRuns = 3
)

$ErrorActionPreference = 'Stop'

function Resolve-TrxInput {
    param([string[]]$Inputs)

    $resolved = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
    foreach ($inputPath in $Inputs) {
        if ([string]::IsNullOrWhiteSpace($inputPath)) {
            continue
        }

        $items = @()
        try {
            if ($inputPath.IndexOfAny([char[]]'*?[]') -ge 0) {
                $items = @(Get-ChildItem -Path $inputPath -File -ErrorAction Stop)
            } else {
                $item = Get-Item -LiteralPath $inputPath -ErrorAction Stop
                if ($item -is [System.IO.DirectoryInfo]) {
                    $items = @(Get-ChildItem -LiteralPath $item.FullName -Filter '*.trx' -File -ErrorAction Stop)
                } else {
                    $items = @($item)
                }
            }
        } catch {
            throw "Input not found or unreadable: $inputPath"
        }

        foreach ($item in $items) {
            if ($item.Extension -ne '.trx') {
                throw "Input is not a TRX file: $($item.FullName)"
            }
            [void]$resolved.Add($item)
        }
    }

    if ($resolved.Count -eq 0) {
        throw "No TRX files matched the provided input."
    }

    $resolved | Sort-Object FullName -Unique
}

function Resolve-AttemptsRoot {
    param([string]$ExplicitRoot)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitRoot)) {
        return (Get-Item -LiteralPath $ExplicitRoot -ErrorAction Stop).FullName
    }

    $repoRoot = Split-Path -Parent $PSScriptRoot
    return Join-Path $repoRoot '.orchestrator\acceptance-gate-attempts'
}

function Resolve-RecentGateAttemptTrx {
    param(
        [string]$Root,
        [string]$GoalFilter,
        [int]$Count
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Acceptance gate attempts root not found: $Root"
    }

    $goalDirectories = @(Get-ChildItem -LiteralPath $Root -Directory -ErrorAction Stop)
    if (-not [string]::IsNullOrWhiteSpace($GoalFilter)) {
        $goalDirectories = @($goalDirectories | Where-Object {
            $_.Name.Equals($GoalFilter, [StringComparison]::OrdinalIgnoreCase) -or
                $_.Name.StartsWith($GoalFilter, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($goalDirectories.Count -eq 0) {
            throw "No acceptance gate attempt directory matched goal: $GoalFilter"
        }
    }

    $attempts = foreach ($goalDirectory in $goalDirectories) {
        foreach ($attemptFile in Get-ChildItem -LiteralPath $goalDirectory.FullName -Filter '*.attempt.json' -File -ErrorAction SilentlyContinue) {
            try {
                $attempt = Get-Content -LiteralPath $attemptFile.FullName -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
                [pscustomobject]@{
                    Attempt = $attempt
                    AttemptFile = $attemptFile
                    StartedAt = if ($attempt.startedAt) { [datetimeoffset]::Parse([string]$attempt.startedAt) } else { [datetimeoffset]$attemptFile.LastWriteTimeUtc }
                }
            } catch {
                [pscustomobject]@{
                    Attempt = $null
                    AttemptFile = $attemptFile
                    StartedAt = [datetimeoffset]$attemptFile.LastWriteTimeUtc
                }
            }
        }
    }

    $recent = @($attempts | Sort-Object StartedAt -Descending | Select-Object -First $Count)
    if ($recent.Count -eq 0) {
        throw "No acceptance gate attempt records found under: $Root"
    }

    $allFiles = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
    foreach ($attempt in $recent) {
        $attemptRecord = $attempt.Attempt
        $paths = [System.Collections.Generic.List[string]]::new()
        if ($null -ne $attemptRecord -and $attemptRecord.testResultPaths) {
            foreach ($path in @($attemptRecord.testResultPaths)) {
                if (-not [string]::IsNullOrWhiteSpace([string]$path)) {
                    [void]$paths.Add([string]$path)
                }
            }
        }

        $resultPath = if ($null -ne $attemptRecord -and $attemptRecord.resultPath) {
            [string]$attemptRecord.resultPath
        } else {
            $attempt.AttemptFile.FullName -replace '\.attempt\.json$', '.result.json'
        }

        if ($paths.Count -eq 0 -and (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
            try {
                $result = Get-Content -LiteralPath $resultPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
                if ($result.acceptance -and $result.acceptance.testResultPaths) {
                    foreach ($path in @($result.acceptance.testResultPaths)) {
                        if (-not [string]::IsNullOrWhiteSpace([string]$path)) {
                            [void]$paths.Add([string]$path)
                        }
                    }
                }
            } catch {
                throw "Acceptance gate result is unreadable: $resultPath"
            }
        }

        if ($paths.Count -eq 0) {
            $prefix = $attempt.AttemptFile.FullName -replace '\.attempt\.json$', ''
            foreach ($trx in Get-ChildItem -LiteralPath (Split-Path -Parent $prefix) -Filter ((Split-Path -Leaf $prefix) + '*.trx') -File -ErrorAction SilentlyContinue) {
                [void]$paths.Add($trx.FullName)
            }
        }

        if ($paths.Count -eq 0) {
            throw "Acceptance gate attempt has no referenced TRX paths: $($attempt.AttemptFile.FullName)"
        }

        foreach ($file in @(Resolve-TrxInput @($paths))) {
            [void]$allFiles.Add($file)
        }
    }

    $allFiles | Sort-Object FullName -Unique
}

function Convert-TrxOutcome {
    param([string]$Outcome)

    if ($Outcome -eq 'NotExecuted') {
        return 'Skipped'
    }

    if ([string]::IsNullOrWhiteSpace($Outcome)) {
        return 'Unknown'
    }

    return $Outcome
}

function Read-TrxDurations {
    param([System.IO.FileInfo]$File)

    try {
        [xml]$trx = Get-Content -LiteralPath $File.FullName -Raw -ErrorAction Stop
    } catch {
        throw "Invalid TRX XML or unreadable input: $($File.FullName)"
    }

    if ($null -eq $trx.TestRun -or $null -eq $trx.TestRun.Results) {
        throw "Invalid TRX XML: missing TestRun/Results in $($File.FullName)"
    }

    $classByTestId = @{}
    foreach ($definition in @($trx.TestRun.TestDefinitions.UnitTest)) {
        if ($null -ne $definition -and $null -ne $definition.TestMethod) {
            $classByTestId[[string]$definition.id] = [string]$definition.TestMethod.className
        }
    }

    $fileBaseName = [System.IO.Path]::GetFileNameWithoutExtension($File.Name)
    $laneMarker = $fileBaseName.IndexOf('.infrastructure-tests-', [StringComparison]::OrdinalIgnoreCase)
    $runKey = if ($laneMarker -gt 0) {
        $fileBaseName.Substring(0, $laneMarker)
    } else {
        $File.FullName
    }

    foreach ($result in @($trx.TestRun.Results.UnitTestResult)) {
        if ($null -eq $result) {
            continue
        }

        $durationText = [string]$result.duration
        $duration = [TimeSpan]::Zero
        if (-not [TimeSpan]::TryParse($durationText, [ref]$duration)) {
            throw "Invalid TRX XML: invalid duration '$durationText' in $($File.FullName)"
        }

        [pscustomobject]@{
            TestName = [string]$result.testName
            TestClass = $classByTestId[[string]$result.testId]
            Duration = $duration
            Outcome = Convert-TrxOutcome ([string]$result.outcome)
            RunKey = $runKey
        }
    }
}

try {
    $files = if ($Path.Count -gt 0) {
        @(Resolve-TrxInput $Path)
    } else {
        $runCount = if ($ByClass) { $RecentRuns } else { 1 }
        @(Resolve-RecentGateAttemptTrx (Resolve-AttemptsRoot $AttemptsRoot) $Goal $runCount)
    }
    $rows = foreach ($file in $files) {
        Read-TrxDurations $file
    }

    if ($ByClass) {
        $missingClass = @($rows | Where-Object { [string]::IsNullOrWhiteSpace($_.TestClass) })
        if ($missingClass.Count -gt 0) {
            throw "TRX input is missing class metadata for $($missingClass.Count) result(s); TestDefinitions/TestMethod.className is required for -ByClass."
        }

        $perRun = @($rows |
            Group-Object RunKey, TestClass |
            ForEach-Object {
                [pscustomobject]@{
                    RunKey = $_.Group[0].RunKey
                    Class = $_.Group[0].TestClass
                    Seconds = ($_.Group | Measure-Object -Property { $_.Duration.TotalSeconds } -Sum).Sum
                }
            })
        $rankedClasses = @($perRun |
            Group-Object Class |
            ForEach-Object {
                [pscustomobject]@{
                    Class = $_.Name
                    SerialSeconds = [math]::Round(($_.Group | Measure-Object Seconds -Maximum).Maximum, 2)
                    Runs = @($_.Group | Select-Object -ExpandProperty RunKey -Unique).Count
                }
            } |
            Sort-Object -Property @{ Expression = 'SerialSeconds'; Descending = $true }, @{ Expression = 'Class'; Descending = $false } |
            Select-Object -First $Top)

        if ($rankedClasses.Count -eq 0) {
            throw "No UnitTestResult entries found in the provided TRX input."
        }

        if ($Format -eq 'Csv') {
            $rankedClasses | ConvertTo-Csv -NoTypeInformation
            exit 0
        }

        "{0,9}  {1,4}  {2}" -f 'Serial', 'Runs', 'Class'
        "{0,9}  {1,4}  {2}" -f '------', '----', '-----'
        foreach ($row in $rankedClasses) {
            "{0,9:N2}s  {1,4}  {2}" -f $row.SerialSeconds, $row.Runs, $row.Class
        }
        exit 0
    }

    $ranked = @($rows | Sort-Object -Property Duration -Descending | Select-Object -First $Top)
    if ($ranked.Count -eq 0) {
        throw "No UnitTestResult entries found in the provided TRX input."
    }

    "{0,4}  {1,9}  {2,-8}  {3}" -f 'Rank', 'Duration', 'Outcome', 'Test'
    "{0,4}  {1,9}  {2,-8}  {3}" -f '----', '--------', '-------', '----'

    $rank = 1
    foreach ($row in $ranked) {
        "{0,4}  {1,9:N2}s  {2,-8}  {3}" -f $rank, $row.Duration.TotalSeconds, $row.Outcome, $row.TestName
        $rank++
    }

    exit 0
} catch {
    Write-Error $_.Exception.Message
    exit 1
}
