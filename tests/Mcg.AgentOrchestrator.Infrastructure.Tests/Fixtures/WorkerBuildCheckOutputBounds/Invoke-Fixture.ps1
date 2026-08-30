param(
    [ValidateSet("Warnings", "CompilerFailure", "NonDiagnosticFailure", "MultipleProjects", "LogUnavailable")]
    [string]$Scenario = "Warnings"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Contract {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) { throw $Message }
}

function Count-DiagnosticLines {
    param(
        [string]$Text,
        [string]$Kind
    )

    $pattern = ": $Kind "
    return @(($Text -split "`r?`n") | Where-Object {
        $_.IndexOf($pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    }).Count
}

function Invoke-CapturedProcess {
    param(
        [string]$FileName,
        [string[]]$ArgumentList,
        [string]$WorkingDirectory
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) { $startInfo.ArgumentList.Add($argument) }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Process did not start: $FileName" }
    $process.StandardInput.Close()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $result = [pscustomobject]@{
        ExitCode = $process.ExitCode
        Stdout = $stdoutTask.GetAwaiter().GetResult()
        Stderr = $stderrTask.GetAwaiter().GetResult()
    }
    $process.Dispose()
    return $result
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\..\.."))
$helperPath = Join-Path $repositoryRoot "scripts\Invoke-WorkerBuildCheck.ps1"
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("mcg-worker-build-fixture-" + [Guid]::NewGuid().ToString("N"))
$workDirectory = Join-Path $temporaryRoot "work"
$shimDirectory = Join-Path $temporaryRoot "shim"
$isolatedRoot = Join-Path $temporaryRoot "isolated"
$shimLog = Join-Path $temporaryRoot "dotnet-arguments.log"
$shellPath = (Get-Process -Id $PID).Path
$originalPath = $env:PATH
$originalIsolatedRoot = $env:MCG_DOTNET_ISOLATED_ROOT
$originalScenario = $env:MCG_BUILD_FIXTURE_SCENARIO
$originalShimLog = $env:DOTNET_SHIM_LOG

try {
    New-Item -ItemType Directory -Force -Path $workDirectory, $shimDirectory, $isolatedRoot | Out-Null
    $projectOne = Join-Path $workDirectory "One.csproj"
    $projectTwo = Join-Path $workDirectory "Two.csproj"
    Set-Content -LiteralPath $projectOne -Value "<Project />"
    Set-Content -LiteralPath $projectTwo -Value "<Project />"

$shimScript = @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
Add-Content -LiteralPath $env:DOTNET_SHIM_LOG -Value ([string]::Join(" ", $Arguments))
if ($Arguments.Count -gt 0 -and $Arguments[0] -eq "build-server") { exit 0 }
$project = if ($Arguments.Count -gt 1) { $Arguments[1] } else { "unknown.csproj" }
$warningPayload = "w" * 190
$detailedLines = [System.Collections.Generic.List[string]]::new()
$exitCode = 0
switch ($env:MCG_BUILD_FIXTURE_SCENARIO) {
    "CompilerFailure" {
        1..20 | ForEach-Object { $detailedLines.Add("$project($_,1): warning FX1000: deterministic warning $warningPayload") }
        $detailedLines.Add("$project(21,1): error CS0001: first deterministic compiler error")
        $detailedLines.Add("$project(22,1): error CS0002: second deterministic compiler error")
        $exitCode = 1
    }
    "NonDiagnosticFailure" {
        $detailedLines.Add("SDK terminated before producing a compiler diagnostic")
        $exitCode = 7
    }
    default {
        1..1800 | ForEach-Object { $detailedLines.Add("$project($_,1): warning FX1000: deterministic warning $warningPayload") }
    }
}

$fileLoggerArgument = @($Arguments | Where-Object { $_ -match '^-flp:' }) | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($fileLoggerArgument)) {
    for ($argumentIndex = 0; $argumentIndex -lt ($Arguments.Count - 1); $argumentIndex++) {
        if ($Arguments[$argumentIndex] -eq '-flp') {
            $fileLoggerArgument = '-flp:' + $Arguments[$argumentIndex + 1]
            break
        }
    }
}
$isHelperInvocation = @($Arguments | Where-Object { $_ -eq '--nologo' }).Count -gt 0
if ($isHelperInvocation -and [string]::IsNullOrWhiteSpace($fileLoggerArgument)) {
    throw "helper invocation omitted file logger argument after cmd transport: $([string]::Join(' || ', $Arguments))"
}
if (-not [string]::IsNullOrWhiteSpace($fileLoggerArgument)) {
    $logPathMatch = [regex]::Match($fileLoggerArgument, '(?i)(?:^|[:;])LogFile=(?<path>[^;]+)')
    if (-not $logPathMatch.Success) { throw "file logger argument omitted LogFile=: $fileLoggerArgument" }
    $detailedLogPath = $logPathMatch.Groups["path"].Value
    $utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllLines($detailedLogPath, $detailedLines, $utf8WithoutBom)
}

$errorsOnly = @($Arguments | Where-Object { $_ -eq '-clp:ErrorsOnly' }).Count -gt 0
if (-not $errorsOnly) {
    for ($argumentIndex = 0; $argumentIndex -lt ($Arguments.Count - 1); $argumentIndex++) {
        if ($Arguments[$argumentIndex] -eq '-clp' -and $Arguments[$argumentIndex + 1] -eq 'ErrorsOnly') {
            $errorsOnly = $true
            break
        }
    }
}
$consoleLines = if ($errorsOnly -and $env:MCG_BUILD_FIXTURE_SCENARIO -ne "NonDiagnosticFailure") {
    @($detailedLines | Where-Object { $_.IndexOf(': error ', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 })
}
else {
    @($detailedLines)
}
$consoleLines | ForEach-Object { Write-Output $_ }
exit $exitCode
'@
    Set-Content -LiteralPath (Join-Path $shimDirectory "dotnet-shim.ps1") -Value $shimScript
    $shimCommand = @'
@echo off
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0dotnet-shim.ps1" %*
exit /b %ERRORLEVEL%
'@
    Set-Content -LiteralPath (Join-Path $shimDirectory "dotnet.cmd") -Value $shimCommand
    $rawScript = @'
param([string]$Project)
& dotnet build $Project 2>&1
exit $LASTEXITCODE
'@
    $rawScriptPath = Join-Path $temporaryRoot "raw-build.ps1"
    Set-Content -LiteralPath $rawScriptPath -Value $rawScript

    $env:PATH = $shimDirectory + [System.IO.Path]::PathSeparator + $originalPath
    $env:MCG_DOTNET_ISOLATED_ROOT = $isolatedRoot
    $env:MCG_BUILD_FIXTURE_SCENARIO = $Scenario
    $env:DOTNET_SHIM_LOG = $shimLog

    if ($Scenario -eq "LogUnavailable") {
        $fixtureGoalPrefix = if ($workDirectory -match '[\\/]\.orchestrator-worktrees[\\/](?<prefix>[^\\/]+)') {
            $Matches["prefix"]
        }
        else {
            "manual"
        }
        $artifactsPath = Join-Path $isolatedRoot "goals\$fixtureGoalPrefix\artifacts"
        New-Item -ItemType Directory -Force -Path $artifactsPath | Out-Null
        "{`"version`":1,`"ownerToken`":`"goal-$fixtureGoalPrefix`"}" | Set-Content -LiteralPath (Join-Path $artifactsPath ".mcg-artifacts-owner.json")
        Set-Content -LiteralPath (Join-Path $artifactsPath "worker-build-logs") -Value "blocks log directory creation"
    }

    $projects = if ($Scenario -eq "MultipleProjects") { @($projectOne, $projectTwo) } else { @($projectOne) }
    $helperArguments = @("-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", $helperPath) + $projects
    $helper = Invoke-CapturedProcess -FileName $shellPath -ArgumentList $helperArguments -WorkingDirectory $workDirectory
    $helperText = $helper.Stdout + $helper.Stderr

    switch ($Scenario) {
        "Warnings" {
            $raw = Invoke-CapturedProcess -FileName $shellPath -ArgumentList @("-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", $rawScriptPath, $projectOne) -WorkingDirectory $workDirectory
            $rawText = $raw.Stdout + $raw.Stderr
            $rawCharacters = $rawText.Length
            $helperCharacters = $helperText.Length
            $rawErrors = Count-DiagnosticLines -Text $rawText -Kind "error"
            $helperErrors = Count-DiagnosticLines -Text $helperText -Kind "error"
            $vectors = Get-Content -LiteralPath $shimLog
            Assert-Contract ($raw.ExitCode -eq $helper.ExitCode) "raw/helper exit codes differ: raw=$($raw.ExitCode) helper=$($helper.ExitCode) helper_output=$helperText"
            Assert-Contract ($raw.ExitCode -eq 0) "warning fixture did not succeed"
            Assert-Contract ($rawCharacters -ge 256000) "raw output was $rawCharacters characters; expected at least 256000"
            Assert-Contract ($helperCharacters -lt 2048) "helper output was $helperCharacters characters; expected below 2048"
            Assert-Contract ($rawErrors -eq $helperErrors -and $rawErrors -eq 0) "raw/helper error counts differ"
            Assert-Contract (@($vectors | Where-Object { $_ -match '^build ' -and $_ -notmatch '--nologo' }).Count -eq 1) "raw argument vector was not recorded"
            Assert-Contract (@($vectors | Where-Object { $_ -match '^build ' -and $_ -match '--nologo' -and $_ -match '-tl(?::|\s+)off' -and $_ -match '-fl(?:\s|$)' -and $_ -match '-flp(?::|\s+)LogFile=' }).Count -eq 1) "helper argument vector omitted the detailed file logger: $([string]::Join(' || ', $vectors))"
            $logs = @(Get-ChildItem -LiteralPath $isolatedRoot -Filter "*.log" -File -Recurse)
            Assert-Contract ($logs.Count -eq 1) "expected one durable project log; found $($logs.Count)"
            $detailedLogText = Get-Content -LiteralPath $logs[0].FullName -Raw
            Assert-Contract (-not $helperText.Contains("deterministic warning")) "warning leaked into model-visible helper output"
            Assert-Contract ($detailedLogText.Contains("deterministic warning")) "durable helper log omitted the warning"
            Assert-Contract ($detailedLogText.Length -ge 256000) "durable helper log did not retain complete warning output"
            $estimatedTokens = [int][Math]::Ceiling($helperCharacters / 4.0)
            Write-Output "PASS fixture: scenario=Warnings raw_chars=$rawCharacters helper_chars=$helperCharacters raw_exit=$($raw.ExitCode) helper_exit=$($helper.ExitCode) raw_errors=$rawErrors helper_errors=$helperErrors approximate_helper_tokens=$estimatedTokens"
        }
        "CompilerFailure" {
            Assert-Contract ($helper.ExitCode -eq 1) "compiler failure did not return exit 1"
            Assert-Contract ($helperText.Contains(": error CS0001:") -and $helperText.Contains(": error CS0002:")) "exact compiler errors were not model-visible"
            Assert-Contract ($helperText.Contains("complete-log:")) "failure omitted complete-log path"
            Assert-Contract ($helperText.Length -lt 4096) "failure context was not bounded"
            $logText = Get-Content -LiteralPath (Get-ChildItem -LiteralPath $isolatedRoot -Filter "*.log" -File -Recurse).FullName -Raw
            Assert-Contract ($logText.Contains("deterministic warning") -and $logText.Contains(": error CS0002:")) "detailed failure log discarded surrounding diagnostics"
            Write-Output "PASS fixture: scenario=CompilerFailure helper_chars=$($helperText.Length) errors=$(Count-DiagnosticLines -Text $helperText -Kind 'error')"
        }
        "NonDiagnosticFailure" {
            Assert-Contract ($helper.ExitCode -eq 1) "non-diagnostic failure did not normalize to exit 1"
            Assert-Contract ($helperText.Contains("FAIL build: 1 error(s)")) "non-diagnostic failure did not retain minimum error count"
            Assert-Contract ($helperText.Contains("context: SDK terminated")) "non-diagnostic failure omitted bounded terminal context"
            Write-Output "PASS fixture: scenario=NonDiagnosticFailure helper_chars=$($helperText.Length)"
        }
        "MultipleProjects" {
            Assert-Contract ($helper.ExitCode -eq 0) "multi-project helper failed"
            Assert-Contract ($helperText.Contains("projects=2")) "multi-project summary omitted project count"
            $logs = @(Get-ChildItem -LiteralPath $isolatedRoot -Filter "*.log" -File -Recurse)
            Assert-Contract ($logs.Count -eq 2) "expected collision-safe logs for two projects; found $($logs.Count)"
            Write-Output "PASS fixture: scenario=MultipleProjects helper_chars=$($helperText.Length) logs=$($logs.Count)"
        }
        "LogUnavailable" {
            Assert-Contract ($helper.ExitCode -eq 1) "unavailable log path incorrectly produced success"
            Assert-Contract ($helperText.Contains("build-check apparatus failure")) "log apparatus failure was not explicit"
            Assert-Contract (-not $helperText.Contains("PASS build:")) "log apparatus failure produced PASS"
            Write-Output "PASS fixture: scenario=LogUnavailable helper_chars=$($helperText.Length)"
        }
    }
}
finally {
    $env:PATH = $originalPath
    $env:MCG_DOTNET_ISOLATED_ROOT = $originalIsolatedRoot
    $env:MCG_BUILD_FIXTURE_SCENARIO = $originalScenario
    $env:DOTNET_SHIM_LOG = $originalShimLog
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
