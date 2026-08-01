Set-StrictMode -Version Latest

$script:ExitCodes = [pscustomobject]@{
    Manifest = 20
    InvalidPartition = 21
    ResultsDirectory = 22
    Build = 23
    MissingAppHost = 24
    InvalidTarget = 25
    Runner = 26
    ZeroTests = 27
}

function Test-MtpWindows {
    return [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
}

function Get-MtpTestExitCodes {
    return $script:ExitCodes
}

function Read-MtpTestManifest {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Acceptance manifest is missing: $Path"
    }

    try {
        $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Acceptance manifest is unreadable: $Path. $($_.Exception.Message)"
    }

    if ($null -eq $manifest.engine -or
        $null -eq $manifest.engine.infrastructureTestLanes -or
        $null -eq $manifest.engine.mtpInvocations) {
        throw "Acceptance manifest is missing engine.infrastructureTestLanes or engine.mtpInvocations: $Path"
    }

    return $manifest
}

function Get-MtpLocalPartitions {
    param([Parameter(Mandatory = $true)]$Manifest)

    if ($null -eq $Manifest.engine.localTestPartitions) {
        throw 'Acceptance manifest is missing engine.localTestPartitions.'
    }

    $lanes = @($Manifest.engine.infrastructureTestLanes)
    $partitions = foreach ($partition in @($Manifest.engine.localTestPartitions)) {
        if ([string]::IsNullOrWhiteSpace([string]$partition.name) -or $null -eq $partition.laneNames) {
            throw 'Every local test partition requires a non-empty name and laneNames.'
        }

        $resolvedLanes = foreach ($laneName in @($partition.laneNames)) {
            $matches = @($lanes | Where-Object {
                ([string]$_.name).Equals([string]$laneName, [System.StringComparison]::OrdinalIgnoreCase)
            })
            if ($matches.Count -ne 1) {
                throw "Local test partition '$($partition.name)' references missing or duplicate lane '$laneName'."
            }
            $matches[0]
        }

        [pscustomobject]@{
            Name = [string]$partition.name
            Description = [string]$partition.description
            Lanes = @($resolvedLanes)
            Filters = @($resolvedLanes | ForEach-Object { [string]$_.filter })
        }
    }

    $duplicate = @($partitions | Group-Object -Property Name | Where-Object Count -gt 1)
    if ($duplicate.Count -gt 0) {
        throw "Acceptance manifest local test partition '$($duplicate[0].Name)' is duplicated."
    }

    return @($partitions)
}

