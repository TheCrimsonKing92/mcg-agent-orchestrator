<#
.SYNOPSIS
  Validate scripts/Show-TestDurations.ps1 against synthetic TRX fixtures.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Test-TestDurations.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$script = Join-Path $repoRoot 'scripts\Show-TestDurations.ps1'
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("mcg-test-durations-" + [Guid]::NewGuid().ToString('N'))

function Write-TrxFixture {
    param(
        [string]$FileName,
        [array]$Results
    )

    $fixtureResults = foreach ($result in $Results) {
        [pscustomobject]@{
            Id = [Guid]::NewGuid().ToString('D')
            Name = $result.Name
            Class = if ($result.Class) { $result.Class } else { ($result.Name -replace '\.[^.]+$', '') }
            Outcome = $result.Outcome
            Duration = $result.Duration
        }
    }
    $resultXml = foreach ($result in $fixtureResults) {
        '      <UnitTestResult executionId="{0}" testId="{0}" testName="{1}" outcome="{2}" duration="{3}" />' -f (
            $result.Id,
            [System.Security.SecurityElement]::Escape($result.Name),
            $result.Outcome,
            $result.Duration
        )
    }
    $definitionXml = foreach ($result in $fixtureResults) {
        '      <UnitTest id="{0}" name="{1}"><TestMethod className="{2}" name="{3}" /></UnitTest>' -f (
            $result.Id,
            [System.Security.SecurityElement]::Escape($result.Name),
            [System.Security.SecurityElement]::Escape($result.Class),
            [System.Security.SecurityElement]::Escape(($result.Name -replace '^.*\.', ''))
        )
    }

    $content = @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun id="synthetic" name="Synthetic" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
$($resultXml -join "`r`n")
  </Results>
  <TestDefinitions>
$($definitionXml -join "`r`n")
  </TestDefinitions>
</TestRun>
"@

    $path = Join-Path $work $FileName
    Set-Content -LiteralPath $path -Value $content -Encoding UTF8
    $path
}

function Invoke-DurationScript {
    param([string[]]$Arguments)

    $powerShellPath = (Get-Process -Id $PID).Path
    $output = & $powerShellPath -NoProfile -ExecutionPolicy Bypass -File $script @Arguments 2>&1
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = @($output | ForEach-Object { [string]$_ })
    }
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

