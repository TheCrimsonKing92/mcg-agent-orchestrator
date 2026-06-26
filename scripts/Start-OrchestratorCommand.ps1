param(
    [string]$Name = "command",

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Arguments.Count -eq 0) {
    throw "Usage: .\scripts\Start-OrchestratorCommand.ps1 [-Name <name>] <orchestrator-args...>"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$appDll = Join-Path $repoRoot "src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0\Mcg.AgentOrchestrator.App.dll"
if (-not (Test-Path -LiteralPath $appDll)) {
    throw "App DLL not found. Build the app first: $appDll"
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
$stdoutPath = Join-Path $logsRoot "operator-$safeName-$stamp.out.log"
$stderrPath = Join-Path $logsRoot "operator-$safeName-$stamp.err.log"
$processArguments = @($appDll) + $Arguments

$process = Start-Process `
    -WindowStyle Hidden `
    -PassThru `
    -FilePath "dotnet" `
    -WorkingDirectory $repoRoot `
    -ArgumentList $processArguments `
    -RedirectStandardOutput $stdoutPath `
    -RedirectStandardError $stderrPath

Write-Output "pid=$($process.Id)"
Write-Output "stdout=$stdoutPath"
Write-Output "stderr=$stderrPath"
Write-Output "args=$($Arguments -join ' ')"