function ConvertTo-MtpFilterArguments {
    param([Parameter(Mandatory = $true)][string]$Filter)

    $arguments = [System.Collections.Generic.List[string]]::new()
    foreach ($rawToken in [regex]::Split($Filter, '[&|]')) {
        $token = $rawToken.Trim().Trim('(', ')').Trim()
        if ($token.Length -eq 0) {
            continue
        }

        $fullyQualifiedName = [regex]::Match(
            $token,
            '^FullyQualifiedName\s*(?<op>!~|~)\s*(?<value>[A-Za-z_][A-Za-z0-9_.]*)$',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($fullyQualifiedName.Success) {
            if ($fullyQualifiedName.Groups['op'].Value -eq '!~') {
                $arguments.Add('--filter-not-class')
            }
            else {
                $arguments.Add('--filter-class')
            }
            $arguments.Add("*$($fullyQualifiedName.Groups['value'].Value)*")
            continue
        }

        $methodName = [regex]::Match(
            $token,
            '^(DisplayName|Name)\s*(?<op>!~|~)\s*(?<value>[^\s&|()]+)$',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($methodName.Success) {
            if ($methodName.Groups['op'].Value -eq '!~') {
                $arguments.Add('--filter-not-method')
            }
            else {
                $arguments.Add('--filter-method')
            }
            $arguments.Add("*$($methodName.Groups['value'].Value)*")
            continue
        }

        $categoryExclusion = [regex]::Match(
            $token,
            '^Category\s*!=\s*(?<value>[A-Za-z_][A-Za-z0-9_.-]*)$',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($categoryExclusion.Success) {
            $arguments.Add('--filter-not-trait')
            $arguments.Add("Category=$($categoryExclusion.Groups['value'].Value)")
            continue
        }

        throw "MTP test filter '$Filter' contains unsupported token '$token'. Use FullyQualifiedName~Class, DisplayName~Method, or Category!=Trait syntax."
    }

    return $arguments.ToArray()
}

function Get-MtpBoundedFileName {
    param(
        [Parameter(Mandatory = $true)][string]$Stem,
        [string]$Suffix = '.trx'
    )

    $sanitized = [regex]::Replace($Stem.Trim(), '[^A-Za-z0-9_.-]+', '-').Trim('-', '.')
    if ([string]::IsNullOrWhiteSpace($sanitized)) {
        $sanitized = 'tests'
    }

    $maximumLength = 200
    if ($sanitized.Length + $Suffix.Length -le $maximumLength) {
        return $sanitized + $Suffix
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($sanitized.Trim())
        $hashBytes = $sha.ComputeHash($bytes)
        $hash = ([System.BitConverter]::ToString($hashBytes) -replace '-', '').Substring(0, 16).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }

    $keep = $maximumLength - $Suffix.Length - $hash.Length - 1
    if ($keep -le 0) {
        return $hash + $Suffix
    }
    return $sanitized.Substring(0, $keep).TrimEnd('-', '.') + '-' + $hash + $Suffix
}

function Get-DefaultMtpResultsRoot {
    $localAppData = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        $localAppData = $env:LOCALAPPDATA
    }
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        throw 'LOCALAPPDATA is unavailable; specify -ResultsRoot under a Low-integrity-writable directory.'
    }
    return Join-Path $localAppData 'Temp\Low\mcg-tests'
}

function Initialize-MtpResultsDirectory {
    param(
        [string]$ResultsRoot,
        [Parameter(Mandatory = $true)][string]$RunLabel
    )

    if ([string]::IsNullOrWhiteSpace($ResultsRoot)) {
        $ResultsRoot = Get-DefaultMtpResultsRoot
    }

    $resolvedRoot = [System.IO.Path]::GetFullPath($ResultsRoot)
    if (Test-MtpWindows) {
        $canonicalLowRoot = [System.IO.Path]::GetFullPath((Get-DefaultMtpResultsRoot)).TrimEnd('\', '/')
        $lowPrefix = $canonicalLowRoot + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolvedRoot.Equals($canonicalLowRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
            -not $resolvedRoot.StartsWith($lowPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Results directory '$resolvedRoot' is not under the Low-integrity-writable root '$canonicalLowRoot'. Use -ResultsRoot beneath that root; an ordinary Medium-integrity directory is not writable by the MTP apphost."
        }
    }

    try {
        New-Item -ItemType Directory -Force -Path $resolvedRoot | Out-Null
        $probe = Join-Path $resolvedRoot ".write-probe-$PID-$([Guid]::NewGuid().ToString('N'))"
        [System.IO.File]::WriteAllText($probe, 'probe')
        Remove-Item -LiteralPath $probe -Force
    }
    catch {
        throw "Results directory '$resolvedRoot' is not writable. MTP requires a Low-integrity-writable results root. $($_.Exception.Message)"
    }

    $safeLabel = Get-MtpBoundedFileName -Stem $RunLabel -Suffix ''
    $runDirectory = Join-Path $resolvedRoot "$safeLabel-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    try {
        New-Item -ItemType Directory -Path $runDirectory -ErrorAction Stop | Out-Null
    }
    catch {
        throw "Could not create invocation results directory '$runDirectory'. MTP requires a Low-integrity-writable results root. $($_.Exception.Message)"
    }
    return $runDirectory
}

function Set-MtpHermeticEnvironment {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$WritableRoot
    )

    $nugetPackages = $env:NUGET_PACKAGES
    $userProfile = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)
    $allowedNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @('PATH', 'PATHEXT', 'SystemRoot', 'WINDIR', 'COMSPEC', 'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432', 'LOCALAPPDATA')) {
        [void]$allowedNames.Add($name)
    }
    foreach ($item in @(Get-ChildItem Env:)) {
        if (-not $allowedNames.Contains($item.Name) -and
            -not $item.Name.StartsWith('DOTNET_', [System.StringComparison]::OrdinalIgnoreCase) -and
            -not $item.Name.StartsWith('NUGET_', [System.StringComparison]::OrdinalIgnoreCase)) {
            [System.Environment]::SetEnvironmentVariable($item.Name, $null, [System.EnvironmentVariableTarget]::Process)
        }
    }

    $profileRoot = Join-Path $WritableRoot 'profile'
    $appData = Join-Path $profileRoot 'AppData\Roaming'
    New-Item -ItemType Directory -Force -Path $profileRoot, $appData | Out-Null
    if ([string]::IsNullOrWhiteSpace($nugetPackages)) {
        $packageProfile = if ([string]::IsNullOrWhiteSpace($userProfile)) { $profileRoot } else { $userProfile }
        $nugetPackages = Join-Path $packageProfile '.nuget\packages'
    }

    $env:HOME = $profileRoot
    $env:USERPROFILE = $profileRoot
    $env:DOTNET_CLI_HOME = $profileRoot
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_NOLOGO = '1'
    $env:NUGET_PACKAGES = $nugetPackages
    $env:APPDATA = $appData
    $env:TEMP = $WritableRoot
    $env:TMP = $WritableRoot
    $env:MCG_ORCHESTRATOR_REPOSITORY_ROOT = [System.IO.Path]::GetFullPath($RepositoryRoot)
}

