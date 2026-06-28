[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Name = "command",
    [string]$AppDll,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Arguments = @($Arguments | ForEach-Object { [string]$_ })
if ($Arguments.Count -eq 0) {
    throw "Usage: .\scripts\Start-OrchestratorCommand.ps1 [-Name <name>] <orchestrator-args...>"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedAppDll = if ([string]::IsNullOrWhiteSpace($AppDll)) {
    Join-Path $repoRoot "src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0\Mcg.AgentOrchestrator.App.dll"
} else {
    [System.IO.Path]::GetFullPath($AppDll)
}
if (-not (Test-Path -LiteralPath $resolvedAppDll)) {
    throw "App DLL not found. Build the app first: $resolvedAppDll"
}

$logsRoot = Join-Path $repoRoot ".orchestrator\logs"
New-Item -ItemType Directory -Force -Path $logsRoot | Out-Null

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

$stamp = Get-Date -Format "yyyyMMddHHmmss"
$safeName = ConvertTo-SafeName $Name
$stdoutPath = [System.IO.Path]::GetFullPath((Join-Path $logsRoot "operator-$safeName-$stamp.out.log"))
$stderrPath = [System.IO.Path]::GetFullPath((Join-Path $logsRoot "operator-$safeName-$stamp.err.log"))
$processArguments = @($resolvedAppDll) + $Arguments
$processArgumentLine = ($processArguments | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join " "
$dotnetPath = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH", "Process")
if ([string]::IsNullOrWhiteSpace($dotnetPath)) {
    $dotnetPath = "dotnet"
}

$previousStdoutLogPath = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", "Process")
$previousStderrLogPath = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", "Process")
try {
    [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", $stdoutPath, "Process")
    [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", $stderrPath, "Process")

    $process = Start-Process `
        -WindowStyle Hidden `
        -PassThru `
        -FilePath $dotnetPath `
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
    args = @($Arguments)
} | ConvertTo-Json -Compress
