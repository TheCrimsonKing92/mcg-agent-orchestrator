param(
    [ValidateRange(1, 3600)]
    [int]$TimeoutSeconds = 900,

    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$normalizedArguments = [System.Collections.Generic.List[string]]::new()
for ($argumentIndex = 0; $argumentIndex -lt $Arguments.Count; $argumentIndex++) {
    $argument = $Arguments[$argumentIndex]
    if ($argument.EndsWith(":", [System.StringComparison]::Ordinal) -and
        $argumentIndex + 1 -lt $Arguments.Count -and
        -not $Arguments[$argumentIndex + 1].StartsWith("-", [System.StringComparison]::Ordinal)) {
        $normalizedArguments.Add($argument + $Arguments[$argumentIndex + 1])
        $argumentIndex++
    }
    else {
        $normalizedArguments.Add($argument)
    }
}
$Arguments = [string[]]$normalizedArguments

if ($Arguments.Count -eq 0 -or
    -not [string]::Equals($Arguments[0], "build", [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Output "DIAGNOSTIC build: exit=2 errors=1 warnings=0 chars=0 log=unavailable"
    Write-Output "error: Invoke-WorkerBuildDiagnostic accepts a dotnet build argument array beginning with 'build'."
    exit 2
}

function ConvertTo-SafePathSegment {
    param([string]$Value)

    $safe = ($Value.Trim().ToLowerInvariant().ToCharArray() | ForEach-Object {
        if ([char]::IsLetterOrDigit($_)) { $_ } else { '-' }
    }) -join ''
    $safe = $safe.Trim('-')
    if ([string]::IsNullOrWhiteSpace($safe)) { return "manual" }
    return $safe
}

function Get-IsolatedRootBase {
    if (-not [string]::IsNullOrWhiteSpace($env:MCG_DOTNET_ISOLATED_ROOT)) {
        return $env:MCG_DOTNET_ISOLATED_ROOT
    }

    if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT -and
        -not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $localLow = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "..\LocalLow"))
        return (Join-Path $localLow "mcg-dotnet-isolated")
    }

    return (Join-Path ([System.IO.Path]::GetTempPath()) "mcg-dotnet-isolated")
}

function Get-GoalPrefix {
    $repositoryRoot = (Get-Location).Path
    $marker = "$([System.IO.Path]::DirectorySeparatorChar).orchestrator-worktrees$([System.IO.Path]::DirectorySeparatorChar)"
    $index = $repositoryRoot.IndexOf($marker, [System.StringComparison]::OrdinalIgnoreCase)
    if ($index -ge 0) {
        $remaining = $repositoryRoot.Substring($index + $marker.Length)
        $separatorIndex = $remaining.IndexOf([System.IO.Path]::DirectorySeparatorChar)
        if ($separatorIndex -ge 0) { $remaining = $remaining.Substring(0, $separatorIndex) }
        if (-not [string]::IsNullOrWhiteSpace($remaining)) { return $remaining }
    }

    return "manual"
}

function Count-DiagnosticLines {
    param(
        [string[]]$Lines,
        [string]$Kind
    )

    $pattern = ": $Kind "
    return @($Lines | Where-Object {
        $_.IndexOf($pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    }).Count
}

$safeGoalPrefix = ConvertTo-SafePathSegment -Value (Get-GoalPrefix)
$runId = "{0}-{1}-{2}" -f (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ"), $PID, ([Guid]::NewGuid().ToString("N").Substring(0, 8))
$logDirectory = Join-Path (Get-IsolatedRootBase) "goals\$safeGoalPrefix\artifacts\worker-build-diagnostics\$runId"
$logPath = Join-Path $logDirectory "dotnet-build.log"
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

try {
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "dotnet"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "dotnet process did not start" }
    $process.StandardInput.Close()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = if ($timedOut) { 124 } else { $process.ExitCode }
    $process.Dispose()

    $timeoutReceipt = if ($timedOut) { "TIMEOUT after $TimeoutSeconds seconds`r`n" } else { "" }
    $completeOutput = "$timeoutReceipt`STDOUT`r`n$stdout`r`nSTDERR`r`n$stderr"
    [System.IO.File]::WriteAllText($logPath, $completeOutput, [System.Text.UTF8Encoding]::new($false))
    $lines = [string[]]@($completeOutput -split "`r?`n")
    $errorCount = Count-DiagnosticLines -Lines $lines -Kind "error"
    $warningCount = Count-DiagnosticLines -Lines $lines -Kind "warning"
    $characterCount = $completeOutput.Length
    $stopwatch.Stop()
    Write-Output "DIAGNOSTIC build: exit=$exitCode errors=$errorCount warnings=$warningCount chars=$characterCount elapsed_ms=$($stopwatch.ElapsedMilliseconds) log=$logPath (not worker build evidence)"
    exit $exitCode
}
catch {
    $stopwatch.Stop()
    Write-Output "DIAGNOSTIC build: exit=1 errors=1 warnings=0 chars=0 elapsed_ms=$($stopwatch.ElapsedMilliseconds) log=$logPath (not worker build evidence)"
    Write-Output "error: diagnostic apparatus failure: $($_.Exception.Message)"
    exit 1
}
