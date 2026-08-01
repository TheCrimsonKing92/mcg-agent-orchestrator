<#
.SYNOPSIS
  Run dotnet test with the structured TRX logger and print a compact summary.

.DESCRIPTION
  Avoids the huge UTF-16 console dumps that are painful to grep. Writes one TRX
  (XML) file per test project under an invocation-owned .test-results directory, then prints per-project
  pass/fail counts plus the name and first error line of every failing test.
  Successful run directories are removed; failed runs are retained for diagnosis.

  Exit code is 0 only when every test project reports zero failures.

.EXAMPLE
  .\scripts\Invoke-TestSummary.ps1
  .\scripts\Invoke-TestSummary.ps1 -Target .\tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj
  .\scripts\Invoke-TestSummary.ps1 -Filter "DisplayName~lifecycle"
#>
[CmdletBinding()]
param(
    [string]$Target = ".\Mcg.AgentOrchestrator.sln",
    [string]$Filter = "",
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$resultsRoot = Join-Path $repoRoot ".test-results"
$results = Join-Path $resultsRoot "run-$PID-$([Guid]::NewGuid().ToString('N'))"
$orchestrator = Join-Path $repoRoot "mcg-orchestrator.cmd"
$appDll = Join-Path $repoRoot "src/Mcg.AgentOrchestrator.App/bin/Debug/net10.0/Mcg.AgentOrchestrator.App.dll"

function Set-HermeticVerificationEnvironment {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $nugetPackages = $env:NUGET_PACKAGES
    $userProfile = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)

    $allowedNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @('PATH', 'PATHEXT', 'SystemRoot', 'WINDIR', 'COMSPEC', 'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432', 'TEMP', 'TMP', 'TMPDIR')) {
        [void]$allowedNames.Add($name)
    }

    foreach ($item in @(Get-ChildItem Env:)) {
        if (-not $allowedNames.Contains($item.Name) -and
            -not $item.Name.StartsWith('DOTNET_', [System.StringComparison]::OrdinalIgnoreCase) -and
            -not $item.Name.StartsWith('NUGET_', [System.StringComparison]::OrdinalIgnoreCase)) {
            [System.Environment]::SetEnvironmentVariable($item.Name, $null, [System.EnvironmentVariableTarget]::Process)
        }
    }

    $profileRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'mcg-hermetic-verification-profile'
    if ([string]::IsNullOrWhiteSpace($nugetPackages)) {
        $packageProfile = if ([string]::IsNullOrWhiteSpace($userProfile)) { $profileRoot } else { $userProfile }
        $nugetPackages = Join-Path $packageProfile '.nuget\packages'
    }
    $appData = Join-Path $profileRoot 'AppData\Roaming'
    $localAppData = Join-Path $profileRoot 'AppData\Local'
    New-Item -ItemType Directory -Force $appData, $localAppData | Out-Null
    $env:HOME = $profileRoot
    $env:USERPROFILE = $profileRoot
    $env:DOTNET_CLI_HOME = $profileRoot
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_NOLOGO = '1'
    $env:NUGET_PACKAGES = $nugetPackages
    $profileRootPath = [System.IO.Path]::GetPathRoot($profileRoot)
    $env:HOMEDRIVE = $profileRootPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $env:HOMEPATH = ([string][System.IO.Path]::DirectorySeparatorChar) + $profileRoot.Substring($profileRootPath.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar)
    $env:APPDATA = $appData
    $env:LOCALAPPDATA = $localAppData
    $env:MCG_ORCHESTRATOR_REPOSITORY_ROOT = [System.IO.Path]::GetFullPath($RepositoryRoot)
}

Set-HermeticVerificationEnvironment -RepositoryRoot $repoRoot

# CS2012/VBCSCompiler lock hygiene before a fresh run.
dotnet build-server shutdown | Out-Null

New-Item -ItemType Directory -Force $results | Out-Null

# Let the TRX logger auto-name one file per test project (no fixed LogFileName,
# which would collide when the target is the whole solution).
$rawLog = Join-Path $results "dotnet-test.log"
$testArgs = @($Target, '--logger', 'trx', '--results-directory', $results, '-clp:ErrorsOnly')
if ($NoBuild) { $testArgs += '--no-build' }
if ($Filter)  { $testArgs += @('--filter', $Filter) }

