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
    $Output = Join-Path $repoRoot "artifacts\mcg-agent-orchestrator-$Runtime"
}

$selfContainedValue = if ($SelfContained.IsPresent) { "true" } else { "false" }

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContainedValue `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $Output

$exe = Join-Path $Output "Mcg.AgentOrchestrator.App.exe"
Write-Host ""
Write-Host "Published: $exe"
Write-Host "Run: `"$exe`" doctor"
Write-Host "Dashboard: `"$exe`" open-dashboard http://localhost:5087/ --refresh 5"