function Get-MtpTargetProjects {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Target
    )

    $targetPath = if ([System.IO.Path]::IsPathRooted($Target)) {
        [System.IO.Path]::GetFullPath($Target)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot $Target))
    }
    if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
        throw "Test target does not exist: $targetPath"
    }

    $invocations = @($Manifest.engine.mtpInvocations)
    if ([System.IO.Path]::GetExtension($targetPath).Equals('.sln', [System.StringComparison]::OrdinalIgnoreCase)) {
        $solutionText = Get-Content -LiteralPath $targetPath -Raw
        $selected = @($invocations | Where-Object {
            $project = ([string]$_.project).Replace('/', '\')
            $solutionText.IndexOf($project, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        })
        if ($selected.Count -eq 0) {
            throw "Solution target contains no manifest-declared MTP test projects: $targetPath"
        }
        return $selected
    }

    $repositoryPrefix = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $targetPath.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Test project target must be inside the repository: $targetPath"
    }
    $relativeTarget = $targetPath.Substring($repositoryPrefix.Length).Replace('\', '/')
    $selected = @($invocations | Where-Object {
        ([string]$_.project).Replace('\', '/').TrimStart('/').Equals(
            $relativeTarget.TrimStart('/'),
            [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($selected.Count -ne 1) {
        throw "Acceptance manifest has no unique direct-MTP invocation for target '$relativeTarget'."
    }
    return $selected
}

function Invoke-MtpBuild {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$DotnetPath
    )

    $buildTarget = if ([System.IO.Path]::IsPathRooted($Target)) {
        [System.IO.Path]::GetFullPath($Target)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot $Target))
    }
    Write-Host "Building test target: $buildTarget ($Configuration)"
    try {
        & $DotnetPath build $buildTarget --configuration $Configuration --nologo --verbosity minimal 2>&1 |
            ForEach-Object { Write-Host $_ }
        $buildExit = $LASTEXITCODE
    }
    catch {
        Write-Host "BUILD FAILURE - could not start '$DotnetPath build': $($_.Exception.Message)"
        return $false
    }
    if ($buildExit -ne 0) {
        Write-Host "BUILD FAILURE - '$DotnetPath build' exited $buildExit. The MTP apphost was not launched."
        return $false
    }
    Write-Host 'Build succeeded.'
    return $true
}

function Resolve-MtpAppHostPath {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]$Invocation,
        [Parameter(Mandatory = $true)][string]$Configuration
    )

    $projectPath = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot ([string]$Invocation.project)))
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    $targetFramework = [string]$project.Project.PropertyGroup.TargetFramework
    if ([string]::IsNullOrWhiteSpace($targetFramework)) {
        $frameworks = [string]$project.Project.PropertyGroup.TargetFrameworks
        $targetFramework = ($frameworks -split ';')[0].Trim()
    }
    if ([string]::IsNullOrWhiteSpace($targetFramework)) {
        throw "Test project has no TargetFramework: $projectPath"
    }

    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
    $extension = if (Test-MtpWindows) { '.exe' } else { '' }
    return Join-Path (Split-Path -Parent $projectPath) "bin\$Configuration\$targetFramework\$projectName$extension"
}

function New-MtpRunnerArguments {
    param(
        [Parameter(Mandatory = $true)]$Invocation,
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string]$ResultsDirectory,
        [Parameter(Mandatory = $true)][string]$TrxFileName,
        [string]$Filter
    )

    $arguments = [System.Collections.Generic.List[string]]::new()
    foreach ($template in @($Invocation.arguments)) {
        $argument = ([string]$template).
            Replace('{executable}', $Executable).
            Replace('{resultsDirectory}', $ResultsDirectory).
            Replace('{trxFileName}', $TrxFileName)
        $arguments.Add($argument)
    }
    if ($arguments.Count -eq 0 -or -not $arguments[0].Equals($Executable, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Manifest MTP invocation for '$($Invocation.project)' must put {executable} first."
    }
    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        $arguments.AddRange([string[]](ConvertTo-MtpFilterArguments -Filter $Filter))
    }
    return $arguments.ToArray()
}

