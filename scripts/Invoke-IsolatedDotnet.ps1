param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$GoalPrefix = $null
$AttemptName = "manual"
$remainingArguments = [System.Collections.Generic.List[string]]::new()
for ($i = 0; $i -lt $Arguments.Count; $i++) {
    if ($Arguments[$i].Equals("-GoalPrefix", [System.StringComparison]::OrdinalIgnoreCase)) {
        if ($i + 1 -ge $Arguments.Count) {
            throw "-GoalPrefix requires a value."
        }

        $GoalPrefix = $Arguments[$i + 1]
        $i++
        continue
    }

    if ($Arguments[$i].Equals("-AttemptName", [System.StringComparison]::OrdinalIgnoreCase)) {
        if ($i + 1 -ge $Arguments.Count) {
            throw "-AttemptName requires a value."
        }

        $AttemptName = $Arguments[$i + 1]
        $i++
        continue
    }

    $remainingArguments.Add($Arguments[$i])
}
$DotnetArguments = $remainingArguments.ToArray()

if ($DotnetArguments.Count -eq 0) {
    throw "Usage: .\scripts\Invoke-IsolatedDotnet.ps1 [-GoalPrefix <goal-prefix>] test Mcg.AgentOrchestrator.sln --verbosity minimal"
}

$RepositoryRoot = (Get-Location).Path

if ($env:MCG_ORCHESTRATOR_WORKER_DISPATCH -eq "1" -or
    $env:MCG_ORCHESTRATOR_WORKER_DISPATCH -eq "true") {
    throw "Worker-side .NET self-verification is disabled. Report tests: not-run - orchestrator acceptance gate verifies via stable slots."
}

function ConvertTo-SafePathSegment {
    param([string]$Value)
    $safe = ($Value.Trim().ToLowerInvariant().ToCharArray() | ForEach-Object {
        if ([char]::IsLetterOrDigit($_)) { $_ } else { '-' }
    }) -join ''
    $safe = $safe.Trim('-')
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return "dotnet"
    }

    return $safe
}

function Get-StableSlotName {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return "manual"
    }

    [int64]$hash = 0
    foreach ($ch in $Value.ToLowerInvariant().ToCharArray()) {
        $hash = (($hash * 31) + [int][char]$ch) % 2147483647
    }

    return "slot-$([Math]::Abs($hash % 4))"
}

