<#
.SYNOPSIS
    Runs dotnet with isolated build artifacts, including reusable per-goal no-build timing passes.

.DESCRIPTION
Use the same -GoalPrefix for both timing passes. For Microsoft.Testing.Platform (MTP) test
    projects, the first measurement builds into the goal's reusable artifact root and then runs
the built test executable with -ReuseArtifacts. The second measurement repeats only the
    -ReuseArtifacts invocation. Reuse verifies the goal owner, test assembly, and MTP executable
    before running xUnit directly without invoking MSBuild or the unsupported VSTest target.
    Builds share a two-lock machine-wide pool; test results never use the lock path.

Set MCG_DOTNET_FORCE_CLEAN_STALE_LEASE_ARTIFACTS=1 to force stale-lease recovery to wipe
the selected slot's artifacts instead of preserving a cache that passes the integrity probe.
This is an operator recovery escape hatch; unset it after the forced-clean run.

.EXAMPLE
Measure-Command {
    .\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix 10f9e458 build tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal
    .\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix 10f9e458 -ReuseArtifacts test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --verbosity minimal
}

    Measures total build plus xUnit time and leaves the output in the goal's reusable artifact root.

.EXAMPLE
Measure-Command { .\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix 10f9e458 -ReuseArtifacts test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --no-build --verbosity minimal }

Reuses the first measurement's output and measures only direct MTP/xUnit execution time.

.EXAMPLE
.\scripts\Invoke-IsolatedDotnet.ps1 -FocusedTest -GoalPrefix 10f9e458 -TestFilter FullyQualifiedName~GoalWorktreeTests -DeclaredMutation scripts/Invoke-IsolatedDotnet.ps1 test tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj

Runs one strictly focused Developer test request under a build-slot lease and emits a JSON receipt.
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
$FocusedTest = $false
$FocusedTestFilter = $null
$FocusedReceiptPath = $null
$FocusedBudgetSeconds = 300
$FocusedLeaseWaitSeconds = 30
$DeclaredMutations = [System.Collections.Generic.List[string]]::new()
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

    if ($Arguments[$i].Equals("-FocusedTest", [System.StringComparison]::OrdinalIgnoreCase)) {
        $FocusedTest = $true
        continue
    }

    if ($Arguments[$i].Equals("-TestFilter", [System.StringComparison]::OrdinalIgnoreCase) -or
        $Arguments[$i].Equals("-Filter", [System.StringComparison]::OrdinalIgnoreCase)) {
        if ($i + 1 -ge $Arguments.Count) {
            $FocusedTestFilter = $null
            continue
        }

        $FocusedTestFilter = $Arguments[++$i]
        continue
    }

    if ($Arguments[$i].Equals("-ReceiptPath", [System.StringComparison]::OrdinalIgnoreCase)) {
        if ($i + 1 -ge $Arguments.Count) {
            $FocusedReceiptPath = $null
            continue
        }

        $FocusedReceiptPath = $Arguments[++$i]
        continue
    }

    if ($Arguments[$i].Equals("-BudgetSeconds", [System.StringComparison]::OrdinalIgnoreCase)) {
        if ($i + 1 -ge $Arguments.Count -or -not [int]::TryParse($Arguments[$i + 1], [ref]$FocusedBudgetSeconds)) {
            $FocusedBudgetSeconds = 0
            continue
        }

        $i++
        continue
    }

    if ($Arguments[$i].Equals("-LeaseWaitSeconds", [System.StringComparison]::OrdinalIgnoreCase)) {
        if ($i + 1 -ge $Arguments.Count -or -not [int]::TryParse($Arguments[$i + 1], [ref]$FocusedLeaseWaitSeconds)) {
            $FocusedLeaseWaitSeconds = 0
            continue
        }

        $i++
        continue
    }

    if ($Arguments[$i].Equals("-DeclaredMutation", [System.StringComparison]::OrdinalIgnoreCase)) {
        if ($i + 1 -lt $Arguments.Count) {
            $DeclaredMutations.Add($Arguments[++$i])
        }
        continue
    }

    $remainingArguments.Add($Arguments[$i])
}
$DotnetArguments = $remainingArguments.ToArray()

