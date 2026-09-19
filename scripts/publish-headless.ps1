param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
$project = Join-Path $repoRoot "src\Mcg.AgentOrchestrator.App\Mcg.AgentOrchestrator.App.csproj"
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $repoRoot "artifacts\mcg-agent-orchestrator-headless-$Runtime"
}
$selfContainedValue = if ($SelfContained.IsPresent) { "true" } else { "false" }

dotnet publish $project -c $Configuration -r $Runtime --self-contained $selfContainedValue `
    -p:PublishSingleFile=false -o $Output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$runtimeConfig = Join-Path $Output "Mcg.AgentOrchestrator.App.runtimeconfig.json"
if (-not (Test-Path -LiteralPath $runtimeConfig -PathType Leaf)) {
    throw "Headless publish did not produce Mcg.AgentOrchestrator.App.runtimeconfig.json; dependency inventory cannot be verified."
}
if (Test-Path (Join-Path $Output "Mcg.AgentOrchestrator.Dashboard.dll")) {
    throw "Headless publish unexpectedly contains Mcg.AgentOrchestrator.Dashboard.dll."
}
if (Select-String -LiteralPath $runtimeConfig -Pattern "Microsoft.AspNetCore.App" -Quiet) {
    throw "Headless publish unexpectedly requires Microsoft.AspNetCore.App."
}

Write-Host "Published headless runtime: $(Join-Path $Output 'Mcg.AgentOrchestrator.App.exe')"