function Clear-ArtifactsDirectory {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Get-BuildMaxCpuCount {
    $configured = $env:MCG_BUILD_MAXCPUCOUNT
    $value = 0
    if ([int]::TryParse($configured, [ref]$value) -and $value -gt 0) {
        return $value
    }

    return 1
}

function Get-IsolatedRootBase {
    if (-not [string]::IsNullOrWhiteSpace($env:MCG_DOTNET_ISOLATED_ROOT)) {
        return $env:MCG_DOTNET_ISOLATED_ROOT
    }

    return (Join-Path ([System.IO.Path]::GetTempPath()) "mcg-dotnet-isolated")
}

function Get-HostTempBase {
    $candidate = [System.IO.Path]::GetTempPath()
    $repositoryRoot = $script:RepositoryRoot
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

function Update-AppDllGitHeadMarker {
    $repositoryRoot = $script:RepositoryRoot
    $appOutput = Join-Path $repositoryRoot "src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0"
    $appDll = Join-Path $appOutput "Mcg.AgentOrchestrator.App.dll"
    if (-not (Test-Path -LiteralPath $appDll -PathType Leaf)) {
        return
    }

    try {
        $gitHead = (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
        if (-not [string]::IsNullOrWhiteSpace($gitHead)) {
            Set-Content -LiteralPath "$appDll.git-head" -Value $gitHead -NoNewline -Encoding ASCII
        }
    }
    catch {
    }
}

function Get-AppDllSnapshot {
    $repositoryRoot = $script:RepositoryRoot
    $appDll = Join-Path $repositoryRoot "src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0\Mcg.AgentOrchestrator.App.dll"
    if (-not (Test-Path -LiteralPath $appDll -PathType Leaf)) {
        return [pscustomobject]@{
            Exists = $false
            Length = 0
            LastWriteTimeUtcTicks = 0
        }
    }

    $item = Get-Item -LiteralPath $appDll
    return [pscustomobject]@{
        Exists = $true
        Length = $item.Length
        LastWriteTimeUtcTicks = $item.LastWriteTimeUtc.Ticks
    }
}

function Test-AppDllChangedSinceSnapshot {
    param([object]$Snapshot)

    $current = Get-AppDllSnapshot
    if (-not $current.Exists) {
        return $false
    }

    return (-not $Snapshot.Exists) -or
        ($current.Length -ne $Snapshot.Length) -or
        ($current.LastWriteTimeUtcTicks -ne $Snapshot.LastWriteTimeUtcTicks)
}

$safeAttemptName = ConvertTo-SafePathSegment -Value $AttemptName
$hostTempBase = Get-HostTempBase
$isolatedRoot = Get-IsolatedRootBase
if ([string]::IsNullOrWhiteSpace($GoalPrefix)) {
    $slotRoot = Join-Path $isolatedRoot "slots\manual"
    $runRoot = Join-Path $isolatedRoot "manual"
    $artifactsPath = Join-Path $slotRoot "artifacts"
    $leaseId = "run-slot-manual"
    $ownerToken = "manual"
    $executionLockPath = Join-Path $slotRoot "lease.execution.lock"
    $staleLockCleared = $false
}
else {
    $safeGoalPrefix = ConvertTo-SafePathSegment -Value $GoalPrefix
    $slotName = Get-StableSlotName -Value $safeGoalPrefix
    $leaseId = "goal-$safeGoalPrefix"
    $runRoot = Join-Path $isolatedRoot "goals\$safeGoalPrefix"
    $slotRoot = Join-Path $isolatedRoot "slots\$slotName"
    $leaseRoot = Join-Path $runRoot "lease"
    $artifactsPath = Join-Path $slotRoot "artifacts"
    $ownerToken = $leaseId
    $executionLockPath = Join-Path $slotRoot "lease.execution.lock"
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
        lastAttemptName = $AttemptName
        staleLockCleared = $staleLockCleared
    }
    ($metadata | ConvertTo-Json -Depth 3) | Set-Content -LiteralPath (Join-Path $leaseRoot "lease.json")
}
$isolatedArguments = @(
    "--artifacts-path",
    $artifactsPath,
    "-maxcpucount:$(Get-BuildMaxCpuCount)",
    "-p:BuildInParallel=false"
)

$processTempPath = Join-Path (Join-Path $hostTempBase "pt\$ownerToken") "$PID"
New-Item -ItemType Directory -Force -Path (Join-Path $hostTempBase "pt") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $hostTempBase "pt\$ownerToken") | Out-Null
New-Item -ItemType Directory -Force -Path $processTempPath | Out-Null

$env:MCG_ORCHESTRATOR_REPOSITORY_ROOT = $RepositoryRoot
$env:TEMP = $processTempPath
$env:TMP = $processTempPath
Remove-Item Env:MCG_WORKER_SANDBOX -ErrorAction SilentlyContinue
Remove-Item Env:MCG_WORKER_ACCOUNT -ErrorAction SilentlyContinue
Remove-Item Env:MCG_WORKER_CREDENTIAL_TARGET -ErrorAction SilentlyContinue
Remove-Item Env:MCG_ORCHESTRATOR_WORKER_DISPATCH -ErrorAction SilentlyContinue

$lockStream = $null
$lockHeld = $false
try {
    $lockDirectory = Split-Path -Parent $executionLockPath
    New-Item -ItemType Directory -Force -Path $lockDirectory | Out-Null
    $deadline = [DateTime]::UtcNow.AddMinutes(5)
    while (-not $lockHeld) {
        $lockStream = [System.IO.File]::Open($executionLockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
        try {
            $lockStream.Lock(0, 1)
            $lockHeld = $true
            Initialize-ArtifactsDirectory -Path $artifactsPath -OwnerToken $ownerToken -ForceClean $staleLockCleared
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

    $appDllBeforeDotnet = Get-AppDllSnapshot
    & dotnet @DotnetArguments @isolatedArguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0 -and (Test-AppDllChangedSinceSnapshot -Snapshot $appDllBeforeDotnet)) {
        Update-AppDllGitHeadMarker
    }
}
finally {
    if ($lockHeld -and $null -ne $lockStream) {
        $lockStream.Unlock(0, 1)
        $lockStream.Dispose()
    }

    & dotnet build-server shutdown *> $null
    Remove-Item -LiteralPath $processTempPath -Force -Recurse -ErrorAction SilentlyContinue
}

exit $exitCode