function Invoke-MtpAppHost {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$OutputLog
    )

    $captured = [System.Collections.Generic.List[string]]::new()
    try {
        & $Executable @($Arguments | Select-Object -Skip 1) 2>&1 | ForEach-Object {
            $line = $_.ToString()
            $captured.Add($line)
            Add-Content -LiteralPath $OutputLog -Value $line
            Write-Host $line
        }
        $runnerExit = $LASTEXITCODE
    }
    catch {
        $line = $_.Exception.Message
        $captured.Add($line)
        Add-Content -LiteralPath $OutputLog -Value $line
        Write-Host $line
        $runnerExit = $script:ExitCodes.Runner
    }

    return [pscustomobject]@{
        ExitCode = $runnerExit
        Output = @($captured)
    }
}

function Read-MtpTrxResult {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        [xml]$trx = Get-Content -LiteralPath $Path -Raw
        $counters = $trx.TestRun.ResultSummary.Counters
        if ($null -eq $counters) {
            throw 'ResultSummary/Counters is missing.'
        }
        return [pscustomobject]@{
            Total = [int]$counters.total
            Passed = [int]$counters.passed
            Failed = [int]$counters.failed
            Skipped = [int]$counters.notExecuted
            Document = $trx
        }
    }
    catch {
        throw "TRX '$Path' is unreadable: $($_.Exception.Message)"
    }
}

