[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Name = "command",
    [string]$AppDll,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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
$stdoutPath = $null
$stderrPath = $null
$processArguments = @()

try {
    $Arguments = @($Arguments | ForEach-Object { [string]$_ })
    if ($Arguments.Count -eq 0) {
        throw "Usage: .\scripts\Start-OrchestratorCommand.ps1 [-Name <name>] <orchestrator-args...>"
    }

    $usesDefaultLauncher = [string]::IsNullOrWhiteSpace($AppDll)
    $resolvedAppDll = if ($usesDefaultLauncher) {
        Join-Path $repoRoot "mcg-orchestrator.cmd"
    } else {
        [System.IO.Path]::GetFullPath($AppDll)
    }

    $logsRoot = Join-Path $repoRoot ".orchestrator\logs"
    New-Item -ItemType Directory -Force -Path $logsRoot | Out-Null

    $stamp = Get-Date -Format "yyyyMMddHHmmss"
    $safeName = ConvertTo-SafeName $Name
    $stdoutPath = [System.IO.Path]::GetFullPath((Join-Path $logsRoot "operator-$safeName-$stamp.out.log"))
    $stderrPath = [System.IO.Path]::GetFullPath((Join-Path $logsRoot "operator-$safeName-$stamp.err.log"))
    $processArguments = @($resolvedAppDll) + $Arguments

    if (-not (Test-Path -LiteralPath $resolvedAppDll -PathType Leaf)) {
        if ($usesDefaultLauncher) {
            throw "Orchestrator launcher not found: $resolvedAppDll"
        }

        throw "App DLL not found. Build the app first: $resolvedAppDll"
    }

    $executablePath = $resolvedAppDll
    $launchArguments = @($Arguments)
    if (-not $usesDefaultLauncher) {
        $dotnetPath = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH", "Process")
        if ([string]::IsNullOrWhiteSpace($dotnetPath)) {
            $dotnetPath = "dotnet"
        }

        $executablePath = $dotnetPath
        $launchArguments = @($resolvedAppDll) + $Arguments
    }
    $processArgumentLine = ($launchArguments | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join " "

    $previousStdoutLogPath = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", "Process")
    $previousStderrLogPath = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", "Process")
    $startedAt = (Get-Date).ToUniversalTime().ToString("O", [System.Globalization.CultureInfo]::InvariantCulture)
    [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", $stdoutPath, "Process")
    [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", $stderrPath, "Process")

    try {
        $process = Start-Process `
            -WindowStyle Hidden `
            -PassThru `
            -FilePath $executablePath `
            -WorkingDirectory $repoRoot `
            -ArgumentList $processArgumentLine `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath
    }
    finally {
        [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", $previousStdoutLogPath, "Process")
        [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", $previousStderrLogPath, "Process")
    }

    [pscustomobject]@{
        pid = [int]$process.Id
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
        args = @($processArguments)
        startedAt = $startedAt
    } | ConvertTo-Json -Compress
}
catch {
    Write-LaunchFailure `
        -Reason $_.Exception.Message `
        -StdoutPath $stdoutPath `
        -StderrPath $stderrPath `
        -LaunchArgs $processArguments
    exit 1
}
