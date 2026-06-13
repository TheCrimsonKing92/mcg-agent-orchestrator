<#
.SYNOPSIS
  Run dotnet test with the structured TRX logger and print a compact summary.

.DESCRIPTION
  Avoids the huge UTF-16 console dumps that are painful to grep. Writes one TRX
  (XML) file per test project under .test-results, then prints per-project
  pass/fail counts plus the name and first error line of every failing test.

  Exit code is 0 only when every test project reports zero failures.

.EXAMPLE
  .\scripts\Invoke-TestSummary.ps1
  .\scripts\Invoke-TestSummary.ps1 -Target .\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj
  .\scripts\Invoke-TestSummary.ps1 -Filter "DisplayName~lifecycle"
#>
[CmdletBinding()]
param(
    [string]$Target = ".\Mcg.AgentOrchestrator.sln",
    [string]$Filter = "",
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$results = Join-Path $repoRoot ".test-results"

# CS2012/VBCSCompiler lock hygiene before a fresh run.
dotnet build-server shutdown | Out-Null

New-Item -ItemType Directory -Force $results | Out-Null
Get-ChildItem $results -Filter *.trx -ErrorAction SilentlyContinue | Remove-Item -Force

# Let the TRX logger auto-name one file per test project (no fixed LogFileName,
# which would collide when the target is the whole solution).
$rawLog = Join-Path $results "dotnet-test.log"
$testArgs = @($Target, '--logger', 'trx', '--results-directory', $results, '-clp:ErrorsOnly')
if ($NoBuild) { $testArgs += '--no-build' }
if ($Filter)  { $testArgs += @('--filter', $Filter) }

dotnet test @testArgs *>&1 | Tee-Object -FilePath $rawLog | Out-Null

$trxFiles = Get-ChildItem $results -Filter *.trx -ErrorAction SilentlyContinue
if (-not $trxFiles) {
    Write-Output "NO TRX OUTPUT - dotnet test did not produce results (likely a build error). Raw tail:"
    Get-Content $rawLog -Tail 15
    exit 1
}

$anyFailed = $false
foreach ($file in $trxFiles) {
    [xml]$trx = Get-Content $file.FullName
    $c = $trx.TestRun.ResultSummary.Counters
    $failed = [int]$c.failed
    if ($failed -gt 0) { $anyFailed = $true }
    "{0}: total={1} passed={2} failed={3} skipped={4}" -f $file.Name, $c.total, $c.passed, $failed, $c.notExecuted
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq 'Failed' } |
        ForEach-Object {
            "  FAIL  $($_.testName)"
            $msg = $_.Output.ErrorInfo.Message
            if ($msg) { "        " + (($msg -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -First 1) }
        }
}

if ($anyFailed) { exit 1 } else { "ALL GREEN"; exit 0 }
