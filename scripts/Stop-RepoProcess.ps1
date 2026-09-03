<#
.SYNOPSIS
  Stop exact process ids after optional command-line guard checks.

.DESCRIPTION
  Repo-bounded wrapper around the orchestrator CLI guarded process stopper. The
  guard and tree stop run in .NET instead of PowerShell CIM/Stop-Process.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [int[]]$Id,

    [string[]]$CommandContains = @(),

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$arguments = @('repo-process-stop')
foreach ($processId in $Id) {
    $arguments += @('--id', $processId.ToString([System.Globalization.CultureInfo]::InvariantCulture))
}
foreach ($needle in $CommandContains) {
    $arguments += @('--command-contains', $needle)
}
if ($Force) {
    $arguments += '--force'
}

try {
    $resolver = Join-Path $repoRoot 'scripts\Resolve-RepoProcessAppDll.ps1'
    $resolvedAppDll = @()
    try {
        if (Test-Path -LiteralPath $resolver -PathType Leaf) {
            $resolvedAppDll = @(& $resolver -RepositoryRoot $repoRoot)
            if ($LASTEXITCODE -ne 0) {
                $resolvedAppDll = @()
            }
        }
    }
    catch {
        $resolvedAppDll = @()
    }

    if ($resolvedAppDll.Count -eq 1) {
        $dotnetHost = if ([string]::IsNullOrWhiteSpace($env:MCG_ORCHESTRATOR_DOTNET_PATH)) {
            'dotnet'
        } else {
            $env:MCG_ORCHESTRATOR_DOTNET_PATH
        }
        & $dotnetHost $resolvedAppDll[0] @arguments
    }
    else {
        & (Join-Path $repoRoot 'scripts\Invoke-OrchestratorCommand.ps1') @arguments
    }
    if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
catch {
    Write-Output "PROCESS_STOP_UNAVAILABLE operation=script-wrapper reason=$($_.Exception.GetType().Name): $($_.Exception.Message)"
    Write-Output 'BACKLOG_CANDIDATE title="Repo process stop degraded" body="Stop-RepoProcess.ps1 could not run the orchestrator-authored guarded stop; preserve this disposition instead of requesting operator approval."'
    exit 1
}
