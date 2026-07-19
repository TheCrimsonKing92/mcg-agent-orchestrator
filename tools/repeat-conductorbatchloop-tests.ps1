[CmdletBinding()]
param(
    [int]$Iterations = 20,
    [string]$Target = ".\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
    [string]$Filter = "FullyQualifiedName~ConductorBatchLoopTests"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$testSummary = Join-Path $repoRoot "scripts\Invoke-TestSummary.ps1"
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source

Push-Location $repoRoot
try {
    for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
        Write-Output ("repeat {0}/{1}: ConductorBatchLoopTests" -f $iteration, $Iterations)
        & $pwsh -NoProfile -ExecutionPolicy Bypass -File $testSummary -Target $Target -Filter $Filter
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            Write-Output ("FAIL repeat {0}/{1}: exit={2}" -f $iteration, $Iterations, $exitCode)
            exit $exitCode
        }
    }

    Write-Output ("PASS repeat {0}/{0}: ConductorBatchLoopTests" -f $Iterations)
    exit 0
}
finally {
    Pop-Location
}
