<#
.SYNOPSIS
  Run manifest-declared test projects through their Microsoft.Testing.Platform apphosts.

.DESCRIPTION
  Builds the target in Debug by default, launches each MTP apphost directly, streams stdout
  and stderr, and prints compact TRX summaries. The target may be the repository solution or
  a test project declared by config/acceptance-manifest.json. Use -NoBuild only when the
  apphost is already current. Clean-run receipts are removed; failed runs are retained.

.EXAMPLE
  .\scripts\Invoke-TestSummary.ps1
  .\scripts\Invoke-TestSummary.ps1 -Target .\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj
  .\scripts\Invoke-TestSummary.ps1 -Filter "DisplayName~lifecycle"
  .\scripts\Invoke-TestSummary.ps1 -Partition GoalWorktree
#>
[CmdletBinding()]
param(
    [string]$Target = '.\Mcg.AgentOrchestrator.sln',
    [string]$Filter = '',
    [string]$Partition = '',
    [switch]$NoBuild,
    [string]$Configuration = 'Debug',
    [string]$ResultsRoot,
    [string]$RunnerPath,
    [string]$DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'config\acceptance-manifest.json'
Import-Module (Join-Path $PSScriptRoot 'MtpTestRunner.psm1') -Force
$exitCodes = Get-MtpTestExitCodes

try {
    $manifest = Read-MtpTestManifest -Path $manifestPath
    $partitions = @(Get-MtpLocalPartitions -Manifest $manifest)
}
catch {
    Write-Host "MANIFEST FAILURE - $($_.Exception.Message)"
    exit ([int]$exitCodes.Manifest)
}

if (-not [string]::IsNullOrWhiteSpace($Partition) -and -not [string]::IsNullOrWhiteSpace($Filter)) {
    Write-Host 'FILTER FAILURE - specify either -Partition or -Filter, not both.'
    exit ([int]$exitCodes.InvalidPartition)
}

$filters = @($Filter)
$runLabel = 'summary'
if (-not [string]::IsNullOrWhiteSpace($Partition)) {
    $selected = @($partitions | Where-Object {
        $_.Name.Equals($Partition, [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($selected.Count -ne 1) {
        $valid = @($partitions | ForEach-Object Name) -join ', '
        Write-Host "UNKNOWN PARTITION '$Partition'. Valid manifest partitions: $valid"
        exit ([int]$exitCodes.InvalidPartition)
    }
    $filters = $selected[0].Filters
    $runLabel = $selected[0].Name
}

$run = Invoke-MtpTestRun `
    -RepositoryRoot $repoRoot `
    -Manifest $manifest `
    -Target $Target `
    -Filters $filters `
    -RunLabel $runLabel `
    -Configuration $Configuration `
    -NoBuild:$NoBuild `
    -ResultsRoot $ResultsRoot `
    -RunnerPath $RunnerPath `
    -DotnetPath $DotnetPath
exit ([int]$run.ExitCode)
