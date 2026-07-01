<#
.SYNOPSIS
  Print the slowest test durations from one or more TRX files.

.DESCRIPTION
  Reads explicit TRX files, directories, or wildcard paths and prints a compact
  globally ranked table. Output is capped to the top 20 tests by default.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Show-TestDurations.ps1 .\.test-results\*.trx

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Show-TestDurations.ps1 .\.test-results\*.trx -Top 50
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Path,

    [ValidateRange(1, 10000)]
    [int]$Top = 20
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
            Duration = $duration
            Outcome = Convert-TrxOutcome ([string]$result.outcome)
        }
    }
}

try {
    $files = @(Resolve-TrxInput $Path)
    $rows = foreach ($file in $files) {
        Read-TrxDurations $file
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
