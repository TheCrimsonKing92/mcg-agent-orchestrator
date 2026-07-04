<#
.SYNOPSIS
  Run the repository's built orchestrator CLI from a stable, repo-bounded script prefix.

.DESCRIPTION
  Use this through scripts\Invoke-RepoScript.ps1 when Codex needs to run foreground
  orchestrator commands without ad-hoc dotnet invocations or repeated permission
  prompts. Arguments are forwarded exactly as PowerShell receives them.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 status ddae01e6

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 attention answer ddae01e6 099efaf9 Model fit evidence is a concise per-role note.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$AppDll,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Arguments = @($Arguments)
if ($Arguments.Count -eq 0) {
    throw "Usage: .\scripts\Invoke-RepoScript.ps1 scripts\Invoke-OrchestratorCommand.ps1 <orchestrator-args...>"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($AppDll)) {
    $launcher = Join-Path $repoRoot "mcg-orchestrator.cmd"
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
        throw "Orchestrator launcher not found: $launcher"
    }

    & $launcher @Arguments
    if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    return
}

$resolvedAppDll = [System.IO.Path]::GetFullPath($AppDll)

$rootWithSeparator = $repoRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not ($resolvedAppDll.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase) -or
          [string]::Equals($resolvedAppDll, $repoRoot, [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "Refusing to run orchestrator DLL outside repository root: $resolvedAppDll"
}

if (-not (Test-Path -LiteralPath $resolvedAppDll -PathType Leaf)) {
    throw "App DLL not found. Build the app first: $resolvedAppDll"
}

& dotnet $resolvedAppDll @Arguments
if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
