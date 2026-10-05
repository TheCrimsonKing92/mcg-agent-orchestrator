[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}

$repoRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$appOutput = if ([string]::IsNullOrWhiteSpace($env:MCG_ORCHESTRATOR_APP_OUTPUT)) {
    Join-Path $repoRoot 'src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0'
} else {
    [System.IO.Path]::GetFullPath($env:MCG_ORCHESTRATOR_APP_OUTPUT)
}
$appDll = Join-Path $appOutput 'Mcg.AgentOrchestrator.App.dll'
$markerPath = "$appDll.git-head"
$relevantPathspec = @(
    'src/Mcg.AgentOrchestrator.App/Program.cs',
    'src/Mcg.AgentOrchestrator.App/Cli',
    'src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj',
    'src/Mcg.AgentOrchestrator.Execution/Processes',
    'src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj',
    'src/Mcg.AgentOrchestrator.Execution/Mcg.AgentOrchestrator.Execution.csproj',
    'Directory.Build.props',
    'Directory.Build.rsp',
    'global.json'
)

try {
    if (-not (Test-Path -LiteralPath $appDll -PathType Leaf) -or
        -not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        Write-Output 'MCG_APP_DLL_UNRESOLVED reason=missing-artifact'
        exit 1
    }

    $markerHead = (Get-Content -Raw -LiteralPath $markerPath).Trim()
    if ($markerHead -notmatch '^[0-9a-fA-F]{40,64}$') {
        exit 1
    }

    $headOutput = @(& git -C $repoRoot rev-parse --verify HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) {
        exit 2
    }

    $currentHead = ($headOutput -join '').Trim()
    if ([string]::IsNullOrWhiteSpace($currentHead)) {
        exit 2
    }

    $workingChanges = @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all -- @relevantPathspec 2>$null)
    if ($LASTEXITCODE -ne 0) {
        exit 2
    }
    if ($workingChanges.Count -gt 0) {
        exit 1
    }

    if ($markerHead -ne $currentHead) {
        & git -C $repoRoot merge-base --is-ancestor $markerHead $currentHead 2>$null
        if ($LASTEXITCODE -ne 0) {
            exit 1
        }

        & git -C $repoRoot diff --quiet $markerHead $currentHead -- @relevantPathspec
        if ($LASTEXITCODE -ne 0) {
            exit 1
        }
    }

    $runDirectoryOutput = @(& (Join-Path $repoRoot 'scripts\resolve-run-dir.ps1') $appDll)
    if ($LASTEXITCODE -ne 0 -or $runDirectoryOutput.Count -ne 1) {
        exit 1
    }

    $resolvedRunDll = Join-Path $runDirectoryOutput[0] 'Mcg.AgentOrchestrator.App.dll'
    if (-not (Test-Path -LiteralPath $resolvedRunDll -PathType Leaf)) {
        exit 1
    }

    Write-Output $resolvedRunDll
    exit 0
}
catch {
    exit 1
}
