<#
.SYNOPSIS
Runs dotnet with stable isolated build artifacts, including reusable no-build timing passes.

.DESCRIPTION
Use the same -GoalPrefix for both timing passes. For Microsoft.Testing.Platform (MTP) test
projects, the first measurement builds into the goal's stable artifact slot and then runs
the built test executable with -ReuseArtifacts. The second measurement repeats only the
-ReuseArtifacts invocation. Reuse verifies the slot owner, test assembly, and MTP executable
before running xUnit directly without invoking MSBuild or the unsupported VSTest target.

.EXAMPLE
Measure-Command {
    .\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix 10f9e458 build tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal
    .\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix 10f9e458 -ReuseArtifacts test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --verbosity minimal
}

Measures total build plus xUnit time and leaves the output in the goal's stable artifact slot.

.EXAMPLE
Measure-Command { .\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix 10f9e458 -ReuseArtifacts test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --verbosity minimal }

Reuses the first measurement's output and measures only direct MTP/xUnit execution time.
#>
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$GoalPrefix = $null
$AttemptName = "manual"
$ReuseArtifacts = $false
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

    if ($Arguments[$i].Equals("-ReuseArtifacts", [System.StringComparison]::OrdinalIgnoreCase)) {
        $ReuseArtifacts = $true
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

function Get-OwnerMarkerToken {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return "<missing>"
    }

    try {
        $marker = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace([string]$marker.ownerToken)) {
            return "<invalid>"
        }

        return [string]$marker.ownerToken
    }
    catch {
        return "<invalid>"
    }
}

function Get-DotnetOptionValue {
    param(
        [string[]]$Values,
        [string[]]$Names
    )

    for ($i = 0; $i -lt $Values.Count; $i++) {
        foreach ($name in $Names) {
            if ($Values[$i].Equals($name, [System.StringComparison]::OrdinalIgnoreCase)) {
                if ($i + 1 -lt $Values.Count) {
                    return $Values[$i + 1]
                }

                return $null
            }

            $prefix = "$name="
            if ($Values[$i].StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $Values[$i].Substring($prefix.Length)
            }
        }
    }

    return $null
}

