[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int]$Iterations = 1
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$testSummary = Join-Path $PSScriptRoot 'Invoke-TestSummary.ps1'
$testProject = Join-Path $repoRoot 'tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj'
$filter = 'DisplayName~DuplicateRegistrationProjectsIdentityDecision|DisplayName~RegistrationWithoutConflictProducesHarnessReceipt|DisplayName~ProcessLauncherConfirmsOwnedJobExitForRealChild|DisplayName~ProcessLauncherInventoriesAndKillsOwnedChild'

for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    $output = @(& powershell.exe -NoProfile -File $testSummary -Target $testProject -Filter $filter 2>&1)
    $exitCode = $LASTEXITCODE
    $receipts = @($output | Where-Object { "$_" -like 'process-lifecycle-receipt *' })
    $exactResult = if ($receipts.Count -eq 0) {
        'receipt-missing'
    }
    else {
        $receipts -join ' || '
    }

    Write-Output (
        "process-lifecycle-harness run=$iteration exit_code=$exitCode exact_result=$exactResult")

    if ($exitCode -ne 0 -or $receipts.Count -eq 0) {
        $output | Where-Object {
            "$_" -match 'failed |FAIL|ZERO TESTS|MTP_TERMINAL_SUMMARY|process-lifecycle-receipt'
        }
        exit $(if ($exitCode -ne 0) { $exitCode } else { 1 })
    }
}

exit 0
