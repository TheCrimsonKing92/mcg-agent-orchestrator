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

& $dotnetHost restore $Target --force-evaluate --nologo --verbosity minimal -p:AuditPipeline=true
if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