$hasFilterMetacharacters = $Filter.IndexOfAny([char[]]'&|<>()') -ge 0
$resolvedTarget = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Target))
$mtpTargets = if ([System.IO.Path]::GetExtension($resolvedTarget).Equals(".sln", [System.StringComparison]::OrdinalIgnoreCase)) {
    $solutionDirectory = Split-Path -Parent $resolvedTarget
    $listedProjects = @(& dotnet sln $resolvedTarget list)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to enumerate projects from solution target: $resolvedTarget"
    }

    $testProjects = @($listedProjects |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -match '\.[A-Za-z]+proj$' } |
        ForEach-Object { [System.IO.Path]::GetFullPath((Join-Path $solutionDirectory $_)) } |
        Where-Object {
            (Select-String -LiteralPath $_ -SimpleMatch '<IsTestProject>true</IsTestProject>' -Quiet) -or
            (Select-String -LiteralPath $_ -SimpleMatch '<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>' -Quiet)
        })
    if ($testProjects.Count -eq 0) {
        throw "Solution target contains no discoverable test projects: $resolvedTarget"
    }

    Write-Host "Discovered $($testProjects.Count) test project(s) from $resolvedTarget."
    $testProjects
}
else {
    @($Target)
}
$usesMtp = @($mtpTargets | Where-Object {
    $candidate = if ([System.IO.Path]::IsPathRooted($_)) {
        [System.IO.Path]::GetFullPath($_)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $repoRoot $_))
    }
    (Test-Path -LiteralPath $candidate -PathType Leaf) -and
        (Select-String -LiteralPath $candidate -SimpleMatch '<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>' -Quiet)
}).Count -eq $mtpTargets.Count
$requiresExactArguments = $usesMtp -or $hasFilterMetacharacters
if ($requiresExactArguments) {
    & $orchestrator gate-status | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $appDll -PathType Leaf)) {
        throw "Unable to prepare orchestrator app DLL for stable-slot-dotnet: $appDll"
    }

    $testExit = 0
    $executionTargets = if ($usesMtp) { $mtpTargets } else { @($Target) }
    foreach ($mtpTarget in $executionTargets) {
        $stableArguments = if ($usesMtp) {
            $arguments = @('mtp-test', $mtpTarget, '--results-directory', $results)
            if ($NoBuild) { $arguments += '--no-build' }
            if ($Filter) { $arguments += @('--filter', $Filter) }
            $arguments
        }
        else {
            @('test') + $testArgs
        }

        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = "dotnet"
        $startInfo.WorkingDirectory = $repoRoot
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in @($appDll, 'stable-slot-dotnet') + $stableArguments) {
            $startInfo.ArgumentList.Add($argument)
        }

        $process = [System.Diagnostics.Process]::Start($startInfo)
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        @($stdout, $stderr) | Add-Content -LiteralPath $rawLog
        if ($process.ExitCode -ne 0) { $testExit = $process.ExitCode }
        $process.Dispose()
    }
}
else {
    & $orchestrator stable-slot-dotnet test @testArgs *>&1 | Tee-Object -FilePath $rawLog | Out-Null
    $testExit = $LASTEXITCODE
}

$trxFiles = Get-ChildItem $results -Filter *.trx -ErrorAction SilentlyContinue
if (-not $trxFiles) {
    Write-Output "NO TRX OUTPUT - dotnet test did not produce results (likely a build error). Raw tail:"
    Get-Content $rawLog -Tail 15
    exit 1
}

$anyFailed = $false
foreach ($file in $trxFiles) {
    [xml]$trx = Get-Content $file.FullName
    $c = $trx.TestRun.ResultSummary.Counters
    $failed = [int]$c.failed
    if ($failed -gt 0) { $anyFailed = $true }
    "{0}: total={1} passed={2} failed={3} skipped={4}" -f $file.Name, $c.total, $c.passed, $failed, $c.notExecuted
    $trx.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq 'Failed' } |
        ForEach-Object {
            "  FAIL  $($_.testName)"
            $msg = $_.Output.ErrorInfo.Message
            if ($msg) { "        " + (($msg -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -First 1) }
        }
}

# `dotnet test` returns non-zero if ANY project failed to build or any test
# failed. A green TRX from one project does not prove the others built/ran, so
# trust the exit code as the source of truth and never report green when it is
# non-zero (this avoids false greens when a project fails to build, e.g. a
# CS2012 VBCSCompiler lock that yields no TRX for that project).
if ($anyFailed -or $testExit -ne 0) {
    if (-not $anyFailed) {
        Write-Output "BUILD/RUN FAILURE - dotnet test exited $testExit but produced no failing TRX (a project likely failed to build / produced no results). Raw tail:"
        Get-Content $rawLog -Tail 15
    }
    exit 1
}
Remove-Item -LiteralPath $results -Recurse -Force -ErrorAction SilentlyContinue
"ALL GREEN"; exit 0
