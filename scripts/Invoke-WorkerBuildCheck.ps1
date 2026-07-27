param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Projects,

    [string]$Configuration = "Debug"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-SafePathSegment {
    param([string]$Value)
    $safe = ($Value.Trim().ToLowerInvariant().ToCharArray() | ForEach-Object {
        if ([char]::IsLetterOrDigit($_)) { $_ } else { '-' }
    }) -join ''
    $safe = $safe.Trim('-')
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return "manual"
    }

    return $safe
}

function Get-BuildSlotName {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return "build-0"
    }

    [int]$hash = 0
    foreach ($ch in $Value.ToLowerInvariant().ToCharArray()) {
        $hash = ($hash + [int][char]$ch) % 2
    }

    return "build-$hash"
}

function Get-IsolatedRootBase {
    if (-not [string]::IsNullOrWhiteSpace($env:MCG_DOTNET_ISOLATED_ROOT)) {
        return $env:MCG_DOTNET_ISOLATED_ROOT
    }

    return (Join-Path ([System.IO.Path]::GetTempPath()) "mcg-dotnet-isolated")
}

function Get-HostTempBase {
    $repositoryRoot = (Get-Location).Path
    $candidate = [System.IO.Path]::GetTempPath()
    if ($candidate.StartsWith($repositoryRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $localTemp = Join-Path $env:LOCALAPPDATA "Temp"
        try {
            $probe = Join-Path $localTemp "mcg-dotnet-probe-$PID"
            New-Item -ItemType Directory -Force -Path $probe | Out-Null
            Remove-Item -LiteralPath $probe -Force -Recurse
            return $localTemp
        }
        catch {
            return $candidate
        }
    }

    return $candidate
}

function Get-BuildMaxCpuCount {
    $configured = $env:MCG_BUILD_MAXCPUCOUNT
    $value = 0
    if ([int]::TryParse($configured, [ref]$value) -and $value -gt 0) {
        return $value
    }

    return 1
}

function Test-ContainsOrdinalIgnoreCase {
    param(
        [string]$Value,
        [string]$Pattern
    )

    return $Value.IndexOf($Pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-DisplayPath {
    param(
        [string]$Root,
        [string]$Path
    )

    $normalizedRoot = $Root.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    if ($Path.StartsWith($normalizedRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Path.Substring($normalizedRoot.Length + 1)
    }

    return $Path
}

function Get-GoalPrefix {
    $repositoryRoot = (Get-Location).Path
    $worktreeMarker = "$([System.IO.Path]::DirectorySeparatorChar).orchestrator-worktrees$([System.IO.Path]::DirectorySeparatorChar)"
    $worktreeIndex = $repositoryRoot.IndexOf($worktreeMarker, [System.StringComparison]::OrdinalIgnoreCase)
    if ($worktreeIndex -ge 0) {
        $prefixStart = $worktreeIndex + $worktreeMarker.Length
        $remaining = $repositoryRoot.Substring($prefixStart)
        $separatorIndex = $remaining.IndexOf([System.IO.Path]::DirectorySeparatorChar)
        if ($separatorIndex -ge 0) {
            $remaining = $remaining.Substring(0, $separatorIndex)
        }

        if (-not [string]::IsNullOrWhiteSpace($remaining)) {
            return $remaining
        }
    }

    $branch = (& git rev-parse --abbrev-ref HEAD 2>$null)
    if ($LASTEXITCODE -eq 0) {
        $branch = $branch.Trim()
        if ($branch.StartsWith("goal/", [System.StringComparison]::OrdinalIgnoreCase)) {
            return $branch.Substring("goal/".Length)
        }
    }

    return "manual"
}

function Clear-ArtifactsDirectory {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Test-OwnerMarkerMatches {
    param(
        [string]$Path,
        [string]$OwnerToken
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    try {
        $marker = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        return [string]::Equals([string]$marker.ownerToken, $OwnerToken, [System.StringComparison]::Ordinal)
    }
    catch {
        return $false
    }
}

function Initialize-ArtifactsDirectory {
    param(
        [string]$Path,
        [string]$OwnerToken,
        [bool]$ForceClean
    )

    $ownerPath = Join-Path $Path ".mcg-artifacts-owner.json"
    $hasEntries = (Test-Path -LiteralPath $Path) -and $null -ne (Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue | Select-Object -First 1)
    if ($ForceClean -or ($hasEntries -and -not (Test-OwnerMarkerMatches -Path $ownerPath -OwnerToken $OwnerToken))) {
        Clear-ArtifactsDirectory -Path $Path
    }
    else {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }

    $marker = [ordered]@{
        version = 1
        ownerToken = $OwnerToken
        ownerProcessId = $PID
        machineName = $env:COMPUTERNAME
        lastAcquiredAt = (Get-Date).ToUniversalTime().ToString("o")
    }
    ($marker | ConvertTo-Json -Depth 3) | Set-Content -LiteralPath $ownerPath
}

$repositoryRoot = (Get-Location).Path
$missingProjects = @()
$projectPaths = @(foreach ($project in $Projects) {
    $candidate = if ([System.IO.Path]::IsPathRooted($project)) {
        $project
    }
    else {
        Join-Path $repositoryRoot $project
    }

    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        $missingProjects += $project
        continue
    }

    (Resolve-Path -LiteralPath $candidate).Path
})

if ($missingProjects.Count -gt 0) {
    Write-Output "FAIL build: missing project(s) (Invoke-WorkerBuildCheck)"
    foreach ($project in $missingProjects) {
        Write-Output "error: project not found: $project"
    }
    exit 1
}

$safeGoalPrefix = ConvertTo-SafePathSegment -Value (Get-GoalPrefix)
$isolatedRoot = Get-IsolatedRootBase
$buildSlotName = Get-BuildSlotName -Value $safeGoalPrefix
$leaseId = "goal-$safeGoalPrefix"
$runRoot = Join-Path $isolatedRoot "goals\$safeGoalPrefix"
$leaseRoot = Join-Path $runRoot "lease"
$artifactsPath = Join-Path $runRoot "artifacts"
$executionLockPath = Join-Path $isolatedRoot "build-slots\$buildSlotName.lock"
New-Item -ItemType Directory -Force -Path $leaseRoot | Out-Null

$lockPath = Join-Path $leaseRoot "lease.lock"
$staleLockCleared = $false
if (Test-Path -LiteralPath $lockPath) {
    $lockText = (Get-Content -LiteralPath $lockPath -Raw).Trim()
    $lockPid = 0
    if ([int]::TryParse($lockText, [ref]$lockPid)) {
        $lockProcess = Get-Process -Id $lockPid -ErrorAction SilentlyContinue
        if ($null -eq $lockProcess) {
            Remove-Item -LiteralPath $lockPath -Force
            $staleLockCleared = $true
        }
    }
}

Set-Content -LiteralPath $lockPath -Value ([string]$PID)
$metadata = [ordered]@{
    version = 1
    goalPrefix = $safeGoalPrefix
    leaseId = $leaseId
    rootPath = $runRoot
    artifactsPath = $artifactsPath
    ownerProcessId = $PID
    machineName = $env:COMPUTERNAME
    lastUsedAt = (Get-Date).ToUniversalTime().ToString("o")
    lastAttemptName = "worker-build-check"
    staleLockCleared = $staleLockCleared
}
($metadata | ConvertTo-Json -Depth 3) | Set-Content -LiteralPath (Join-Path $leaseRoot "lease.json")

$processTempPath = Join-Path (Join-Path (Get-HostTempBase) "pt\$leaseId") "$PID"
New-Item -ItemType Directory -Force -Path (Join-Path (Get-HostTempBase) "pt") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path (Get-HostTempBase) "pt\$leaseId") | Out-Null
New-Item -ItemType Directory -Force -Path $processTempPath | Out-Null

$env:MCG_ORCHESTRATOR_REPOSITORY_ROOT = $repositoryRoot
$env:TEMP = $processTempPath
$env:TMP = $processTempPath
Remove-Item Env:MCG_WORKER_SANDBOX -ErrorAction SilentlyContinue
Remove-Item Env:MCG_WORKER_ACCOUNT -ErrorAction SilentlyContinue
Remove-Item Env:MCG_WORKER_CREDENTIAL_TARGET -ErrorAction SilentlyContinue

$lockStream = $null
$lockHeld = $false
$failedOutput = [System.Collections.Generic.List[string]]::new()
$exitCode = 0
try {
    $lockDirectory = Split-Path -Parent $executionLockPath
    New-Item -ItemType Directory -Force -Path $lockDirectory | Out-Null
    $deadline = [DateTime]::UtcNow.AddMinutes(5)
    while (-not $lockHeld) {
        $lockStream = [System.IO.File]::Open($executionLockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
        try {
            $lockStream.Lock(0, 1)
            $lockHeld = $true
            Initialize-ArtifactsDirectory -Path $artifactsPath -OwnerToken $leaseId -ForceClean $staleLockCleared
        }
        catch [System.IO.IOException] {
            $lockStream.Dispose()
            $lockStream = $null
            if ([DateTime]::UtcNow -ge $deadline) {
                throw "Timed out waiting for build lease execution lock: $executionLockPath"
            }

            Start-Sleep -Milliseconds 100
        }
    }

    $isolatedArguments = @(
        "--artifacts-path",
        $artifactsPath,
        "-maxcpucount:$(Get-BuildMaxCpuCount)",
        "-p:BuildInParallel=false",
        "-clp:ErrorsOnly"
    )

    foreach ($projectPath in $projectPaths) {
        $relativeProject = Get-DisplayPath -Root $repositoryRoot -Path $projectPath
        $output = & dotnet build $projectPath --nologo --configuration $Configuration --verbosity minimal @isolatedArguments 2>&1
        $projectExitCode = $LASTEXITCODE
        if ($projectExitCode -ne 0) {
            $exitCode = 1
            $failedOutput.Add("project: $relativeProject")
            foreach ($line in $output) {
                $text = [string]$line
                if (-not [string]::IsNullOrWhiteSpace($text)) {
                    $failedOutput.Add($text)
                }
            }
        }
    }
}
finally {
    if ($lockHeld -and $null -ne $lockStream) {
        & dotnet build-server shutdown *> $null
        $lockStream.Unlock(0, 1)
        $lockStream.Dispose()
    }

    Remove-Item -LiteralPath $processTempPath -Force -Recurse -ErrorAction SilentlyContinue
}

if ($exitCode -eq 0) {
    Write-Output "PASS build: 0 errors (Invoke-WorkerBuildCheck) projects=$($projectPaths.Count)"
}
else {
    $errorLines = $failedOutput | Where-Object { Test-ContainsOrdinalIgnoreCase -Value $_ -Pattern ": error " }
    $errorCount = @($errorLines).Count
    if ($errorCount -eq 0) {
        $errorCount = 1
    }

    Write-Output "FAIL build: $errorCount error(s) (Invoke-WorkerBuildCheck)"
    foreach ($line in $failedOutput) {
        Write-Output $line
    }
}

exit $exitCode
