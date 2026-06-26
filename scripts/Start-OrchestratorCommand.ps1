param(
    [string]$Name = "command",
    [string]$AppDll,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Arguments = @($Arguments)
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

$stamp = Get-Date -Format "yyyyMMddHHmmss"
$safeName = ConvertTo-SafeName $Name
$stdoutPath = [System.IO.Path]::GetFullPath((Join-Path $logsRoot "operator-$safeName-$stamp.out.log"))
$stderrPath = [System.IO.Path]::GetFullPath((Join-Path $logsRoot "operator-$safeName-$stamp.err.log"))
$processArguments = @($resolvedAppDll) + $Arguments

$previousStdoutLogPath = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", "Process")
$previousStderrLogPath = [Environment]::GetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", "Process")
try {
    [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", $stdoutPath, "Process")
    [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", $stderrPath, "Process")

    $process = Start-Process `
        -WindowStyle Hidden `
        -PassThru `
        -FilePath "dotnet" `
        -WorkingDirectory $repoRoot `
        -ArgumentList $processArguments `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath
}
finally {
    [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH", $previousStdoutLogPath, "Process")
    [Environment]::SetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH", $previousStderrLogPath, "Process")
}

[pscustomobject]@{
    pid = [int]$process.Id
    logPath = $stdoutPath
    stdoutPath = $stdoutPath
    stderrPath = $stderrPath
    args = @($Arguments)
} | ConvertTo-Json -Compress
