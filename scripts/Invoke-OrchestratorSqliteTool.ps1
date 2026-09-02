<#
.SYNOPSIS
  Run the repository SQLite utility through the stable repo script wrapper.

.DESCRIPTION
  This keeps operator reads and repairs on the same allowlist-friendly path as the
  other repo helpers. Prefer:

    .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorSqliteTool.ps1 ...

  over ad-hoc `dotnet run --project ...` commands in Codex.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Arguments = @($args | ForEach-Object { [string]$_ })

if ($Arguments.Count -eq 0) {
    throw "Usage: .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorSqliteTool.ps1 <sqlite-tool-args...>"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "scripts\OrchestratorSqliteTools\OrchestratorSqliteTools.csproj"
$artifactPath = Join-Path $repoRoot "scripts\OrchestratorSqliteTools\bin\Debug\net10.0\OrchestratorSqliteTools.dll"
$artifactDirectory = Split-Path -Parent $artifactPath
$markerPath = "$artifactPath.git-head"
$freshnessScript = Join-Path $PSScriptRoot "Test-OrchestratorArtifactFreshness.ps1"
$markerScript = Join-Path $PSScriptRoot "Update-AppDllGitHeadMarker.ps1"
$toolSourcePath = Join-Path $repoRoot "scripts\OrchestratorSqliteTools"
$coreSourcePath = Join-Path $repoRoot "src\Mcg.AgentOrchestrator.Core"
$dotnetHost = if ([string]::IsNullOrWhiteSpace($env:MCG_ORCHESTRATOR_DOTNET_PATH)) {
    "dotnet"
}
else {
    $env:MCG_ORCHESTRATOR_DOTNET_PATH
}
$toolCommand = $Arguments[0]
$toolArguments = @()
if ($Arguments.Count -gt 1) {
    $toolArguments = $Arguments[1..($Arguments.Count - 1)]
}

function Get-ArtifactFreshnessExitCode {
    $requiredArtifacts = @(
        $artifactPath,
        (Join-Path $artifactDirectory "OrchestratorSqliteTools.deps.json"),
        (Join-Path $artifactDirectory "OrchestratorSqliteTools.runtimeconfig.json")
    )
    if ($requiredArtifacts | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }) {
        return 1
    }

    if (-not (Get-ChildItem -LiteralPath $artifactDirectory -Recurse -Filter "e_sqlite3.dll" -File -ErrorAction SilentlyContinue | Select-Object -First 1)) {
        return 1
    }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $freshnessScript `
        -RepositoryRoot $repoRoot `
        -ArtifactPath $artifactPath `
        -MarkerPath $markerPath `
        $toolSourcePath $coreSourcePath `
            (Join-Path $repoRoot "Directory.Build.props") `
            (Join-Path $repoRoot "Directory.Build.rsp") `
            (Join-Path $repoRoot "global.json") *> $null
    return $LASTEXITCODE
}

function Write-ActionableFailure {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    [Console]::Error.WriteLine("ERROR: $Message")
    exit 1
}

function Assert-FreshnessCheckAvailable {
    param(
        [Parameter(Mandatory = $true)]
        [int]$ExitCode
    )

    if ($ExitCode -eq 2) {
        Write-ActionableFailure "SQLite helper artifact freshness could not be validated because git rev-parse failed; ensure Git is available for this repository, then retry."
    }
}

$hashAlgorithm = [System.Security.Cryptography.SHA256]::Create()
try {
    $repoHashBytes = $hashAlgorithm.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($repoRoot.ToUpperInvariant()))
    $repoHash = ([System.BitConverter]::ToString($repoHashBytes)).Replace("-", "").Substring(0, 16)
}
finally {
    $hashAlgorithm.Dispose()
}

$artifactMutex = [System.Threading.Mutex]::new($false, "Local\mcg-sqlite-tool-artifact-$repoHash")
$mutexHeld = $false
try {
    try {
        $mutexHeld = $artifactMutex.WaitOne([TimeSpan]::FromSeconds(30))
    }
    catch [System.Threading.AbandonedMutexException] {
        $mutexHeld = $true
    }

    if (-not $mutexHeld) {
        Write-ActionableFailure "SQLite helper artifact validation or rebuild is already in progress; retry after the current operator command finishes."
    }

    $freshnessExitCode = Get-ArtifactFreshnessExitCode
    Assert-FreshnessCheckAvailable -ExitCode $freshnessExitCode
    if ($freshnessExitCode -ne 0) {
        $requiredAssets = @(
            (Join-Path $repoRoot "scripts\OrchestratorSqliteTools\obj\project.assets.json"),
            (Join-Path $repoRoot "src\Mcg.AgentOrchestrator.Core\obj\project.assets.json")
        )
        $missingAssets = @($requiredAssets | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
        if ($missingAssets.Count -gt 0) {
            Write-ActionableFailure "SQLite helper artifact is missing or stale and no-restored build assets are unavailable; run .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-PackageAudit.ps1 online, then retry."
        }

        $buildLog = [System.IO.Path]::GetTempFileName()
        try {
            & $dotnetHost build $projectPath --no-restore --nologo --verbosity quiet `
                -clp:ErrorsOnly -p:UseSharedCompilation=false -p:McgIsolatedArtifactsPath= *> $buildLog
            $buildExit = $LASTEXITCODE
        }
        catch {
            $buildExit = 1
        }
        finally {
            Remove-Item -LiteralPath $buildLog -Force -ErrorAction SilentlyContinue
        }

        if ($buildExit -ne 0 -or -not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
            Write-ActionableFailure "SQLite helper no-restore rebuild failed; run .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-PackageAudit.ps1 online, then retry."
        }

        & $markerScript -RepositoryRoot $repoRoot -MarkerPath $markerPath
        $freshnessExitCode = Get-ArtifactFreshnessExitCode
        Assert-FreshnessCheckAvailable -ExitCode $freshnessExitCode
        if ($freshnessExitCode -ne 0) {
            Write-ActionableFailure "SQLite helper rebuild did not produce an artifact matching current source and git HEAD; inspect the repository state, then retry."
        }
    }
}
finally {
    if ($mutexHeld) {
        $artifactMutex.ReleaseMutex()
    }

    $artifactMutex.Dispose()
}

& $dotnetHost $artifactPath $toolCommand @toolArguments
if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