function Invoke-MtpTestRun {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$Target,
        [string[]]$Filters = @(''),
        [Parameter(Mandatory = $true)][string]$RunLabel,
        [ValidateRange(1, 25)][int]$Repeat = 1,
        [string]$Configuration = 'Debug',
        [switch]$NoBuild,
        [string]$ResultsRoot,
        [string]$RunnerPath,
        [string]$DotnetPath = 'dotnet'
    )

    try {
        $projects = @(Get-MtpTargetProjects -Manifest $Manifest -RepositoryRoot $RepositoryRoot -Target $Target)
    }
    catch {
        Write-Host "TARGET FAILURE - $($_.Exception.Message)"
        return [pscustomobject]@{ ExitCode = $script:ExitCodes.InvalidTarget; ResultsDirectory = $null }
    }
    try {
        $runDirectory = Initialize-MtpResultsDirectory -ResultsRoot $ResultsRoot -RunLabel $RunLabel
    }
    catch {
        Write-Host "RESULTS DIRECTORY FAILURE - $($_.Exception.Message)"
        return [pscustomobject]@{ ExitCode = $script:ExitCodes.ResultsDirectory; ResultsDirectory = $null }
    }

    Set-MtpHermeticEnvironment -RepositoryRoot $RepositoryRoot -WritableRoot $runDirectory
    Write-Host "Results directory: $runDirectory"
    if (-not $NoBuild) {
        if (-not (Invoke-MtpBuild -RepositoryRoot $RepositoryRoot -Target $Target -Configuration $Configuration -DotnetPath $DotnetPath)) {
            Write-Host "Retained diagnostic directory: $runDirectory"
            return [pscustomobject]@{ ExitCode = $script:ExitCodes.Build; ResultsDirectory = $runDirectory }
        }
    }

    $filterList = @($Filters)
    if ($filterList.Count -eq 0) {
        $filterList = @('')
    }
    $invocationIndex = 0
    foreach ($project in $projects) {
        $expectedAppHost = Resolve-MtpAppHostPath -RepositoryRoot $RepositoryRoot -Invocation $project -Configuration $Configuration
        $executable = if ([string]::IsNullOrWhiteSpace($RunnerPath)) { $expectedAppHost } else { [System.IO.Path]::GetFullPath($RunnerPath) }
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            $projectPath = Join-Path $RepositoryRoot ([string]$project.project)
            Write-Host "MISSING APPHOST - expected '$executable'. Build it with: $DotnetPath build `"$projectPath`" --configuration $Configuration"
            Write-Host "Retained diagnostic directory: $runDirectory"
            return [pscustomobject]@{ ExitCode = $script:ExitCodes.MissingAppHost; ResultsDirectory = $runDirectory }
        }

        foreach ($filter in $filterList) {
            for ($attempt = 1; $attempt -le $Repeat; $attempt++) {
                $invocationIndex++
                $projectName = [System.IO.Path]::GetFileNameWithoutExtension([string]$project.project)
                $stem = "$RunLabel-$projectName-$invocationIndex-r$attempt-$filter"
                $trxFileName = Get-MtpBoundedFileName -Stem $stem
                $trxPath = Join-Path $runDirectory $trxFileName
                $outputLog = Join-Path $runDirectory ([System.IO.Path]::ChangeExtension($trxFileName, '.runner.log'))
                try {
                    $arguments = New-MtpRunnerArguments -Invocation $project -Executable $executable -ResultsDirectory $runDirectory -TrxFileName $trxFileName -Filter $filter
                }
                catch {
                    Write-Host "RUNNER/TOOLING FAILURE - $($_.Exception.Message)"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return [pscustomobject]@{ ExitCode = $script:ExitCodes.Runner; ResultsDirectory = $runDirectory }
                }

                Write-Host "Running MTP apphost: $executable"
                if (-not [string]::IsNullOrWhiteSpace($filter)) {
                    Write-Host "Filter: $filter"
                }
                Write-Host "Runner output log: $outputLog"
                $run = Invoke-MtpAppHost -Executable $executable -Arguments $arguments -OutputLog $outputLog
                if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
                    Write-Host "RUNNER/TOOLING FAILURE - apphost exited $($run.ExitCode) without producing TRX '$trxPath'. This is not a compile failure. Captured stderr/stdout: $outputLog"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    $exitCode = if ($run.ExitCode -ne 0) { $run.ExitCode } else { $script:ExitCodes.Runner }
                    return [pscustomobject]@{ ExitCode = $exitCode; ResultsDirectory = $runDirectory }
                }

                Write-Host "TRX: $trxPath"
                try {
                    $summary = Read-MtpTrxResult -Path $trxPath
                }
                catch {
                    Write-Host "RUNNER/TOOLING FAILURE - $($_.Exception.Message) Apphost exit: $($run.ExitCode). Captured stderr/stdout: $outputLog"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return [pscustomobject]@{ ExitCode = $script:ExitCodes.Runner; ResultsDirectory = $runDirectory }
                }
                Write-Host ("{0}: total={1} passed={2} failed={3} skipped={4}" -f $trxFileName, $summary.Total, $summary.Passed, $summary.Failed, $summary.Skipped)
                if ($summary.Total -le 0) {
                    Write-Host "ZERO TESTS - filter matched no tests. Apphost exit: $($run.ExitCode). TRX: $trxPath"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return [pscustomobject]@{ ExitCode = $script:ExitCodes.ZeroTests; ResultsDirectory = $runDirectory }
                }
                if ($summary.Failed -gt 0) {
                    foreach ($failedResult in @($summary.Document.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'Failed')) {
                        Write-Host "  FAIL  $($failedResult.testName)"
                        $message = [string]$failedResult.Output.ErrorInfo.Message
                        if (-not [string]::IsNullOrWhiteSpace($message)) {
                            Write-Host "        $(($message -split "`r?`n" | Where-Object { $_.Trim() })[0])"
                        }
                    }
                    Write-Host "TEST FAILURES - $($summary.Failed) test(s) failed. TRX: $trxPath"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    $exitCode = if ($run.ExitCode -ne 0) { $run.ExitCode } else { 1 }
                    return [pscustomobject]@{ ExitCode = $exitCode; ResultsDirectory = $runDirectory }
                }
                if ($run.ExitCode -ne 0) {
                    Write-Host "RUNNER/TOOLING FAILURE - apphost exited $($run.ExitCode) although its TRX contains no failing tests. This is not a compile failure. Captured stderr/stdout: $outputLog"
                    Write-Host "Retained diagnostic directory: $runDirectory"
                    return [pscustomobject]@{ ExitCode = $run.ExitCode; ResultsDirectory = $runDirectory }
                }
            }
        }
    }

    Write-Host "ALL GREEN - clean-run TRX receipts were under '$runDirectory' and will now be removed."
    Remove-Item -LiteralPath $runDirectory -Recurse -Force -ErrorAction SilentlyContinue
    return [pscustomobject]@{ ExitCode = 0; ResultsDirectory = $runDirectory }
}

Export-ModuleMember -Function @(
    'Get-MtpTestExitCodes',
    'Read-MtpTestManifest',
    'Get-MtpLocalPartitions',
    'ConvertTo-MtpFilterArguments',
    'Get-MtpBoundedFileName',
    'Get-DefaultMtpResultsRoot',
    'Initialize-MtpResultsDirectory',
    'Get-MtpTargetProjects',
    'New-MtpRunnerArguments',
    'Read-MtpTrxResult',
    'Invoke-MtpTestRun'
)