try {
    New-Item -ItemType Directory -Force $work | Out-Null

    $first = Write-TrxFixture 'first.trx' @(
        @{ Name = 'Mcg.Acceptance.FastTest'; Outcome = 'Passed'; Duration = '00:00:01.2500000' },
        @{ Name = 'Mcg.Acceptance.SlowestTest'; Outcome = 'Failed'; Duration = '00:00:12.3400000' },
        @{ Name = 'Mcg.Acceptance.SkippedTest'; Outcome = 'NotExecuted'; Duration = '00:00:02.0000000' }
    )
    $second = Write-TrxFixture 'second.trx' @(
        @{ Name = 'Mcg.Acceptance.MiddleTest'; Outcome = 'Passed'; Duration = '00:00:05.5000000' }
    )

    $topTwo = Invoke-DurationScript @($first, $second, '-Top', '2')
    Assert-True ($topTwo.ExitCode -eq 0) "Expected top-two invocation to pass. Output: $($topTwo.Output -join ' | ')"
    $dataLines = @($topTwo.Output | Where-Object { $_ -match '^\s+\d+\s+' })
    Assert-True ($dataLines.Count -eq 2) "Expected exactly two ranked rows, got $($dataLines.Count)."
    Assert-True ($dataLines[0] -match 'Mcg\.Acceptance\.SlowestTest' -and $dataLines[0] -match '12\.34s' -and $dataLines[0] -match 'Failed') 'Expected slowest failed test first with 12.34s.'
    Assert-True ($dataLines[1] -match 'Mcg\.Acceptance\.MiddleTest' -and $dataLines[1] -match '5\.50s' -and $dataLines[1] -match 'Passed') 'Expected second file result to be aggregated and ranked second.'
    Assert-True (-not (($topTwo.Output -join "`n") -match 'FastTest|SkippedTest')) 'Expected top-N cap to omit lower-ranked tests.'

    $all = Invoke-DurationScript @((Join-Path $work '*.trx'), '-Top', '10')
    Assert-True ($all.ExitCode -eq 0) "Expected glob invocation to pass. Output: $($all.Output -join ' | ')"
    Assert-True (($all.Output -join "`n") -match 'Mcg\.Acceptance\.SkippedTest') 'Expected glob input to include skipped result.'
    Assert-True (($all.Output -join "`n") -match 'Skipped') 'Expected NotExecuted outcome to print as Skipped.'

    $runOneA = Write-TrxFixture 'run-one.infrastructure-tests-a.trx' @(
        @{ Name = 'Mcg.Acceptance.AlphaTests.First'; Class = 'Mcg.Acceptance.AlphaTests'; Outcome = 'Passed'; Duration = '00:00:03.0000000' }
    )
    $runOneB = Write-TrxFixture 'run-one.infrastructure-tests-b.trx' @(
        @{ Name = 'Mcg.Acceptance.AlphaTests.Second'; Class = 'Mcg.Acceptance.AlphaTests'; Outcome = 'Passed'; Duration = '00:00:04.0000000' },
        @{ Name = 'Mcg.Acceptance.BetaTests.First'; Class = 'Mcg.Acceptance.BetaTests'; Outcome = 'Passed'; Duration = '00:00:05.0000000' }
    )
    $runTwo = Write-TrxFixture 'run-two.infrastructure-tests-a.trx' @(
        @{ Name = 'Mcg.Acceptance.AlphaTests.First'; Class = 'Mcg.Acceptance.AlphaTests'; Outcome = 'Passed'; Duration = '00:00:06.0000000' },
        @{ Name = 'Mcg.Acceptance.BetaTests.First'; Class = 'Mcg.Acceptance.BetaTests'; Outcome = 'Passed'; Duration = '00:00:08.0000000' }
    )
    $byClass = Invoke-DurationScript @($runOneA, $runOneB, $runTwo, '-ByClass', '-Format', 'Csv', '-Top', '10')
    Assert-True ($byClass.ExitCode -eq 0) "Expected per-class invocation to pass. Output: $($byClass.Output -join ' | ')"
    $classRows = @($byClass.Output | ConvertFrom-Csv)
    Assert-True ($classRows.Count -eq 2) "Expected two per-class rows, got $($classRows.Count)."
    Assert-True ($classRows[0].Class -eq 'Mcg.Acceptance.BetaTests' -and $classRows[0].SerialSeconds -eq '8') 'Expected Beta max serial duration to be 8 seconds.'
    Assert-True ($classRows[1].Class -eq 'Mcg.Acceptance.AlphaTests' -and $classRows[1].SerialSeconds -eq '7') 'Expected Alpha tests to sum to 7 seconds within run one.'
    Assert-True ($classRows[1].Runs -eq '2') 'Expected Alpha timing to report two receipt sets.'

    $attemptRoot = Join-Path $work 'acceptance-gate-attempts'
    $goalId = '11112222333344445555666677778888'
    $goalDirectory = Join-Path $attemptRoot $goalId
    New-Item -ItemType Directory -Force $goalDirectory | Out-Null
    $gateTrx = Write-TrxFixture 'gate-attempt.dotnet-test.trx' @(
        @{ Name = 'Mcg.Acceptance.GateSlow'; Outcome = 'Passed'; Duration = '00:00:09.0000000' }
    )
    $attemptId = '11112222-0-20260717120000000-abcdef'
    $attemptPath = Join-Path $goalDirectory "$attemptId.attempt.json"
    $resultPath = Join-Path $goalDirectory "$attemptId.result.json"
    @{
        attemptId = $attemptId
        goalId = $goalId
        goalPrefix = '11112222'
        startedAt = '2026-07-17T12:00:00.0000000+00:00'
        completedAt = '2026-07-17T12:01:00.0000000+00:00'
        outcome = 'Passed'
        resultPath = $resultPath
        testResultPaths = @($gateTrx)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $attemptPath -Encoding UTF8
    @{
        kind = 'accepted'
        acceptance = @{
            passed = $true
            unmetCriteria = @()
            testResultPaths = @($gateTrx)
        }
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding UTF8

    $failedTrx = Write-TrxFixture 'failed-newer.dotnet-test.trx' @(
        @{ Name = 'Mcg.Acceptance.FailedNewer'; Outcome = 'Failed'; Duration = '00:00:30.0000000' }
    )
    $failedAttemptId = '11112222-0-20260717130000000-fedcba'
    $failedAttemptPath = Join-Path $goalDirectory "$failedAttemptId.attempt.json"
    $failedResultPath = Join-Path $goalDirectory "$failedAttemptId.result.json"
    @{
        attemptId = $failedAttemptId
        goalId = $goalId
        goalPrefix = '11112222'
        startedAt = '2026-07-17T13:00:00.0000000+00:00'
        completedAt = '2026-07-17T13:01:00.0000000+00:00'
        outcome = 'Failed'
        resultPath = $failedResultPath
        testResultPaths = @($failedTrx)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $failedAttemptPath -Encoding UTF8
    @{
        kind = 'rejected'
        acceptance = @{
            passed = $false
            unmetCriteria = @('tests failed')
            testResultPaths = @($failedTrx)
        }
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $failedResultPath -Encoding UTF8

    $partialAttemptId = '11112222-0-20260717140000000-aabbcc'
    $partialAttemptPath = Join-Path $goalDirectory "$partialAttemptId.attempt.json"
    $partialResultPath = Join-Path $goalDirectory "$partialAttemptId.result.json"
    $partialTrx = Write-TrxFixture 'partial-newest.dotnet-test.trx' @(
        @{ Name = 'Mcg.Acceptance.PartialNewer'; Outcome = 'Passed'; Duration = '00:00:40.0000000' }
    )
    $missingTrx = Join-Path $work 'missing-newest.dotnet-test.trx'
    @{
        attemptId = $partialAttemptId
        goalId = $goalId
        goalPrefix = '11112222'
        startedAt = '2026-07-17T14:00:00.0000000+00:00'
        completedAt = '2026-07-17T14:01:00.0000000+00:00'
        outcome = 'Passed'
        resultPath = $partialResultPath
        testResultPaths = @($partialTrx, $missingTrx)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $partialAttemptPath -Encoding UTF8
    @{
        kind = 'accepted'
        acceptance = @{
            passed = $true
            unmetCriteria = @()
            testResultPaths = @($partialTrx, $missingTrx)
        }
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $partialResultPath -Encoding UTF8

    $mismatchedAttemptId = '11112222-0-20260717150000000-bbccdd'
    $mismatchedAttemptPath = Join-Path $goalDirectory "$mismatchedAttemptId.attempt.json"
    $mismatchedResultPath = Join-Path $goalDirectory "$mismatchedAttemptId.result.json"
    $mismatchedTrx = Write-TrxFixture 'mismatched-newest.dotnet-test.trx' @(
        @{ Name = 'Mcg.Acceptance.MismatchedNewer'; Outcome = 'Passed'; Duration = '00:00:50.0000000' }
    )
    @{
        attemptId = $mismatchedAttemptId
        goalId = $goalId
        goalPrefix = '11112222'
        startedAt = '2026-07-17T15:00:00.0000000+00:00'
        completedAt = '2026-07-17T15:01:00.0000000+00:00'
        outcome = 'Passed'
        resultPath = $mismatchedResultPath
        testResultPaths = @($mismatchedTrx, $missingTrx)
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $mismatchedAttemptPath -Encoding UTF8
    @{
        kind = 'accepted'
        acceptance = @{
            passed = $true
            unmetCriteria = @()
            testResultPaths = @($mismatchedTrx)
        }
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $mismatchedResultPath -Encoding UTF8

    $latestGate = Invoke-DurationScript @('-AttemptsRoot', $attemptRoot, '-Goal', '11112222', '-Top', '1')
    Assert-True ($latestGate.ExitCode -eq 0) "Expected latest gate attempt invocation to pass. Output: $($latestGate.Output -join ' | ')"
    $latestGateOutput = $latestGate.Output -join "`n"
    Assert-True ($latestGateOutput -match 'Mcg\.Acceptance\.GateSlow') 'Expected latest clean, complete gate attempt TRX to be resolved.'
    Assert-True ($latestGateOutput -notmatch 'FailedNewer') 'Expected a newer failed attempt not to displace a clean receipt set.'
    Assert-True ($latestGateOutput -notmatch 'PartialNewer') 'Expected a newer partial receipt set not to displace a complete receipt set.'
    Assert-True ($latestGateOutput -notmatch 'MismatchedNewer') 'Expected a newer mismatched receipt set not to displace a complete receipt set.'

    $missing = Invoke-DurationScript @((Join-Path $work 'missing.trx'))
    Assert-True ($missing.ExitCode -ne 0) 'Expected missing input to fail.'
    Assert-True (($missing.Output -join "`n") -match 'Input not found or unreadable') 'Expected clear missing-input error.'

    $invalid = Join-Path $work 'invalid.trx'
    Set-Content -LiteralPath $invalid -Value '<not-xml' -Encoding UTF8
    $invalidResult = Invoke-DurationScript @($invalid)
    Assert-True ($invalidResult.ExitCode -ne 0) 'Expected invalid XML to fail.'
    Assert-True (($invalidResult.Output -join "`n") -match 'Invalid TRX XML') 'Expected clear invalid-XML error.'

    Write-Output 'PASS: Show-TestDurations.ps1 validation'
    exit 0
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
