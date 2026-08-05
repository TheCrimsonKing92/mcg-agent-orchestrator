<#
.SYNOPSIS
  Run or list manifest-backed Infrastructure test-suite partitions through the MTP apphost.

.DESCRIPTION
  Reads local partition groupings and acceptance lane filters from
  config/acceptance-manifest.json. Builds the Infrastructure test project by default, then
  launches its Microsoft.Testing.Platform executable directly and prints live runner output
  plus a compact TRX summary. Clean-run receipts are removed; failed runs are retained.

.EXAMPLE
  .\scripts\Invoke-InfrastructureTestPartition.ps1 -List
  .\scripts\Invoke-InfrastructureTestPartition.ps1 -Partition GoalWorktree
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'List', Mandatory = $true)]
    [switch]$List,

    [Parameter(ParameterSetName = 'Run', Mandatory = $true)]
    [string]$Partition,

    [Parameter(ParameterSetName = 'Run')]
    [ValidateRange(1, 25)]
    [int]$Repeat = 1,

    [Parameter(ParameterSetName = 'Run')]
    [string]$Configuration = 'Debug',

    [Parameter(ParameterSetName = 'Run')]
    [switch]$NoBuild,

    [Parameter(ParameterSetName = 'Run')]
    [string]$ResultsRoot,

    [Parameter(ParameterSetName = 'Run')]
    [string]$RunnerPath,

    [Parameter(ParameterSetName = 'Run')]
    [string]$DotnetPath = 'dotnet',

    [Parameter(ParameterSetName = 'Run')]
    [ValidateRange(1, 86400)]
    [int]$TestHostTimeoutSeconds = 780
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'config\acceptance-manifest.json'
$target = 'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj'
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

if ($List) {
    Write-Host 'Infrastructure test partitions (config/acceptance-manifest.json):'
    foreach ($entry in $partitions) {
        Write-Host ("- {0}: {1}" -f $entry.Name, $entry.Description)
        foreach ($lane in $entry.Lanes) {
            Write-Host ("  {0}: {1}" -f $lane.name, $lane.filter)
        }
        foreach ($filter in $entry.AdditionalFilters) {
            Write-Host ("  Additional compatibility coverage: {0}" -f $filter)
        }
    }
    exit 0
}

$selected = @($partitions | Where-Object {
    $_.Name.Equals($Partition, [System.StringComparison]::OrdinalIgnoreCase)
})
if ($selected.Count -ne 1) {
    $valid = @($partitions | ForEach-Object Name) -join ', '
    Write-Host "UNKNOWN PARTITION '$Partition'. Valid manifest partitions: $valid"
    exit ([int]$exitCodes.InvalidPartition)
}

Write-Host "Infrastructure partition: $($selected[0].Name)"
Write-Host "Target: $target"
$run = Invoke-MtpTestRun `
    -RepositoryRoot $repoRoot `
    -Manifest $manifest `
    -Target $target `
    -Filters $selected[0].Filters `
    -RunLabel $selected[0].Name `
    -Repeat $Repeat `
    -Configuration $Configuration `
    -NoBuild:$NoBuild `
    -ResultsRoot $ResultsRoot `
    -RunnerPath $RunnerPath `
    -DotnetPath $DotnetPath `
    -TestHostTimeoutSeconds $TestHostTimeoutSeconds
if ($run.ExitCode -eq 0) {
    Write-Host 'PARTITION GREEN'
}
Write-MtpTerminalSummary -Result $run
exit ([int]$run.ExitCode)
