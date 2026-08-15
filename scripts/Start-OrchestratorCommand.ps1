[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Name = "command",
    [string]$AppDll,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$driveJournalSchemaVersion = 1

function ConvertTo-SafeName {
    param([string]$Value)
    $safe = ($Value.Trim().ToLowerInvariant().ToCharArray() | ForEach-Object {
        if ([char]::IsLetterOrDigit($_)) { $_ } else { '-' }
    }) -join ''
    $safe = $safe.Trim('-')
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return "command"
    }

    return $safe
}

function ConvertTo-CommandLineArgument {
    param([string]$Value)

    if ($null -eq $Value) {
        return '""'
    }

    if ($Value.Length -gt 0 -and $Value.IndexOfAny([char[]]@(' ', "`t", "`n", "`r", '"')) -lt 0) {
        return $Value
    }

    $quoted = [System.Text.StringBuilder]::new()
    [void]$quoted.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            [void]$quoted.Append('\', ($backslashes * 2) + 1)
            [void]$quoted.Append('"')
            $backslashes = 0
            continue
        }

        if ($backslashes -gt 0) {
            [void]$quoted.Append('\', $backslashes)
            $backslashes = 0
        }

        [void]$quoted.Append($character)
    }

    if ($backslashes -gt 0) {
        [void]$quoted.Append('\', $backslashes * 2)
    }

    [void]$quoted.Append('"')
    return $quoted.ToString()
}

function Get-ArgumentValue {
    param(
        [string[]]$Values,
        [string]$Name
    )

    for ($i = 0; $i -lt $Values.Count; $i++) {
        if ($Values[$i].Equals($Name, [System.StringComparison]::OrdinalIgnoreCase)) {
            if (($i + 1) -lt $Values.Count) {
                return $Values[$i + 1]
            }

            return $null
        }
    }

    return $null
}

function Test-HasArgument {
    param(
        [string[]]$Values,
        [string]$Name
    )

    foreach ($value in $Values) {
        if ($value.Equals($Name, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Write-LastDriveJournal {
    param(
        [string]$RepositoryRoot,
        [string]$JournalPath,
        [string]$BatchName,
        [string]$ApplicationDll,
        [string[]]$CommandArguments
    )

    if ($CommandArguments.Count -lt 2 -or
        -not $CommandArguments[0].Equals("conduct", [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-HasArgument -Values $CommandArguments -Name "--loop")) {
        return
    }

    $orchestratorRoot = Split-Path -Parent $JournalPath
    New-Item -ItemType Directory -Force -Path $orchestratorRoot | Out-Null

    $journal = [ordered]@{
        schemaVersion = $driveJournalSchemaVersion
        writtenAt = (Get-Date).ToUniversalTime().ToString("O", [System.Globalization.CultureInfo]::InvariantCulture)
        repoRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
        name = $BatchName
        appDll = if ([string]::IsNullOrWhiteSpace($ApplicationDll)) { $null } else { [System.IO.Path]::GetFullPath($ApplicationDll) }
        arguments = @($CommandArguments)
        policy = Get-ArgumentValue -Values $CommandArguments -Name "--policy"
        pollSeconds = Get-ArgumentValue -Values $CommandArguments -Name "--poll-seconds"
        watchInterval = Get-ArgumentValue -Values $CommandArguments -Name "--watch-interval"
        maxDuration = Get-ArgumentValue -Values $CommandArguments -Name "--max-duration"
        maxIterations = Get-ArgumentValue -Values $CommandArguments -Name "--max-iterations"
        watch = Test-HasArgument -Values $CommandArguments -Name "--watch"
        daemon = Test-HasArgument -Values $CommandArguments -Name "--daemon"
    }

    $json = $journal | ConvertTo-Json -Depth 8
    Set-Content -LiteralPath $JournalPath -Value $json -Encoding UTF8
}

function Write-LaunchFailure {
    param(
        [string]$Reason,
        [string]$StdoutPath,
        [string]$StderrPath,
        [string[]]$LaunchArgs
    )

    $errorPayload = [ordered]@{
        reason = $Reason
    }

    if (-not [string]::IsNullOrWhiteSpace($StdoutPath)) {
        $errorPayload.stdoutPath = $StdoutPath
    }

    if (-not [string]::IsNullOrWhiteSpace($StderrPath)) {
        $errorPayload.stderrPath = $StderrPath
    }

    if ($null -ne $LaunchArgs) {
        $errorPayload.args = @($LaunchArgs)
    }

    [Console]::Error.WriteLine(($errorPayload | ConvertTo-Json -Compress))
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $repoRoot "mcg-orchestrator.cmd"
$stdoutPath = $null
$stderrPath = $null
$processArguments = @()

try {
    $Arguments = @($Arguments | ForEach-Object { [string]$_ })
    if ($Arguments.Count -eq 0) {
        throw "Usage: .\scripts\Start-OrchestratorCommand.ps1 [-Name <name>] <orchestrator-args...>"
    }

    $usesLauncher = [string]::IsNullOrWhiteSpace($AppDll)
    $targetExecutable = if ($usesLauncher) {
        $launcher
    } else {
        [System.IO.Path]::GetFullPath($AppDll)
    }

    $logsRoot = Join-Path $repoRoot ".orchestrator\logs"
    New-Item -ItemType Directory -Force -Path $logsRoot | Out-Null

    $stamp = Get-Date -Format "yyyyMMddHHmmss"
    $safeName = ConvertTo-SafeName $Name
    $stdoutPath = [System.IO.Path]::GetFullPath((Join-Path $logsRoot "operator-$safeName-$stamp.out.log"))
    $stderrPath = [System.IO.Path]::GetFullPath((Join-Path $logsRoot "operator-$safeName-$stamp.err.log"))
    $processArguments = @($targetExecutable) + $Arguments

    if (-not (Test-Path -LiteralPath $targetExecutable -PathType Leaf)) {
        throw "Launcher or App DLL not found: $targetExecutable"
    }

    $dotnetPath = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH", "Process")
    if ([string]::IsNullOrWhiteSpace($dotnetPath)) {
        $dotnetPath = "dotnet"
    }

    $processFilePath = $dotnetPath
    $launchArguments = $processArguments
    if ($usesLauncher) {
        $processFilePath = $env:ComSpec
        if ([string]::IsNullOrWhiteSpace($processFilePath)) {
            $processFilePath = "cmd.exe"
        }

        $launchArguments = @("/d", "/c") + $processArguments
    }

    $processArgumentLine = ($launchArguments | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join " "

    $journalPath = Join-Path $repoRoot ".orchestrator\last-drive.json"
    Write-LastDriveJournal `
        -RepositoryRoot $repoRoot `
        -JournalPath $journalPath `
        -BatchName $Name `
        -ApplicationDll $AppDll `
        -CommandArguments $Arguments

    $previousStdoutLogPath = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", "Process")
    $previousStderrLogPath = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", "Process")
    $previousBatchName = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME", "Process")
    $startedAt = (Get-Date).ToUniversalTime().ToString("O", [System.Globalization.CultureInfo]::InvariantCulture)
    [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", $stdoutPath, "Process")
    [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", $stderrPath, "Process")
    [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME", $Name, "Process")

    try {
        $process = Start-Process `
            -WindowStyle Hidden `
            -PassThru `
            -FilePath $processFilePath `
            -WorkingDirectory $repoRoot `
            -ArgumentList $processArgumentLine `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath
    }
    finally {
        [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", $previousStdoutLogPath, "Process")
        [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", $previousStderrLogPath, "Process")
        [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_CONDUCT_BATCH_NAME", $previousBatchName, "Process")
    }

    $requestKey = $null
    for ($index = 0; $index -lt $Arguments.Count; $index++) {
        if ($Arguments[$index] -eq "--request-key" -and $index + 1 -lt $Arguments.Count) {
            $requestKey = $Arguments[$index + 1]
            break
        }
        if ($Arguments[$index].StartsWith("--request-key=", [System.StringComparison]::OrdinalIgnoreCase)) {
            $requestKey = $Arguments[$index].Substring("--request-key=".Length)
            break
        }
    }

    $receipt = [ordered]@{
        pid = [int]$process.Id
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
        args = @($processArguments)
        startedAt = $startedAt
    }
    if (-not [string]::IsNullOrWhiteSpace($requestKey)) {
        $receipt["requestKey"] = $requestKey
    }
    [pscustomobject]$receipt | ConvertTo-Json -Compress
}
catch {
    Write-LaunchFailure `
        -Reason $_.Exception.Message `
        -StdoutPath $stdoutPath `
        -StderrPath $stderrPath `
        -LaunchArgs $processArguments
    exit 1
}
