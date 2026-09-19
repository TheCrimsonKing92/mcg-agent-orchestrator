param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
$project = Join-Path $repoRoot "src\Mcg.AgentOrchestrator.Dashboard\Mcg.AgentOrchestrator.Dashboard.csproj"
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $repoRoot "artifacts\mcg-agent-orchestrator-dashboard-$Runtime"
}
$selfContainedValue = if ($SelfContained.IsPresent) { "true" } else { "false" }

dotnet publish $project -c $Configuration -r $Runtime --self-contained $selfContainedValue `
    -p:PublishSingleFile=false -o $Output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Published dashboard host: $(Join-Path $Output 'Mcg.AgentOrchestrator.Dashboard.exe')"
Write-Host "The headless launcher finds this component when both publishes are placed in the same directory."
