<#
.SYNOPSIS
  Run or list stable Infrastructure test-suite partitions.

.DESCRIPTION
  Provides repo-bounded, named partitions for high-cost Infrastructure test
  areas without changing the full acceptance gate. Test execution uses the
  isolated dotnet wrapper and prints compact TRX-backed results.

.EXAMPLE
  .\scripts\Invoke-InfrastructureTestPartition.ps1 -List
  .\scripts\Invoke-InfrastructureTestPartition.ps1 -Partition Cli
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'List', Mandatory = $true)]
    [switch]$List,

    [Parameter(ParameterSetName = 'Run', Mandatory = $true)]
    [ValidateSet('Cli', 'WorkerDispatch', 'GoalWorktree', 'Dashboard', 'Conductor')]
    [string]$Partition
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$target = 'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj'

$partitions = [ordered]@{
    Cli = [pscustomobject]@{
        Description = 'CLI command/help coverage; largest current Infrastructure test file is CliCommandTests.'
        Filters = @('FullyQualifiedName~CliCommandTests', 'FullyQualifiedName~CliHelpTests')
    }
    WorkerDispatch = [pscustomobject]@{
        Description = 'Worker dispatch/profile/process/sandbox coverage; includes the WorkerDispatch god-class area.'
        Filters = @(
            'FullyQualifiedName~WorkerDispatchTests',
            'FullyQualifiedName~WorkerProfileTests',
            'FullyQualifiedName~WorkerProcessJobsTests',
            'FullyQualifiedName~WorkerShellTests',
            'FullyQualifiedName~WorkerSandboxCapabilityPlannerTests',
            'FullyQualifiedName~DispatchProcessHostTests'
        )
    }
    GoalWorktree = [pscustomobject]@{
        Description = 'Goal worktree and acceptance-verifier coverage, including focused and full worktree test files.'
        Filters = @('FullyQualifiedName~GoalWorktreeTests', 'FullyQualifiedName~GoalAcceptanceVerifierTests')
    }
    Dashboard = [pscustomobject]@{
        Description = 'Dashboard rendering, host, and validation-harness coverage.'
        Filters = @(
            'FullyQualifiedName~DashboardRenderingTests',
            'FullyQualifiedName~DashboardHostTests',
            'FullyQualifiedName~DashboardValidationHarnessTests'
        )
    }
    Conductor = [pscustomobject]@{
        Description = 'Conductor advance/batch/driver/watch-sweep coverage.'
        Filters = @(
            'FullyQualifiedName~AdvanceLoopTests',
            'FullyQualifiedName~ConductorBatchLoopTests',
            'FullyQualifiedName~ConductorDriverTests',
            'FullyQualifiedName~ConductWatchSweepScopingTests'
        )
    }
}

if ($List) {
    "Infrastructure test partitions:"
    foreach ($name in $partitions.Keys) {
        $entry = $partitions[$name]
        "- {0}: {1}" -f $name, $entry.Description
        "  Filters: {0}" -f ($entry.Filters -join ', ')
    }
    exit 0
}

$selected = $partitions[$Partition]
"Infrastructure partition: $Partition"
"Target: $target"
"Filters: $($selected.Filters -join ', ')"

function Write-TrxSummary {
    param([string]$ResultsPath)

    $trxFiles = Get-ChildItem $ResultsPath -Filter *.trx -ErrorAction SilentlyContinue
    if (-not $trxFiles) {
        Write-Output "NO TRX OUTPUT - dotnet test did not produce results."
        $script:LastTrxGreen = $false
        return
    }

    $green = $true
    foreach ($file in $trxFiles) {
        [xml]$trx = Get-Content $file.FullName
        $c = $trx.TestRun.ResultSummary.Counters
        $total = [int]$c.total
        $failed = [int]$c.failed
        if ($total -le 0) {
            $green = $false
        }

        if ($failed -gt 0) {
            $green = $false
        }

        "{0}: total={1} passed={2} failed={3} skipped={4}" -f $file.Name, $c.total, $c.passed, $failed, $c.notExecuted
        if ($total -le 0) {
            "  ZERO TESTS - filter matched no tests."
        }

        $trx.TestRun.Results.UnitTestResult |
            Where-Object { $_.outcome -eq 'Failed' } |
            ForEach-Object {
                "  FAIL  $($_.testName)"
                $msg = $_.Output.ErrorInfo.Message
                if ($msg) {
                    "        " + (($msg -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -First 1)
                }
            }
    }

    $script:LastTrxGreen = $green
}

$isolatedDotnet = Join-Path $PSScriptRoot 'Invoke-IsolatedDotnet.ps1'
$resultsRoot = Join-Path $repoRoot '.test-results'
for ($index = 0; $index -lt $selected.Filters.Count; $index++) {
    $filter = $selected.Filters[$index]
    "Running filter: $filter"

    New-Item -ItemType Directory -Force $resultsRoot | Out-Null
    Get-ChildItem $resultsRoot -Filter *.trx -ErrorAction SilentlyContinue | Remove-Item -Force

    $testArguments = @(
        '-GoalPrefix', 'infra-partition',
        '-AttemptName', "infra-$($Partition.ToLowerInvariant())-$index",
        'test',
        (Join-Path $repoRoot $target),
        '--logger', 'trx',
        '--results-directory', $resultsRoot,
        '-clp:ErrorsOnly',
        '--filter', $filter
    )

    & $isolatedDotnet @testArguments | Out-Null
    $testExit = $LASTEXITCODE
    $script:LastTrxGreen = $false
    Write-TrxSummary -ResultsPath $resultsRoot
    if ($testExit -ne 0 -or -not $script:LastTrxGreen) {
        if ($testExit -ne 0) {
            exit $testExit
        }

        exit 1
    }
}
"PARTITION GREEN"; exit 0
