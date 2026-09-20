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

function Get-BuildConcurrencySlotCount {
    $sourcePath = Join-Path $PSScriptRoot "..\src\Mcg.AgentOrchestrator.Infrastructure\Workspaces\DotnetBuildEnvironmentManager.cs"
    $source = Get-Content -LiteralPath $sourcePath -Raw
    $match = [regex]::Match($source, 'public const int BuildConcurrencySlotCount = (?<count>\d+);')
    if (-not $match.Success) {
        throw "Could not resolve BuildConcurrencySlotCount from $sourcePath"
    }

    return [int]$match.Groups["count"].Value
}

function Get-BuildSlotName {
    param(
        [string]$Value,
        [int]$SlotCount
    )
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return "build-0"
    }

    [int]$hash = 0
    foreach ($ch in $Value.ToLowerInvariant().ToCharArray()) {
        $hash = ($hash + [int][char]$ch) % $SlotCount
    }

    return "build-$hash"
}

function Get-IsolatedRootBase {
    if (-not [string]::IsNullOrWhiteSpace($env:MCG_DOTNET_ISOLATED_ROOT)) {
        return $env:MCG_DOTNET_ISOLATED_ROOT
    }

    if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT -and
        -not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        # DispatchProcessHost redirects TEMP into each worker's private sandbox. LocalLow is the
        # machine-user shared Low-integrity location, so workers and the acceptance lane resolve
        # the same four authoritative build-slot locks without widening worker write access.
        $localLow = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "..\LocalLow"))
        return (Join-Path $localLow "mcg-dotnet-isolated")
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

function ConvertTo-OutputLines {
    param([object[]]$Output)

    return [string[]]@($Output | ForEach-Object { [string]$_ })
}

