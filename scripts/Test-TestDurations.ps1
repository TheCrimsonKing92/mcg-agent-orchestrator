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

    $resultXml = foreach ($result in $Results) {
        '      <UnitTestResult executionId="{0}" testId="{0}" testName="{1}" outcome="{2}" duration="{3}" />' -f (
            [Guid]::NewGuid().ToString('D'),
            [System.Security.SecurityElement]::Escape($result.Name),
            $result.Outcome,
            $result.Duration
        )
    }

    $content = @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun id="synthetic" name="Synthetic" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
$($resultXml -join "`r`n")
  </Results>
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