function Get-TestProjectPath {
    param([string[]]$Values)

    for ($i = 0; $i -lt $Values.Count - 1; $i++) {
        if (-not $Values[$i].Equals("test", [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        for ($candidateIndex = $i + 1; $candidateIndex -lt $Values.Count; $candidateIndex++) {
            $candidate = $Values[$candidateIndex]
            if ($candidate.StartsWith("-", [System.StringComparison]::Ordinal)) {
                continue
            }

            $extension = [System.IO.Path]::GetExtension($candidate)
            if ($extension -in @(".csproj", ".fsproj", ".vbproj")) {
                return $candidate
            }
        }
    }

    return $null
}

function Get-ReusableTestArtifacts {
    param(
        [string]$ArtifactsPath,
        [string]$OwnerToken,
        [string[]]$DotnetArguments
    )

    $projectPath = Get-TestProjectPath -Values $DotnetArguments
    if ([string]::IsNullOrWhiteSpace($projectPath)) {
        return [pscustomobject]@{
            Success = $false
            Reason = "Reuse requires 'dotnet test' with an explicit .csproj, .fsproj, or .vbproj path."
            ExpectedArtifactPath = (Join-Path $ArtifactsPath "bin\<project>\<configuration>_<target-framework>\<project>.dll")
            FoundOwnerToken = Get-OwnerMarkerToken -Path (Join-Path $ArtifactsPath ".mcg-artifacts-owner.json")
        }
    }

    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
    $configuration = Get-DotnetOptionValue -Values $DotnetArguments -Names @("--configuration", "-c")
    $targetFramework = Get-DotnetOptionValue -Values $DotnetArguments -Names @("--framework", "-f")
    $layoutName = if ([string]::IsNullOrWhiteSpace($configuration)) { "<configuration>" } else { $configuration.ToLowerInvariant() }
    $projectArtifactsPath = Join-Path (Join-Path $ArtifactsPath "bin") $projectName
    $expectedArtifactPath = Join-Path (Join-Path $projectArtifactsPath $layoutName) "$projectName.dll"
    $isWindowsHost = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
    $executableName = if ($isWindowsHost) { "$projectName.exe" } else { $projectName }
    $expectedExecutablePath = Join-Path (Join-Path $projectArtifactsPath $layoutName) $executableName
    $ownerPath = Join-Path $ArtifactsPath ".mcg-artifacts-owner.json"
    $foundOwnerToken = Get-OwnerMarkerToken -Path $ownerPath

    if (-not [string]::Equals($foundOwnerToken, $OwnerToken, [System.StringComparison]::Ordinal)) {
        return [pscustomobject]@{
            Success = $false
            Reason = "The artifact slot is not owned by this invocation."
            ExpectedArtifactPath = $expectedArtifactPath
            FoundOwnerToken = $foundOwnerToken
        }
    }

    $candidateDirectories = if (Test-Path -LiteralPath $projectArtifactsPath -PathType Container) {
        @(Get-ChildItem -LiteralPath $projectArtifactsPath -Directory -ErrorAction SilentlyContinue | Where-Object {
            [string]::IsNullOrWhiteSpace($configuration) -or
            $_.Name.Equals($configuration, [System.StringComparison]::OrdinalIgnoreCase) -or
            ($_.Name.StartsWith("${configuration}_", [System.StringComparison]::OrdinalIgnoreCase) -and
                ([string]::IsNullOrWhiteSpace($targetFramework) -or
                    $_.Name.EndsWith("_${targetFramework}", [System.StringComparison]::OrdinalIgnoreCase)))
        })
    }
    else {
        @()
    }

    $assemblyArtifacts = @($candidateDirectories | ForEach-Object {
        $candidatePath = Join-Path $_.FullName "$projectName.dll"
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            [pscustomobject]@{
                AssemblyPath = $candidatePath
                ExecutablePath = Join-Path $_.FullName $executableName
                DependencyDirectory = $_.FullName
            }
        }
    })

    if ($assemblyArtifacts.Count -ne 1) {
        $reason = if ($assemblyArtifacts.Count -eq 0) {
            "The reusable test assembly was not found."
        }
        else {
            "More than one reusable test assembly matched; pass --configuration and --framework to select one."
        }
        return [pscustomobject]@{
            Success = $false
            Reason = $reason
            ExpectedArtifactPath = $expectedArtifactPath
            FoundOwnerToken = $foundOwnerToken
        }
    }

    $selectedArtifact = $assemblyArtifacts[0]
    if (-not (Test-Path -LiteralPath $selectedArtifact.ExecutablePath -PathType Leaf)) {
        return [pscustomobject]@{
            Success = $false
            Reason = "The reusable Microsoft.Testing.Platform executable was not found."
            ExpectedArtifactPath = $expectedExecutablePath
            FoundOwnerToken = $foundOwnerToken
        }
    }

    return [pscustomobject]@{
        Success = $true
        AssemblyPath = $selectedArtifact.AssemblyPath
        ExecutablePath = $selectedArtifact.ExecutablePath
        DependencyDirectory = $selectedArtifact.DependencyDirectory
        ExpectedArtifactPath = $expectedArtifactPath
        FoundOwnerToken = $foundOwnerToken
    }
}

function ConvertTo-MtpFilterArguments {
    param([string]$Filter)

    $result = [System.Collections.Generic.List[string]]::new()
    foreach ($rawToken in [System.Text.RegularExpressions.Regex]::Split($Filter, "[&|]")) {
        $token = $rawToken.Trim().Trim([char[]]@("(", ")")).Trim()
        if ([string]::IsNullOrWhiteSpace($token)) {
            continue
        }

        $fullyQualifiedName = [System.Text.RegularExpressions.Regex]::Match(
            $token,
            "^FullyQualifiedName\s*(?<op>!~|~)\s*(?<value>[A-Za-z_][A-Za-z0-9_.]*)$",
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($fullyQualifiedName.Success) {
            $result.Add($(if ($fullyQualifiedName.Groups["op"].Value -eq "!~") { "--filter-not-class" } else { "--filter-class" }))
            $result.Add("*$($fullyQualifiedName.Groups["value"].Value)*")
            continue
        }

        $categoryExclusion = [System.Text.RegularExpressions.Regex]::Match(
            $token,
            "^Category\s*!=\s*(?<value>[A-Za-z_][A-Za-z0-9_.-]*)$",
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($categoryExclusion.Success) {
            $result.Add("--filter-not-trait")
            $result.Add("Category=$($categoryExclusion.Groups["value"].Value)")
            continue
        }

        throw "MTP test filter '$Filter' contains unsupported token '$token'."
    }

    return $result.ToArray()
}

function Get-MtpTestArguments {
    param([string[]]$Values)

    $projectPath = Get-TestProjectPath -Values $Values
    $result = [System.Collections.Generic.List[string]]::new()
    for ($i = 1; $i -lt $Values.Count; $i++) {
        $value = $Values[$i]
        if ($value.Equals($projectPath, [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("--no-build", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("--no-restore", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("--nologo", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("--", [System.StringComparison]::Ordinal) -or
            $value.StartsWith("-p:", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.StartsWith("/p:", [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        if ($value.Equals("--configuration", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("-c", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("--framework", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("-f", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("--verbosity", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("-v", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("--logger", [System.StringComparison]::OrdinalIgnoreCase)) {
            $i++
            continue
        }

        if ($value.Equals("--filter", [System.StringComparison]::OrdinalIgnoreCase)) {
            if ($i + 1 -ge $Values.Count) {
                throw "--filter requires a value."
            }

            foreach ($filterArgument in (ConvertTo-MtpFilterArguments -Filter $Values[++$i])) {
                $result.Add($filterArgument)
            }
            continue
        }

        if ($value.StartsWith("--filter=", [System.StringComparison]::OrdinalIgnoreCase)) {
            foreach ($filterArgument in (ConvertTo-MtpFilterArguments -Filter $value.Substring("--filter=".Length))) {
                $result.Add($filterArgument)
            }
            continue
        }

        if ($value.StartsWith("--configuration=", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.StartsWith("--framework=", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.StartsWith("--verbosity=", [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $result.Add($value)
    }

    if (-not $result.Contains("--no-ansi")) {
        $result.Add("--no-ansi")
    }
    if (-not $result.Contains("--progress")) {
        $result.Add("--progress")
        $result.Add("off")
    }

    return $result.ToArray()
}

function Get-ReuseBuildArguments {
    param([string[]]$Values)

    $projectPath = Get-TestProjectPath -Values $Values
    $result = [System.Collections.Generic.List[string]]::new()
    $result.Add("build")
    $result.Add($projectPath)
    for ($i = 0; $i -lt $Values.Count; $i++) {
        $value = $Values[$i]
        if ($value.Equals("--configuration", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("-c", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("--framework", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("-f", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("--verbosity", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("-v", [System.StringComparison]::OrdinalIgnoreCase)) {
            if ($i + 1 -lt $Values.Count) {
                $result.Add($value)
                $result.Add($Values[++$i])
            }
            continue
        }

        if ($value.StartsWith("--configuration=", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.StartsWith("--framework=", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.StartsWith("--verbosity=", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.StartsWith("-p:", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.StartsWith("/p:", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("--no-restore", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("--nologo", [System.StringComparison]::OrdinalIgnoreCase)) {
            $result.Add($value)
        }
    }

    return $result.ToArray()
}

function ConvertTo-CommandText {
    param([string[]]$Values)

    return ($Values | ForEach-Object {
        if ($_ -match '[\s'']') {
            "'$($_.Replace("'", "''"))'"
        }
        else {
            $_
        }
    }) -join " "
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
$exitCode = 1
try {
    $lockDirectory = Split-Path -Parent $executionLockPath
    New-Item -ItemType Directory -Force -Path $lockDirectory | Out-Null
    $deadline = [DateTime]::UtcNow.AddMinutes(5)
    while (-not $lockHeld) {
        $lockStream = [System.IO.File]::Open($executionLockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
        try {
            $lockStream.Lock(0, 1)
            $lockHeld = $true
            if (-not $ReuseArtifacts) {
                Initialize-ArtifactsDirectory -Path $artifactsPath -OwnerToken $ownerToken -ForceClean $staleLockCleared
            }
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

    if ($ReuseArtifacts) {
        $reuse = Get-ReusableTestArtifacts -ArtifactsPath $artifactsPath -OwnerToken $ownerToken -DotnetArguments $DotnetArguments
        if (-not $reuse.Success) {
            $buildArguments = Get-ReuseBuildArguments -Values $DotnetArguments
            $goalArgument = if ([string]::IsNullOrWhiteSpace($GoalPrefix)) {
                ""
            }
            else {
                " -GoalPrefix $(ConvertTo-CommandText -Values @($GoalPrefix))"
            }
            $buildCommand = ".\scripts\Invoke-IsolatedDotnet.ps1$goalArgument $(ConvertTo-CommandText -Values $buildArguments)"
            [Console]::Error.WriteLine(
                "Artifact reuse precondition failed: $($reuse.Reason) Probed artifact path: '$($reuse.ExpectedArtifactPath)'. " +
                "Expected owner token: '$ownerToken'; found owner token: '$($reuse.FoundOwnerToken)'. " +
                "Produce the artifacts with: $buildCommand")
            $exitCode = 86
        }
        else {
            Write-Host "Reusing test assembly '$($reuse.AssemblyPath)' and MTP executable '$($reuse.ExecutablePath)' with dependency directory '$($reuse.DependencyDirectory)'."
        }
    }

    if ($exitCode -ne 86) {
        $appDllBeforeDotnet = Get-AppDllSnapshot
        if ($ReuseArtifacts) {
            $mtpArguments = Get-MtpTestArguments -Values $DotnetArguments
            & $reuse.ExecutablePath @mtpArguments
        }
        else {
            & dotnet @DotnetArguments @isolatedArguments
        }
        $exitCode = $LASTEXITCODE
        if ($exitCode -eq 0 -and (Test-AppDllChangedSinceSnapshot -Snapshot $appDllBeforeDotnet)) {
            Update-AppDllGitHeadMarker
        }
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