if ($FocusedTest) {
    $validFocusedFilter = -not [string]::IsNullOrWhiteSpace($FocusedTestFilter) -and
        [System.Text.RegularExpressions.Regex]::IsMatch(
            $FocusedTestFilter,
            '\AFullyQualifiedName~[A-Za-z_][A-Za-z0-9_.]*(?:\|FullyQualifiedName~[A-Za-z_][A-Za-z0-9_.]*)*\z',
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $validFocusedFilter -or
        $FocusedBudgetSeconds -lt 1 -or $FocusedBudgetSeconds -gt 300 -or
        $FocusedLeaseWaitSeconds -lt 1 -or $FocusedLeaseWaitSeconds -gt $FocusedBudgetSeconds) {
        [ordered]@{
            version = 1
            filter = $FocusedTestFilter
            outcome = "INVALID"
            exitCode = 4
            reason = "invalid-focused-request"
        } | ConvertTo-Json -Compress | Write-Output
        exit 4
    }
}

if ($DotnetArguments.Count -eq 0) {
    throw "Usage: .\scripts\Invoke-IsolatedDotnet.ps1 [-GoalPrefix <goal-prefix>] test Mcg.AgentOrchestrator.sln --verbosity minimal"
}

$RepositoryRoot = (Get-Location).Path

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

    if (Test-CustodyMarkerIsLive -Marker $marker) {
        throw (
            "Artifact slot takeover refused because acceptance attempt '$([string]$marker.attemptId)' " +
            "has live custody of '$ArtifactsPath'. Wait for the acceptance attempt to reach a terminal state before retrying.")
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
    $takeoverRequired = $ForceClean -or ($hasEntries -and -not (Test-OwnerMarkerMatches -Path $ownerPath -OwnerToken $OwnerToken))
    if ($takeoverRequired) {
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

function Get-Sha256Text {
    param([AllowEmptyString()][string]$Value)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-FocusedWorktreeState {
    $head = (& git -C $script:RepositoryRoot rev-parse HEAD 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) {
        throw "Focused tests require a git worktree with a readable HEAD."
    }

    $topLevel = (& git -C $script:RepositoryRoot rev-parse --show-toplevel 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($topLevel)) {
        throw "Focused tests could not resolve the git worktree root."
    }

    $dirtyFiles = @(& git -C $script:RepositoryRoot diff --no-ext-diff --name-only HEAD -- 2>$null) |
        ForEach-Object { ([string]$_).Trim().Replace('\', '/') } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
    if ($LASTEXITCODE -ne 0) {
        throw "Focused tests could not inspect tracked-file state."
    }

    $dirtyPatch = @(& git -C $script:RepositoryRoot diff --no-ext-diff --binary HEAD -- 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Focused tests could not hash tracked-file state."
    }
    $digestInput = ($dirtyPatch -join "`n")
    return [pscustomobject]@{
        Root = [System.IO.Path]::GetFullPath($topLevel)
        Commit = $head
        DirtyFiles = @($dirtyFiles)
        DirtyDigest = Get-Sha256Text -Value $digestInput
        IsDirty = $dirtyFiles.Count -gt 0
    }
}

function ConvertTo-DeclaredMutationPath {
    param(
        [string]$Value,
        [string]$WorktreeRoot
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $fullPath = if ([System.IO.Path]::IsPathRooted($Value)) {
        [System.IO.Path]::GetFullPath($Value)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $WorktreeRoot $Value))
    }
    $rootPrefix = $WorktreeRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Declared mutation '$Value' is outside the current worktree."
    }

    return [System.IO.Path]::GetRelativePath($WorktreeRoot, $fullPath).Replace('\', '/')
}

function Get-FocusedRequest {
    param([string[]]$Values)

    if ($ReuseArtifacts) {
        throw "-ReuseArtifacts cannot be combined with -FocusedTest; focused mode owns freshness checks."
    }
    if ($Values.Count -lt 2 -or
        -not $Values[0].Equals("test", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Focused mode requires: test <explicit test project>."
    }

    $project = $Values[1]
    if ([System.IO.Path]::GetExtension($project) -notin @('.csproj', '.fsproj', '.vbproj')) {
        throw "Focused mode requires an explicit .csproj, .fsproj, or .vbproj test project."
    }

    $configuration = "Debug"
    $framework = $null
    $verbosity = "minimal"
    $noRestore = $false
    for ($i = 2; $i -lt $Values.Count; $i++) {
        $value = $Values[$i]
        if ($value.Equals("--configuration", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("-c", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("--framework", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("-f", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("--verbosity", [System.StringComparison]::OrdinalIgnoreCase) -or
            $value.Equals("-v", [System.StringComparison]::OrdinalIgnoreCase)) {
            if ($i + 1 -ge $Values.Count -or $Values[$i + 1].StartsWith('-')) {
                throw "Focused mode option '$value' requires a value."
            }
            $optionValue = $Values[++$i]
            if ($value.Equals("--configuration", [System.StringComparison]::OrdinalIgnoreCase) -or $value.Equals("-c", [System.StringComparison]::OrdinalIgnoreCase)) {
                $configuration = $optionValue
            }
            elseif ($value.Equals("--framework", [System.StringComparison]::OrdinalIgnoreCase) -or $value.Equals("-f", [System.StringComparison]::OrdinalIgnoreCase)) {
                $framework = $optionValue
            }
            else {
                $verbosity = $optionValue
            }
            continue
        }
        if ($value.Equals("--no-restore", [System.StringComparison]::OrdinalIgnoreCase)) {
            $noRestore = $true
            continue
        }
        if ($value.Equals("--nologo", [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        throw "Focused mode rejects unsupported argument '$value'."
    }

    $projectPath = if ([System.IO.Path]::IsPathRooted($project)) { $project } else { Join-Path $script:RepositoryRoot $project }
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Focused test project was not found: $project"
    }

    $testArguments = [System.Collections.Generic.List[string]]::new()
    $testArguments.Add("test")
    $testArguments.Add($project)
    $testArguments.Add("--no-build")
    $testArguments.Add("--filter")
    $testArguments.Add($FocusedTestFilter)
    $testArguments.Add("--configuration")
    $testArguments.Add($configuration)
    if (-not [string]::IsNullOrWhiteSpace($framework)) {
        $testArguments.Add("--framework")
        $testArguments.Add($framework)
    }

    $buildArguments = [System.Collections.Generic.List[string]]::new()
    $buildArguments.Add("build")
    $buildArguments.Add($project)
    $buildArguments.Add("--configuration")
    $buildArguments.Add($configuration)
    $buildArguments.Add("--verbosity")
    $buildArguments.Add($verbosity)
    $buildArguments.Add("--nologo")
    if (-not [string]::IsNullOrWhiteSpace($framework)) {
        $buildArguments.Add("--framework")
        $buildArguments.Add($framework)
    }
    if ($noRestore) {
        $buildArguments.Add("--no-restore")
    }

    return [pscustomobject]@{
        Project = $project
        ProjectPath = [System.IO.Path]::GetFullPath($projectPath)
        Configuration = $configuration
        Framework = $framework
        TestArguments = $testArguments.ToArray()
        BuildArguments = $buildArguments.ToArray()
    }
}

function Enter-FocusedBuildSlot {
    param(
        [string]$IsolatedRoot,
        [int]$SlotCount,
        [int]$PreferredSlot,
        [DateTime]$Deadline
    )

    $lockDirectory = Join-Path $IsolatedRoot "build-slots"
    New-Item -ItemType Directory -Force -Path $lockDirectory | Out-Null
    while ([DateTime]::UtcNow -lt $Deadline) {
        for ($offset = 0; $offset -lt $SlotCount; $offset++) {
            $slot = ($PreferredSlot + $offset) % $SlotCount
            $path = Join-Path $lockDirectory "build-$slot.lock"
            $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
            try {
                $stream.Lock(0, 1)
                return [pscustomobject]@{
                    Slot = $slot
                    Id = "build-$slot"
                    Path = $path
                    Stream = $stream
                }
            }
            catch [System.IO.IOException] {
                $stream.Dispose()
            }
        }

        Start-Sleep -Milliseconds 50
    }

    return $null
}

function Invoke-FocusedChildProcess {
    param(
        [string]$FileName,
        [string[]]$ProcessArguments,
        [int]$BudgetMilliseconds
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $script:RepositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ProcessArguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $process.Start()) {
            throw "Could not start '$FileName'."
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $completed = $process.WaitForExit([Math]::Max(1, $BudgetMilliseconds))
        if (-not $completed) {
            try {
                $process.Kill($true)
            }
            catch {
            }
            $process.WaitForExit()
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        return [pscustomobject]@{
            TimedOut = -not $completed
            ExitCode = if ($completed) { $process.ExitCode } else { $null }
            ElapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
            Stdout = $stdout
            Stderr = $stderr
        }
    }
    finally {
        $stopwatch.Stop()
        $process.Dispose()
    }
}

function Write-FocusedLog {
    param(
        [string]$Path,
        [string]$Stdout,
        [string]$Stderr
    )

    $maximumCharacters = 262144
    $text = "STDOUT`r`n$Stdout`r`nSTDERR`r`n$Stderr"
    if ($text.Length -gt $maximumCharacters) {
        $text = $text.Substring(0, $maximumCharacters) + "`r`n[truncated]"
    }
    [System.IO.File]::WriteAllText($Path, $text)
}

function Get-FocusedBuildFingerprint {
    param(
        [object]$WorktreeState,
        [object]$Request
    )

    return Get-Sha256Text -Value ($WorktreeState.Commit + "`n" + $WorktreeState.DirtyDigest + "`n" + $Request.ProjectPath + "`n" + $Request.Configuration + "`n" + $Request.Framework)
}

function Test-FocusedBuildIsCurrent {
    param(
        [string]$ArtifactsPath,
        [object]$Reuse,
        [string]$Fingerprint
    )

    if (-not $Reuse.Success) {
        return $false
    }
    $statePath = Join-Path $ArtifactsPath ".mcg-focused-build-state.json"
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        try {
            $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
            if ([string]::Equals([string]$state.fingerprint, $Fingerprint, [System.StringComparison]::Ordinal)) {
                return $true
            }
        }
        catch {
        }
    }

    $assembly = Get-Item -LiteralPath $Reuse.AssemblyPath
    $trackedInputs = @(& git -C $script:RepositoryRoot ls-files -- '*.cs' '*.fs' '*.vb' '*.csproj' '*.fsproj' '*.vbproj' '*.props' '*.targets' 'global.json' 2>$null)
    if ($LASTEXITCODE -ne 0 -or $trackedInputs.Count -eq 0) {
        return $false
    }
    foreach ($relativePath in $trackedInputs) {
        $inputPath = Join-Path $script:RepositoryRoot ([string]$relativePath)
        if ((Test-Path -LiteralPath $inputPath -PathType Leaf) -and
            (Get-Item -LiteralPath $inputPath).LastWriteTimeUtc -gt $assembly.LastWriteTimeUtc) {
            return $false
        }
    }
    return $true
}

function Write-FocusedReceipt {
    param(
        [System.Collections.IDictionary]$Receipt,
        [string]$Path
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temporaryPath = "$Path.$PID.tmp"
    $json = $Receipt | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($temporaryPath, $json)
    Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    [Console]::Out.WriteLine(($Receipt | ConvertTo-Json -Depth 8 -Compress))
}

function Read-FocusedTrx {
    param([string]$Path)

    [xml]$trx = Get-Content -LiteralPath $Path -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($null -eq $counters) {
        throw "TRX has no ResultSummary/Counters."
    }
    $failures = @($trx.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'Failed')
    $maximumFailures = 8
    $failureSummaries = @($failures | Select-Object -First $maximumFailures | ForEach-Object {
        $messageLines = @(([string]$_.Output.ErrorInfo.Message -split "`r?`n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $message = if ($messageLines.Count -eq 0) { $null } else { $messageLines[0].Trim() }
        if ($null -ne $message -and $message.Length -gt 512) {
            $message = $message.Substring(0, 512)
        }
        [ordered]@{ name = [string]$_.testName; assertion = $message }
    })
    return [pscustomobject]@{
        Total = [int]$counters.total
        Passed = [int]$counters.passed
        Failed = [int]$counters.failed
        Skipped = [int]$counters.notExecuted
        Failures = $failureSummaries
        OmittedFailures = [Math]::Max(0, $failures.Count - $maximumFailures)
    }
}

function Invoke-FocusedTestMode {
    $started = [DateTime]::UtcNow
    $slotLease = $null
    $isolatedRoot = Get-IsolatedRootBase
    $safeGoalPrefix = if ([string]::IsNullOrWhiteSpace($GoalPrefix)) { "focused-$PID" } else { ConvertTo-SafePathSegment -Value $GoalPrefix }
    $runRoot = Join-Path $isolatedRoot "goals\$safeGoalPrefix"
    $defaultReceiptDirectory = Join-Path $runRoot "focused-tests"
    $receiptPath = Join-Path $defaultReceiptDirectory ("focused-{0}-{1}.receipt.json" -f ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff')), ([Guid]::NewGuid().ToString('N').Substring(0, 8)))
    $receipt = [ordered]@{
        version = 1
        filter = $FocusedTestFilter
        outcome = "BLOCKED"
        exitCode = 5
        reason = "unhandled-exception"
        total = $null
        passed = $null
        failed = $null
        skipped = $null
        elapsedSeconds = 0
        budgetSeconds = $FocusedBudgetSeconds
        slotId = $null
        leaseReleased = $false
        worktreeRoot = $null
        commit = $null
        trackedDirtyStateDigest = $null
        trackedDirtyFiles = @()
        declaredMutations = @($DeclaredMutations)
        failingTests = @()
        omittedFailingTests = 0
        trxPath = $null
        outputLogPath = $null
        receiptPath = $receiptPath
        project = $null
        testArguments = @()
        buildPerformed = $false
        buildReused = $false
    }
    try {
        $request = Get-FocusedRequest -Values $DotnetArguments
        $receipt.project = $request.Project
        $receipt.testArguments = @($request.TestArguments)
        $worktree = Get-FocusedWorktreeState
        $receipt.worktreeRoot = $worktree.Root
        $receipt.commit = $worktree.Commit
        $receipt.trackedDirtyStateDigest = $worktree.DirtyDigest
        $receipt.trackedDirtyFiles = @($worktree.DirtyFiles)

        $declared = @($DeclaredMutations | ForEach-Object { ConvertTo-DeclaredMutationPath -Value $_ -WorktreeRoot $worktree.Root })
        $receipt.declaredMutations = $declared
        $undeclared = @($worktree.DirtyFiles | Where-Object { $_ -notin $declared })
        if ($undeclared.Count -gt 0) {
            $receipt.reason = "undeclared-dirty"
            $receipt.undeclaredDirtyFiles = $undeclared
            return $receipt
        }

        $preferredSlotName = Get-BuildSlotName -Value $safeGoalPrefix -SlotCount (Get-BuildConcurrencySlotCount)
        $preferredSlot = [int]$preferredSlotName.Substring("build-".Length)
        $leaseDeadline = $started.AddSeconds([Math]::Min($FocusedLeaseWaitSeconds, $FocusedBudgetSeconds))
        $slotLease = Enter-FocusedBuildSlot -IsolatedRoot $isolatedRoot -SlotCount (Get-BuildConcurrencySlotCount) -PreferredSlot $preferredSlot -Deadline $leaseDeadline

        if (-not [string]::IsNullOrWhiteSpace($FocusedReceiptPath)) {
            $candidateReceiptPath = [System.IO.Path]::GetFullPath($FocusedReceiptPath)
            $repositoryPrefix = $worktree.Root.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
            if ($candidateReceiptPath.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Focused receipt path must be outside the worktree."
            }
            $receiptPath = $candidateReceiptPath
        }
        $receipt.receiptPath = $receiptPath

        if ($null -eq $slotLease) {
            $receipt.exitCode = 3
            $receipt.reason = "no-slot"
            return $receipt
        }
        $receipt.slotId = $slotLease.Id

        $ownerToken = "goal-$safeGoalPrefix"
        $artifactsPath = if ($slotLease.Slot -eq $preferredSlot) {
            Join-Path $runRoot "artifacts"
        }
        else {
            Join-Path $runRoot "focused-artifacts\$($slotLease.Id)"
        }
        Initialize-ArtifactsDirectory -Path $artifactsPath -OwnerToken $ownerToken -ForceClean $false

        $processTempPath = Join-Path (Get-HostTempBase) "pt\focused-$safeGoalPrefix\$PID"
        New-Item -ItemType Directory -Force -Path $processTempPath | Out-Null
        $env:MCG_ORCHESTRATOR_REPOSITORY_ROOT = $script:RepositoryRoot
        $env:TEMP = $processTempPath
        $env:TMP = $processTempPath
        Remove-Item Env:MCG_WORKER_SANDBOX -ErrorAction SilentlyContinue
        Remove-Item Env:MCG_WORKER_ACCOUNT -ErrorAction SilentlyContinue
        Remove-Item Env:MCG_WORKER_CREDENTIAL_TARGET -ErrorAction SilentlyContinue
        Remove-Item Env:MCG_ORCHESTRATOR_WORKER_DISPATCH -ErrorAction SilentlyContinue

        $outputDirectory = Join-Path $processTempPath "results"
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
        $fingerprint = Get-FocusedBuildFingerprint -WorktreeState $worktree -Request $request
        $reuse = Get-ReusableTestArtifacts -ArtifactsPath $artifactsPath -OwnerToken $ownerToken -DotnetArguments $request.TestArguments
        if (-not (Test-FocusedBuildIsCurrent -ArtifactsPath $artifactsPath -Reuse $reuse -Fingerprint $fingerprint)) {
            $receipt.buildPerformed = $true
            $buildArguments = @($request.BuildArguments) + @(
                "--artifacts-path", $artifactsPath,
                "-maxcpucount:$(Get-BuildMaxCpuCount)",
                "-p:BuildInParallel=false"
            )
            $remainingMilliseconds = [int][Math]::Floor(($started.AddSeconds($FocusedBudgetSeconds) - [DateTime]::UtcNow).TotalMilliseconds)
            if ($remainingMilliseconds -le 0) {
                $receipt.exitCode = 2
                $receipt.reason = "budget-exceeded"
                return $receipt
            }
            $build = Invoke-FocusedChildProcess -FileName "dotnet" -ProcessArguments $buildArguments -BudgetMilliseconds $remainingMilliseconds
            $buildLogPath = Join-Path $outputDirectory "build.log"
            Write-FocusedLog -Path $buildLogPath -Stdout $build.Stdout -Stderr $build.Stderr
            $receipt.outputLogPath = $buildLogPath
            if ($build.TimedOut) {
                $receipt.exitCode = 2
                $receipt.reason = "budget-exceeded"
                return $receipt
            }
            if ($build.ExitCode -ne 0) {
                $receipt.reason = "build-failed"
                $receipt.buildExitCode = $build.ExitCode
                return $receipt
            }
            [ordered]@{
                version = 1
                fingerprint = $fingerprint
                commit = $worktree.Commit
                trackedDirtyStateDigest = $worktree.DirtyDigest
                project = $request.Project
                configuration = $request.Configuration
                framework = $request.Framework
                builtAt = [DateTime]::UtcNow.ToString('o')
            } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactsPath ".mcg-focused-build-state.json")
            $reuse = Get-ReusableTestArtifacts -ArtifactsPath $artifactsPath -OwnerToken $ownerToken -DotnetArguments $request.TestArguments
            if (-not $reuse.Success) {
                $receipt.reason = "build-artifact-missing"
                return $receipt
            }
        }
        else {
            $receipt.buildReused = $true
        }

        $trxName = "focused-$([Guid]::NewGuid().ToString('N')).trx"
        $trxPath = Join-Path $outputDirectory $trxName
        $testArguments = @(
            "--no-ansi", "--progress", "off",
            "--results-directory", $outputDirectory,
            "--report-trx", "--report-trx-filename", $trxName,
            "--long-running", "120"
        ) + @(ConvertTo-MtpFilterArguments -Filter $FocusedTestFilter)
        $remainingMilliseconds = [int][Math]::Floor(($started.AddSeconds($FocusedBudgetSeconds) - [DateTime]::UtcNow).TotalMilliseconds)
        if ($remainingMilliseconds -le 0) {
            $receipt.exitCode = 2
            $receipt.reason = "budget-exceeded"
            return $receipt
        }
        $testRun = Invoke-FocusedChildProcess -FileName $reuse.ExecutablePath -ProcessArguments $testArguments -BudgetMilliseconds $remainingMilliseconds
        $testLogPath = Join-Path $outputDirectory "test.log"
        Write-FocusedLog -Path $testLogPath -Stdout $testRun.Stdout -Stderr $testRun.Stderr
        $receipt.outputLogPath = $testLogPath
        $receipt.testProcessExitCode = $testRun.ExitCode
        if ($testRun.TimedOut) {
            $receipt.exitCode = 2
            $receipt.reason = "budget-exceeded"
            return $receipt
        }
        if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
            $receipt.reason = "missing-trx"
            return $receipt
        }

        $summary = Read-FocusedTrx -Path $trxPath
        $receipt.trxPath = $trxPath
        $receipt.total = $summary.Total
        $receipt.passed = $summary.Passed
        $receipt.failed = $summary.Failed
        $receipt.skipped = $summary.Skipped
        $receipt.failingTests = @($summary.Failures)
        $receipt.omittedFailingTests = $summary.OmittedFailures
        if ($summary.Total -le 0) {
            $receipt.reason = "no-tests"
            return $receipt
        }
        if ($summary.Failed -gt 0) {
            $receipt.outcome = "FAIL"
            $receipt.exitCode = 1
            $receipt.reason = "test-failures"
            return $receipt
        }
        if ($testRun.ExitCode -ne 0) {
            $receipt.reason = "runner-failed"
            return $receipt
        }

        $receipt.outcome = "PASS"
        $receipt.exitCode = 0
        $receipt.reason = $null
        return $receipt
    }
    catch {
        $receipt.error = $_.Exception.Message
        return $receipt
    }
    finally {
        $receipt.elapsedSeconds = [Math]::Round(([DateTime]::UtcNow - $started).TotalSeconds, 3)
        try {
            $after = Get-FocusedWorktreeState
            $receipt.worktreeStateAfter = [ordered]@{
                commit = $after.Commit
                trackedDirtyStateDigest = $after.DirtyDigest
                trackedDirtyFiles = @($after.DirtyFiles)
                unchanged = $null -ne $receipt.trackedDirtyStateDigest -and $receipt.trackedDirtyStateDigest -eq $after.DirtyDigest -and $receipt.commit -eq $after.Commit
            }
            if ($null -ne $receipt.trackedDirtyStateDigest -and -not $receipt.worktreeStateAfter.unchanged) {
                $receipt.outcome = "BLOCKED"
                $receipt.exitCode = 5
                $receipt.reason = "worktree-state-changed"
            }
        }
        catch {
            $receipt.worktreeStateAfter = [ordered]@{ error = $_.Exception.Message; unchanged = $false }
        }
        if ($null -ne $slotLease) {
            try {
                $slotLease.Stream.Unlock(0, 1)
                $receipt.leaseReleased = $true
            }
            finally {
                $slotLease.Stream.Dispose()
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($receiptPath)) {
            try {
                Write-FocusedReceipt -Receipt $receipt -Path $receiptPath
            }
            catch {
                [Console]::Error.WriteLine("Focused receipt write failed: $($_.Exception.Message)")
                [Console]::Out.WriteLine(($receipt | ConvertTo-Json -Depth 8 -Compress))
            }
        }
        else {
            [Console]::Out.WriteLine(($receipt | ConvertTo-Json -Depth 8 -Compress))
        }
    }
}

if ($FocusedTest) {
    $focusedResult = Invoke-FocusedTestMode
    exit ([int]$focusedResult.exitCode)
}

if ($env:MCG_ORCHESTRATOR_WORKER_DISPATCH -eq "1" -or
    $env:MCG_ORCHESTRATOR_WORKER_DISPATCH -eq "true") {
    throw "Worker-side .NET self-verification is disabled. Use Invoke-WorkerBuildCheck for build-only verification; the orchestrator acceptance gate owns test execution."
}

$safeAttemptName = ConvertTo-SafePathSegment -Value $AttemptName
$hostTempBase = Get-HostTempBase
$isolatedRoot = Get-IsolatedRootBase
$buildConcurrencySlotCount = Get-BuildConcurrencySlotCount
if ([string]::IsNullOrWhiteSpace($GoalPrefix)) {
    $runId = "manual-$PID-$([Guid]::NewGuid().ToString('N'))"
    $runRoot = Join-Path $isolatedRoot "runs\$runId"
    $artifactsPath = Join-Path $runRoot "artifacts"
    $leaseId = "run-$runId"
    $ownerToken = $runId
    $executionLockPath = Join-Path $isolatedRoot "build-slots\build-$($PID % $buildConcurrencySlotCount).lock"
    $staleLockCleared = $false
}
else {
    $safeGoalPrefix = ConvertTo-SafePathSegment -Value $GoalPrefix
    $buildSlotName = Get-BuildSlotName -Value $safeGoalPrefix -SlotCount $buildConcurrencySlotCount
    $leaseId = "goal-$safeGoalPrefix"
    $runRoot = Join-Path $isolatedRoot "goals\$safeGoalPrefix"
    $leaseRoot = Join-Path $runRoot "lease"
    $artifactsPath = Join-Path $runRoot "artifacts"
    $ownerToken = $leaseId
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
            & dotnet build-server shutdown *> $null
            $lockStream.Unlock(0, 1)
            $lockStream.Dispose()
            $lockStream = $null
            $lockHeld = $false
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
        & dotnet build-server shutdown *> $null
        $lockStream.Unlock(0, 1)
        $lockStream.Dispose()
    }

    Remove-Item -LiteralPath $processTempPath -Force -Recurse -ErrorAction SilentlyContinue
    if ([string]::IsNullOrWhiteSpace($GoalPrefix) -and $exitCode -eq 0) {
        Remove-Item -LiteralPath $runRoot -Force -Recurse -ErrorAction SilentlyContinue
    }
}

exit $exitCode
