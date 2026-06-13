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

$stamp = Get-Date -Format "yyyyMMdd-HHmmssfff"
$suffix = [guid]::NewGuid().ToString("N").Substring(0, 8)
$safeAttemptName = ConvertTo-SafePathSegment -Value $AttemptName
if ([string]::IsNullOrWhiteSpace($GoalPrefix)) {
    $runRoot = Join-Path ([System.IO.Path]::GetTempPath()) "mcg-dotnet-isolated\runs"
    $artifactsPath = Join-Path $runRoot "attempts\$safeAttemptName-$stamp-$PID-$suffix"
    $leaseId = "run-$stamp-$PID-$suffix"
    $executionLockPath = $null
}
else {
    $safeGoalPrefix = ConvertTo-SafePathSegment -Value $GoalPrefix
    $leaseId = "goal-$safeGoalPrefix"
    $runRoot = Join-Path ([System.IO.Path]::GetTempPath()) "mcg-dotnet-isolated\goals\$safeGoalPrefix"
    $leaseRoot = Join-Path $runRoot "lease"
    $artifactsPath = Join-Path $leaseRoot "artifacts"
    $executionLockPath = Join-Path $leaseRoot "lease.execution.lock"
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
    "-maxcpucount:1",
    "-p:UseSharedCompilation=false"
)

$env:DOTNET_CLI_USE_MSBUILD_SERVER = "0"
$env:MSBUILDDISABLENODEREUSE = "1"
$env:UseSharedCompilation = "false"
$env:MCG_ORCHESTRATOR_REPOSITORY_ROOT = (Get-Location).Path

$lockStream = $null
$lockHeld = $false
try {
    if ($null -ne $executionLockPath) {
        $lockDirectory = Split-Path -Parent $executionLockPath
        New-Item -ItemType Directory -Force -Path $lockDirectory | Out-Null
        $deadline = [DateTime]::UtcNow.AddMinutes(5)
        while (-not $lockHeld) {
            $lockStream = [System.IO.File]::Open($executionLockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
            try {
                $lockStream.Lock(0, 1)
                $lockHeld = $true
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
    }

    & dotnet @DotnetArguments @isolatedArguments
    $exitCode = $LASTEXITCODE
}
finally {
    if ($lockHeld -and $null -ne $lockStream) {
        $lockStream.Unlock(0, 1)
        $lockStream.Dispose()
    }

    & dotnet build-server shutdown *> $null
}

exit $exitCode
