<#
.SYNOPSIS
  Print bounded process details for exact process ids or parent process ids.

.DESCRIPTION
  Repo-bounded wrapper around the orchestrator CLI process inspector. The process
  query runs in .NET instead of PowerShell CIM so Codex can use this helper without
  ad-hoc permission prompts or Windows sandbox runner CIM failures.
#>
[CmdletBinding()]
param(
    [int[]]$Id = @(),
    [int[]]$ParentId = @(),
    [string[]]$Name = @(),
    [string[]]$CommandContains = @(),
    [int]$Newest = 25,
    [switch]$ConductLoop,
    [switch]$DispatchHost,
    [switch]$IncludeChildren,
    [switch]$Locks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$arguments = @('repo-process-info')
foreach ($processId in $Id) {
    $arguments += @('--id', $processId.ToString([System.Globalization.CultureInfo]::InvariantCulture))
}
foreach ($parentProcessId in $ParentId) {
    $arguments += @('--parent-id', $parentProcessId.ToString([System.Globalization.CultureInfo]::InvariantCulture))
}
foreach ($processName in $Name) {
    $arguments += @('--name', $processName)
}
foreach ($needle in $CommandContains) {
    $arguments += @('--command-contains', $needle)
}

$arguments += @('--newest', $Newest.ToString([System.Globalization.CultureInfo]::InvariantCulture))
if ($ConductLoop) { $arguments += '--conduct-loop' }
if ($DispatchHost) { $arguments += '--dispatch-host' }
if ($IncludeChildren) { $arguments += '--include-children' }
if ($Locks) { $arguments += '--locks' }

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
    Write-Output "PROCESS_QUERY_UNAVAILABLE operation=script-wrapper reason=$($_.Exception.GetType().Name): $($_.Exception.Message)"
    Write-Output 'BACKLOG_CANDIDATE title="Repo process helper degraded" body="Get-RepoProcessInfo.ps1 could not run the orchestrator-authored process query; preserve this disposition instead of requesting operator approval."'
    exit 1
}
