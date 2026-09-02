[CmdletBinding()]
param(
    [string]$Target
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Target)) {
    $Target = Join-Path $repoRoot "Mcg.AgentOrchestrator.sln"
}

$dotnetHost = if ([string]::IsNullOrWhiteSpace($env:MCG_ORCHESTRATOR_DOTNET_PATH)) {
    "dotnet"
}
else {
    $env:MCG_ORCHESTRATOR_DOTNET_PATH
}

# This is the explicit online availability/bootstrap path. Vulnerability auditing remains
# a separate fail-closed operation in Invoke-PackageAudit.ps1. If that audit fails after
# writing error-bearing assets, this command is the explicit recovery path for routine reads.
& $dotnetHost restore $Target --force-evaluate --nologo --verbosity minimal -p:NuGetAudit=false
if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