function Get-DiagnosticLines {
    param(
        [string[]]$Lines,
        [string]$Kind
    )

    $pattern = ": $Kind "
    return [string[]]@($Lines | Where-Object { Test-ContainsOrdinalIgnoreCase -Value $_ -Pattern $pattern })
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

function Test-CustodyMarkerIsLive {
    param([object]$Marker)

    if ($null -eq $Marker -or [string]::IsNullOrWhiteSpace([string]$Marker.attemptId)) {
        return $false
    }

    $hintPath = [string]$Marker.livenessCheckHint
    if (-not [string]::IsNullOrWhiteSpace($hintPath) -and
        (Test-Path -LiteralPath $hintPath -PathType Leaf)) {
        try {
            $attempt = Get-Content -LiteralPath $hintPath -Raw | ConvertFrom-Json
            if (-not [string]::Equals(
                    [string]$attempt.attemptId,
                    [string]$Marker.attemptId,
                    [System.StringComparison]::Ordinal)) {
                return $false
            }

            $numericOutcome = 0
            $outcomeText = [string]$attempt.outcome
            $isRunning = [string]::Equals(
                $outcomeText,
                "Running",
                [System.StringComparison]::OrdinalIgnoreCase) -or
                ([int]::TryParse($outcomeText, [ref]$numericOutcome) -and $numericOutcome -eq 0)
            if (-not $isRunning) {
                return $false
            }

            $ownerProcessId = [int]$attempt.ownerProcessId
            if (-not [string]::Equals(
                    [string]$Marker.machineName,
                    [Environment]::MachineName,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                $lastHeartbeatAt = [DateTimeOffset]::MinValue
                return [DateTimeOffset]::TryParse([string]$attempt.lastHeartbeatAt, [ref]$lastHeartbeatAt) -and
                    ([DateTimeOffset]::UtcNow - $lastHeartbeatAt) -le [TimeSpan]::FromMinutes(2)
            }

            $owner = Get-Process -Id $ownerProcessId -ErrorAction SilentlyContinue
            if ($null -eq $owner) {
                return $false
            }

            $acquiredAt = [DateTimeOffset]::MinValue
            return -not [DateTimeOffset]::TryParse([string]$Marker.acquiredAt, [ref]$acquiredAt) -or
                $owner.StartTime.ToUniversalTime() -le $acquiredAt.UtcDateTime.AddSeconds(1)
        }
        catch {
            # Protect a live owner while its atomic lifecycle record is briefly unavailable.
        }
    }

    try {
        $acquiredAt = [DateTimeOffset]::MinValue
        $hasAcquiredAt = [DateTimeOffset]::TryParse([string]$Marker.acquiredAt, [ref]$acquiredAt)
        if (-not [string]::Equals(
                [string]$Marker.machineName,
                [Environment]::MachineName,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return $hasAcquiredAt -and
                ([DateTimeOffset]::UtcNow - $acquiredAt) -le [TimeSpan]::FromHours(6)
        }

        $owner = Get-Process -Id ([int]$Marker.ownerProcessId) -ErrorAction SilentlyContinue
        if ($null -eq $owner) {
            return $false
        }

        return -not $hasAcquiredAt -or
            $owner.StartTime.ToUniversalTime() -le $acquiredAt.UtcDateTime.AddSeconds(1)
    }
    catch {
        return $false
    }
}

function Assert-CustodyAllowsTakeover {
    param([string]$ArtifactsPath)

    $custodyPath = Join-Path $ArtifactsPath ".mcg-artifacts-custody.json"
    if (-not (Test-Path -LiteralPath $custodyPath -PathType Leaf)) {
        return
    }

    try {
        $marker = Get-Content -LiteralPath $custodyPath -Raw | ConvertFrom-Json
    }
    catch {
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($env:MCG_ACCEPTANCE_GATE_ATTEMPT_ID) -and
        [string]::Equals(
            [string]$marker.attemptId,
            $env:MCG_ACCEPTANCE_GATE_ATTEMPT_ID,
            [System.StringComparison]::Ordinal)) {
        return
    }

    if (Test-CustodyMarkerIsLive -Marker $marker) {
        throw (
            "Artifact slot takeover refused because acceptance attempt '$([string]$marker.attemptId)' " +
            "has live custody of '$ArtifactsPath'. Wait for the acceptance attempt to reach a terminal state before retrying.")
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
        Assert-CustodyAllowsTakeover -ArtifactsPath $Path
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
$buildConcurrencySlotCount = Get-BuildConcurrencySlotCount
$buildSlotName = Get-BuildSlotName -Value $safeGoalPrefix -SlotCount $buildConcurrencySlotCount
$leaseId = "goal-$safeGoalPrefix"
$runRoot = Join-Path $isolatedRoot "goals\$safeGoalPrefix"
$leaseRoot = Join-Path $runRoot "lease"
$artifactsPath = Join-Path $runRoot "artifacts"
$executionLockPath = Join-Path $isolatedRoot "build-slots\$buildSlotName.lock"
New-Item -ItemType Directory -Force -Path $leaseRoot | Out-Null

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
    staleLockCleared = $false
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
$failureRecords = [System.Collections.Generic.List[object]]::new()
$exitCode = 0
$apparatusFailure = $null
$warningCount = 0
$capturedCharacterCount = 0
$runStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$runLogRoot = $null
try {
    $lockDirectory = Split-Path -Parent $executionLockPath
    New-Item -ItemType Directory -Force -Path $lockDirectory | Out-Null
    $deadline = [DateTime]::UtcNow.AddMinutes(5)
    while (-not $lockHeld) {
        $lockStream = [System.IO.File]::Open($executionLockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
        try {
            $lockStream.Lock(0, 1)
            $lockHeld = $true
            Initialize-ArtifactsDirectory -Path $artifactsPath -OwnerToken $leaseId -ForceClean $false
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
        "-p:McgIsolatedArtifactsPath=$artifactsPath",
        "-maxcpucount:$(Get-BuildMaxCpuCount)",
        "-p:BuildInParallel=false",
        "-clp:ErrorsOnly",
        "-tl:off"
    )

    $runId = "{0}-{1}-{2}" -f (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ"), $PID, ([Guid]::NewGuid().ToString("N").Substring(0, 8))
    $runLogRoot = Join-Path $artifactsPath "worker-build-logs\$runId"
    New-Item -ItemType Directory -Force -Path $runLogRoot | Out-Null

    for ($projectIndex = 0; $projectIndex -lt $projectPaths.Count; $projectIndex++) {
        $projectPath = $projectPaths[$projectIndex]
        $relativeProject = Get-DisplayPath -Root $repositoryRoot -Path $projectPath
        $safeProjectName = ConvertTo-SafePathSegment -Value ([System.IO.Path]::GetFileNameWithoutExtension($projectPath))
        $logPath = Join-Path $runLogRoot ("{0:D2}-{1}.log" -f ($projectIndex + 1), $safeProjectName)
        $fileLoggerArguments = @(
            "-fl",
            "-flp:LogFile=$logPath;Verbosity=normal;Encoding=UTF-8;Append=false"
        )
        $output = @(& dotnet build $projectPath --nologo --configuration $Configuration --verbosity minimal @isolatedArguments @fileLoggerArguments 2>&1)
        $projectExitCode = $LASTEXITCODE
        $outputLines = ConvertTo-OutputLines -Output $output
        if ($projectExitCode -ne 0) {
            $exitCode = 1
            $failureRecords.Add([pscustomobject]@{
                Project = $relativeProject
                LogPath = $logPath
                Lines = $outputLines
            })
        }

        try {
            if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
                throw "MSBuild did not create the detailed file logger output."
            }

            $detailedOutputLines = [string[]]@(Get-Content -LiteralPath $logPath)
            $warningCount += @(Get-DiagnosticLines -Lines $detailedOutputLines -Kind "warning").Count
            $capturedCharacterCount += [System.IO.File]::ReadAllText($logPath).Length
        }
        catch {
            if ($projectExitCode -eq 0) {
                $failureRecords.Add([pscustomobject]@{
                    Project = $relativeProject
                    LogPath = $logPath
                    Lines = $outputLines
                })
            }
            $exitCode = 1
            $apparatusFailure = "could not retain detailed build log '$logPath': $($_.Exception.Message)"
            break
        }
    }
}
catch {
    $exitCode = 1
    $apparatusFailure = $_.Exception.Message
}
finally {
    if ($lockHeld -and $null -ne $lockStream) {
        $lockStream.Unlock(0, 1)
        $lockStream.Dispose()
    }

    Remove-Item -LiteralPath $processTempPath -Force -Recurse -ErrorAction SilentlyContinue
}

$runStopwatch.Stop()
$logSummary = if ([string]::IsNullOrWhiteSpace($runLogRoot)) { "unavailable" } else { $runLogRoot }
if ($exitCode -eq 0) {
    Write-Output "PASS build: 0 errors (Invoke-WorkerBuildCheck) projects=$($projectPaths.Count) warnings=$warningCount captured_chars=$capturedCharacterCount elapsed_ms=$($runStopwatch.ElapsedMilliseconds) logs=$logSummary"
}
else {
    $errorLines = [string[]]@($failureRecords | ForEach-Object { Get-DiagnosticLines -Lines $_.Lines -Kind "error" })
    $errorCount = @($errorLines).Count
    if ($errorCount -eq 0) {
        $errorCount = 1
    }

    Write-Output "FAIL build: $errorCount error(s) (Invoke-WorkerBuildCheck) projects=$($projectPaths.Count) warnings=$warningCount captured_chars=$capturedCharacterCount elapsed_ms=$($runStopwatch.ElapsedMilliseconds) logs=$logSummary"
    if (-not [string]::IsNullOrWhiteSpace($apparatusFailure)) {
        Write-Output "error: build-check apparatus failure: $apparatusFailure"
    }

    $remainingContextLines = 4
    foreach ($record in $failureRecords) {
        Write-Output "project: $($record.Project)"
        Write-Output "complete-log: $($record.LogPath)"
        foreach ($line in (Get-DiagnosticLines -Lines $record.Lines -Kind "error")) {
            Write-Output $line
        }

        if ($remainingContextLines -gt 0) {
            $contextLines = [string[]]@($record.Lines | Where-Object {
                -not [string]::IsNullOrWhiteSpace($_) -and
                -not (Test-ContainsOrdinalIgnoreCase -Value $_ -Pattern ": error ")
            } | Select-Object -Last $remainingContextLines)
            foreach ($line in $contextLines) {
                Write-Output "context: $line"
            }
            $remainingContextLines -= $contextLines.Count
        }
    }
}

exit $exitCode
